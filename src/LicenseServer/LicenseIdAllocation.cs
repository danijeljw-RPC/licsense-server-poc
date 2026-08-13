using System.Data;
using System.Globalization;
using LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace LicenseServer;

public sealed class LicensingOptions
{
    public string IdTimeZone { get; set; } = "Australia/Adelaide";
}

public interface ILicenseBusinessDateResolver
{
    DateOnly Resolve(DateTimeOffset utcInstant);
}

internal sealed class ConfiguredLicenseBusinessDateResolver : ILicenseBusinessDateResolver
{
    private readonly TimeZoneInfo timeZone;

    public ConfiguredLicenseBusinessDateResolver(IOptions<LicensingOptions> options)
    {
        var id = string.IsNullOrWhiteSpace(options.Value.IdTimeZone)
            ? "Australia/Adelaide"
            : options.Value.IdTimeZone.Trim();
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new InvalidOperationException($"Licensing:IdTimeZone '{id}' was not found on this host.", exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new InvalidOperationException($"Licensing:IdTimeZone '{id}' is invalid on this host.", exception);
        }
    }

    public DateOnly Resolve(DateTimeOffset utcInstant)
    {
        var local = TimeZoneInfo.ConvertTime(utcInstant.ToUniversalTime(), timeZone);
        return DateOnly.FromDateTime(local.DateTime);
    }
}

public sealed class LicenseIdAllocator(
    ApplicationDbContext db,
    ILicenseBusinessDateResolver businessDates)
{
    internal const int MaximumDailyValue = 0xFFFFFF;

    public async Task<string> AllocateAsync(
        DateTimeOffset utcInstant,
        CancellationToken cancellationToken = default)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("License IDs must be allocated inside the license creation transaction.");
        var businessDate = businessDates.Resolve(utcInstant);
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = """
            INSERT INTO "LicenseIdCounters" ("BusinessDate", "LastValue")
            VALUES (@businessDate, 1)
            ON CONFLICT ("BusinessDate") DO UPDATE
            SET "LastValue" = "LicenseIdCounters"."LastValue" + 1
            WHERE "LicenseIdCounters"."LastValue" < 16777215
            RETURNING "LastValue";
            """;
        var dateParameter = command.CreateParameter();
        dateParameter.ParameterName = "businessDate";
        dateParameter.DbType = DbType.Date;
        dateParameter.Value = businessDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add(dateParameter);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null or DBNull)
        {
            throw new LicenseIdExhaustedException(
                $"The daily license ID range for {businessDate:yyyy-MM-dd} is exhausted at 0xFFFFFF.");
        }

        var value = Convert.ToInt32(result, CultureInfo.InvariantCulture);
        return FormattableString.Invariant($"LIC-{businessDate:yyyy}-{businessDate:MMdd}{value:X6}");
    }
}

internal sealed class LicenseIdExhaustedException(string message) : InvalidOperationException(message);
