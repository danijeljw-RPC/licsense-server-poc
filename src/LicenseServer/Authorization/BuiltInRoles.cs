namespace LicenseServer.Authorization;

internal static class BuiltInRoles
{
    public const string LegacyAdministrator = "Administrator";
    public const string SystemAdministrator = "System Administrator";
    public const string LicenseManager = "License Manager";
    public const string LicenseIssuer = "License Issuer";
    public const string SupportAgent = "Support Agent";
    public const string ProductAdministrator = "Product Administrator";
    public const string Auditor = "Auditor";
    public const string BillingAutomation = "Billing Automation";

    public static readonly IReadOnlyList<string> All =
    [
        SystemAdministrator, LicenseManager, LicenseIssuer, SupportAgent,
        ProductAdministrator, Auditor, BillingAutomation
    ];

    private static readonly Dictionary<string, IReadOnlyList<string>> Matrix =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [SystemAdministrator] = Permissions.All,
            [LicenseManager] =
            [
                Permissions.LicensesRead, Permissions.LicensesIssue, Permissions.LicensesUpdate,
                Permissions.LicensesCancel, Permissions.LicensesRevoke, Permissions.LicensesImport,
                Permissions.ActivationsManage,
                Permissions.CustomersRead, Permissions.CustomersManage, Permissions.ProductsRead,
                Permissions.ApiKeysManageSelf, Permissions.AuditRead,
                Permissions.DeploymentKeysRead, Permissions.DeploymentKeysManage
            ],
            [LicenseIssuer] =
            [
                Permissions.LicensesRead, Permissions.LicensesIssue, Permissions.CustomersRead,
                Permissions.ProductsRead, Permissions.ApiKeysManageSelf
            ],
            [SupportAgent] =
            [
                Permissions.LicensesRead, Permissions.ActivationsManage, Permissions.CustomersRead,
                Permissions.ProductsRead, Permissions.ApiKeysManageSelf, Permissions.DeploymentKeysRead
            ],
            [ProductAdministrator] =
            [Permissions.ProductsRead, Permissions.ProductsManage, Permissions.ApiKeysManageSelf],
            [Auditor] =
            [
                Permissions.LicensesRead, Permissions.CustomersRead, Permissions.ProductsRead,
                Permissions.UsersRead, Permissions.ApiKeysManageSelf, Permissions.AuditRead,
                Permissions.DeploymentKeysRead
            ],
            [BillingAutomation] =
            [
                Permissions.LicensesRead, Permissions.LicensesIssue, Permissions.LicensesUpdate,
                Permissions.CustomersRead, Permissions.CustomersManage, Permissions.ProductsRead,
                Permissions.BillingManage
            ]
        };

    public static IReadOnlyList<string> PermissionsFor(string role) =>
        Matrix.TryGetValue(role, out var permissions) ? permissions : [];
}
