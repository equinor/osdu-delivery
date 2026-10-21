using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Cli;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// <c>sqlflow cache prune</c> on SQL Server: the purge of a partition cache's history the Cache page offers, from the command
/// line, through the one retention pass. A dry run says what it would prune and changes nothing; the purge prunes it under the
/// account the command runs as; days that are not a whole number in range are a usage error, and a partition with no cache
/// is refused.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class CachePruneCommandTests : IDisposable
{
    private const string Scope = "dev";

    private static readonly DateTimeOffset Start = new(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);

    private readonly OsduTestDatabase _catalog = new();

    public void Dispose() => _catalog.Dispose();

    [Fact]
    public async Task A_dry_run_says_what_the_purge_prunes_and_the_purge_prunes_it_as_the_account_the_command_runs_as()
    {
        var labels = new List<string>();
        for (var day = 0; day < 4; day++)
        {
            var write = await _catalog.Caches().MergeAsync(
                Scope, "welldb-osdu-00-reference-cache",
                [new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure", [ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:m", new Dictionary<string, string> { ["Name"] = "metre-" + day })])],
                new CacheCapture(null, "tests", "seeded"), Start.AddDays(day));
            labels.Add(write.Snapshot.Version);
        }

        var (exit, output, _) = await RunAsync("prune", Scope, "--keep-days", "0", "--dry-run");
        Assert.Equal(0, exit);
        Assert.Contains("cache of partition dev: Pruning, keeping only the current version, the one it replaced and every pinned version, removes the records of 2 version(s)", output, StringComparison.Ordinal);
        Assert.Contains($"would prune  {labels[0]}", output, StringComparison.Ordinal);
        Assert.DoesNotContain(await _catalog.Caches().ListVersionsAsync(Scope), v => v.Pruned);

        (exit, output, _) = await RunAsync("prune", Scope, "--keep-days", "0");
        Assert.Equal(0, exit);
        Assert.Contains("Pruned the records of 2 version(s)", output, StringComparison.Ordinal);
        Assert.Contains($"pruned  {labels[1]}", output, StringComparison.Ordinal);
        var account = RunActors.LocalAccount();
        Assert.All(
            (await _catalog.Caches().ListVersionsAsync(Scope)).Where(v => v.Pruned),
            v => Assert.Equal(account.Length <= 200 ? account : account[..200], v.PrunedBy));

        // Left out, the days are the partition's retention: seven, which nothing left is past.
        (exit, output, _) = await RunAsync("prune", Scope, "--json");
        Assert.Equal(0, exit);
        Assert.Contains("\"retentionDays\": 7", output, StringComparison.Ordinal);
        Assert.Contains("\"pruned\": []", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Days_out_of_range_are_a_usage_error_and_a_partition_without_a_cache_is_refused()
    {
        foreach (var days in new[] { "-1", "seven", "36501", "1.5" })
        {
            var (exit, _, usage) = await RunAsync("prune", Scope, "--keep-days", days);
            Assert.Equal(1, exit);
            Assert.Contains("--keep-days is a whole number of days from 0 to 36500", usage, StringComparison.Ordinal);
        }

        // Given no value, the option is refused too, rather than falling back to the partition's retention.
        var (none, _, missing) = await RunAsync("prune", Scope, "--keep-days");
        Assert.Equal(1, none);
        Assert.Contains("and none was given", missing, StringComparison.Ordinal);
        var (negative, _, dashed) = await RunAsync("prune", Scope, "--keep-days", "-3");
        Assert.Equal(1, negative);
        Assert.Contains("not '-3'", dashed, StringComparison.Ordinal);

        var refused = await Assert.ThrowsAsync<FlowValidationException>(() => RunAsync("prune", "nowhere", "--keep-days", "3"));
        Assert.Contains("The cache of partition 'nowhere' holds no version, so it has no history to prune.", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs <c>sqlflow cache prune</c> as the command line would, with the options the module declares for the verb, over an
    /// engine on the test database; returns the exit code, what it printed, and the reason of a usage error.
    /// </summary>
    private async Task<(int Exit, string Output, string Usage)> RunAsync(string subcommand, string target, params string[] options)
    {
        var declared = new DeliveryCliModule().Verbs.Single(v => v.Name == "cache");
        await using var services = new ServiceCollection()
            .AddSingleton(Samples.Engine(ledger: null, cache: _catalog.Caches()))
            .BuildServiceProvider();
        await using var output = new StringWriter();
        var usage = string.Empty;
        var command = new ModuleCommand(
            services,
            new CliArguments(["cache", subcommand, target, .. options], declared.ValueOptions),
            output,
            TextWriter.Null,
            reason =>
            {
                usage = reason ?? string.Empty;
                return 1;
            },
            CancellationToken.None);
        var exit = await DeliveryVerbs.CachePruneAsync(command, target);
        return (exit, output.ToString(), usage);
    }
}
