namespace LicenseServer.Tests;

public sealed class CustomerEmailTests
{
    [Theory]
    [InlineData("  Customer.Email+Tag@Example.COM  ", "customer.email+tag@example.com")]
    [InlineData("person@sub.example.org", "person@sub.example.org")]
    public void TryNormalizeReturnsOneTrimmedLowerCaseAddress(string input, string expected)
    {
        Assert.True(CustomerEmails.TryNormalize(input, out var normalized, out var error));
        Assert.Equal(expected, normalized);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("Display Name <person@example.com>")]
    [InlineData("two@@example.com")]
    [InlineData("person @example.com")]
    public void TryNormalizeRejectsMissingOrNonAddressInput(string? input)
    {
        Assert.False(CustomerEmails.TryNormalize(input, out var normalized, out var error));
        Assert.Equal(string.Empty, normalized);
        Assert.False(string.IsNullOrEmpty(error));
    }
}
