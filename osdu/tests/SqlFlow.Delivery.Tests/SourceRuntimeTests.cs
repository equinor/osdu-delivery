using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Source;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>Hands each interface of a source a fake target of its own, so a test sees what each one sent.</summary>
public sealed class InterfaceProtocols : IProtocolFactory
{
    private readonly Dictionary<string, FakeProtocol> _protocols = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>The fake target of <paramref name="interfaceName"/> (<c>""</c> for a flow in the single form).</summary>
    public FakeProtocol this[string interfaceName]
    {
        get
        {
            lock (_gate)
            {
                if (!_protocols.TryGetValue(interfaceName, out var protocol))
                {
                    protocol = new FakeProtocol();
                    _protocols[interfaceName] = protocol;
                }

                return protocol;
            }
        }
    }

    /// <summary>Every flow a protocol was built for, in order.</summary>
    public List<string> Built { get; } = [];

    public Task<IDeliveryProtocol> CreateAsync(FlowDefinition flow, HttpRuntime http, ILoggerFactory loggers, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Built.Add(flow.Label);
        }

        return Task.FromResult<IDeliveryProtocol>(this[flow.Interface ?? string.Empty]);
    }
}

/// <summary>The ingestion tables of a source in memory: one set of tables per record table the interfaces read.</summary>
public sealed class MemoryEstate : IIngestionSourceFactory
{
    private readonly Dictionary<string, MemoryIngestionTables> _tables = new(StringComparer.OrdinalIgnoreCase);

    public MemoryIngestionTables this[string recordObject] => _tables[recordObject];

    public MemoryEstate Add(string recordObject, MemoryIngestionTables tables)
    {
        _tables[recordObject] = tables;
        return this;
    }

    public IIngestionSource Open(FlowDefinition flow, IReadOnlyDictionary<string, string> values, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return _tables.TryGetValue(flow.Source.Record.Object, out var tables)
            ? tables.Open(flow, values, loggers)
            : throw new DeliveryException($"Flow '{flow.Label}': the table {flow.Source.Record.Object} was not found on the source database.");
    }
}

/// <summary>
/// A run of a source (osdu/docs/reference/flow/interfaces.md) through the executor the platform calls: every
/// interface planned and delivered in the waves of its dependencies under a ledger of its own, the preflight that stops a run
/// before anything is sent, an interface that stops taking only the interfaces waiting for it along, a selection of
/// interfaces, a run of one interface, a fan-out member told which interface it plans, and a flow in the single form
/// stopped by an outage.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class SourceRuntimeTests : IDisposable
{
    private const string WellboreTable = "OsduData.silver.Wellbore";
    private const string ArchiveTable = "OsduData.silver.WellboreArchive";
    private const string WellLogTable = "OsduData.silver.WellLog";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 1, 7, 0, 0, TimeSpan.Zero));
    private readonly string _root = Samples.NewTempDirectory();
    private readonly InterfaceProtocols _protocols = new();
    private readonly RecordingListener _events = new();

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file still open by a finished run is the temporary folder's to clean up.
        }
    }

    /// <summary>
    /// The wells source: its wellbores, the well logs waiting for them, and optionally an archive of wellbores read from a
    /// table of its own and waiting for nothing. <paramref name="wellbores"/> is a YAML fragment the wellbores interface adds.
    /// </summary>
    private string SourceYaml(
        bool archive = false, string wellbores = "", string welllogMapping = "WellLog@1.4.0", bool welllogsAfter = true,
        string wellboreMapping = "Wellbore@1.0.0", string archiveMapping = "Wellbore@1.0.0", string? mappingsDirectory = null)
    {
        var root = _root.Replace('\\', '/');
        var mappings = (mappingsDirectory ?? Samples.FixtureMappings).Replace('\\', '/');
        var archiveInterface = archive
            ? $$"""
                archive:
                  record: { object: {{ArchiveTable}}, key: [facility_name], primaryKey: RecId }
                  datasets:
                    aliases: { object: OsduData.silver.WellboreAlias, join: { facility_name: facility_name }, orderBy: [alias_name] }
                  mapping: {{archiveMapping}}
              """
            : string.Empty;
        return ($$"""
            flowType: delivery
            name: wells
            parameters:
              logSource: { required: true }
            source:
              connection: ${env:OSDU_DATA_DB}
              lastModified: update_date
              work: '{{root}}/work/{logSource}'
            render:
              mappings: '{{mappings}}'
              parameters:
                dataPartition: dev
                aclOwner: data.welldb.owners@dev.dataservices.energy
                aclViewer: data.welldb.viewers@dev.dataservices.energy
                legalTag: dev-osdu-default-legal
            target:
              endpoint: http://localhost:9/facade-api
              headers:
                data-partition-id: {{Samples.SampleCacheScope}}
              protocolOptions:
                ddmsRoot: /api/os-wellbore-ddms
            reliability:
              retry: { attempts: 3, recordBaseDelayMinutes: 1 }
            parallelism:
              recordsAtOnce: 1
              renderThreads: 1
              interfacesAtOnce: 1
            interfaces:
              wellbores:
                record: { object: {{WellboreTable}}, key: [facility_name], primaryKey: RecId }
                datasets:
                  aliases: { object: OsduData.silver.WellboreAlias, join: { facility_name: facility_name }, orderBy: [alias_name] }
                mapping: {{wellboreMapping}}
                {{wellbores}}
              welllogs:
                record: { object: {{WellLogTable}}, key: [source_project, log_id], primaryKey: RecId, scope: { log_source: logSource } }
                datasets:
                  curves: { object: OsduData.silver.WellLogCurve, join: { source_project: source_project, log_id: log_id }, orderBy: [curve_ordinal] }
                bulk: { root: '{{root}}/curves', locationColumn: curve_folder, pattern: "chunk_*.parquet", hashColumn: payload_hash, chunkCountColumn: chunk_count }
                mapping: {{welllogMapping}}
                {{(welllogsAfter ? "after: [wellbores]" : string.Empty)}}
            {{archiveInterface}}
            """).ReplaceLineEndings("\n");
    }

    /// <summary>The source, loaded from a file under the test's root, as a node reads it.</summary>
    private SourceDefinition Load(string yaml)
    {
        var path = Path.Combine(_root, "flows", "wells.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, yaml);
        return new DeliveryDocumentLoader().LoadSource(path);
    }

    /// <summary>The wellbores of the sample estate as rows of a wellbore table.</summary>
    private MemoryIngestionTables Wellbores(string prefix = "")
    {
        var tables = new MemoryIngestionTables(_clock);
        var rowNumber = 0L;
        foreach (var wellbore in FixtureWellbores.Wellbores)
        {
            rowNumber++;
            var record = new MemoryRecord
            {
                Row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["RecId"] = rowNumber,
                    ["facility_name"] = prefix + wellbore.FacilityName,
                    ["facility_description"] = wellbore.Description,
                    ["facility_id"] = wellbore.FacilityId,
                    ["update_date"] = wellbore.UpdateDateUtc,
                    // The wellbore a sidetrack was kicked off from: none of the sample wellbores is one.
                    ["kickoff_wellbore"] = null,
                },
                UpdatedUtc = _clock.GetUtcNow().UtcDateTime,
                FileName = "wellbore_20260901.csv",
                RowNumber = rowNumber,
            };

            foreach (var alias in wellbore.Aliases)
            {
                record.AddChild("aliases", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["facility_name"] = prefix + wellbore.FacilityName,
                    ["alias_name"] = alias,
                });
            }

            record.DatasetUpdatedUtc["aliases"] = _clock.GetUtcNow().UtcDateTime;
            tables.Add(record);
        }

        return tables;
    }

    private async Task<MemoryEstate> EstateAsync(bool archive = false)
    {
        var estate = new MemoryEstate()
            .Add(WellboreTable, Wellbores())
            .Add(WellLogTable, await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime, time: _clock));
        return archive ? estate.Add(ArchiveTable, Wellbores("ARCHIVE-")) : estate;
    }

    private EngineContext Engine(IIngestionSourceFactory sources)
        => Samples.Engine(_db.Ledger(_clock), _clock, protocols: _protocols, sources: sources) with { Listener = _events };

    private static async Task<DocumentExecutionResult> RunAsync(
        EngineContext engine, SourceDefinition source, string operation = DeliveryOperations.Deliver, DeliveryRunPayload? payload = null,
        IRunFanOut? fanOut = null)
    {
        using var provider = new ServiceCollection().AddSingleton(engine).AddSingleton<PartitionLedgers>().BuildServiceProvider();
        return await new DeliveryExecutor(provider).ExecuteAsync(
            new DeliveryFlowDocument { Source = source },
            source.SourcePath!,
            new DocumentExecutionOptions
            {
                RunId = Guid.CreateVersion7(),
                Actor = "test",
                FanOut = fanOut,
                Parameters = new RunParameters
                {
                    Operation = operation,
                    Values = new Dictionary<string, string>(SampleEstate.Values, StringComparer.Ordinal),
                    Payload = payload?.ToJson(),
                },
            },
            CancellationToken.None);
    }

    private static SourceRunOutcome Outcome(DocumentExecutionResult result)
        => Assert.IsType<SourceRunOutcome>(result.Result);

    [Fact]
    public async Task An_interface_whose_mapping_reads_no_cache_still_takes_the_after_step_its_cache_block_names()
    {
        // The wellbores render with a mapping that reads no cache, and their interface names the wellbores of the partition's
        // cache for its after step; the well logs name none. The placeholder target answers no storage read: the step says so
        // and the run stands.
        var caches = _db.Caches();
        await Samples.ImportSampleCacheAsync(caches);
        await _db.DeclareCacheAsync(Samples.SampleCacheScope, "wells-wellbore-cache", new Snapshots.ReferenceTypeSpec
        {
            Name = "Wellbore",
            EntityType = "master-data--Wellbore",
            Kind = "osdu:wks:master-data--Wellbore:*",
            Fields = [new Snapshots.ReferenceFieldSpec("data.FacilityName")],
        });
        var source = Load(SourceYaml(wellbores: "cache: { after: [master-data--Wellbore] }"));
        var engine = Engine(await EstateAsync()) with { Cache = caches };

        var result = await RunAsync(engine, source);

        Assert.True(result.Success, result.Error);
        var outcome = Outcome(result);
        var wellbores = Assert.IsType<DeliverOutcome>(outcome.Interfaces[0].Result);
        Assert.Equal(2, wellbores.Delivered);
        var after = Assert.IsType<Engine.Snapshots.CacheStepOutcome>(wellbores.CacheAfter);
        Assert.Equal(Samples.SampleCacheScope, after.Scope);
        Assert.Contains("A refresh of the cache flows brings the records in", after.Skipped, StringComparison.Ordinal);
        Assert.Null(Assert.IsType<DeliverOutcome>(outcome.Interfaces[1].Result).CacheAfter);

        // The run's artifact carries the step's sentence for the run page.
        var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.RunDirectory!, "run.json"))).RootElement;
        var summary = artifact.GetProperty("result").GetProperty("interfaces")[0].GetProperty("result").GetProperty("cacheAfter").GetProperty("summary").GetString();
        Assert.StartsWith($"The cache of partition '{Samples.SampleCacheScope}' was not brought up to date after the run: ", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_source_delivers_every_interface_in_the_waves_of_its_dependencies_under_a_ledger_each()
    {
        var source = Load(SourceYaml());
        var engine = Engine(await EstateAsync());

        var result = await RunAsync(engine, source);

        Assert.True(result.Success, result.Error);
        var outcome = Outcome(result);
        Assert.Equal("wells", outcome.Source);
        Assert.Equal(2, outcome.Completed);
        Assert.Equal(0, outcome.Stopped + outcome.Skipped);
        Assert.Equal(7, outcome.Delivered);
        Assert.Equal(7, outcome.RowsLoaded);
        var wellbores = outcome.Interfaces[0];
        var welllogs = outcome.Interfaces[1];
        Assert.Equal(("wellbores", 1, "storage", InterfaceStates.Completed), (wellbores.Interface, wellbores.Wave, wellbores.Route, wellbores.State));
        Assert.Equal(("welllogs", 2, "ddms", InterfaceStates.Completed), (welllogs.Interface, welllogs.Wave, welllogs.Route, welllogs.State));
        Assert.Equal(["wellbores"], welllogs.WaitsFor);
        Assert.Equal(FlowId.Of("wells/welllogs"), welllogs.FlowId);
        Assert.Equal(5, Assert.IsType<DeliverOutcome>(welllogs.Result).Delivered);
        Assert.Equal(2, Assert.IsType<DeliverOutcome>(wellbores.Result).Delivered);

        // Each interface sent its own records to its own target, the wellbores first.
        Assert.Equal(2, _protocols["wellbores"].Deliveries.Count);
        Assert.Equal(5, _protocols["welllogs"].Deliveries.Count);
        var trace = _events.Events.Where(e => e.Kind.StartsWith("interface.", StringComparison.Ordinal)).Select(e => $"{e.Kind} {e.Interface}").ToList();
        Assert.Equal(["interface.started wellbores", "interface.completed wellbores", "interface.started welllogs", "interface.completed welllogs"], trace);
        Assert.All(_events.Events.Where(e => e.Kind.StartsWith("record.", StringComparison.Ordinal)), e => Assert.Equal($"wells/{e.Interface}", e.FlowName));

        // The ledger keeps each interface apart, with the flow and interface as the name its rows carry.
        var ledger = engine.Ledger!;
        Assert.Equal(2, (await ledger.StatsAsync(FlowId.Of("wells/wellbores"), _clock.GetUtcNow().UtcDateTime)).Delivered);
        Assert.Equal(5, (await ledger.StatsAsync(FlowId.Of("wells/welllogs"), _clock.GetUtcNow().UtcDateTime)).Delivered);
        var submissions = await ledger.ListSubmissionsAsync(FlowId.Of("wells/welllogs"), 10);
        Assert.Equal("wells/welllogs", Assert.Single(submissions).FlowName);
        var activities = await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = FlowId.Of("wells/wellbores"), Max = 10 });
        Assert.All(activities, a => Assert.Equal("wells/wellbores", a.FlowName));

        // The run's artifact says what each interface did.
        var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.RunDirectory!, "run.json"))).RootElement;
        var interfaces = artifact.GetProperty("result").GetProperty("interfaces");
        Assert.Equal(2, interfaces.GetArrayLength());
        Assert.Equal("welllogs", interfaces[1].GetProperty("interface").GetString());
        Assert.Equal(5, interfaces[1].GetProperty("result").GetProperty("delivered").GetInt64());
        Assert.Equal(7, artifact.GetProperty("result").GetProperty("rowsLoaded").GetInt64());

        // A second run finds nothing changed and sends nothing.
        var again = await RunAsync(engine, source);
        Assert.True(again.Success, again.Error);
        Assert.Equal(0, Outcome(again).Delivered);
        Assert.Equal(7, _protocols["wellbores"].Deliveries.Count + _protocols["welllogs"].Deliveries.Count);
    }

    [Fact]
    public async Task The_preflight_names_every_problem_at_once_and_nothing_is_planned_or_sent()
    {
        var source = Load(SourceYaml(welllogMapping: "WellLog@9.9.9"));
        var estate = await EstateAsync();
        estate[WellboreTable].ShapeProblem = "the table OsduData.silver.Wellbore, declared under interfaces.wellbores.record.object, was not found";
        _protocols["wellbores"].Reachable = false;
        var engine = Engine(estate);

        var result = await RunAsync(engine, source);

        Assert.False(result.Success);
        Assert.IsType<OperationFailure>(result.Result);
        Assert.Contains("The preflight of 'wells' found 3 problem(s), so nothing was planned or sent", result.Error, StringComparison.Ordinal);
        Assert.Contains("interface 'welllogs':", result.Error, StringComparison.Ordinal);
        Assert.Contains("WellLog@9.9.9", result.Error, StringComparison.Ordinal);
        Assert.Contains("interface 'wellbores': Flow 'wells/wellbores': the table OsduData.silver.Wellbore", result.Error, StringComparison.Ordinal);
        Assert.Contains("interface 'wellbores': the storage route's service is failing (HTTP 503) at /about", result.Error, StringComparison.Ordinal);
        Assert.Empty(_protocols["wellbores"].Deliveries);
        Assert.Empty(_protocols["welllogs"].Deliveries);
        Assert.Empty(await engine.Ledger!.ListSubmissionsAsync(FlowId.Of("wells/wellbores"), 10));
        Assert.DoesNotContain(_events.Events, e => e.Kind.StartsWith("interface.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_interface_that_stops_takes_only_the_interfaces_waiting_for_it_and_the_next_run_does_the_rest()
    {
        var source = Load(SourceYaml(archive: true, wellbores: "failWhen: { outageFailures: 2 }"));
        var engine = Engine(await EstateAsync(archive: true));
        _protocols["wellbores"].FailWith = _ => new OsduStatusException(503, "HTTP 503 Service Unavailable from PUT /records");

        var result = await RunAsync(engine, source);

        Assert.False(result.Success);
        var outcome = Outcome(result);
        Assert.Equal(
            [("wellbores", InterfaceStates.Stopped), ("welllogs", InterfaceStates.Skipped), ("archive", InterfaceStates.Completed)],
            outcome.Interfaces.Select(i => (i.Interface, i.State)).ToArray());
        Assert.Contains("an outage: 2 records in a row could not reach the service", outcome.Interfaces[0].Reason, StringComparison.Ordinal);
        Assert.Equal("it waits for wellbores, which did not complete", outcome.Interfaces[1].Reason);
        Assert.Null(outcome.Interfaces[1].StartedUtc);
        Assert.Contains("1 of 3 interface(s) completed", result.Error, StringComparison.Ordinal);
        Assert.Contains("stopped: wellbores (an outage", result.Error, StringComparison.Ordinal);
        Assert.Contains("skipped: welllogs (it waits for wellbores, which did not complete)", result.Error, StringComparison.Ordinal);
        Assert.Contains("What completed is in the ledger, and the next run does what is left.", result.Error, StringComparison.Ordinal);
        Assert.Equal(2, _protocols["archive"].Deliveries.Count);
        Assert.Empty(_protocols["welllogs"].Deliveries);
        Assert.Contains(_events.Events, e => e.Kind == "interface.stopped" && e.Interface == "wellbores");
        Assert.Contains(_events.Events, e => e.Kind == "interface.skipped" && e.Interface == "welllogs");

        // The tries that failed are the records' own; nothing was held or failed for good.
        var wellbores = await engine.Ledger!.ListAsync(FlowId.Of("wells/wellbores"), new RecordQuery { Max = 10 });
        Assert.All(wellbores, r => Assert.Equal(RecordStatus.Pending, r.Status));

        // Once the service is back and the retries are due, the next run finishes the wellbores and then the well logs.
        _protocols["wellbores"].FailWith = null;
        _clock.Advance(TimeSpan.FromMinutes(10));
        var again = await RunAsync(engine, source);
        Assert.True(again.Success, again.Error);
        Assert.All(Outcome(again).Interfaces, i => Assert.Equal(InterfaceStates.Completed, i.State));
        Assert.Equal(2, (await engine.Ledger.StatsAsync(FlowId.Of("wells/wellbores"), _clock.GetUtcNow().UtcDateTime)).Delivered);
        Assert.Equal(5, _protocols["welllogs"].Deliveries.Count);
        Assert.Equal(2, _protocols["archive"].Deliveries.Count);
    }

    [Fact]
    public async Task A_run_selects_interfaces_and_does_not_wait_for_the_ones_it_leaves_out()
    {
        var source = Load(SourceYaml(archive: true));
        var engine = Engine(await EstateAsync(archive: true));

        var result = await RunAsync(engine, source, payload: new DeliveryRunPayload { Interfaces = ["welllogs", "archive"] });

        Assert.True(result.Success, result.Error);
        var outcome = Outcome(result);
        Assert.Equal(["welllogs", "archive"], outcome.Interfaces.Select(i => i.Interface));

        // The well logs refer to wellbores, which the archive delivers too: they wait for the archive, and not for the
        // wellbores interface the run leaves out, although after: names it.
        Assert.Equal((2, 1), (outcome.Interfaces[0].Wave, outcome.Interfaces[1].Wave));
        Assert.Equal(["archive"], outcome.Interfaces[0].WaitsFor);
        Assert.StartsWith(
            "archive: osdu.data.WellboreID refers to master-data--Wellbore, which archive delivers",
            Assert.Single(outcome.Interfaces[0].WaitReasons), StringComparison.Ordinal);
        Assert.Empty(_protocols["wellbores"].Deliveries);
        Assert.Equal(5, _protocols["welllogs"].Deliveries.Count);

        var unknown = await RunAsync(engine, source, payload: new DeliveryRunPayload { Interfaces = ["cores"] });
        Assert.False(unknown.Success);
        Assert.Contains("Flow 'wells' has no interface 'cores'; it declares wellbores, welllogs, archive.", unknown.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_of_the_first_records_of_a_source_takes_the_first_of_each_interface_s_own_scope()
    {
        var source = Load(SourceYaml());
        var engine = Engine(await EstateAsync());

        var result = await RunAsync(engine, source, payload: new DeliveryRunPayload { MaxRecords = 1 });

        Assert.True(result.Success, result.Error);
        var outcome = Outcome(result);
        Assert.Equal(["wellbores", "welllogs"], outcome.Interfaces.Select(i => i.Interface));
        Assert.All(outcome.Interfaces, i => Assert.Equal("the first 1 record(s) of the scope (maxRecords)", Assert.IsType<DeliverOutcome>(i.Result).Selection));
        Assert.Single(_protocols["wellbores"].Deliveries);
        Assert.Single(_protocols["welllogs"].Deliveries);

        // A run of the source afterwards delivers what each interface's first run left.
        var whole = await RunAsync(engine, source);
        Assert.True(whole.Success, whole.Error);
        Assert.Equal(2, _protocols["wellbores"].Deliveries.Count);
        Assert.Equal(5, _protocols["welllogs"].Deliveries.Count);
    }

    [Fact]
    public async Task A_source_runs_its_interfaces_in_the_order_their_mappings_refer_to_each_other()
    {
        // Nothing declares an order: the well log mapping fills osdu.data.WellboreID, which the template says refers to
        // wellbores, and the wellbores interface delivers them.
        var source = Load(SourceYaml(welllogsAfter: false));
        var engine = Engine(await EstateAsync());

        var result = await RunAsync(engine, source);

        Assert.True(result.Success, result.Error);
        var (wellbores, welllogs) = (Outcome(result).Interfaces[0], Outcome(result).Interfaces[1]);
        Assert.Equal((1, 2), (wellbores.Wave, welllogs.Wave));
        Assert.Equal(["wellbores"], welllogs.WaitsFor);
        Assert.Equal(
            $"wellbores: osdu.data.WellboreID refers to master-data--Wellbore, which wellbores delivers ({Samples.WellboreKind})",
            Assert.Single(welllogs.WaitReasons));
        var trace = _events.Events.Where(e => e.Kind.StartsWith("interface.", StringComparison.Ordinal)).Select(e => $"{e.Kind} {e.Interface}").ToList();
        Assert.Equal(["interface.started wellbores", "interface.completed wellbores", "interface.started welllogs", "interface.completed welllogs"], trace);

        // The run's artifact says why.
        var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.RunDirectory!, "run.json"))).RootElement;
        var reasons = artifact.GetProperty("result").GetProperty("interfaces")[1].GetProperty("waitReasons");
        Assert.Contains("osdu.data.WellboreID", reasons[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_the_ledger_removes_what_refers_to_a_record_first_and_the_next_run_delivers_everything_again()
    {
        var source = Load(SourceYaml(welllogsAfter: false));
        var engine = Engine(await EstateAsync());
        Assert.True((await RunAsync(engine, source)).Success);
        var ledger = engine.Ledger!;
        var partition = (await ledger.GetLedgerAsync(FlowId.Of("wells/welllogs")))!.Partition!;
        _clock.Advance(TimeSpan.FromMinutes(10));
        var seen = _events.Events.Count;

        var result = await RunAsync(engine, source, DeliveryOperations.DeleteLedger, new DeliveryRunPayload { Confirm = partition });

        Assert.True(result.Success, result.Error);
        var outcome = Outcome(result);
        Assert.Equal((7, 7, 7), (outcome.Removed, outcome.Deleted, outcome.RowsLoaded));

        // The well logs refer to the wellbores, so their ledger goes first and the wellbores' waits for it.
        var (wellbores, welllogs) = (outcome.Interfaces[0], outcome.Interfaces[1]);
        Assert.Equal((2, 1), (wellbores.Wave, welllogs.Wave));
        Assert.Equal(["welllogs"], wellbores.WaitsFor);
        Assert.Equal(5, Assert.IsType<DeleteLedgerOutcome>(welllogs.Result).Removed);
        var trace = _events.Events.Skip(seen).Where(e => e.Kind.StartsWith("interface.", StringComparison.Ordinal)).Select(e => $"{e.Kind} {e.Interface}").ToList();
        Assert.Equal(["interface.started welllogs", "interface.completed welllogs", "interface.started wellbores", "interface.completed wellbores"], trace);
        Assert.Equal(5, _protocols["welllogs"].Deletes.Count);
        Assert.Equal(2, _protocols["wellbores"].Deletes.Count);
        Assert.All(_protocols["welllogs"].Deletes.Concat(_protocols["wellbores"].Deletes), d => Assert.Equal(RemovalScope.Record, d.Scope));
        foreach (var name in new[] { "wells/wellbores", "wells/welllogs" })
        {
            Assert.Equal(0, (await ledger.StatsAsync(FlowId.Of(name), _clock.GetUtcNow().UtcDateTime)).Total);
            Assert.Empty(await ledger.ListSubmissionsAsync(FlowId.Of(name), 10));
        }

        // Nothing is left that says a row was planned: the next run reads every row and delivers each as a new record.
        var again = await RunAsync(engine, source);
        Assert.True(again.Success, again.Error);
        Assert.Equal(7, Outcome(again).Delivered);
        Assert.Equal(7 + 7, _protocols["wellbores"].Deliveries.Count + _protocols["welllogs"].Deliveries.Count);
    }

    [Fact]
    public async Task A_run_deleting_the_ledger_that_names_another_partition_deletes_nothing()
    {
        var source = Load(SourceYaml(welllogsAfter: false));
        var engine = Engine(await EstateAsync());
        Assert.True((await RunAsync(engine, source)).Success);

        var result = await RunAsync(engine, source, DeliveryOperations.DeleteLedger, new DeliveryRunPayload { Confirm = "elsewhere" });

        Assert.False(result.Success);
        var outcome = Outcome(result);
        Assert.Equal(
            [("wellbores", InterfaceStates.Skipped), ("welllogs", InterfaceStates.Stopped)],
            outcome.Interfaces.Select(i => (i.Interface, i.State)).ToArray());
        Assert.Contains("so nothing was removed or deleted", outcome.Interfaces[1].Reason, StringComparison.Ordinal);
        Assert.Contains("deleting the ledger again does the rest", result.Error, StringComparison.Ordinal);
        Assert.Empty(_protocols["welllogs"].Deletes);
        Assert.Empty(_protocols["wellbores"].Deletes);
        Assert.Equal(5, (await engine.Ledger!.StatsAsync(FlowId.Of("wells/welllogs"), _clock.GetUtcNow().UtcDateTime)).Delivered);
    }

    [Fact]
    public async Task A_remove_run_takes_the_records_it_names_out_of_osdu_in_its_interface_and_out_of_the_ledger_when_asked()
    {
        var source = Load(SourceYaml());
        var engine = Engine(await EstateAsync());
        Assert.True((await RunAsync(engine, source)).Success);
        var ledger = engine.Ledger!;
        var logs = FlowId.Of("wells/welllogs");
        var keys = (await ledger.ListKeysAsync(logs, new RecordQuery(), 10)).Take(2).Select(k => k.Value).ToList();

        // A source of several interfaces removes in one interface's ledger, which the run has to name.
        var nameless = await RunAsync(engine, source, DeliveryOperations.Remove, new DeliveryRunPayload { Filter = new RecordQuery(), Scope = RemovalChoice.Record });
        Assert.False(nameless.Success);
        Assert.Contains("a removal works in one interface's ledger, which 'interface' names in its payload", nameless.Error, StringComparison.Ordinal);
        Assert.Empty(_protocols["welllogs"].Deletes);

        var result = await RunAsync(engine, source, DeliveryOperations.Remove, new DeliveryRunPayload
        {
            Interface = "welllogs",
            RecordKeys = keys,
            Scope = RemovalChoice.Record,
            PurgeLedger = true,
        });

        Assert.True(result.Success, result.Error);
        var outcome = Assert.IsType<RemoveRunOutcome>(result.Result);
        Assert.Equal(("record", 2, 2, 2, 0), (outcome.Scope, outcome.Selected, outcome.Removed, outcome.Purged, outcome.Failed));
        Assert.Equal(2, outcome.RowsLoaded);
        Assert.Equal(2, _protocols["welllogs"].Deletes.Count);
        Assert.All(_protocols["welllogs"].Deletes, d => Assert.Equal(RemovalScope.Record, d.Scope));
        Assert.Empty(_protocols["wellbores"].Deletes);

        // What OSDU answered for is gone from the ledger, and the wellbores the well logs refer to are as they were.
        Assert.Equal(3, (await ledger.StatsAsync(logs, _clock.GetUtcNow().UtcDateTime)).Total);
        Assert.Equal(2, (await ledger.StatsAsync(FlowId.Of("wells/wellbores"), _clock.GetUtcNow().UtcDateTime)).Delivered);

        // The removal's activity names the run that made it, and the run's artifact says what it did, for the run page.
        var activity = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = logs, Kind = "delete" }));
        Assert.Equal(result.RunId, activity.RunId);
        Assert.Equal("completed", activity.Outcome);
        var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.RunDirectory!, "run.json"))).RootElement.GetProperty("result");
        Assert.Equal(("remove", 2L, 2), (artifact.GetProperty("operation").GetString(), artifact.GetProperty("rowsLoaded").GetInt64(), artifact.GetProperty("purged").GetInt32()));
    }

    [Fact]
    public async Task A_remove_run_ends_failed_when_osdu_refuses_a_record_and_still_says_what_became_of_each()
    {
        var source = Load(SourceYaml());
        var engine = Engine(await EstateAsync());
        Assert.True((await RunAsync(engine, source)).Success);
        var ledger = engine.Ledger!;
        var logs = FlowId.Of("wells/welllogs");
        var refused = (await ledger.GetRecordAsync(logs, (await ledger.ListKeysAsync(logs, new RecordQuery(), 10))[0]))!;
        _protocols["welllogs"].Refuse.Add(refused.TargetId!);

        // Every record the listing matches, resolved by the run itself.
        var result = await RunAsync(engine, source, DeliveryOperations.Remove, new DeliveryRunPayload
        {
            Interface = "welllogs",
            Filter = new RecordQuery { Status = RecordStatus.Delivered },
            Scope = RemovalChoice.Record,
            PurgeLedger = true,
        });

        Assert.False(result.Success);
        var outcome = Assert.IsType<RemoveRunOutcome>(result.Result);
        Assert.Equal((5, 4, 1, 4), (outcome.Selected, outcome.Removed, outcome.Failed, outcome.Purged));
        Assert.Equal(refused.DeliveryKey.Value, Assert.Single(outcome.Records, r => r.Outcome == "failed").DeliveryKey);
        Assert.Contains("1 of 5 record(s) of 'wells/welllogs' could not be removed from OSDU: ", result.Error, StringComparison.Ordinal);
        Assert.Contains("the fake target refuses this removal", result.Error, StringComparison.Ordinal);

        // The record OSDU refused stays in the ledger as it was; the others went from OSDU and from the ledger.
        Assert.Equal(RecordStatus.Delivered, (await ledger.GetRecordAsync(logs, refused.DeliveryKey))!.Status);
        Assert.Equal(1, (await ledger.StatsAsync(logs, _clock.GetUtcNow().UtcDateTime)).Total);
    }

    [Fact]
    public async Task The_preflight_refuses_interfaces_that_refer_to_each_other_until_after_says_which_goes_first()
    {
        // Both wellbore interfaces fill osdu.data.KickOffWellbore, which refers to wellbores: each could refer to the other's.
        var mappings = SidetrackMappings();
        var engine = Engine(await EstateAsync(archive: true));

        var refused = await RunAsync(engine, Load(SourceYaml(archive: true, wellboreMapping: "Sidetrack@1.0.0", archiveMapping: "Sidetrack@1.0.0", mappingsDirectory: mappings)));

        Assert.False(refused.Success);
        Assert.Contains("The preflight of 'wells' found 1 problem(s), so nothing was planned or sent", refused.Error, StringComparison.Ordinal);
        Assert.Contains("the order of the interfaces: The interfaces wellbores -> archive -> wellbores wait for each other", refused.Error, StringComparison.Ordinal);
        Assert.Contains("wellbores: osdu.data.KickOffWellbore refers to master-data--Wellbore, which archive delivers", refused.Error, StringComparison.Ordinal);
        Assert.Contains("Name the interface that waits for the other with after:", refused.Error, StringComparison.Ordinal);
        Assert.Empty(_protocols["wellbores"].Deliveries);
        Assert.Empty(_protocols["archive"].Deliveries);

        // Told which goes first, the run takes the archive, then the wellbores, then the well logs that refer to both.
        var ordered = await RunAsync(
            engine,
            Load(SourceYaml(archive: true, wellbores: "after: [archive]", wellboreMapping: "Sidetrack@1.0.0", archiveMapping: "Sidetrack@1.0.0", mappingsDirectory: mappings)));

        Assert.True(ordered.Success, ordered.Error);
        var outcome = Outcome(ordered);
        Assert.Equal(
            [("wellbores", 2), ("welllogs", 3), ("archive", 1)],
            outcome.Interfaces.Select(i => (i.Interface, i.Wave)).ToArray());
        Assert.Equal(["archive"], outcome.Interfaces[0].WaitsFor);
        Assert.Empty(outcome.Interfaces[2].WaitsFor);
        Assert.Equal(["wellbores", "archive"], outcome.Interfaces[1].WaitsFor);
        Assert.All(outcome.Interfaces, i => Assert.Equal(InterfaceStates.Completed, i.State));
    }

    /// <summary>
    /// The sample mappings, plus Sidetrack@1.0.0: the sample wellbore mapping that also fills osdu.data.KickOffWellbore from
    /// an optional column, so the wellbores it renders may refer to other wellbores.
    /// </summary>
    private string SidetrackMappings()
    {
        var directory = Path.Combine(_root, "sidetrack-mappings");
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(Samples.FixtureMappings, "*.yaml"))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        }

        var wellbore = File.ReadAllText(Path.Combine(Samples.FixtureMappings, "Wellbore@1.0.0.yaml")).ReplaceLineEndings("\n");
        const string Anchor = "\n    # Alternative names:";
        Assert.Contains(Anchor, wellbore, StringComparison.Ordinal);
        var sidetrack = wellbore
            .Replace("\nname: Wellbore\n", "\nname: Sidetrack\n", StringComparison.Ordinal)
            .Replace(
                Anchor,
                "\n    KickOffWellbore: { $from: kickoff_wellbore, $required: false }\n" + Anchor,
                StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(directory, "Sidetrack@1.0.0.yaml"), sidetrack);
        return directory;
    }

    [Fact]
    public async Task A_run_of_one_interface_answers_as_a_run_of_that_flow_alone()
    {
        var source = Load(SourceYaml());
        var engine = Engine(await EstateAsync());

        var wellbores = await RunAsync(engine, source, payload: new DeliveryRunPayload { Interface = "wellbores" });
        Assert.True(wellbores.Success, wellbores.Error);
        var delivered = Assert.IsType<DeliverOutcome>(wellbores.Result);
        Assert.Equal(2, delivered.Delivered);
        Assert.Empty(_protocols["welllogs"].Deliveries);

        // A plan of the whole source reports each interface's plan.
        var plan = await RunAsync(engine, source, DeliveryOperations.Plan);
        Assert.True(plan.Success, plan.Error);
        var planned = Outcome(plan);
        Assert.Equal(0, Assert.IsType<PlanOutcome>(planned.Interfaces[0].Result).Deliveries);
        Assert.Equal(5, Assert.IsType<PlanOutcome>(planned.Interfaces[1].Result).Deliveries);

        // Records belong to one interface: a run on them without its name is refused, with it they are redelivered.
        var key = SampleEstate.Key(0);
        var nameless = await RunAsync(engine, source, payload: new DeliveryRunPayload { RecordKeys = [key.Value] });
        Assert.False(nameless.Success);
        Assert.Contains("a run on records or slices names the interface they belong to with 'interface' in its payload", nameless.Error, StringComparison.Ordinal);

        var logs = await RunAsync(engine, source, payload: new DeliveryRunPayload { Interface = "welllogs" });
        Assert.True(logs.Success, logs.Error);
        var redelivered = await RunAsync(engine, source, payload: new DeliveryRunPayload { Interface = "welllogs", RecordKeys = [key.Value], Redeliver = RedeliverScopes.Metadata });
        Assert.True(redelivered.Success, redelivered.Error);
        Assert.Equal(1, Assert.IsType<DeliverOutcome>(redelivered.Result).Delivered);

        // A submission names its interface by the ledger it belongs to.
        var submission = Assert.IsType<DeliverOutcome>(logs.Result).SubmissionId;
        var verify = await RunAsync(engine, source, DeliveryOperations.Drain, new DeliveryRunPayload { SubmissionId = submission });
        Assert.True(verify.Success, verify.Error);
        Assert.IsType<DrainOutcome>(verify.Result);

        var elsewhere = await RunAsync(
            engine, source, DeliveryOperations.Drain, new DeliveryRunPayload { SubmissionId = submission, Interface = "wellbores" });
        Assert.False(elsewhere.Success);
        // The submission names the partition its ledger is kept under; a flow whose partition is its header's is told no partition to run in.
        Assert.Contains(
            $"Submission {submission:D} belongs to flow 'wells/welllogs' in partition '{Samples.SampleCacheScope}', not 'wells/wellbores'.", elsewhere.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Run it in partition", elsewhere.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fan_out_member_is_told_which_interface_it_works_on()
    {
        var yaml = SourceYaml().Replace("  interfacesAtOnce: 1\n", "  interfacesAtOnce: 1\n  nodes: 2\n  minRowsPerNode: 1\n  recordsPerBatch: 1\n", StringComparison.Ordinal);
        var source = Load(yaml);
        var estate = await EstateAsync();
        var engine = Engine(estate);
        var members = new MemberRuns(engine, source);

        var result = await RunAsync(engine, source, payload: new DeliveryRunPayload { Interfaces = ["welllogs"] }, fanOut: members);

        Assert.True(result.Success, result.Error);
        Assert.NotEmpty(members.Enqueued);
        Assert.All(members.Enqueued, member => Assert.Equal("welllogs", member.Payload.Interface));
        Assert.All(members.Enqueued, member => Assert.Equal(DeliveryOperations.Deliver, member.Operation));
        Assert.All(members.Enqueued, member => Assert.NotEmpty(member.Payload.Slices));

        // Two parts: each sends under half the flow's rate limit, taken from its payload by the executor that runs it.
        Assert.All(members.Enqueued, member => Assert.Equal(2, member.Payload.RateShare));
        Assert.Equal(5, (await engine.Ledger!.StatsAsync(FlowId.Of("wells/welllogs"), _clock.GetUtcNow().UtcDateTime)).Delivered);
    }

    [Fact]
    public async Task A_flow_in_the_single_form_stops_on_an_outage_and_hands_the_rest_of_its_work_back()
    {
        var local = Samples.LocalFlow(_root);
        var flow = local with { FailWhen = new FlowFailWhen { OutageFailures = 2 } };
        var tables = await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime, time: _clock);
        var engine = Engine(tables);
        _protocols[string.Empty].FailWith = _ => new DeliveryException("HTTP transport failure calling PUT /records: connection refused", new HttpRequestException("refused"));

        var result = await RunAsync(engine, SourceDefinition.Of(Samples.InFolder(flow, _root)));

        Assert.False(result.Success);
        Assert.IsType<OperationFailure>(result.Result);
        Assert.Contains("'welldb-welllog-03-header-delivery' stopped: an outage: 2 records in a row could not reach the service", result.Error, StringComparison.Ordinal);
        Assert.Contains("the next run carries on from there", result.Error, StringComparison.Ordinal);

        // The two tries that failed are charged; the records the stop reached before they were sent are handed back untried.
        var records = await engine.Ledger!.ListAsync(flow.Id, new RecordQuery { Max = 10 });
        Assert.Equal(5, records.Count);
        Assert.All(records, r => Assert.Equal(RecordStatus.Pending, r.Status));
        Assert.Equal(2, records.Count(r => r.AttemptCount == 1));
        Assert.Equal(3, records.Count(r => r.AttemptCount == 0));
        Assert.Equal(2, _protocols[string.Empty].Deliveries.Count);
    }

    [Fact]
    public async Task Nothing_is_sent_after_a_stop_however_the_freed_slot_is_handed_on()
    {
        // The slot a stopping delivery frees can be handed to the next record at the moment the stop is signalled; the
        // stop wins every time, so the record behind it is handed back untried.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var db = new OsduTestDatabase();
            var root = Path.Combine(_root, "attempt-" + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var protocols = new InterfaceProtocols();
            protocols[string.Empty].FailWith = _ => new DeliveryException("HTTP transport failure", new HttpRequestException("refused"));
            var tables = await SampleEstate.BuildAsync(root, _clock.GetUtcNow().UtcDateTime, time: _clock);
            var engine = Samples.Engine(db.Ledger(_clock), _clock, protocols: protocols, sources: tables);
            var flow = Samples.InFolder(Samples.LocalFlow(root) with { FailWhen = new FlowFailWhen { OutageFailures = 2 } }, root);

            var result = await RunAsync(engine, SourceDefinition.Of(flow));

            Assert.False(result.Success);
            Assert.Equal(2, protocols[string.Empty].Deliveries.Count);
            Assert.Equal(1, protocols[string.Empty].MaxInFlight);
        }
    }

    /// <summary>Member runs executed inline through the executor, as another node would run them.</summary>
    private sealed class MemberRuns(EngineContext engine, SourceDefinition source) : IRunFanOut
    {
        private readonly Dictionary<Guid, RunFanOutMember> _members = [];

        public List<(string Operation, DeliveryRunPayload Payload)> Enqueued { get; } = [];

        public async Task<RunFanOutHandle> EnqueueAsync(IReadOnlyList<RunParameters> members, bool wholeNode, CancellationToken ct)
        {
            var ids = new List<Guid>();
            foreach (var member in members)
            {
                Enqueued.Add((DeliveryOperations.Of(member), DeliveryRunPayload.Parse(member)));
                using var provider = new ServiceCollection().AddSingleton(engine).AddSingleton<PartitionLedgers>().BuildServiceProvider();
                var runId = Guid.CreateVersion7();
                var result = await new DeliveryExecutor(provider).ExecuteAsync(
                    new DeliveryFlowDocument { Source = source },
                    source.SourcePath!,
                    new DocumentExecutionOptions { RunId = runId, Actor = "member", Parameters = member },
                    ct);
                ids.Add(runId);
                _members[runId] = new RunFanOutMember(
                    runId, ids.Count, result.Success ? "succeeded" : "failed", result.Error, JsonSerializer.Serialize(result.Result, result.Result.GetType(), Json));
            }

            return new RunFanOutHandle(Guid.NewGuid(), ids);
        }

        public Task<IReadOnlyList<RunFanOutMember>> MembersAsync(RunFanOutHandle handle, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RunFanOutMember>>(handle.RunIds.Select(id => _members[id]).ToList());

        public Task CancelAsync(RunFanOutHandle handle, CancellationToken ct) => Task.CompletedTask;
    }
}
