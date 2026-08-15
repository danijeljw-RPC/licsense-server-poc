using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LicenseServer.Authorization;
using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using SoftwareLicensing;

namespace LicenseServer;

/// <summary>
/// Implements the "License import" pipeline from
/// docs/superpowers/specs/2026-08-14-key-ring-signing-design.md: verifies an offline-signed
/// license artifact against the server's own live key ring (the same verifier the server's
/// online paths use - not a second one for imports), then stores the artifact byte-for-byte
/// alongside a relational search/listing index built from it. The artifact itself, not the
/// index, remains the source of truth for an imported record - see the "Resolved design
/// questions" subsection for why each column here exists.
/// </summary>
internal sealed partial class LicenseImportService(
    ApplicationDbContext db,
    ILicenseVerifier verifier,
    TimeProvider clock,
    PermissionGuard permissions)
{
    public const int MaxUploadBytes = 262_144; // 256 KB, per the design's upload limit.

    public async Task<StoreResult<ImportedLicense>> ImportAsync(
        byte[] rawEnvelope,
        string? fileName,
        string? contactEmail,
        string actor,
        CancellationToken cancellationToken = default)
    {
        await permissions.RequireAsync(Permissions.LicensesImport);

        if (rawEnvelope.Length == 0)
            return StoreResult<ImportedLicense>.BadRequest("An uploaded license file is required.", "file");
        if (rawEnvelope.Length > MaxUploadBytes)
            return StoreResult<ImportedLicense>.BadRequest("The uploaded license file exceeds the 256 KB limit.", "file");
        if (!CustomerEmails.TryNormalize(contactEmail, out var normalizedContactEmail, out var contactEmailError))
            return StoreResult<ImportedLicense>.BadRequest(contactEmailError!, "contactEmail");

        string envelopeText;
        try
        {
            // Strict decoding: a lossy GetString (the default) silently replaces invalid bytes
            // with U+FFFD instead of failing, which would let us "successfully" verify and store
            // a signature over text that is not actually what was uploaded.
            envelopeText = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(rawEnvelope);
        }
        catch (DecoderFallbackException)
        {
            return StoreResult<ImportedLicense>.BadRequest("The uploaded file is not valid UTF-8 text.", "file");
        }

        VerifiedLicense verified;
        try
        {
            // Steps 1-5 of the design's pipeline (parse, envelope/format/algorithm shape, keyId
            // lookup against the live key ring rejecting unknown/revoked, signature verification,
            // LicenseSchema.Parse) all happen inside this one call - it is the exact verifier the
            // server's own online signing/verification paths use.
            verified = verifier.Verify(envelopeText);
        }
        catch (LicenseValidationException exception)
        {
            return StoreResult<ImportedLicense>.BadRequest(exception.Message, "file");
        }

        // ArtifactHasContactEmail is true whenever the key is present at all, even if its value
        // isn't a string - LicenseSchema.Parse allows any metadata value to be a string, number,
        // or boolean, so a non-string contactEmail must still be cross-checked (and rejected,
        // since it can never normalize-equal an email) rather than silently treated the same as
        // the key being absent entirely.
        if (ArtifactHasContactEmail(verified.Data.Metadata, out var artifactContactEmail))
        {
            if (!CustomerEmails.TryNormalize(artifactContactEmail, out var normalizedArtifactEmail, out _)
                || !string.Equals(normalizedArtifactEmail, normalizedContactEmail, StringComparison.Ordinal))
            {
                return StoreResult<ImportedLicense>.BadRequest(
                    "The supplied contactEmail does not match the license artifact's own metadata.contactEmail.",
                    "contactEmail");
            }
        }

        if (verified.Data.LicenseId.Length > 19)
        {
            return StoreResult<ImportedLicense>.BadRequest(
                "The license artifact's licenseId is longer than 19 characters, which this server's schema cannot store.",
                "file");
        }
        if (verified.Data.Customer.Length > 200)
        {
            return StoreResult<ImportedLicense>.BadRequest(
                "The license artifact's customer name is longer than 200 characters, which this server's schema cannot store.",
                "file");
        }

        var products = new List<(ProductEntitlement Entitlement, string Code)>();
        foreach (var entitlement in verified.Data.Entitlements)
        {
            // A signed artifact can legitimately name any non-blank product string
            // (LicenseGenerator performs no catalog lookup), but this server's catalog rejects
            // codes outside CK_ProductDefinitions_Code. Validate every product up front and
            // reject the whole upload rather than silently mangling a signed product name into a
            // conforming one, or letting a raw check-constraint violation reach the caller as a 500.
            if (!ProductCodePattern().IsMatch(entitlement.Product))
            {
                return StoreResult<ImportedLicense>.BadRequest(
                    $"Product '{entitlement.Product}' cannot be used as a catalog code; codes must match " +
                    "^[a-z0-9][a-z0-9-]{0,99}$.", "file");
            }
            // Likewise, edition/licenseType pass LicenseSchema.Parse (which accepts any string) but
            // are enforced by CK_Entitlements_Edition/CK_Entitlements_LicenseType at the DB level.
            if (!LicenseEditions.IsSupported(entitlement.Edition))
            {
                return StoreResult<ImportedLicense>.BadRequest(
                    $"Product '{entitlement.Product}' has an edition ('{entitlement.Edition}') this server does not support.",
                    "file");
            }
            if (!LicenseTerms.IsSupportedType(entitlement.LicenseType))
            {
                return StoreResult<ImportedLicense>.BadRequest(
                    $"Product '{entitlement.Product}' has a license type ('{entitlement.LicenseType}') this server does not support.",
                    "file");
            }
            products.Add((entitlement, entitlement.Product));
        }

        var sha256 = SHA256.HashData(rawEnvelope);
        if (await db.Licenses.AnyAsync(x => x.LicenseId == verified.Data.LicenseId, cancellationToken))
            return StoreResult<ImportedLicense>.Conflict($"A license with ID '{verified.Data.LicenseId}' already exists.");
        if (await db.Licenses.AnyAsync(x => x.ImportedSignedEnvelopeSha256 == sha256, cancellationToken))
            return StoreResult<ImportedLicense>.Conflict("This exact license artifact has already been imported.");

        var now = clock.GetUtcNow();
        var autoCreatedProducts = new List<ProductDefinition>();
        var entitlements = new List<Entitlement>();
        foreach (var (entitlement, code) in products)
        {
            var product = await db.ProductDefinitions.SingleOrDefaultAsync(x => x.Code == code, cancellationToken);
            if (product is null)
            {
                // IsActive = false is load-bearing: LicenseStore only issues against active
                // products and the active-product listing excludes it, so an import can never
                // quietly add a sellable catalog entry - an operator must review and activate it.
                product = new ProductDefinition
                {
                    Id = Guid.NewGuid(),
                    Code = code,
                    DisplayName = code,
                    Description = "Auto-created by license import; review and activate before selling against this product.",
                    IsActive = false,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.ProductDefinitions.Add(product);
                autoCreatedProducts.Add(product);
            }

            entitlements.Add(new Entitlement
            {
                Id = Guid.NewGuid(),
                ProductDefinitionId = product.Id,
                ProductDefinition = product,
                Product = code,
                Edition = entitlement.Edition,
                LicenseType = entitlement.LicenseType,
                Seats = entitlement.Seats,
                UpdatesUntil = entitlement.UpdatesUntil,
                License = null!,
                // Preserves this product's own expiresAt (and any other artifact fields) as a
                // search/listing index only - not the signing source of truth for this record.
                MetadataJson = entitlement.Json.ToJsonString()
            });
        }

        var license = new LicenseRecord
        {
            Id = Guid.NewGuid(),
            LicenseId = verified.Data.LicenseId,
            Customer = new Customer
            {
                Id = Guid.NewGuid(),
                Name = verified.Data.Customer,
                Email = normalizedContactEmail,
                NormalizedEmail = normalizedContactEmail,
                CreatedAt = now
            },
            // Imports arrive with no operator-facing activation code, so there is no legitimate
            // value for this required column. A random, discarded-preimage hash - the same
            // reasoning as the imported Activation's TokenHash below - means the normal
            // activate-a-new-device flow fails closed by construction rather than through a
            // nullable column or sentinel every check would need to special-case.
            ActivationCodeHash = RandomNumberGenerator.GetBytes(32),
            ActivationCodeHashVersion = ActivationCodeHasher.CurrentVersion,
            // The server's searchable index of the license, not a copy of the signed artifact -
            // deliberately just the resolved contact email, so this column can never be mistaken
            // for (or drift from) the signed content, which is stored separately and verbatim.
            MetadataJson = new JsonObject { ["contactEmail"] = normalizedContactEmail }.ToJsonString(),
            IssuedAt = verified.Data.IssuedAt,
            // Index-only approximation, not authoritative: a multi-product artifact can have
            // entitlements that expire independently, which this single column cannot represent.
            // Null (never expires, for search/listing) if any entitlement has no expiry of its
            // own; otherwise the latest per-product expiry, so the license doesn't read as
            // "expired" while any product's coverage is still live. Anything needing precision
            // reads the stored verbatim bytes or each Entitlement's own MetadataJson.
            ExpiresAt = products.Any(p => p.Entitlement.ExpiresAt is null)
                ? null
                : products.Max(p => p.Entitlement.ExpiresAt),
            Provenance = LicenseProvenance.Imported,
            ImportedSignedEnvelope = rawEnvelope,
            ImportedSignedEnvelopeSha256 = sha256,
            ImportedAt = now,
            ImportedBy = actor,
            Entitlements = entitlements
        };

        if (verified.Data.DeviceBinding is { } binding && verified.Data.Activation is { } activation)
        {
            license.Activations.Add(new Activation
            {
                Id = Guid.NewGuid(),
                ActivationId = activation.ActivationId,
                License = license,
                RequestId = Guid.NewGuid(),
                DeviceIdHash = binding.DeviceId,
                DeviceIdSuffix = binding.DeviceId[^8..],
                DeviceName = binding.DeviceName,
                Mode = activation.Mode,
                // No server-issued token exists for this pre-existing activation to hash (the
                // offline artifact never carried one). A random, discarded-preimage hash means
                // refresh AND deactivation both fail closed automatically for every mode, not
                // through a branch someone could forget - admin-side license revoke remains the
                // supported lifecycle action for this activation.
                TokenHash = RandomNumberGenerator.GetBytes(32),
                ActivatedAt = activation.ActivatedAt,
                RefreshAfter = activation.RefreshAfter,
                LeaseExpiresAt = activation.LeaseExpiresAt
            });
        }

        db.Licenses.Add(license);

        var fileNameSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fileName ?? string.Empty)));
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = actor,
            Action = "license.imported",
            TargetType = "license",
            TargetId = license.LicenseId,
            Result = "success",
            ContextJson = JsonSerializer.Serialize(new
            {
                fileNameSha256,
                keyId = verified.KeyId,
                products = products.Select(p => p.Code).ToArray(),
                autoCreatedProducts = autoCreatedProducts.Select(p => p.Code).ToArray()
            }),
            TimestampUtc = now
        });
        foreach (var product in autoCreatedProducts)
        {
            db.AuditRecords.Add(new AuditRecord
            {
                Actor = actor,
                Action = "product.auto-created",
                TargetType = "product",
                TargetId = product.Id.ToString("D"),
                Result = "success",
                ContextJson = JsonSerializer.Serialize(new { product.Code, reason = "license.import", licenseId = license.LicenseId }),
                TimestampUtc = now
            });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return StoreResult<ImportedLicense>.Conflict("The license could not be imported concurrently. Retry the request.");
        }

        return StoreResult<ImportedLicense>.Ok(new ImportedLicense(
            license.LicenseId,
            products.Select(p => p.Code).ToArray(),
            autoCreatedProducts.Select(p => p.Code).ToArray()));
    }

    private static bool ArtifactHasContactEmail(JsonObject? metadata, out string? contactEmail)
    {
        contactEmail = null;
        if (metadata is null || !metadata.TryGetPropertyValue("contactEmail", out var node))
            return false;

        // The key is present either way; only extract a string value into contactEmail when
        // there is one. A present-but-non-string value leaves contactEmail null, which
        // CustomerEmails.TryNormalize(null, ...) rejects, so the caller's mismatch path fires
        // instead of the check being skipped as if the key were absent.
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            contactEmail = text;
        return true;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProductCodePattern();
}

internal sealed record ImportedLicense(
    string LicenseId, IReadOnlyList<string> Products, IReadOnlyList<string> AutoCreatedProducts);
