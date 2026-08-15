namespace LicenseServer.Authorization;

internal static class Permissions
{
    public const string ClaimType = "permission";
    public const string LicensesRead = "licenses.read";
    public const string LicensesIssue = "licenses.issue";
    public const string LicensesUpdate = "licenses.update";
    public const string LicensesCancel = "licenses.cancel";
    public const string LicensesRevoke = "licenses.revoke";
    public const string LicensesImport = "licenses.import";
    public const string ActivationsManage = "activations.manage";
    public const string CustomersRead = "customers.read";
    public const string CustomersManage = "customers.manage";
    public const string ProductsRead = "products.read";
    public const string ProductsManage = "products.manage";
    public const string UsersRead = "users.read";
    public const string UsersManage = "users.manage";
    public const string ApiKeysManageSelf = "apiKeys.manageSelf";
    public const string ApiKeysManageAll = "apiKeys.manageAll";
    public const string AuditRead = "audit.read";
    public const string BillingManage = "billing.manage";
    public const string SigningKeysManage = "signingKeys.manage";

    public static readonly IReadOnlyList<string> All =
    [
        LicensesRead, LicensesIssue, LicensesUpdate, LicensesCancel, LicensesRevoke, LicensesImport,
        ActivationsManage, CustomersRead, CustomersManage, ProductsRead, ProductsManage,
        UsersRead, UsersManage, ApiKeysManageSelf, ApiKeysManageAll, AuditRead, BillingManage,
        SigningKeysManage
    ];

    public static readonly IReadOnlySet<string> HighRisk = new HashSet<string>(StringComparer.Ordinal)
    {
        UsersManage, ApiKeysManageAll, LicensesRevoke, SigningKeysManage
    };
}
