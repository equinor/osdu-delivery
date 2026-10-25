using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Cli;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// <c>sqlflow check --connect</c> opens the ingestion tables the way a run opens them: the mapping's columns are checked
/// against the tables as a run checks them, so a column the table lacks fails the check naming it, and the window it
/// reports is the one the next run reads (<see cref="ScopeReads"/>), the whole scope included when the rules moved.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CliCheckConnectTests : IDisposable
{
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file a failed assertion left open is removed with the temporary folder by the operating system.
        }
    }

    private async Task<(EngineContext Engine, FlowRuntime Runtime, MemoryIngestionTables Tables)> EstateAsync(Action<MemoryIngestionTables>? change = null, int? maxRecords = null)
    {
        var tables = await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime.AddMinutes(-5), time: _clock);
        change?.Invoke(tables);
        var engine = Samples.Engine(ledger: null, _clock, sources: tables);
        var flow = Samples.LocalFlow(_root);
        flow = flow with { Source = flow.Source with { Record = flow.Source.Record with { MaxRecords = maxRecords } } };
        var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        return (engine, runtime, tables);
    }

    [Fact]
    public async Task A_column_the_mapping_reads_and_the_table_lacks_fails_the_connected_check_naming_it()
    {
        var (engine, runtime, _) = await EstateAsync(tables =>
        {
            foreach (var record in tables.Records)
            {
                record.Row.Remove("log_run");
            }
        });
        using (runtime)
        {
            var refused = await Assert.ThrowsAsync<FlowValidationException>(() => DeliveryVerbs.ReadAsync(engine, runtime, CancellationToken.None));

            Assert.Contains("log_run", refused.Message, StringComparison.Ordinal);
            Assert.Contains("the record table does not hold", refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Tables_that_hold_every_column_report_what_the_next_run_reads_and_why()
    {
        var (engine, runtime, tables) = await EstateAsync();
        using (runtime)
        {
            var read = await DeliveryVerbs.ReadAsync(engine, runtime, CancellationToken.None);

            Assert.Equal("full", read["selection"]!.GetValue<string>().Split(' ')[0]);
            Assert.Contains("no ledger keeps a watermark", read["why"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Contains(read["columns"]!["record"]!.AsArray(), c => c!.GetValue<string>() == "log_run");
            Assert.NotNull(read["issues"]);
            Assert.Equal(SourceSelectionKind.Full, Assert.Single(tables.Selections).Kind);
        }
    }

    [Fact]
    public async Task A_flow_its_document_limits_is_reported_as_reading_its_first_records_by_key_as_the_run_reads_them()
    {
        var (engine, runtime, tables) = await EstateAsync(maxRecords: 3);
        using (runtime)
        {
            var read = await DeliveryVerbs.ReadAsync(engine, runtime, CancellationToken.None);

            Assert.Equal("the first 3 record(s) of the scope (source.record.maxRecords)", read["selection"]!.GetValue<string>());
            Assert.StartsWith("source.record.maxRecords limits every run of scope ", read["why"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal(3, read["candidates"]!.GetValue<long>());
            var selection = Assert.Single(tables.Selections);
            Assert.Equal((SourceSelectionKind.Keys, 3), (selection.Kind, selection.Keys.Count));
        }
    }

    [Fact]
    public async Task A_scope_last_planned_under_other_rules_is_reported_as_read_whole_as_the_run_reads_it()
    {
        var (engine, runtime, tables) = await EstateAsync();
        using (runtime)
        {
            var scope = Planner.ScopeKey(runtime.Parameters);
            var through = _clock.GetUtcNow().UtcDateTime.AddHours(-1);
            var watermark = new SourceWatermark(runtime.Flow.Id, scope, through, Guid.NewGuid(), through, ContextHash: "rules-of-an-older-mapping");

            var read = await DeliveryVerbs.ReadAsync(engine with { Ledger = WatermarkLedger.Holding(watermark) }, runtime, CancellationToken.None);

            Assert.Equal("full", read["selection"]!.GetValue<string>().Split(' ')[0]);
            Assert.Contains("other rules", read["why"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal(SourceSelectionKind.Full, Assert.Single(tables.Selections).Kind);
        }
    }

    [Theory]
    [InlineData(false, ScopeReadReason.NoLedger)]
    [InlineData(true, ScopeReadReason.NoWatermark)]
    public async Task Without_a_watermark_a_scope_is_read_whole(bool withLedger, ScopeReadReason reason)
    {
        var (_, runtime, _) = await EstateAsync();
        using (runtime)
        {
            var read = await ScopeReads.DecideAsync(withLedger ? WatermarkLedger.Holding(null) : null, runtime.Flow, runtime.Parameters, runtime.Mapping.Context);

            Assert.Equal(reason, read.Reason);
            Assert.Equal(SourceSelectionKind.Full, read.Selection.Kind);
            Assert.Null(read.Watermark);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("same")]
    public async Task Under_the_same_rules_a_scope_is_read_from_its_watermark_less_the_overlap(string? hash)
    {
        var (_, runtime, _) = await EstateAsync();
        using (runtime)
        {
            var through = _clock.GetUtcNow().UtcDateTime.AddHours(-1);
            var planned = hash is null ? null : runtime.Mapping.Context.RulesHash();
            var watermark = new SourceWatermark(runtime.Flow.Id, Planner.ScopeKey(runtime.Parameters), through, Guid.NewGuid(), through, planned);

            var read = await ScopeReads.DecideAsync(WatermarkLedger.Holding(watermark), runtime.Flow, runtime.Parameters, runtime.Mapping.Context);

            Assert.Equal(ScopeReadReason.SinceWatermark, read.Reason);
            Assert.Equal(SourceSelectionKind.Incremental, read.Selection.Kind);
            Assert.Equal(through.AddSeconds(-runtime.Flow.Source.Incremental.OverlapSeconds), read.Selection.LowerUtc);
            Assert.Contains("the next run reads the rows changed after", read.Why(runtime.Flow, runtime.Mapping.Context), StringComparison.Ordinal);
        }
    }

    /// <summary>A ledger that answers the scope's watermark and nothing else: what deciding a scope's read asks of it.</summary>
    public class WatermarkLedger : DispatchProxy
    {
        private SourceWatermark? _watermark;

        public static ILedger Holding(SourceWatermark? watermark)
        {
            var ledger = Create<ILedger, WatermarkLedger>();
            ((WatermarkLedger)(object)ledger)._watermark = watermark;
            return ledger;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(ILedger.GetWatermarkAsync))
            {
                return Task.FromResult(_watermark);
            }

            var refused = new InvalidOperationException($"Deciding a scope's read asks the ledger for its watermark alone, not {targetMethod.Name}.");
            ExceptionDispatchInfo.SetCurrentStackTrace(refused);
            throw refused;
        }
    }
}
