using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Storage;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A source that delivers directional surveys and well logs through the one ddms route
/// (osdu/docs/reference/flow/ddms.md, Where a record goes): the executor runs both interfaces with the real protocol
/// against a fake Wellbore DDMS, each record goes to the collection serving its entity type, a survey whose stations
/// its record does not describe is held before anything is sent, and every request keeps to the pinned contracts.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DdmsSourceTests : IDisposable
{
    private const string TrajectoryTable = "OsduData.silver.WellboreTrajectory";
    private const string WellLogTable = "OsduData.silver.WellLog";
    private const string Root = "/api/os-wellbore-ddms";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 1, 7, 0, 0, TimeSpan.Zero));
    private readonly string _root = Samples.NewTempDirectory();

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

    private string SourceYaml()
    {
        var root = _root.Replace('\\', '/');
        var mappings = Samples.FixtureMappings.Replace('\\', '/');
        return ($$"""
            flowType: delivery
            name: surveys
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
              endpoint: http://localhost
              headers:
                data-partition-id: {{Samples.SampleCacheScope}}
              ddms:
                wellbore:
                  root: {{Root}}
            reliability:
              retry: { attempts: 1 }
            parallelism:
              concurrency: 1
              renderers: 1
              interfaces: 1
            interfaces:
              trajectories:
                record: { object: {{TrajectoryTable}}, key: [source_project, survey_id], primaryKey: RecId }
                datasets:
                  stations: { object: OsduData.silver.WellboreTrajectoryStation, join: { source_project: source_project, survey_id: survey_id }, orderBy: [station_ordinal] }
                bulk: { root: '{{root}}/stations', locationColumn: station_folder, pattern: "chunk_*.parquet", hashColumn: payload_hash, chunkCountColumn: chunk_count }
                mapping: WellboreTrajectory@1.0.0
              welllogs:
                record: { object: {{WellLogTable}}, key: [source_project, log_id], primaryKey: RecId, scope: { log_source: logSource } }
                datasets:
                  curves: { object: OsduData.silver.WellLogCurve, join: { source_project: source_project, log_id: log_id }, orderBy: [curve_ordinal] }
                bulk: { root: '{{root}}/curves', locationColumn: curve_folder, pattern: "chunk_*.parquet", hashColumn: payload_hash, chunkCountColumn: chunk_count }
                mapping: WellLog@1.4.0
            """).ReplaceLineEndings("\n");
    }

    private SourceDefinition Load()
    {
        var path = Path.Combine(_root, "flows", "surveys.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, SourceYaml());
        return new DeliveryDocumentLoader().LoadSource(path);
    }

    /// <summary>
    /// One survey as a row of the trajectory table, with a row per station property its record describes, and its
    /// stations as a parquet file carrying <paramref name="columns"/>, measured depth first.
    /// </summary>
    private async Task<MemoryRecord> SurveyAsync(long rowNumber, string surveyId, string wellbore, IReadOnlyList<(string Name, string Type, string Unit)> stations, IReadOnlyList<string> columns)
    {
        var directory = Path.Combine(_root, "stations", "PROJECT_A", surveyId);
        Directory.CreateDirectory(directory);
        var rows = Enumerable.Range(0, 5)
            .Select(i => (IReadOnlyDictionary<string, object?>)columns.ToDictionary(c => c, c => (object?)(c == "MD" ? 1000.0 + i : 10.0 + i), StringComparer.Ordinal))
            .ToList();
        var names = columns.Select(c => (c, typeof(double))).ToList();
        var metadata = PandasMetadata.Range(names, 0, 5);
        await using (var file = File.Create(Path.Combine(directory, "chunk_0000.parquet")))
        {
            await ParquetFiles.WriteAsync(file, names, rows, metadata);
        }

        var updated = _clock.GetUtcNow().UtcDateTime;
        var record = new MemoryRecord
        {
            Row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["RecId"] = rowNumber,
                ["source_project"] = "PROJECT_A",
                ["survey_id"] = surveyId,
                ["wellbore_uwi"] = wellbore,
                ["survey_name"] = "GYRO_2026",
                ["survey_type"] = "Gyro",
                ["survey_version"] = "1",
                ["top_md"] = "1000",
                ["base_md"] = "1004",
                ["elev_meas_ref"] = "23.5 M",
                ["update_date"] = updated,
                ["station_folder"] = "PROJECT_A/" + surveyId,
                ["payload_hash"] = "stations-" + surveyId,
                ["chunk_count"] = 1L,
            },
            UpdatedUtc = updated,
            FileName = "trajectory_20260901.csv",
            RowNumber = rowNumber,
        };

        for (var i = 0; i < stations.Count; i++)
        {
            record.AddChild("stations", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["source_project"] = "PROJECT_A",
                ["survey_id"] = surveyId,
                ["station_ordinal"] = (long)i,
                ["station_property"] = stations[i].Name,
                ["property_type"] = stations[i].Type,
                ["property_unit"] = stations[i].Unit,
            });
        }

        record.DatasetUpdatedUtc["stations"] = updated;
        return record;
    }

    private static async Task<DocumentExecutionResult> RunAsync(EngineContext engine, SourceDefinition source)
    {
        using var provider = new ServiceCollection().AddSingleton(engine).AddSingleton<PartitionLedgers>().BuildServiceProvider();
        return await new DeliveryExecutor(provider).ExecuteAsync(
            new DeliveryFlowDocument { Source = source },
            source.SourcePath!,
            new DocumentExecutionOptions
            {
                RunId = Guid.CreateVersion7(),
                Actor = "test",
                Parameters = new RunParameters
                {
                    Operation = DeliveryOperations.Deliver,
                    Values = new Dictionary<string, string>(SampleEstate.Values, StringComparer.Ordinal),
                },
            },
            CancellationToken.None);
    }

    /// <summary>A Wellbore DDMS and the legal service beside it, answering every call the route makes.</summary>
    private static FakeHttpHandler WellboreDdms()
    {
        var versions = 0;
        return new FakeHttpHandler()
            .On(HttpMethod.Post, "/api/legal/v1/legaltags:validate", HttpStatusCode.OK, """{"invalidLegalTags":[]}""")
            .On(HttpMethod.Get, Root + "/about", HttpStatusCode.OK, """{"service":"Wellbore DDMS"}""")
            .OnMatch(
                r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/data", StringComparison.Ordinal),
                _ => FakeHttpHandler.Json(HttpStatusCode.OK, "{}"))
            .OnMatch(
                r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.StartsWith(Root + "/ddms/v3/", StringComparison.Ordinal),
                _ => FakeHttpHandler.Json(HttpStatusCode.OK, "{\"recordIdVersions\":[\"written:" + (++versions).ToString(CultureInfo.InvariantCulture) + "\"]}"))
            .OnMatch(
                r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.StartsWith(Root + "/ddms/v3/", StringComparison.Ordinal),
                _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"version":1700000000000001}"""));
    }

    [Fact]
    public async Task A_source_delivers_surveys_and_well_logs_through_the_ddms_route_each_to_its_own_collection()
    {
        var trajectories = new MemoryIngestionTables(_clock);
        trajectories.Add(await SurveyAsync(1, "T-1001", "OSDU-DEV-1-A", [("MD", "MD", "M"), ("INC", "Inclination", "DEG"), ("AZI", "AzimuthTN", "DEG")], ["MD", "INC", "AZI"]));

        // This survey's file carries an azimuth its record does not describe; the DDMS would refuse its stations.
        trajectories.Add(await SurveyAsync(2, "T-1002", "OSDU-DEV-1-B", [("MD", "MD", "M"), ("INC", "Inclination", "DEG")], ["MD", "INC", "AZI"]));
        var estate = new MemoryEstate()
            .Add(TrajectoryTable, trajectories)
            .Add(WellLogTable, await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime, time: _clock));
        var handler = WellboreDdms();
        using var protocols = new FakeOsduProtocols(handler);
        var engine = Samples.Engine(_db.Ledger(_clock), _clock, protocols: protocols, sources: estate);
        var source = Load();

        var result = await RunAsync(engine, source);

        Assert.True(result.Success, result.Error);
        var outcome = Assert.IsType<SourceRunOutcome>(result.Result);
        Assert.Equal(["trajectories", "welllogs"], outcome.Interfaces.Select(i => i.Interface));
        Assert.All(outcome.Interfaces, i => Assert.Equal(("ddms", InterfaceStates.Completed), (i.Route, i.State)));
        var surveys = Assert.IsType<DeliverOutcome>(outcome.Interfaces[0].Result);
        var logs = Assert.IsType<DeliverOutcome>(outcome.Interfaces[1].Result);
        var reasons = (await engine.Ledger!.GetRecordsAsync(
                FlowId.Of("surveys/trajectories"),
                [DeliveryKey.Derive("wells", ["PROJECT_A", "T-1001"]), DeliveryKey.Derive("wells", ["PROJECT_A", "T-1002"])]))
            .Values.Select(r => $"{r.SourceKey}: {r.Status} {r.LastError}");
        Assert.True((surveys.Delivered, surveys.Held) == (1L, 1L), string.Join(" | ", reasons));
        Assert.Equal(5, logs.Delivered);

        // Each record went to the collection serving its entity type, record first and its bulk data after it.
        string Call(FakeHttpHandler.Request c) => c.Method + " " + c.Uri.AbsolutePath;
        var surveyId = "dev:work-product-component--WellboreTrajectory:" + DeliveryKey.Derive("wells", ["PROJECT_A", "T-1001"]).Value.ToString("N");
        var trajectoryCalls = handler.Calls.Select(Call).Where(c => c.Contains("/wellboretrajectories", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            [
                "POST " + Root + "/ddms/v3/wellboretrajectories",
                "POST " + Root + "/ddms/v3/wellboretrajectories/" + surveyId + "/data",
                "GET " + Root + "/ddms/v3/wellboretrajectories/" + surveyId,
            ],
            trajectoryCalls);
        Assert.Equal(5, handler.Calls.Count(c => Call(c) == "POST " + Root + "/ddms/v3/welllogs"));
        Assert.Equal(5, handler.Calls.Count(c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath.StartsWith(Root + "/ddms/v3/welllogs/", StringComparison.Ordinal)));

        // The survey's record names the stations its bulk data carries, each typed from the partition's cache.
        var written = JsonNode.Parse(handler.Calls.Single(c => Call(c) == "POST " + Root + "/ddms/v3/wellboretrajectories").Body!)!.AsArray().Single()!;
        Assert.Equal(surveyId, (string?)written["id"]);
        Assert.Equal(
            ["MD", "INC", "AZI"],
            written["data"]!["AvailableTrajectoryStationProperties"]!.AsArray().Select(s => (string?)s!["Name"]));
        Assert.Equal(
            "dev:reference-data--TrajectoryStationPropertyType:MD:",
            (string?)written["data"]!["AvailableTrajectoryStationProperties"]![0]!["TrajectoryStationPropertyTypeID"]);

        // Every call keeps to its contract.
        OsduContracts.AssertConform(handler.Calls, null, OsduContracts.WellboreDdms, OsduContracts.Legal);

        // The held survey says why, in the ledger of its interface.
        var ledger = engine.Ledger!;
        var trajectoryLedger = FlowId.Of("surveys/trajectories");
        var stats = await ledger.StatsAsync(trajectoryLedger, _clock.GetUtcNow().UtcDateTime);
        Assert.Equal((1L, 1L), (stats.Delivered, stats.Held));
        var heldKey = DeliveryKey.Derive("wells", ["PROJECT_A", "T-1002"]);
        var held = (await ledger.GetRecordsAsync(trajectoryLedger, [heldKey]))[heldKey];
        Assert.Contains("the bulk column(s) AZI match no data.AvailableTrajectoryStationProperties[].Name", held.LastError, StringComparison.Ordinal);
        Assert.Equal(5, (await ledger.StatsAsync(FlowId.Of("surveys/welllogs"), _clock.GetUtcNow().UtcDateTime)).Delivered);
    }
}
