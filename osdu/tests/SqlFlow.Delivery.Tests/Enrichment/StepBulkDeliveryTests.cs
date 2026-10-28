using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A step supplying a well log's bulk data, end to end (osdu/docs/reference/flow/enrich-bulk.md): the sample well logs delivered
/// on the ddms route, their curves named by a table of their own. Every run is made as the executor makes it, over the sample
/// estate, the module's database on SQL Server and a fake protocol counting what is sent. A log with a row of the step goes with
/// its bulk data from that row's folder; one without goes without; new curves send the bulk data alone; nothing changed sends
/// nothing; a row arriving sends its log's bulk data; and two rows hold the log.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class StepBulkDeliveryTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();
    private readonly string _curves;
    private DateTime? _readThrough;

    public StepBulkDeliveryTests()
    {
        _curves = Directory.CreateDirectory(Path.Combine(_root, "curves")).FullName;
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        _db.Dispose();
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private FlowPayload Bulk => new() { Root = _curves, LocationColumn = "curve_folder", Pattern = "*.parquet", HashColumn = "curves_hash", Step = "grid" };

    private FlowDefinition Flow()
    {
        var flow = Samples.LocalFlow(_root);
        var datasets = new Dictionary<string, FlowSourceDataset>(flow.Source.Datasets, StringComparer.Ordinal)
        {
            ["grid"] = new FlowSourceDataset
            {
                Object = "OsduData.silver.WellLogGrid",
                Join = new Dictionary<string, string>(StringComparer.Ordinal) { ["source_project"] = "source_project", ["log_id"] = "log_id" },
            },
        };
        return flow with
        {
            Source = flow.Source with { Datasets = datasets, Payloads = new Dictionary<string, FlowPayload>(StringComparer.Ordinal) { [PayloadParts.Bulk] = Bulk } },
            Target = flow.Target with { Protocol = DeliveryProtocol.Ddms, ProtocolOptions = flow.Target.ProtocolOptions with { Payload = null } },
            Enrich = [new EnrichStep { Name = "grid", ReadsRows = true, Bulk = Bulk }],
        };
    }

    private async Task<(FakeProtocol Sent, OsduLedger Ledger, Guid FlowId)> DeliverAsync(MemoryIngestionTables tables)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var templates = new MemoryTemplates(Samples.TemplateKinds().Select(Samples.SampleTemplate));
        var engine = Samples.Engine(ledger, _clock, templates: templates, sources: tables) with { Protocols = new FakeProtocolFactory(protocol) };
        var runtime = await FlowRuntime.CreateAsync(engine, Flow(), SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);

        var selection = SourceSelection.Incremental(_readThrough);
        runtime.Selection = selection;
        runtime.RunId = Guid.CreateVersion7();
        await DeliveryExecutor.DeliverRequestedAsync(runtime, selection, DeliveryRunPayload.None, Samples.Logger<DeliveryExecutor>(), CancellationToken.None);
        runtime.Selection = selection;
        await runtime.RunAsync(force: false);
        _readThrough = Now;
        return (protocol, ledger, runtime.Flow.Id);
    }

    /// <summary>Gives a log its curve rows, each naming a folder of its own holding one parquet file with the content given.</summary>
    private void Curves(MemoryRecord record, params string[] contents)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        record.Datasets.Remove("grid");
        for (var i = 0; i < contents.Length; i++)
        {
            var folder = $"{record.Row["source_project"]}-{record.Row["log_id"]}-{i}";
            var path = Directory.CreateDirectory(Path.Combine(_curves, folder)).FullName;
            File.WriteAllText(Path.Combine(path, "curves.parquet"), contents[i]);
            record.AddChild("grid", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["source_project"] = record.Row["source_project"],
                ["log_id"] = record.Row["log_id"],
                ["curve_folder"] = folder,
                ["curves_hash"] = Hashing.ContentHash.Of(contents[i]),
            });
        }

        record.DatasetUpdatedUtc["grid"] = Now;
    }

    private static DeliveryWork Of(FakeProtocol sent, int index) => Assert.Single(sent.Deliveries, d => d.Key == SampleEstate.Key(index));

    [Fact]
    public async Task A_logs_bulk_data_goes_from_the_steps_row_and_only_when_it_moves()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        Assert.True(tables.Records.Count >= 3, "The sample estate holds too few logs for this scenario.");
        Curves(tables.Records[0], "curves of log 0");

        // 1. Every log goes; the one with a row of the step carries its bulk data from that row's folder, the others none.
        var (sent, ledger, flowId) = await DeliverAsync(tables);
        Assert.Equal(tables.Records.Count, sent.Deliveries.Count);
        var first = Of(sent, 0);
        Assert.True(first.DeliverPayload);
        Assert.EndsWith("curves.parquet", Assert.Single(await first.Payload!.ListChunksAsync()).Path, StringComparison.Ordinal);
        Assert.False(Of(sent, 1).DeliverPayload);
        Assert.Equal(Hashing.ContentHash.Of("curves of log 0"), (await ledger.GetRecordAsync(flowId, SampleEstate.Key(0)))!.PayloadHash);

        // 2. Nothing changed: nothing is sent.
        (sent, _, _) = await DeliverAsync(tables);
        Assert.Empty(sent.Deliveries);

        // 3. New curves for the log: its bulk data goes alone, the record as OSDU holds it.
        Curves(tables.Records[0], "curves of log 0, corrected");
        (sent, _, _) = await DeliverAsync(tables);
        var corrected = Assert.Single(sent.Deliveries);
        Assert.Equal((true, false), (corrected.DeliverPayload, corrected.DeliverMetadata));

        // 4. A row arriving for a log that had none sends its bulk data.
        Curves(tables.Records[1], "curves of log 1");
        (sent, _, _) = await DeliverAsync(tables);
        Assert.True(Assert.Single(sent.Deliveries).DeliverPayload);

        // 5. Two rows for one log: it has one set of bulk data, so it is held.
        Curves(tables.Records[2], "a", "b");
        (sent, ledger, _) = await DeliverAsync(tables);
        Assert.Empty(sent.Deliveries);
        var held = (await ledger.GetRecordAsync(flowId, SampleEstate.Key(2)))!;
        Assert.Equal(RecordStatus.Held, held.Status);
        Assert.Contains("names the record's bulk data by its one row, and its dataset holds 2 rows", held.LastError, StringComparison.Ordinal);
    }
}
