using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Engine.Workflows;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Restricting the records of a flow whose mapping's enrichment registers each log's LAS file as a dataset, linked from the log
/// (osdu/docs/reference/concepts/access-restriction.md, What a restriction reaches): the sample well logs delivered through the
/// Wellbore DDMS under the stand-in OSDU's platform root, each with its dataset registered through the Dataset service, the
/// module's database on SQL Server. A restriction replaces the ACL of the log and of its dataset in one metadata patch, the
/// ledger keeps the ACL each had, a dataset storage will not change keeps the log from being restricted and what was changed
/// is given back, and lifting gives each its own ACL back, the dataset of a log removed from OSDU too.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class StepRestrictionTests : IDisposable
{
    private const string Hold = "data.restricted.owners@dev.dataservices.energy";

    /// <summary>The Wellbore DDMS under the platform root, carrying the log's dataset lists over, as the well log estate's delivery does.</summary>
    private static readonly ProtocolOptions Ddms = new() { DdmsRoot = FakeOsduPlatform.DdmsRoot, PreserveDataKeys = ["Datasets", "DDMSDatasets"] };

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();
    private readonly string _las;
    private readonly string _mappings;
    private readonly FakeOsduPlatform _platform = new();
    private readonly HttpRuntime _http;

    public StepRestrictionTests()
    {
        _las = Directory.CreateDirectory(Path.Combine(_root, "las")).FullName;
        _mappings = Directory.CreateDirectory(Path.Combine(_root, "mappings")).FullName;
        EnrichmentSuite.WriteWellLogMapping(_mappings, "lasfile:\n  rows: one\n  mapping: WellLogLasDataset@1.0.0\n", data: "Datasets: [{ $link: lasfile }]");
        EnrichmentSuite.WriteEnrichmentMapping(_mappings, "WellLogLasDataset", EnrichmentSuite.DatasetKind, "tags:\n  LasFolder: { $from: las_folder }\n");
        _http = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), TimeProvider.System, _platform, allowLoopback: true);
    }

    public void Dispose()
    {
        _http.Dispose();
        _platform.Dispose();
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file a run still holds open is the temporary folder's to clean up.
        }
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task A_restriction_reaches_the_dataset_beside_the_log_and_lifting_gives_each_the_acl_it_had()
    {
        var (runtime, ledger) = await DeliveredAsync();
        using (runtime)
        {
            var logId = TargetOf(await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)));
            var dataset = DatasetOf(logId);
            var logAcl = Acl(logId);
            Assert.True(Acl(dataset).SameAs(logAcl));

            // The dataset was given viewers of its own since it was registered: lifting gives it those, not the log's.
            _platform.Records[dataset]["acl"]!["viewers"] = new JsonArray("data.welldb.viewers@dev.dataservices.energy", "data.lasreaders.viewers@dev.dataservices.energy");
            var datasetAcl = Acl(dataset);
            var restriction = await RegisterAsync("legal-hold");

            runtime.RunId = Guid.CreateVersion7();
            runtime.Actor = "user:steward";
            var summary = await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Restrict, restriction: "legal-hold");
            Assert.True(summary.Failed == 0, string.Join("; ", summary.Records.Select(r => r.Detail)));
            Assert.Equal(1, summary.Restricted);
            Assert.Contains("with the 1 record(s) its enrichments add beside it", Assert.Single(summary.Records).Detail, StringComparison.Ordinal);

            // One patch took the log and its dataset, and OSDU holds both under the restriction's groups.
            var patch = Assert.Single(_platform.AclPatches);
            Assert.Equal([logId, dataset], patch.Ids);
            Assert.True(Acl(logId).SameAs(restriction.Acl));
            Assert.True(Acl(dataset).SameAs(restriction.Acl));

            // The ledger keeps the ACL each had, the dataset's its own.
            var record = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            var kept = RestrictedAccess.Parse(record!.RestrictedFromAcl!);
            Assert.True(kept.Record.SameAs(logAcl));
            var (keptId, keptAcl) = Assert.Single(kept.BesideAcls);
            Assert.Equal(dataset, keptId);
            Assert.True(keptAcl.SameAs(datasetAcl));
            Assert.Contains(dataset, (await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(0), 20)).First(a => a.Phase == AttemptPhases.Restrict).ResultJson!, StringComparison.Ordinal);

            // A log without a LAS file has nothing beside it, and is restricted alone.
            var other = TargetOf(await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(2)));
            Assert.Equal(1, (await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(2)]), RemovalChoice.Restrict, restriction: "legal-hold")).Restricted);
            Assert.Equal([other], _platform.AclPatches[^1].Ids);

            // Lifting gives the log and its dataset the ACL each had, in one patch per ACL.
            var before = _platform.AclPatches.Count;
            var lifted = await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0), SampleEstate.Key(2)]), RemovalChoice.Unrestrict);
            Assert.Equal((2, 0), (lifted.Lifted, lifted.Failed));
            Assert.Contains(lifted.Records, r => r.Detail.Contains("the 1 record(s) beside it were given back their access", StringComparison.Ordinal));
            Assert.True(Acl(logId).SameAs(logAcl));
            Assert.True(Acl(dataset).SameAs(datasetAcl));
            Assert.Equal(2, _platform.AclPatches.Count - before);
            Assert.Null((await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)))!.RestrictedFromAcl);
        }
    }

    [Fact]
    public async Task A_log_whose_acl_names_more_groups_than_four_thousand_characters_hold_is_restricted_and_given_every_group_back()
    {
        var (runtime, ledger) = await DeliveredAsync();
        using (runtime)
        {
            // A log's mapping gives it the flow's group and every group of its field and its country: here a hundred.
            var logId = TargetOf(await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(2)));
            _platform.Records[logId]["acl"]!["viewers"] = new JsonArray([.. Enumerable.Range(1, 100).Select(n => (JsonNode?)$"data.field-{n:D3}.access.viewers@dev.dataservices.energy")]);
            var logAcl = Acl(logId);
            Assert.True(logAcl.ToJson().Length > 4000);
            await RegisterAsync("legal-hold");

            var summary = await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(2)]), RemovalChoice.Restrict, restriction: "legal-hold");
            Assert.True(summary.Restricted == 1, summary.Describe());
            var kept = (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(2)))!.RestrictedFromAcl!;
            Assert.True(kept.Length > 4000);
            Assert.True(RestrictedAccess.Parse(kept).Record.SameAs(logAcl));

            Assert.Equal(1, (await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(2)]), RemovalChoice.Unrestrict)).Lifted);
            Assert.True(Acl(logId).SameAs(logAcl));
        }
    }

    [Fact]
    public async Task A_dataset_the_flow_may_not_change_keeps_the_log_from_being_restricted_and_what_was_changed_is_given_back()
    {
        var (runtime, ledger) = await DeliveredAsync();
        using (runtime)
        {
            var logId = TargetOf(await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)));
            var dataset = DatasetOf(logId);
            var logAcl = Acl(logId);

            // Someone else owns the dataset now, so storage will not let the flow change it.
            _platform.Records[dataset]["acl"]!["owners"] = new JsonArray("data.someone-else@dev.dataservices.energy");
            var datasetAcl = Acl(dataset);
            await RegisterAsync("legal-hold");

            var summary = await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Restrict, restriction: "legal-hold");
            Assert.Equal((0, 1), (summary.Restricted, summary.Failed));
            var detail = Assert.Single(summary.Records).Detail;
            Assert.Contains("OSDU refused the restriction for part of the record", detail, StringComparison.Ordinal);
            Assert.Contains(dataset, detail, StringComparison.Ordinal);

            // The log was given its ACL back at once, the dataset was never changed, and the record follows its source again.
            Assert.Equal(2, _platform.AclPatches.Count);
            Assert.Equal([logId], _platform.AclPatches[1].Ids);
            Assert.True(Acl(logId).SameAs(logAcl));
            Assert.True(Acl(dataset).SameAs(datasetAcl));
            var record = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal((null, null), (record!.RestrictionId, record.RestrictedFromAcl));
            Assert.NotNull(record.PlanRequestedUtc);
        }
    }

    [Fact]
    public async Task A_log_or_dataset_storage_withholds_from_the_flow_fails_unchanged_and_a_skipped_log_is_named_with_why()
    {
        var (runtime, ledger) = await DeliveredAsync();
        using (runtime)
        {
            var logId = TargetOf(await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)));
            var dataset = DatasetOf(logId);
            await RegisterAsync("legal-hold");

            // Storage leaves a record the flow's identity may not read out of every list of its answer: the dataset first.
            _platform.ReadWithheld.Add(dataset);
            var summary = await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Restrict, restriction: "legal-hold");
            Assert.Equal((0, 0, 1), (summary.Restricted, summary.Skipped, summary.Failed));
            var detail = Assert.Single(summary.Records).Detail;
            Assert.Contains("1 of the 1 record(s) its enrichments add beside it", detail, StringComparison.Ordinal);
            Assert.Contains("may not read", detail, StringComparison.Ordinal);
            Assert.Contains("add the flow's identity to an owner group", detail, StringComparison.Ordinal);
            Assert.Empty(_platform.AclPatches);
            var record = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal((null, null), (record!.RestrictionId, record.RestrictedFromAcl));

            // Then the log itself, and then a log storage did not answer for.
            _platform.ReadWithheld.Clear();
            _platform.ReadWithheld.Add(logId);
            detail = Assert.Single((await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Restrict, restriction: "legal-hold")).Records).Detail;
            Assert.StartsWith("OSDU did not say which ACL the record holds (storage answered without it, as it does a record the caller may not read", detail, StringComparison.Ordinal);
            _platform.ReadWithheld.Clear();
            _platform.ReadRetried.Add(logId);
            detail = Assert.Single((await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Restrict, restriction: "legal-hold")).Records).Detail;
            Assert.Contains("retryRecords", detail, StringComparison.Ordinal);
            Assert.EndsWith("ask again once storage answers for it", detail, StringComparison.Ordinal);
            Assert.Empty(_platform.AclPatches);

            // A log removed outside the flow is skipped, and the line the run log and the record's history carry says why.
            _platform.ReadRetried.Clear();
            _platform.Removed.Add(logId);
            summary = await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Restrict, restriction: "legal-hold");
            Assert.Equal((0, 1, 0), (summary.Restricted, summary.Skipped, summary.Failed));
            var line = summary.Describe();
            Assert.Contains("1 skipped, 0 failed; skipped ", line, StringComparison.Ordinal);
            Assert.Contains("storage names it under invalidRecords", line, StringComparison.Ordinal);
            Assert.Contains(record.Label ?? record.SourceKey!, line, StringComparison.Ordinal);
            Assert.Empty(_platform.AclPatches);
        }
    }

    [Fact]
    public async Task Lifting_the_restriction_of_a_log_removed_from_osdu_gives_the_dataset_beside_it_its_access_back()
    {
        var (runtime, ledger) = await DeliveredAsync();
        using (runtime)
        {
            var logId = TargetOf(await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)));
            var dataset = DatasetOf(logId);
            var datasetAcl = Acl(dataset);
            await RegisterAsync("legal-hold");
            Assert.Equal(1, (await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Restrict, restriction: "legal-hold")).Restricted);

            // Removing the log alone leaves its dataset in OSDU, still under the restriction's groups.
            Assert.Equal(1, (await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Record)).Removed);
            Assert.Contains(logId, _platform.Removed);
            Assert.DoesNotContain(dataset, _platform.Removed);

            var lifted = await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Unrestrict);
            Assert.Equal(1, lifted.Lifted);
            Assert.Contains("nothing was asked of it, and the 1 record(s) beside it were given back their access", Assert.Single(lifted.Records).Detail, StringComparison.Ordinal);
            Assert.Equal([dataset], _platform.AclPatches[^1].Ids);
            Assert.True(Acl(dataset).SameAs(datasetAcl));
        }
    }

    /// <summary>The sample estate, two logs with a LAS file each and the rest without, delivered once.</summary>
    private async Task<(FlowRuntime Runtime, OsduLedger Ledger)> DeliveredAsync()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        Assert.True(tables.Records.Count >= 3, "The sample estate holds too few logs for this scenario.");
        LasFile(tables.Records[0], "~Version Information A");
        LasFile(tables.Records[1], "~Version Information B");
        var ledger = _db.Ledger(_clock);
        var client = new OsduHttpClient(
            _http, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        var options = new ProtocolOptions { UploadHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-ms-blob-type"] = "BlockBlob" } };
        var route = new EnrichPartsProtocol(new OsduDdmsProtocol(client, Ddms, NullLogger.Instance), client, options);
        var templates = new MemoryTemplates(Samples.TemplateKinds().Select(Samples.SampleTemplate));
        var engine = Samples.Engine(ledger, _clock, templates: templates, sources: tables) with { Protocols = new FakeProtocolFactory(route) };
        var runtime = await FlowRuntime.CreateAsync(engine, Flow(), SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);

        runtime.RunId = Guid.CreateVersion7();
        runtime.Actor = "schedule:test";
        runtime.Selection = SourceSelection.Full();
        var run = await runtime.RunAsync(force: false);
        Assert.Equal(tables.Records.Count, run.Work.Delivered);
        _clock.Advance(TimeSpan.FromMinutes(1));
        return (runtime, ledger);
    }

    /// <summary>The sample flow over the Wellbore DDMS under the platform root, its logs' LAS files read beside them.</summary>
    private FlowDefinition Flow()
    {
        var flow = Samples.LocalFlow(_root);
        var datasets = new Dictionary<string, FlowSourceDataset>(flow.Source.Datasets, StringComparer.Ordinal)
        {
            ["lasfile"] = new FlowSourceDataset
            {
                Object = "OsduData.silver.WellLogFile",
                Join = new Dictionary<string, string>(StringComparer.Ordinal) { ["source_project"] = "source_project", ["log_id"] = "log_id" },
                Files = new FlowPayload { Root = _las, LocationColumn = "las_folder", Pattern = "*.las", HashColumn = "las_hash", ContentType = "text/plain" },
            },
        };
        return flow with
        {
            Source = flow.Source with { Datasets = datasets, Payloads = new Dictionary<string, FlowPayload>(StringComparer.Ordinal) },
            Render = flow.Render with { MappingsDirectory = _mappings },
            Target = flow.Target with
            {
                Endpoint = FakeOsduPlatform.Endpoint,
                Auth = new TargetAuth { Type = TargetAuthType.None },
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" },
                Protocol = DeliveryProtocol.Ddms,
                ProtocolOptions = Ddms,
            },
            Reliability = flow.Reliability with { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
        };
    }

    /// <summary>Gives a log its LAS file, in a folder of its own with the content given.</summary>
    private void LasFile(MemoryRecord record, string content)
    {
        var folder = $"{record.Row["source_project"]}-{record.Row["log_id"]}";
        var path = Directory.CreateDirectory(Path.Combine(_las, folder)).FullName;
        File.WriteAllText(Path.Combine(path, "log.las"), content);
        record.AddChild("lasfile", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["source_project"] = record.Row["source_project"],
            ["log_id"] = record.Row["log_id"],
            ["las_folder"] = folder,
            ["las_hash"] = Hashing.ContentHash.Of(content),
        });
        record.DatasetUpdatedUtc["lasfile"] = Now;
    }

    private async Task<AccessRestrictionState> RegisterAsync(string name)
    {
        await using var db = _db.CreateDbContext();
        var row = new DeliveryAccessRestriction
        {
            Partition = "dev",
            Name = name,
            Viewers = AccessRestrictionState.GroupsJson([Hold]),
            Owners = AccessRestrictionState.GroupsJson([Hold]),
            CreatedUtc = Now,
            CreatedBy = "user:admin",
            UpdatedUtc = Now,
            UpdatedBy = "user:admin",
        };
        db.DeliveryAccessRestrictions.Add(row);
        await db.SaveChangesAsync();
        return AccessRestrictionState.Of(row);
    }

    private static string TargetOf(RecordState? record) => record?.TargetId ?? throw new InvalidOperationException("The record was not delivered.");

    private static string DatasetOf(string recordId) => WorkflowValues.DerivedDatasetId(recordId, "dataset--File.Generic", "enrich.lasfile");

    private RecordAcl Acl(string id) => RecordAcl.OfRecord(_platform.Records[id]) ?? throw new InvalidOperationException($"{id} carries no ACL");
}
