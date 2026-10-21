using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// One input, two OSDU pipelines on different versions of one schema. The sample well log rows (and the curve files they
/// point at) are loaded once into the ingestion tables. The sample flow delivers them as WellLog 1.4.0; a second flow
/// renders the very same rows with the very same mapping entries as WellLog 1.5.0, the next version the Open Group
/// publishes (saved from the data definitions as a fixture, Fixtures/templates). Each pipeline keeps its own ledger over the
/// shared rows. Two versions of one kind name the same OSDU record for a row in a partition, so the second pipeline delivers
/// to a partition of its own; in the first pipeline's partition it is held, and the way to move that partition to the new
/// version is the first pipeline's own mapping, which keeps each record's OSDU id and history.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class SchemaVersionPipelinesTests : IDisposable
{
    /// <summary>The partition the WellLog 1.5.0 pipeline delivers to, beside the sample partition the 1.4.0 pipeline uses.</summary>
    private const string NextPartition = "dev-next";

    private const string CurrentFlowName = "welldb-welllog-03-header-delivery";

    private const string NextFlowName = "wells-welllog-next";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task One_input_feeds_two_pipelines_on_two_versions_of_the_schema_and_each_keeps_its_own_ledger()
    {
        var estate = await EstateAsync();
        using var current = await PipelineAsync(estate, Samples.LocalFlow(_root));
        using var next = await PipelineAsync(estate, NextFlow(NextFlowName, NextPartition));
        Assert.Equal(CurrentFlowName, current.Runtime.Flow.Name);

        var (currentRun, currentSubmission) = await RunAsync(current, estate.Ledger);
        var (nextRun, nextSubmission) = await RunAsync(next, estate.Ledger);
        Assert.Equal((SampleEstate.Logs().Count, SampleEstate.Logs().Count), (currentRun.Delivered, nextRun.Delivered));

        // The same rows, each sent twice: as WellLog 1.4.0 into the sample partition and as WellLog 1.5.0 into the
        // next one, with the same curve files and, through each partition's own cache and its own search, each
        // partition's own references.
        for (var i = 0; i < SampleEstate.Logs().Count; i++)
        {
            var key = SampleEstate.Key(i);
            var sentCurrent = Assert.Single(current.Protocol.Deliveries, w => w.Key == key);
            var sentNext = Assert.Single(next.Protocol.Deliveries, w => w.Key == key);
            Assert.Equal((WellLogVersions.CurrentKind, WellLogVersions.NextKind), (Text(sentCurrent.Document, "kind"), Text(sentNext.Document, "kind")));
            Assert.Equal(TargetId.Compose(Samples.SamplePartition, "work-product-component--WellLog", key), sentCurrent.TargetId);
            Assert.Equal(TargetId.Compose(NextPartition, "work-product-component--WellLog", key), sentNext.TargetId);
            Assert.StartsWith(Samples.SamplePartition + ":master-data--Wellbore:", Text(sentCurrent.Document["data"], "WellboreID"), StringComparison.Ordinal);
            Assert.StartsWith(NextPartition + ":master-data--Wellbore:", Text(sentNext.Document["data"], "WellboreID"), StringComparison.Ordinal);
            Assert.Equal(Text(sentCurrent.Document["data"], "Name"), Text(sentNext.Document["data"], "Name"));
            Assert.True(sentCurrent.DeliverPayload && sentNext.DeliverPayload);

            // Each pipeline's record says which schema version, mapping and partition cache built what it delivered, and
            // both name the same ingestion file and row.
            var currentRecord = await estate.Ledger.GetRecordAsync(current.Runtime.Flow.Id, key);
            var nextRecord = await estate.Ledger.GetRecordAsync(next.Runtime.Flow.Id, key);
            Assert.Equal((RecordStatus.Delivered, RecordStatus.Delivered), (currentRecord!.Status, nextRecord!.Status));
            Assert.Equal(("WellLog@1.4.0", Samples.SampleTemplate(WellLogVersions.CurrentKind).Version, Samples.SampleCacheScope), Rendered(currentRecord));
            Assert.Equal(("WellLog@1.5.0", WellLogVersions.NextTemplateVersion, NextPartition), Rendered(nextRecord));
            Assert.Equal((sentCurrent.TargetId, sentNext.TargetId), (currentRecord.ClaimedTargetId, nextRecord.ClaimedTargetId));
            Assert.Equal((SampleEstate.FileName, (long?)(i + 1)), (currentRecord.SourceFileName, currentRecord.SourceRowNumber));
            Assert.Equal((SampleEstate.FileName, (long?)(i + 1)), (nextRecord.SourceFileName, nextRecord.SourceRowNumber));
            Assert.Equal(currentSubmission, Assert.Single(await estate.Ledger.ListAttemptsAsync(current.Runtime.Flow.Id, key, 10)).SubmissionId);
            Assert.Equal(nextSubmission, Assert.Single(await estate.Ledger.ListAttemptsAsync(next.Runtime.Flow.Id, key, 10)).SubmissionId);
            Assert.Equal(2, (await estate.Ledger.LookupAsync(key.Value.ToString(), 10)).Count);
        }

        var submissions = (await estate.Ledger.GetSubmissionAsync(currentSubmission), await estate.Ledger.GetSubmissionAsync(nextSubmission));
        Assert.Equal(("WellLog@1.4.0", "WellLog@1.5.0"), (submissions.Item1!.MappingReference, submissions.Item2!.MappingReference));
        Assert.Equal(submissions.Item1.SourceObject, submissions.Item2.SourceObject);
        Assert.Equal(
            ((long)SampleEstate.Logs().Count, (long)SampleEstate.Logs().Count),
            ((await estate.Ledger.StatsAsync(current.Runtime.Flow.Id, Now)).Delivered, (await estate.Ledger.StatsAsync(next.Runtime.Flow.Id, Now)).Delivered));

        // A change to the one input reaches both pipelines, and each records it in its own history.
        current.Protocol.Deliveries.Clear();
        next.Protocol.Deliveries.Clear();
        _clock.Advance(TimeSpan.FromMinutes(10));
        SampleEstate.Change(estate.Tables.Records[0], "log_service", "HAL", Now, SampleWellLogs.UpdatedUtc(0).AddHours(2));
        Assert.Equal(1, (await RunAsync(current, estate.Ledger)).Work.Delivered);
        Assert.Equal(1, (await RunAsync(next, estate.Ledger)).Work.Delivered);
        Assert.Equal(("HAL", "HAL"), (Text(Assert.Single(current.Protocol.Deliveries).Document["data"], "ActivityType"), Text(Assert.Single(next.Protocol.Deliveries).Document["data"], "ActivityType")));
        Assert.Equal(2, (await estate.Ledger.ListAttemptsAsync(current.Runtime.Flow.Id, SampleEstate.Key(0), 10)).Count);
        Assert.Equal(2, (await estate.Ledger.ListAttemptsAsync(next.Runtime.Flow.Id, SampleEstate.Key(0), 10)).Count);

        // Taking the 1.5.0 records out of OSDU takes only those: the 1.4.0 records stay delivered where they are.
        next.Runtime.Actor = "gui:tahir";
        var removal = await next.Runtime.RemoveAsync(RemovalSelection.Of([.. Enumerable.Range(0, SampleEstate.Logs().Count).Select(SampleEstate.Key)]), RemovalChoice.Record);
        Assert.Equal(SampleEstate.Logs().Count, removal.Removed);
        Assert.All(next.Protocol.Deletes, d => Assert.StartsWith(NextPartition + ":", d.TargetId, StringComparison.Ordinal));
        Assert.Empty(current.Protocol.Deletes);
        Assert.Equal(SampleEstate.Logs().Count, (await estate.Ledger.StatsAsync(next.Runtime.Flow.Id, Now)).Deleted);
        Assert.Equal(SampleEstate.Logs().Count, (await estate.Ledger.StatsAsync(current.Runtime.Flow.Id, Now)).Delivered);
    }

    [Fact]
    public async Task A_second_version_of_the_kind_in_the_same_partition_is_held_because_it_names_the_same_osdu_records()
    {
        var estate = await EstateAsync();
        using var current = await PipelineAsync(estate, Samples.LocalFlow(_root));
        using var clash = await PipelineAsync(estate, NextFlow(NextFlowName, Samples.SampleCacheScope));
        Assert.Equal(SampleEstate.Logs().Count, (await RunAsync(current, estate.Ledger)).Work.Delivered);

        // WellLog 1.5.0 in the sample partition renders the ids the 1.4.0 pipeline owns: nothing is sent, and every record
        // is held with the owner named, so the reason is on the record rather than in OSDU as alternating kinds.
        var (run, submissionId) = await RunAsync(clash, estate.Ledger);
        Assert.Equal(0, run.Processed);
        Assert.Empty(clash.Protocol.Deliveries);
        Assert.Equal((long)SampleEstate.Logs().Count, (await estate.Ledger.GetSubmissionAsync(submissionId))!.Held);
        for (var i = 0; i < SampleEstate.Logs().Count; i++)
        {
            var held = await estate.Ledger.GetRecordAsync(clash.Runtime.Flow.Id, SampleEstate.Key(i));
            Assert.Equal((RecordStatus.Held, (string?)null), (held!.Status, held.ClaimedTargetId));
            Assert.Contains($"already claimed by flow '{CurrentFlowName}'", held.LastError, StringComparison.Ordinal);
            var owner = await estate.Ledger.GetRecordAsync(current.Runtime.Flow.Id, SampleEstate.Key(i));
            Assert.Equal(("WellLog@1.4.0", RecordStatus.Delivered), (Rendered(owner!).Mapping, owner!.Status));
        }
    }

    [Fact]
    public async Task Moving_a_pipeline_to_the_next_version_updates_its_records_in_place_and_keeps_their_history()
    {
        var estate = await EstateAsync();
        using var before = await PipelineAsync(estate, Samples.LocalFlow(_root));
        var (_, firstSubmission) = await RunAsync(before, estate.Ledger);

        // The same flow, pinned to the WellLog 1.5.0 mapping. Nothing changed in the input; the schema did, so every record
        // is rendered again, and only the document is sent: the same OSDU record gets a new version of the new kind.
        _clock.Advance(TimeSpan.FromMinutes(10));
        using var after = await PipelineAsync(estate, NextFlow(CurrentFlowName, Samples.SampleCacheScope));
        Assert.Equal(before.Runtime.Flow.Id, after.Runtime.Flow.Id);
        var (upgrade, secondSubmission) = await RunAsync(after, estate.Ledger);
        Assert.Equal(SampleEstate.Logs().Count, upgrade.Delivered);
        foreach (var sent in after.Protocol.Deliveries)
        {
            var earlier = Assert.Single(before.Protocol.Deliveries, w => w.Key == sent.Key);
            Assert.Equal((WellLogVersions.NextKind, earlier.TargetId), (Text(sent.Document, "kind"), sent.TargetId));
            Assert.True(sent.DeliverMetadata);
            Assert.False(sent.DeliverPayload);
            Assert.NotNull(sent.ExistingVersion);
        }

        // One record per row, whose history says which schema version each delivery carried.
        for (var i = 0; i < SampleEstate.Logs().Count; i++)
        {
            var key = SampleEstate.Key(i);
            var record = await estate.Ledger.GetRecordAsync(after.Runtime.Flow.Id, key);
            Assert.Equal(("WellLog@1.5.0", WellLogVersions.NextTemplateVersion, Samples.SampleCacheScope), Rendered(record!));
            var attempts = await estate.Ledger.ListAttemptsAsync(after.Runtime.Flow.Id, key, 10);
            Assert.Equal([secondSubmission, firstSubmission], attempts.Select(a => a.SubmissionId!.Value));
            Assert.Equal("metadata", attempts[0].Phase);
        }

        Assert.Equal(
            ["WellLog@1.5.0", "WellLog@1.4.0"],
            (await estate.Ledger.ListSubmissionsAsync(after.Runtime.Flow.Id, 10)).Select(s => s.MappingReference));
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The shared input and what both pipelines render with: the templates of both versions and both partitions' caches.</summary>
    private async Task<Estate> EstateAsync()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        // The sample templates hold both versions: WellLog 1.5.0 is among the fixtures' template files.
        var templates = Samples.SampleTemplates;
        var caches = _db.Caches();
        await Samples.ImportSampleCacheAsync(caches);
        await WellLogVersions.ImportPartitionCacheAsync(caches, NextPartition, NextFlowName);
        return new Estate(tables, templates, caches, _db.Ledger(_clock));
    }

    /// <summary>
    /// The sample flow turned into a WellLog 1.5.0 pipeline named <paramref name="name"/> delivering to
    /// <paramref name="partition"/>: the sample mapping with only its schema version changed, so both pipelines render the
    /// same entries from the same rows.
    /// </summary>
    private FlowDefinition NextFlow(string name, string partition)
        => WellLogVersions.OnNextVersion(Samples.LocalFlow(_root), name, partition, _root);

    private async Task<Pipeline> PipelineAsync(Estate estate, FlowDefinition flow)
    {
        var protocol = new FakeProtocol();
        var engine = Samples.Engine(estate.Ledger, _clock, new FakeProtocolFactory(protocol), estate.Templates, estate.Caches, estate.Tables);
        var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await estate.Ledger.RegisterAsync(runtime.Flow);
        return new Pipeline(runtime, protocol);
    }

    /// <summary>One run's work: plan into batches, drain them, and close the submission, as a deliver run does.</summary>
    private async Task<(WorkerSummary Work, Guid SubmissionId)> RunAsync(Pipeline pipeline, OsduLedger ledger)
    {
        var runtime = pipeline.Runtime;
        var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
        if (intake.NothingToDo)
        {
            return (WorkerSummary.Empty, intake.Submission.SubmissionId);
        }

        var worker = new DeliveryWorker(
            ledger, runtime.Context.Payloads, runtime.Context.Stores, pipeline.Protocol, runtime.Flow, _clock,
            CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), runtime.Flow.Name + "-worker") { MaxWait = null };
        var summary = await worker.DrainAsync(intake.Submission.SubmissionId);
        await runtime.Intake.CompleteAsync(intake.Submission.SubmissionId, runtime.Flow.Id);
        return (summary, intake.Submission.SubmissionId);
    }

    /// <summary>What built a record's delivered document: the mapping, the schema version and the partition whose cache it read.</summary>
    private static (string Mapping, string Schema, string Cache) Rendered(RecordState record)
    {
        var context = JsonNode.Parse(record.RenderContext ?? throw new InvalidOperationException($"Record {record.DeliveryKey} holds no render context."))!;
        return (Text(context, "mapping"), Text(context, "schema"), Text(context, "cache"));
    }

    private static string Text(JsonNode? node, string name)
        => node?[name]?.GetValue<string>() ?? throw new InvalidOperationException($"The document has no '{name}'.");

    private sealed record Estate(MemoryIngestionTables Tables, ITemplateSource Templates, ICacheStore Caches, OsduLedger Ledger);

    private sealed record Pipeline(FlowRuntime Runtime, FakeProtocol Protocol) : IDisposable
    {
        public void Dispose() => Runtime.Dispose();
    }
}
