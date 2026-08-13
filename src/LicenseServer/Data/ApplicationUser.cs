using Microsoft.AspNetCore.Identity;

namespace LicenseServer.Data;

public sealed class ApplicationUser : IdentityUser
{
    public const string HumanAccountType = "human";
    public const string ServiceAccountType = "service";

    public string AccountType { get; set; } = HumanAccountType;
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset? DisabledAt { get; set; }
    public string? DisabledBy { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
}
