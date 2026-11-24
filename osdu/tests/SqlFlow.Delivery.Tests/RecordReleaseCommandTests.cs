using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Cli.Hosting;
using SqlFlow.Delivery.Cli;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// <c>sqlflow records release</c> on SQL Server (osdu/docs/reference/cli/records.md, records release): it releases the
/// records <c>--key</c> names, the ones <c>--issue</c> keeps blocked, or with <c>--all</c> every blocked record, and exactly
/// one of the three is given. A command that names none releases nothing, as the API and the MCP server refuse a release that
/// names none, so a mistyped command never releases a whole flow.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class RecordReleaseCommandTests : IAsyncLifetime, IDisposable
{
    private const string Label = "release-flow";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();
    private readonly FlowDefinition _flow;
    private readonly IReadOnlyList<RecordState> _held;

    public RecordReleaseCommandTests()
    {
        _flow = Samples.LocalFlow(_root);
        _held = new[] { "W-1", "W-2", "W-3" }.Select(Held).ToList();
    }

    private OsduLedger Ledger => _db.Ledger(_clock);

    public async Task InitializeAsync()
    {
        await Ledger.RegisterAsync(_flow.Id);
        await Ledger.MarkHeldAsync(_flow.Id, _held);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file a failed assertion left open is removed with the temporary folder by the operating system.
        }
    }

    private RecordState Held(string sourceKey) => new()
    {
        DeliveryKey = DeliveryKey.Derive("release", [sourceKey]),
        FlowId = _flow.Id,
        SourceKey = sourceKey,
        MappingName = "Thing",
        LastSubmissionId = Guid.NewGuid(),
        PendingSourceFileName = "wells.csv",
        PendingSourceRowNumber = 1,
        LastError = "osdu.tags.Tag4: dataset.tag4 is empty, and the entry is required",
    };

    [Fact]
    public async Task A_release_that_names_no_records_releases_none_and_says_how_to_name_them()
    {
        var (exit, output, usage) = await ReleaseAsync();

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, output);
        Assert.Contains("--key", usage, StringComparison.Ordinal);
        Assert.Contains("--issue", usage, StringComparison.Ordinal);
        Assert.Contains("--all", usage, StringComparison.Ordinal);

        // Nothing was released: every record is still blocked for --all to release.
        var (_, all, _) = await ReleaseAsync("--all");
        Assert.StartsWith($"{Label}: 3 record(s) released.", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task All_is_not_asked_beside_named_records_and_names_every_blocked_record_the_keys_left()
    {
        var first = _held[0].DeliveryKey.Value.ToString("D");

        var (both, bothOutput, refused) = await ReleaseAsync("--all", "--key", first);
        Assert.Equal((1, string.Empty), (both, bothOutput));
        Assert.Contains("one of them", refused, StringComparison.Ordinal);

        var (named, one, _) = await ReleaseAsync("--key", first);
        Assert.Equal(0, named);
        Assert.StartsWith($"{Label}: 1 record(s) released of the 1 named.", one, StringComparison.Ordinal);

        var (every, rest, _) = await ReleaseAsync("--all");
        Assert.Equal(0, every);
        Assert.StartsWith($"{Label}: 2 record(s) released.", rest, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs <c>sqlflow records release</c> as the command line would, with the options the module declares for the verb, over
    /// an engine on the test database; returns the exit code, what it printed, and the reason of a usage error.
    /// </summary>
    private async Task<(int Exit, string Output, string Usage)> ReleaseAsync(params string[] options)
    {
        var declared = new DeliveryCliModule().Verbs.Single(v => v.Name == "records");
        await using var services = new ServiceCollection().BuildServiceProvider();
        await using var output = new StringWriter();
        var usage = string.Empty;
        var command = new ModuleCommand(
            services,
            new CliArguments(["records", "release", "flow.yaml", .. options], declared.ValueOptions),
            output,
            TextWriter.Null,
            reason =>
            {
                usage = reason ?? string.Empty;
                return 1;
            },
            CancellationToken.None);
        var ledger = Ledger;
        var exit = await DeliveryRecordVerbs.ReleaseAsync(command, Samples.Engine(ledger, _clock), _flow, Label, CancellationToken.None);
        return (exit, output.ToString(), usage);
    }
}
