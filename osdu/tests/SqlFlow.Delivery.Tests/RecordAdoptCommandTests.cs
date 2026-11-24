using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using SqlFlow.Delivery.Cli;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// <c>sqlflow records adopt</c> on SQL Server (osdu/docs/reference/cli/records.md, records adopt): every adoption, of the
/// records <c>--key</c> names as of every record or an issue's, takes the partition the ledger is kept in typed back with
/// <c>--confirm</c>, the same rule the API's flow routes keep, and nothing is asked without it.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class RecordAdoptCommandTests : IAsyncLifetime, IDisposable
{
    private const string Label = "adopt-flow";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();
    private readonly FlowDefinition _flow;
    private readonly IReadOnlyList<RecordState> _held;

    public RecordAdoptCommandTests()
    {
        _flow = Samples.LocalFlow(_root);
        _held = new[] { "N-1", "N-2", "N-3" }.Select(Occupied).ToList();
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

    /// <summary>A record held because OSDU already holds a record at the id its key makes, which an adoption may be asked of.</summary>
    private RecordState Occupied(string sourceKey)
    {
        var targetId = $"{TestLedgers.Partition}:reference-data--ExternalCatalogNamespace:{sourceKey}";
        return new RecordState
        {
            DeliveryKey = DeliveryKey.Derive("adopt", [sourceKey]),
            FlowId = _flow.Id,
            SourceKey = sourceKey,
            MappingName = "Thing",
            LastSubmissionId = Guid.NewGuid(),
            PendingSourceFileName = "namespaces.json",
            PendingSourceRowNumber = 1,
            LastError = string.Create(CultureInfo.InvariantCulture, $"{OccupiedHolds.Prefix}{targetId} at version 7, the OSDU id made from the key, and no record of the ledger claimed it"),
            OccupiedTargetId = targetId,
            OccupiedTargetVersion = 7,
        };
    }

    [Fact]
    public async Task Records_named_by_key_are_adopted_only_with_the_partition_typed_back()
    {
        var first = _held[0].DeliveryKey.Value.ToString("D");

        var (unconfirmed, nothing, typeIt) = await AdoptAsync("--key", first);
        Assert.Equal((1, string.Empty), (unconfirmed, nothing));
        Assert.Contains($"partition {TestLedgers.Partition}", typeIt, StringComparison.Ordinal);
        Assert.EndsWith($"Type the partition back with --confirm {TestLedgers.Partition}.", typeIt, StringComparison.Ordinal);

        var (mistyped, none, differs) = await AdoptAsync("--key", first, "--confirm", "prod");
        Assert.Equal((1, string.Empty), (mistyped, none));
        Assert.EndsWith($"are kept in '{TestLedgers.Partition}'. Nothing was adopted.", differs, StringComparison.Ordinal);
        Assert.Null((await Ledger.GetRecordAsync(_flow.Id, _held[0].DeliveryKey))!.AdoptRequestedUtc);

        // The partition is read case aside, as every typed-back partition is.
        var (named, one, _) = await AdoptAsync("--key", first, "--confirm", TestLedgers.Partition.ToUpperInvariant());
        Assert.Equal(0, named);
        Assert.StartsWith($"{Label}: 1 record(s) of the 1 named asked to adopt", one, StringComparison.Ordinal);
        Assert.NotNull((await Ledger.GetRecordAsync(_flow.Id, _held[0].DeliveryKey))!.AdoptRequestedUtc);

        var (every, rest, _) = await AdoptAsync("--confirm", TestLedgers.Partition);
        Assert.Equal(0, every);
        Assert.StartsWith($"{Label}: 2 record(s) held for an id OSDU holds asked to adopt", rest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_issue_the_flow_has_no_record_under_is_refused_saying_where_the_issues_are_listed()
    {
        var refused = await Assert.ThrowsAsync<FlowValidationException>(() => AdoptAsync("--issue", "0123456789abcdef", "--confirm", TestLedgers.Partition));

        Assert.Equal(
            $"No record of {_flow.Label} is blocked by issue 0123456789abcdef: its records were released, their source changed, or they were planned again since. 'records issues' lists the issues it has.",
            refused.Message);
    }

    [Fact]
    public async Task An_issue_s_records_are_adopted_together()
    {
        var issue = ProblemSignature.Format(ProblemSignature.Of(_held[0].LastError));

        var (exit, output, _) = await AdoptAsync("--issue", issue, "--confirm", TestLedgers.Partition);

        Assert.Equal(0, exit);
        Assert.StartsWith($"{Label}: 3 record(s) issue {issue} kept held asked to adopt", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs <c>sqlflow records adopt</c> as the command line would, with the options the module declares for the verb, over an
    /// engine on the test database; returns the exit code, what it printed, and the reason of a usage error.
    /// </summary>
    private async Task<(int Exit, string Output, string Usage)> AdoptAsync(params string[] options)
    {
        var declared = new DeliveryCliModule().Verbs.Single(v => v.Name == "records");
        await using var services = new ServiceCollection().BuildServiceProvider();
        await using var output = new StringWriter();
        var usage = string.Empty;
        var command = new ModuleCommand(
            services,
            new CliArguments(["records", "adopt", "flow.yaml", .. options], declared.ValueOptions),
            output,
            TextWriter.Null,
            reason =>
            {
                usage = reason ?? string.Empty;
                return 1;
            },
            CancellationToken.None);
        var ledger = Ledger;
        var exit = await DeliveryRecordVerbs.AdoptAsync(command, Samples.Engine(ledger, _clock), _flow, Label, CancellationToken.None);
        return (exit, output.ToString(), usage);
    }
}
