using Microsoft.AspNetCore.Identity;

namespace LicenseServer.Data;

public sealed class ApplicationUser : IdentityUser
{
    public bool MustChangePassword { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
}
