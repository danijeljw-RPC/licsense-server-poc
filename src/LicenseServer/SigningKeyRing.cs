using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SoftwareLicensing;

namespace LicenseServer;

/// <summary>
/// Filesystem/crypto-only key discovery: given a directory, finds "&lt;keyId&gt;.private.pem" /
/// "&lt;keyId&gt;.public.pem" pairs. Never throws for a single bad file — records the problem
/// against that key and keeps scanning the rest of the directory.
/// </summary>
public static class KeyDirectoryScanner
{
    public sealed record ScannedKey(
        string KeyId, string? PrivatePem, string? PublicPem, bool Valid, string? Error);

    public static IReadOnlyList<ScannedKey> Scan(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return [];

        var privateByKeyId = new Dictionary<string, string>(StringComparer.Ordinal);
        var publicByKeyId = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            string? keyId = null;
            var isPrivate = false;

            if (name.EndsWith(SigningKeyFiles.PrivateSuffix, StringComparison.Ordinal))
            {
                keyId = name[..^SigningKeyFiles.PrivateSuffix.Length];
                isPrivate = true;
            }
            else if (name.EndsWith(SigningKeyFiles.PublicSuffix, StringComparison.Ordinal))
            {
                keyId = name[..^SigningKeyFiles.PublicSuffix.Length];
            }
            else
            {
                continue; // not a recognized key filename; ignore (README, checksums, etc.)
            }

            if (!SigningKeyFiles.IsValidKeyId(keyId))
                continue; // invalid keyId shape; ignore rather than fail the whole scan

            try
            {
                var text = File.ReadAllText(file);
                if (isPrivate) privateByKeyId[keyId] = text;
                else publicByKeyId[keyId] = text;
            }
            catch (IOException)
            {
                // Transient read failure (e.g. mid-write during an atomic file replace); skip this
                // file for this scan pass, the next periodic reload will pick it up.
            }
        }

        var results = new List<ScannedKey>();

        foreach (var (keyId, publicPem) in publicByKeyId)
        {
            var hasPrivate = privateByKeyId.TryGetValue(keyId, out var privatePem);
            if (!EcdsaKeyPairs.TryValidatePublicKey(publicPem, out var publicError))
            {
                results.Add(new ScannedKey(keyId, null, null, false, publicError));
                continue;
            }

            if (!hasPrivate)
            {
                // Public-only is the expected shape of a verification-only/historical key.
                results.Add(new ScannedKey(keyId, null, publicPem, true, null));
                continue;
            }

            if (!EcdsaKeyPairs.TryValidatePair(privatePem!, publicPem, out var pairError))
            {
                results.Add(new ScannedKey(keyId, null, null, false, pairError));
                continue;
            }

            results.Add(new ScannedKey(keyId, privatePem, publicPem, true, null));
        }

        // A private key with no matching public key can't be verified against, so it is never
        // trusted for signing either - it never appears in the ring, only logged by the caller.
        foreach (var keyId in privateByKeyId.Keys.Except(publicByKeyId.Keys))
        {
            results.Add(new ScannedKey(
                keyId, null, null, false,
                $"'{keyId}.private.pem' has no matching '{keyId}.public.pem' and was ignored."));
        }

        return results;
    }
}

/// <summary>
/// Owns the live signing key ring: periodically reconciles the configured key directory against the
/// "SigningKeys" table (the durable audit trail and revocation authority) and publishes an immutable
/// snapshot readers see atomically. Deliberately uses a periodic timer only, not a FileSystemWatcher -
/// bind-mounted volumes don't reliably deliver watcher events in Docker anyway (the full design's own
/// fallback timer would be needed regardless), and a bounded-delay poll is far simpler to reason about
/// and test correctly than a debounced watcher for a POC-scale key ring.
/// </summary>
public sealed partial class SigningKeyRingService(
    IOptions<LicensingOptions> options,
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<SigningKeyRingService> logger)
    : BackgroundService, ILicenseKeyRing, ILicenseSigner, ILicenseVerifier
{
    private sealed record PemPair(string? PrivatePem, string PublicPem);

    private sealed record KeyRingSnapshot(
        IReadOnlyList<SigningKeyInfo> Keys, string? DefaultKeyId, IReadOnlyDictionary<string, PemPair> Pem)
    {
        public static readonly KeyRingSnapshot Empty = new([], null, new Dictionary<string, PemPair>());
    }

    private readonly SemaphoreSlim reloadGate = new(1, 1);
    private KeyRingSnapshot snapshot = KeyRingSnapshot.Empty;

    public string? DefaultKeyId => snapshot.DefaultKeyId;
    public IReadOnlyList<SigningKeyInfo> Keys => snapshot.Keys;
    public SigningKeyInfo? Find(string keyId) => snapshot.Keys.FirstOrDefault(k => k.KeyId == keyId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = Math.Max(5, options.Value.KeyRingReloadIntervalSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await ReloadAsync(stoppingToken);
    }

    /// <summary>
    /// Scans the key directory, reconciles the result against "SigningKeys", and atomically swaps in
    /// a new snapshot. Any failure leaves the previously published snapshot in place - a bad reload
    /// never drops the server to an empty or crashed key ring.
    /// </summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await reloadGate.WaitAsync(cancellationToken);
        try
        {
            var scanned = KeyDirectoryScanner.Scan(options.Value.KeyDirectory);
            foreach (var bad in scanned.Where(s => !s.Valid))
                LogKeyExcluded(logger, bad.KeyId, bad.Error);

            var now = clock.GetUtcNow();
            var scannedByKeyId = scanned.Where(s => s.Valid).ToDictionary(s => s.KeyId, StringComparer.Ordinal);

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Captured before this pass's upserts: distinguishes "database has never had any
            // SigningKeys row" (apply the bootstrap seed) from "a key was revoked and IsDefault is
            // now unset on every row" (must NOT re-seed - that would silently defeat revocation's
            // fail-closed guarantee the moment the next reload runs).
            var isFirstEverReload = !await db.SigningKeys.AnyAsync(cancellationToken);

            foreach (var (keyId, found) in scannedByKeyId)
            {
                var existing = await db.SigningKeys.SingleOrDefaultAsync(x => x.KeyId == keyId, cancellationToken);
                var hasPrivate = found.PrivatePem is not null;

                if (existing is null)
                {
                    db.SigningKeys.Add(new SigningKeyRecord
                    {
                        Id = Guid.NewGuid(),
                        KeyId = keyId,
                        Algorithm = LicenseConstants.Algorithm,
                        PublicKeyPem = found.PublicPem!,
                        Provider = "file-directory",
                        CreatedAt = now,
                        DiscoveredAt = now,
                        LastSeenAt = now,
                        RetiredAt = hasPrivate ? null : now
                    });
                }
                else
                {
                    existing.LastSeenAt = now;
                    existing.PublicKeyPem = found.PublicPem!;
                    if (!hasPrivate && existing.RetiredAt is null)
                        existing.RetiredAt = now;
                    else if (hasPrivate && existing.RetiredAt is not null)
                        existing.RetiredAt = null; // restoring the private file un-retires the key
                }
            }

            // Flush discovered/updated rows first: the seed-default lookup below queries the
            // database directly, which does not see this scan's own not-yet-saved Added entities.
            await db.SaveChangesAsync(cancellationToken);

            if (isFirstEverReload
                && !await db.SigningKeys.AnyAsync(x => x.IsDefault, cancellationToken)
                && !string.IsNullOrWhiteSpace(options.Value.DefaultSigningKey))
            {
                var seedRow = await db.SigningKeys.SingleOrDefaultAsync(
                    x => x.KeyId == options.Value.DefaultSigningKey, cancellationToken);
                if (seedRow is not null)
                {
                    seedRow.IsDefault = true;
                    await db.SaveChangesAsync(cancellationToken);
                }
            }

            var rows = await db.SigningKeys.AsNoTracking().ToListAsync(cancellationToken);
            var infos = new List<SigningKeyInfo>();
            var pem = new Dictionary<string, PemPair>(StringComparer.Ordinal);
            string? defaultKeyId = null;

            foreach (var row in rows)
            {
                scannedByKeyId.TryGetValue(row.KeyId, out var found);
                var hasPrivate = found?.PrivatePem is not null;
                var revoked = row.RevokedAt is not null;
                var status = revoked ? SigningKeyStatus.Revoked
                    : hasPrivate ? SigningKeyStatus.Active
                    : SigningKeyStatus.VerificationOnly;

                infos.Add(new SigningKeyInfo(
                    row.KeyId, row.Algorithm, hasPrivate, true,
                    CanSign: hasPrivate && !revoked,
                    CanVerify: !revoked,
                    status, revoked ? row.RevocationReason : null,
                    row.IsDefault, row.DiscoveredAt, row.LastSeenAt, row.RetiredAt,
                    row.RevokedAt, row.RevokedBy, row.RevocationReason));

                pem[row.KeyId] = new PemPair(found?.PrivatePem, row.PublicKeyPem);
                if (row.IsDefault) defaultKeyId = row.KeyId;
            }

            Interlocked.Exchange(ref snapshot, new KeyRingSnapshot(infos, defaultKeyId, pem));
        }
        catch (Exception ex)
        {
            LogReloadFailed(logger, ex);
        }
        finally
        {
            reloadGate.Release();
        }
    }

    public LicenseSigningResult Sign(JsonObject license, string? requestedKeyId)
    {
        var current = snapshot;
        string keyId;

        if (string.IsNullOrWhiteSpace(requestedKeyId))
        {
            if (current.DefaultKeyId is null)
            {
                LogNoDefaultKey(logger);
                return new LicenseSigningResult(false, null, "no_default_key", "No default signing key is configured.");
            }

            keyId = current.DefaultKeyId;
        }
        else
        {
            keyId = requestedKeyId;
        }

        var info = current.Keys.FirstOrDefault(k => k.KeyId == keyId);
        if (info is null)
            return new LicenseSigningResult(false, null, "unknown_key", $"Signing key '{keyId}' is not registered.");
        if (!info.CanSign)
            return new LicenseSigningResult(
                false, null, "cannot_sign", $"Signing key '{keyId}' cannot currently sign (status: {info.Status}).");
        if (!current.Pem.TryGetValue(keyId, out var pemPair) || pemPair.PrivatePem is null)
            return new LicenseSigningResult(false, null, "cannot_sign", $"Private key material for '{keyId}' is unavailable.");

        using var key = ECDsa.Create();
        key.ImportFromPem(pemPair.PrivatePem);

        return new LicenseSigningResult(true, LicenseEnvelope.Sign(license, keyId, key), null, null);
    }

    public VerifiedLicense Verify(string signedLicenseJson)
    {
        var current = snapshot;
        var trusted = current.Keys
            .Where(k => k.CanVerify)
            .ToDictionary(k => k.KeyId, k => current.Pem[k.KeyId].PublicPem, StringComparer.Ordinal);
        return LicenseVerifier.Verify(signedLicenseJson, trusted);
    }

    // --- Admin-facing mutations, reused by both the Blazor UI and the /api/v1/admin/signing-keys endpoints ---

    public async Task SetDefaultAsync(string keyId, string actor, CancellationToken cancellationToken = default)
    {
        var info = Find(keyId);
        if (info is null || !info.CanSign)
            throw new InvalidOperationException($"Signing key '{keyId}' does not exist or cannot currently sign.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.SigningKeys.Where(x => x.IsDefault)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDefault, false), cancellationToken);

        var target = await db.SigningKeys.SingleAsync(x => x.KeyId == keyId, cancellationToken);
        target.IsDefault = true;

        db.AuditRecords.Add(new AuditRecord
        {
            Actor = actor, Action = "signingKey.setDefault", TargetType = "signingKey", TargetId = keyId,
            Result = "success", TimestampUtc = clock.GetUtcNow()
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await ReloadAsync(cancellationToken);
    }

    public async Task RevokeAsync(string keyId, string reason, string actor, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("A revocation reason is required.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.SigningKeys.SingleOrDefaultAsync(x => x.KeyId == keyId, cancellationToken)
            ?? throw new InvalidOperationException($"Signing key '{keyId}' does not exist.");

        if (row.RevokedAt is null)
        {
            row.RevokedAt = clock.GetUtcNow();
            row.RevokedBy = actor;
            row.RevocationReason = reason;
            row.IsDefault = false; // a revoked key can never remain the default; fail closed, no auto-substitution

            db.AuditRecords.Add(new AuditRecord
            {
                Actor = actor, Action = "signingKey.revoke", TargetType = "signingKey", TargetId = keyId,
                Result = "success", ContextJson = System.Text.Json.JsonSerializer.Serialize(new { reason }),
                TimestampUtc = row.RevokedAt.Value
            });

            await db.SaveChangesAsync(cancellationToken);
        }

        await ReloadAsync(cancellationToken);
    }

    [LoggerMessage(4001, LogLevel.Warning, "Signing key '{KeyId}' was excluded from the key ring: {Error}")]
    private static partial void LogKeyExcluded(ILogger logger, string keyId, string? error);

    [LoggerMessage(4002, LogLevel.Error, "Signing key ring reload failed; keeping the previously published snapshot.")]
    private static partial void LogReloadFailed(ILogger logger, Exception exception);

    [LoggerMessage(4003, LogLevel.Error, "Cannot sign: no default signing key is configured.")]
    private static partial void LogNoDefaultKey(ILogger logger);
}
