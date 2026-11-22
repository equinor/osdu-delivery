using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Why a record was delivered, through real runs (osdu/docs/reference/concepts/delivery-reasons.md): the sample estate
/// delivered by runs pinned to estate commits, its mapping edited in place between them with the rows left as they are,
/// and a record redelivered by an intervention. Each delivered attempt keeps its reason, the
/// record keeps its document's paths and the commit its rules came from, and a submission counts its deliveries by trigger.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DeliveryReasonRunTests : IDisposable
{
    private const string FirstCommit = "268b2527d1c04e2fa3b9c8d7e6f5a4b3c2d1e0f9";
    private const string SecondCommit = "852db64a9c0e4f7b8d1e2a3b4c5d6e7f80912345";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_mapping_edit_of_acl_viewers_with_the_source_unchanged_is_delivered_as_a_mapping_change_naming_acl_viewers()
    {
        var original = (await File.ReadAllTextAsync(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml"))).Replace("\r\n", "\n", StringComparison.Ordinal);
        using var estate = await EstateAsync(original);
        var logs = SampleEstate.Logs().Count;
        var key = SampleEstate.Key(0);

        // The first run, pinned to the first commit: every log is a first delivery, and each record keeps its document's
        // paths and the commit its rules came from.
        Assert.Equal(logs, (await estate.RunAsync(FirstCommit)).Delivered);
        var first = await estate.RecordAsync(key);
        Assert.Equal(FirstCommit, first.RenderCommit);
        Assert.NotNull(first.RenderPaths);
        var created = Assert.Single(await estate.DeliveredAsync(key));
        Assert.Equal(TriggerKinds.FirstDelivery, created.Triggers);
        Assert.Equal("first delivery", DeliveryReason.OfResult(created.ResultJson)!.Summary());

        // The second commit changes the mapping's acl.viewers rule, and nothing else: the rows are as they were.
        const string viewers = "    viewers: [\"{$param.aclViewer}\"]\n";
        Assert.Contains(viewers, original, StringComparison.Ordinal);
        await estate.WriteMappingAsync(original.Replace(viewers, "    viewers: [\"{$param.aclViewer}\", \"data.country-no.viewers@{$param.dataPartition}.example.com\"]\n", StringComparison.Ordinal));
        _clock.Advance(TimeSpan.FromMinutes(10));
        var second = await estate.RunAsync(SecondCommit);
        Assert.Equal(logs, second.Delivered);

        var record = await estate.RecordAsync(key);
        Assert.Equal((first.SourceFingerprint, first.PayloadHash), (record.SourceFingerprint, record.PayloadHash));
        var attempts = await estate.DeliveredAsync(key);
        Assert.Equal(2, attempts.Count);
        var latest = attempts[0];
        Assert.Equal(TriggerKinds.MappingChanged, latest.Triggers);

        var reason = DeliveryReason.OfResult(latest.ResultJson)!;
        var mapping = Assert.Single(reason.Triggers);
        Assert.Equal(ReasonKinds.MappingChanged, mapping.Kind);
        Assert.Equal(RenderContext.Parse(first.RenderContext!).MappingFingerprint, mapping.FromFingerprint);
        Assert.Equal(RenderContext.Parse(record.RenderContext!).MappingFingerprint, mapping.ToFingerprint);
        Assert.NotEqual(mapping.FromFingerprint, mapping.ToFingerprint);
        Assert.Equal((FirstCommit, SecondCommit), (mapping.FromCommit, mapping.ToCommit));
        Assert.Equal([new PathChange("acl.viewers", PathChangeKind.Changed)], reason.Paths!.Listed);
        Assert.Equal("mapping WellLog@1.4.0 changed (852db64); acl.viewers changed", reason.Summary());
        Assert.Equal(SecondCommit, record.RenderCommit);
        Assert.NotEqual(first.RenderPaths, record.RenderPaths);

        // The submission counts its deliveries by what sent them.
        var counted = await estate.Ledger.CountDeliveredTriggersAsync(latest.SubmissionId!.Value);
        Assert.Equal((logs, 0L), (counted.Delivered, counted.NotRecorded));
        Assert.Equal([(ReasonKinds.MappingChanged, (long)logs)], counted.ByTrigger);

        // Nothing moved since: the next run sends nothing.
        Assert.Equal(0, (await estate.RunAsync(SecondCommit)).Delivered);
    }

    [Fact]
    public async Task A_redelivery_an_operator_asked_for_names_the_intervention_and_who_asked()
    {
        var original = await File.ReadAllTextAsync(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml"));
        using var estate = await EstateAsync(original);
        await estate.RunAsync(FirstCommit);
        var key = SampleEstate.Key(1);

        _clock.Advance(TimeSpan.FromMinutes(5));
        var now = _clock.GetUtcNow().UtcDateTime;
        var activity = await estate.Ledger.StartActivityAsync(new ActivityRecord
        {
            FlowId = estate.FlowId,
            FlowName = "rules-welllog-" + _suffix,
            Kind = "redeliver",
            Actor = "user:ada",
            StartedUtc = now,
            DeliveryKey = key.Value,
        });
        Assert.Equal(1, await estate.Ledger.ForceRedeliverAsync(estate.FlowId, [key], new RedeliverSelection(RedeliverScope.All, []), activity.ActivityId, now));
        await estate.Ledger.CompleteActivityAsync(activity.ActivityId, "completed", "marked 1 record(s) for redelivery of all", null, now.AddSeconds(1));

        // The run plans the record it was asked to in a pass of its own, before its window.
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, (await estate.RunAsync(FirstCommit)).RequestedRecords);
        var delivered = await estate.DeliveredAsync(key);
        Assert.Equal(2, delivered.Count);

        var latest = delivered[0];
        Assert.Equal(TriggerKinds.Requested, latest.Triggers);
        var request = Assert.Single(DeliveryReason.OfResult(latest.ResultJson)!.Triggers);
        Assert.Equal((activity.ActivityId, "redeliver", "user:ada"), (request.ActivityId!.Value, request.Activity, request.Actor));
        Assert.Equal("redeliver asked by user:ada; no path changed", DeliveryReason.OfResult(latest.ResultJson)!.Summary());
    }

    private async Task<Estate> EstateAsync(string mapping)
    {
        var mappings = Path.Combine(_root, "mappings");
        Directory.CreateDirectory(mappings);
        var file = Path.Combine(mappings, "WellLog@1.4.0.yaml");
        await File.WriteAllTextAsync(file, mapping);

        // Rows written an hour ago, well before the overlap below the watermark, so a run after the first reads none of them.
        var tables = await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime.AddHours(-1), time: _clock);
        var caches = _db.Caches();
        await Samples.ImportSampleCacheAsync(caches);
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock, new FakeProtocolFactory(new FakeProtocol()), cache: caches, sources: tables);
        var provider = new ServiceCollection().AddSingleton(engine).AddSingleton<PartitionLedgers>().BuildServiceProvider();
        var flow = Samples.InFolder(Samples.LocalFlow(_root), _root);
        flow = flow with { Name = "rules-welllog-" + _suffix, Render = flow.Render with { MappingsDirectory = mappings } };
        return new Estate(flow, file, ledger, provider);
    }

    /// <summary>The sample estate, delivered by a flow rendering with a mapping file of its own, run as a pinned schedule runs it.</summary>
    private sealed class Estate(FlowDefinition flow, string mappingFile, OsduLedger ledger, ServiceProvider provider) : IDisposable
    {
        private Guid? _flowId;

        public OsduLedger Ledger => ledger;

        public Guid FlowId => _flowId ?? throw new InvalidOperationException("The flow has not run yet, so no ledger holds it.");

        public Task WriteMappingAsync(string yaml) => File.WriteAllTextAsync(mappingFile, yaml);

        public async Task<DeliverOutcome> RunAsync(string commit)
        {
            var result = await new DeliveryExecutor(provider).ExecuteAsync(
                new DeliveryFlowDocument { Source = SourceDefinition.Of(flow) },
                flow.SourcePath!,
                new DocumentExecutionOptions
                {
                    Parameters = new RunParameters { Operation = DeliveryOperations.Deliver, Values = new Dictionary<string, string>(SampleEstate.Values, StringComparer.Ordinal) },
                    Commit = commit,
                },
                CancellationToken.None);
            Assert.True(result.Success, result.Error);
            _flowId ??= Assert.Single(await ledger.ListLedgersAsync(null), l => string.Equals(l.FlowName, flow.Name, StringComparison.Ordinal)).FlowId;
            return Assert.IsType<DeliverOutcome>(result.Result);
        }

        public async Task<RecordState> RecordAsync(Identity.DeliveryKey key)
            => await ledger.GetRecordAsync(FlowId, key) ?? throw new InvalidOperationException($"The ledger holds no record {key}.");

        /// <summary>The record's delivered attempts, newest first.</summary>
        public async Task<IReadOnlyList<AttemptRecord>> DeliveredAsync(Identity.DeliveryKey key)
            => [.. (await ledger.ListAttemptsAsync(FlowId, key, 50)).Where(a => a.Outcome == AttemptOutcome.Delivered).OrderByDescending(a => a.AttemptId)];

        public void Dispose() => provider.Dispose();
    }
}
