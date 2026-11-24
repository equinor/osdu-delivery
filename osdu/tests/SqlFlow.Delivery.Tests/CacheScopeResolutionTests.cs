using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The cache scope a flow's partition names as a run reaches it (<see cref="CacheScope.ResolvedAsync"/>), the one rule the
/// catalog sync and the mapping builder's cache picker share: a reference this host resolves is resolved and normalized; one
/// it cannot resolve stays as written; and a failure that is not a reference left unresolved, a defect, is not hidden.
/// </summary>
public sealed class CacheScopeResolutionTests
{
    private static readonly SecretResolver Secrets = new([new FixedProvider("vault", "dev"), new BrokenProvider()]);

    [Fact]
    public async Task A_reference_this_host_resolves_names_the_scope_it_resolves_to()
        => Assert.Equal("dev", await CacheScope.ResolvedAsync("${vault:partition}", Secrets, "flow.yaml", CancellationToken.None));

    [Theory]
    [InlineData("${env:SQLFLOW_TEST_PARTITION_THAT_IS_NEVER_SET}")]
    [InlineData("${unknown:partition}")]
    public async Task A_reference_this_host_cannot_resolve_names_the_scope_as_it_is_written(string declared)
        => Assert.Equal(declared, await CacheScope.ResolvedAsync(declared, new SecretResolver([new EnvSecretProvider()]), "flow.yaml", CancellationToken.None));

    [Fact]
    public async Task A_defect_while_resolving_is_not_taken_for_a_reference_left_unresolved()
    {
        var defect = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CacheScope.ResolvedAsync("${broken:partition}", Secrets, "flow.yaml", CancellationToken.None));
        Assert.Equal("a defect in the provider", defect.Message);
    }

    /// <summary>A provider answering every locator with one value.</summary>
    private sealed class FixedProvider(string scheme, string value) : ISecretProvider
    {
        public string Scheme => scheme;

        public string Resolve(string locator) => value;
    }

    /// <summary>A provider with a defect: it fails as no provider should, with something other than a SqlFlowException.</summary>
    private sealed class BrokenProvider : ISecretProvider
    {
        public string Scheme => "broken";

        public string Resolve(string locator) => throw new InvalidOperationException("a defect in the provider");
    }
}
