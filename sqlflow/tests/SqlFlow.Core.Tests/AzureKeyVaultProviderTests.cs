using Azure;
using Azure.Identity;
using SqlFlow.Azure;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using Xunit;

namespace SqlFlow.Tests;

public sealed class AzureKeyVaultProviderTests
{
    [Fact]
    public void ParseLocator_SplitsVaultAndSecret()
    {
        var (vault, secret) = AzureKeyVaultSecretProvider.ParseLocator("myvault/mysecret");
        Assert.Equal("myvault", vault);
        Assert.Equal("mysecret", secret);
    }

    [Fact]
    public void ParseLocator_KeepsNestedSecretPath()
    {
        // Only the first '/' separates vault from the secret name; the rest is the secret part.
        var (vault, secret) = AzureKeyVaultSecretProvider.ParseLocator("v/a/b");
        Assert.Equal("v", vault);
        Assert.Equal("a/b", secret);
    }

    [Theory]
    [InlineData("novault")]      // no separator
    [InlineData("/secret")]      // empty vault
    [InlineData("vault/")]       // empty secret
    public void ParseLocator_BadFormat_Throws(string locator)
        => Assert.Throws<SqlFlowException>(() => AzureKeyVaultSecretProvider.ParseLocator(locator));

    [Fact]
    public void Provider_Scheme_IsKeyvault()
    {
        var provider = new AzureKeyVaultSecretProvider(new AzureCredentialFactory());
        Assert.Equal("keyvault", provider.Scheme);
    }

    public static TheoryData<Exception, string> VaultFailures => new()
    {
        { new RequestFailedException(403, "The user does not have secrets get permission.", "Forbidden", null), "Key Vault 'ops' refused it (HTTP 403, Forbidden) when reading secret 'api-client'." },
        { new RequestFailedException(0, "No such host is known."), "Key Vault 'ops' could not be reached when reading secret 'api-client'." },
        { new AuthenticationFailedException("ManagedIdentityCredential authentication unavailable."), "No Azure credential could sign in to read secret 'api-client' from Key Vault 'ops'." },
    };

    [Theory]
    [MemberData(nameof(VaultFailures))]
    public async Task A_vault_that_fails_a_read_fails_it_as_every_provider_does_naming_the_vault_and_the_secret(Exception failure, string message)
    {
        var provider = new AzureKeyVaultSecretProvider(_ => new FailingVault(failure));

        var thrown = await Assert.ThrowsAsync<SqlFlowException>(() => provider.ResolveAsync("ops/api-client"));
        Assert.Equal(message, thrown.Message);
        Assert.Same(failure, thrown.InnerException);
        Assert.Equal(message, Assert.Throws<SqlFlowException>(() => provider.Resolve("ops/api-client")).Message);
    }

    [Fact]
    public async Task A_secret_the_vault_does_not_hold_is_not_found_and_a_held_one_is_read()
    {
        var provider = new AzureKeyVaultSecretProvider(vault => new HeldVault(vault == "ops" ? new Dictionary<string, string> { ["api-client"] = "value-1" } : []));

        Assert.Equal("value-1", await provider.ResolveAsync("ops/api-client"));
        var missing = await Assert.ThrowsAsync<SqlFlowException>(() => provider.ResolveAsync("ops/other"));
        Assert.Equal("Key Vault secret 'other' was not found in vault 'ops'.", missing.Message);
    }

    /// <summary>A vault whose every read fails with <c>failure</c>, as Azure's client fails.</summary>
    private sealed class FailingVault(Exception failure) : ISecretVault
    {
        public Task<string?> GetSecretAsync(string name, CancellationToken ct = default) => Task.FromException<string?>(failure);

        public Task<string> SetSecretAsync(string name, string value, CancellationToken ct = default) => Task.FromException<string>(failure);

        public Task<bool> DeleteSecretAsync(string name, CancellationToken ct = default) => Task.FromException<bool>(failure);
    }

    /// <summary>A vault holding <c>secrets</c>, answering null for a name it does not hold.</summary>
    private sealed class HeldVault(IReadOnlyDictionary<string, string> secrets) : ISecretVault
    {
        public Task<string?> GetSecretAsync(string name, CancellationToken ct = default) => Task.FromResult(secrets.TryGetValue(name, out var value) ? value : null);

        public Task<string> SetSecretAsync(string name, string value, CancellationToken ct = default) => throw new NotSupportedException("The test vault is read-only.");

        public Task<bool> DeleteSecretAsync(string name, CancellationToken ct = default) => throw new NotSupportedException("The test vault is read-only.");
    }
}
