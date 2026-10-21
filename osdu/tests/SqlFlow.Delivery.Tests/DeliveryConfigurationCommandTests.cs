using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Cli;
using SqlFlow.Delivery.Data;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The central configuration and the partition registry as <c>sqlflow config</c> and <c>sqlflow partition</c> keep them, on
/// SQL Server: every change recorded under the account the command runs as, in the form every command line change takes;
/// one check of what a property may hold, the store's, for the command line as for the API (a value trimmed, a description
/// bounded by its column, a repository the catalog must hold); and a listing narrowed by the repository and the partition
/// it is asked for. The verbs run over a command of the suite's own (<see cref="ModuleCommand"/>), against the suites' test
/// database (<see cref="OsduTestServer"/>), its catalog included.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DeliveryConfigurationCommandTests : IDisposable
{
    /// <summary>The catalog reference the commands are given with <c>--db</c>; the suite's resolver answers it with the test database.</summary>
    private const string CatalogReference = "${env:CONFIG_SUITE_CATALOG}";

    private static readonly DateTime Now = new(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);

    private readonly OsduTestDatabase _module = new();
    private readonly List<Guid> _repos = [];

    public void Dispose()
    {
        try
        {
            if (_repos.Count > 0)
            {
                using var catalog = _module.CreateCatalogContext();
                catalog.Repos.Where(r => _repos.Contains(r.Id)).ExecuteDelete();
            }
        }
        finally
        {
            _module.Dispose();
        }
    }

    /// <summary>What a command line change is recorded under: the account this process runs as, cut to the column's width.</summary>
    private static string Account
    {
        get
        {
            var account = RunActors.LocalAccount();
            return account.Length <= 200 ? account : account[..200];
        }
    }

    [Fact]
    public async Task Config_and_partition_changes_are_recorded_under_the_account_the_command_runs_as()
    {
        Assert.StartsWith(RunActors.CliPrefix, Account, StringComparison.Ordinal);

        Assert.Equal(0, (await ConfigAsync("set", "OSDU_URL", "--value", "https://osdu.example.com")).Exit);
        await using (var db = _module.CreateDbContext())
        {
            Assert.Equal(Account, (await db.DeliveryConfigProperties.SingleAsync(p => p.Name == "OSDU_URL")).UpdatedBy);
        }

        Assert.Equal(0, (await PartitionAsync("add", "dev")).Exit);
        Assert.Equal(0, (await PartitionAsync("add", "test", "--description", "the test platform")).Exit);
        Assert.Equal(0, (await PartitionAsync("describe", "dev", "--description", "the development platform")).Exit);
        Assert.Equal(0, (await PartitionAsync("default", "test")).Exit);
        await using (var db = _module.CreateDbContext())
        {
            var partitions = await db.DeliveryPartitions.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
            Assert.Equal(["dev", "test"], partitions.Select(p => p.Name));
            Assert.All(partitions, p => Assert.Equal((Account, Account), (p.CreatedBy, p.UpdatedBy)));
            Assert.True(partitions[1].IsDefault);
        }
    }

    [Fact]
    public async Task Config_list_narrows_to_the_repository_and_the_partition_it_is_given_and_lists_every_scope_without_either()
    {
        var repo = await RegisterRepositoryAsync();
        var store = new DeliveryConfigStore(_module.CreateDbContext);
        await using (var catalog = _module.CreateCatalogContext())
        {
            await store.SetAsync(null, null, "OSDU_URL", "https://osdu.example.com", null, "suite", Now, catalog);
            await store.SetAsync(null, "test", "OSDU_URL", "https://test.osdu.example.com", null, "suite", Now, catalog);
            await store.SetAsync(repo, null, "OSDU_LEGAL_TAG", "estate-legal", null, "suite", Now, catalog);
            await store.SetAsync(repo, "test", "OSDU_LEGAL_TAG", "estate-test-legal", null, "suite", Now, catalog);
        }

        // Every scope, by name, then repository, then partition; then one partition's values at every scope: the control
        // plane's and the repository's.
        Assert.Equal(
            ["estate-legal", "estate-test-legal", "https://osdu.example.com", "https://test.osdu.example.com"],
            Values((await ConfigAsync("list")).Output));
        Assert.Equal(["estate-test-legal", "https://test.osdu.example.com"], Values((await ConfigAsync("list", "--partition", "test")).Output));

        // One repository's values in every partition, and in one partition.
        Assert.Equal(["estate-legal", "estate-test-legal"], Values((await ConfigAsync("list", "--repo", repo.ToString("D"))).Output));
        Assert.Equal(["estate-test-legal"], Values((await ConfigAsync("list", "--repo", repo.ToString("D"), "--partition", "test")).Output));

        // A filter that matches nothing says what it was narrowed to.
        Assert.Equal("no configuration property is set in partition prod", (await ConfigAsync("list", "--partition", "prod")).Output.Trim());

        // A partition written as a reference names none.
        var reference = await Assert.ThrowsAsync<FlowValidationException>(() => ConfigAsync("list", "--partition", "${env:PART}"));
        Assert.Contains("is not a data-partition-id", reference.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_property_set_for_a_repository_needs_a_repository_the_catalog_holds()
    {
        var unknown = Guid.NewGuid();
        var refused = await Assert.ThrowsAsync<RepositoryNotRegisteredException>(
            () => ConfigAsync("set", "OSDU_URL", "--value", "https://osdu.example.com", "--repo", unknown.ToString("D"), "--db", CatalogReference));
        Assert.Contains($"No repository {unknown:D} is registered", refused.Message, StringComparison.Ordinal);
        await using (var db = _module.CreateDbContext())
        {
            Assert.False(await db.DeliveryConfigProperties.AnyAsync());
        }

        var repo = await RegisterRepositoryAsync();
        Assert.Equal(0, (await ConfigAsync("set", "OSDU_URL", "--value", "https://osdu.example.com", "--repo", $" {repo:D} ", "--db", CatalogReference)).Exit);
        await using (var db = _module.CreateDbContext())
        {
            Assert.Equal(repo, (await db.DeliveryConfigProperties.SingleAsync()).RepoId);
        }

        // Without a catalog to check it in, the command says which connection it could not resolve and how to give one.
        var unresolved = await Assert.ThrowsAsync<FlowValidationException>(
            () => ConfigAsync("set", "OSDU_URL", "--value", "https://osdu.example.com", "--repo", repo.ToString("D"), "--db", "${env:CONFIG_SUITE_NOWHERE}"));
        Assert.Contains("${env:CONFIG_SUITE_NOWHERE} did not resolve", unresolved.Message, StringComparison.Ordinal);
        Assert.Contains("SQLFLOW_CATALOG_DB", unresolved.Message, StringComparison.Ordinal);

        // A property of a repository no longer registered can still be found and removed.
        await using (var catalog = _module.CreateCatalogContext())
        {
            await catalog.Repos.Where(r => r.Id == repo).ExecuteDeleteAsync();
        }

        Assert.Equal(["https://osdu.example.com"], Values((await ConfigAsync("list", "--repo", repo.ToString("D"))).Output));
        Assert.Equal("OSDU_URL removed", (await ConfigAsync("remove", "OSDU_URL", "--repo", repo.ToString("D"))).Output.Trim());
    }

    [Fact]
    public async Task The_value_is_trimmed_and_a_value_or_description_the_column_cannot_hold_is_refused_before_the_save()
    {
        Assert.Equal(0, (await ConfigAsync("set", "OSDU_URL", "--value", "  https://osdu.example.com  ", "--description", "  the platform  ")).Exit);
        await using (var db = _module.CreateDbContext())
        {
            var row = await db.DeliveryConfigProperties.SingleAsync();
            Assert.Equal(("https://osdu.example.com", "the platform"), (row.Value, row.Description));
        }

        var blank = await Assert.ThrowsAsync<FlowValidationException>(() => ConfigAsync("set", "OSDU_URL", "--value", "   "));
        Assert.Contains("'OSDU_URL' needs a value; remove the property instead", blank.Message, StringComparison.Ordinal);

        var description = new string('d', DeliveryConfigNames.MaxDescriptionLength + 1);
        var long1 = await Assert.ThrowsAsync<FlowValidationException>(() => ConfigAsync("set", "OSDU_URL", "--value", "https://other.example.com", "--description", description));
        Assert.Contains($"the description of 'OSDU_URL' is {DeliveryConfigNames.MaxDescriptionLength + 1} characters", long1.Message, StringComparison.Ordinal);
        Assert.Contains($"at most {DeliveryConfigNames.MaxDescriptionLength}", long1.Message, StringComparison.Ordinal);

        var value = new string('v', DeliveryConfigNames.MaxValueLength + 1);
        var long2 = await Assert.ThrowsAsync<FlowValidationException>(() => ConfigAsync("set", "OSDU_URL", "--value", value));
        Assert.Contains($"longer than {DeliveryConfigNames.MaxValueLength} characters ({DeliveryConfigNames.MaxValueLength + 1})", long2.Message, StringComparison.Ordinal);

        // Nothing refused reached the row.
        await using (var db = _module.CreateDbContext())
        {
            var row = await db.DeliveryConfigProperties.SingleAsync();
            Assert.Equal(("https://osdu.example.com", "the platform"), (row.Value, row.Description));
        }
    }

    [Fact]
    public async Task The_store_clips_an_actor_to_its_column_refuses_a_repository_it_cannot_check_and_reads_a_partition_written_literally()
    {
        var store = new DeliveryConfigStore(_module.CreateDbContext);
        var row = await store.SetAsync(null, null, "OSDU_URL", "https://osdu.example.com", null, "user:" + new string('a', 300), Now, null);
        Assert.Equal(200, row.UpdatedBy.Length);

        var unchecked1 = await Assert.ThrowsAsync<FlowValidationException>(() => store.SetAsync(Guid.NewGuid(), null, "OSDU_URL", "x", null, "suite", Now, null));
        Assert.Contains("has no catalog connection to check it in", unchecked1.Message, StringComparison.Ordinal);

        var effective = await Assert.ThrowsAsync<FlowValidationException>(() => store.EffectiveAsync(Guid.NewGuid(), "${env:PART}"));
        Assert.Contains("is not a data-partition-id", effective.Message, StringComparison.Ordinal);
        Assert.Equal("https://osdu.example.com", (await store.EffectiveAsync(Guid.NewGuid(), " test "))["OSDU_URL"]);
    }

    /// <summary>The values a text listing prints, in its order.</summary>
    private static List<string> Values(string listing)
        => listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(" = ", 2)[1].Split("  [", 2)[0])
            .ToList();

    private async Task<Guid> RegisterRepositoryAsync()
    {
        var id = Guid.NewGuid();
        await using var catalog = _module.CreateCatalogContext();
        catalog.Repos.Add(new CatalogRepo { Id = id, Name = "config-suite-" + id.ToString("N")[..12], FirstSeenUtc = Now, LastSyncUtc = Now });
        await catalog.SaveChangesAsync();
        _repos.Add(id);
        return id;
    }

    private Task<(int Exit, string Output)> ConfigAsync(params string[] args) => RunAsync("config", DeliveryConfigVerbs.RunAsync, args);

    private Task<(int Exit, string Output)> PartitionAsync(params string[] args) => RunAsync("partition", DeliveryPartitionVerbs.RunAsync, args);

    /// <summary>
    /// Runs <paramref name="verb"/> as the command line would, with the options the module declares for it, over the module's
    /// database and catalog; a usage error fails the test with its reason.
    /// </summary>
    private async Task<(int Exit, string Output)> RunAsync(string verb, Func<ModuleCommand, Task<int>> handler, string[] args)
    {
        var declared = new DeliveryCliModule().Verbs.Single(v => v.Name == verb);
        await using var services = new ServiceCollection()
            .AddSingleton(new DeliveryConfigStore(_module.CreateDbContext))
            .AddSingleton(new DeliveryPartitionRegistry(_module.CreateDbContext))
            .AddSingleton<ISecretResolver>(new OneReference(CatalogReference, _module.ConnectionString))
            .BuildServiceProvider();
        await using var output = new StringWriter();
        var command = new ModuleCommand(
            services,
            new CliArguments([verb, .. args], declared.ValueOptions),
            output,
            TextWriter.Null,
            reason => throw new InvalidOperationException($"'{verb} {string.Join(' ', args)}' was refused as a usage error: {reason}"),
            CancellationToken.None);
        var exit = await handler(command);
        return (exit, output.ToString());
    }

    /// <summary>A resolver that knows one reference, the way a node's environment knows its catalog variable, and no other.</summary>
    private sealed class OneReference(string reference, string value) : ISecretResolver
    {
        public string Resolve(string text)
            => string.Equals(text, reference, StringComparison.Ordinal)
                ? value
                : throw new SqlFlowException($"Environment variable '{text}' is not set.");

        public Task<string> ResolveAsync(string text, CancellationToken ct = default) => Task.FromResult(Resolve(text));
    }
}
