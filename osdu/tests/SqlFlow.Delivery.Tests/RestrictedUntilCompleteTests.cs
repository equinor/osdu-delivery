using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Core;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Restricted until complete end to end (osdu/docs/reference/flow/mapping-restricted-access.md): the sample well logs, their
/// mapping marking <c>data.Source</c> <c>$restrictWhenMissing</c> under <c>restrictedAccess: legal-hold</c>, delivered through
/// the ddms route to a stand-in OSDU, with the module's database on SQL Server. A log whose creator is missing goes out under
/// the restriction's groups without the property, the ledger keeps the access OSDU holds it under and what it goes without,
/// and the delivery that finds the creator gives it its own access back.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class RestrictedUntilCompleteTests : IDisposable
{
    private const string Hold = "data.restricted.owners@dev.dataservices.energy";
    private const string Restriction = "legal-hold";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();
    private readonly FakeOsduPlatform _platform = new();
    private readonly FakeOsduProtocols _protocols;
    private int _changes;

    public RestrictedUntilCompleteTests()
    {
        _protocols = new FakeOsduProtocols(_platform);
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public void Dispose()
    {
        _protocols.Dispose();
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

    [Fact]
    public async Task A_log_missing_its_creator_goes_out_restricted_and_gets_its_own_access_back_once_the_creator_arrives()
    {
        var tables = await EstateAsync();
        Change(tables, 0, "creator", null);
        var restriction = await RegisterAsync(Restriction, Hold);
        var (runtime, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverAsync(runtime);
            var id = TargetOf(0);
            var open = TargetOf(1);

            // OSDU holds the log without the property, readable only by the restriction's groups; a log with its creator is
            // delivered with the access its mapping gives.
            Assert.True(Acl(id).SameAs(restriction.Acl));
            Assert.Null(_platform.Records[id]["data"]!["Source"]);
            Assert.False(Acl(open).SameAs(restriction.Acl));
            Assert.NotNull(_platform.Records[open]["data"]!["Source"]);

            // The ledger keeps the access OSDU holds it under, since when, and what it goes without; the record is delivered,
            // and no operator restricted it.
            var record = (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)))!;
            Assert.Equal((RecordStatus.Delivered, restriction.RestrictionId, (int?)null), (record.Status, record.IncompleteRestrictionId, record.RestrictionId));
            Assert.True(record.RestrictedUntilComplete);
            Assert.False(record.Restricted);
            var since = Assert.NotNull(record.IncompleteSinceUtc);
            var missing = Assert.Single(record.IncompleteMissing!.Split('\n'));
            Assert.Equal("osdu.data.Source: dataset.creator is empty", missing);
            Assert.Equal(ProblemSignature.Of(record.IncompleteMissing), record.IncompleteHash);
            var other = (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(1)))!;
            Assert.Equal((null, null, null, null), (other.IncompleteRestrictionId, other.IncompleteSinceUtc, other.IncompleteMissing, other.IncompleteHash));

            // The try that sent it says what it did to the record's access, in its result and its detail.
            var (access, detail) = await LastDeliveryAsync(ledger, runtime.Flow.Id);
            Assert.True(access!["restricted"]!.GetValue<bool>());
            Assert.Equal(restriction.RestrictionId, access["restrictionId"]!.GetValue<int>());
            Assert.Equal(Restriction, access["restriction"]!.GetValue<string>());
            Assert.Equal([missing], access["missing"]!.AsArray().Select(m => m!.GetValue<string>()));
            Assert.Null(access["was"]);
            Assert.Contains($"restricted until complete under access restriction '{Restriction}': {missing}", detail, StringComparison.Ordinal);

            // It is counted and listed apart, by itself and by what it goes without.
            Assert.Equal(1, (await ledger.StatsAsync(runtime.Flow.Id, Now)).RestrictedUntilComplete);
            Assert.Equal([SampleEstate.Key(0)], (await ledger.ListAsync(runtime.Flow.Id, new RecordQuery { Incomplete = true })).Select(r => r.DeliveryKey));
            Assert.Equal([SampleEstate.Key(0)], (await ledger.ListAsync(runtime.Flow.Id, new RecordQuery { Missing = record.IncompleteHash })).Select(r => r.DeliveryKey));
            Assert.Empty(await ledger.ListAsync(runtime.Flow.Id, new RecordQuery { Missing = record.IncompleteHash + 1 }));
            Assert.Equal(1, (await ledger.CountAsync(runtime.Flow.Id, new RecordQuery { Incomplete = true }, 100)).Count);

            // The restriction stays registered while OSDU holds a record under it.
            await using (var db = _db.CreateDbContext())
            {
                var listed = Assert.Single(await Catalog.AccessRestrictionRegistry.ListAsync(db, "dev"));
                Assert.Equal((0, 1), (listed.Records, listed.RestrictedUntilComplete));
                var inUse = await Assert.ThrowsAsync<Catalog.AccessRestrictionInUseException>(() => Catalog.AccessRestrictionRegistry.RemoveAsync(db, restriction.RestrictionId));
                Assert.Contains("OSDU holds 1 record(s) restricted until complete under access restriction 'legal-hold'", inUse.Message, StringComparison.Ordinal);
            }

            // A delivery that still misses the creator keeps it restricted, and since when.
            var version = LatestVersion(id);
            Change(tables, 0, "log_version", "9");
            await DeliverAsync(runtime);
            Assert.True(LatestVersion(id) > version);
            Assert.True(Acl(id).SameAs(restriction.Acl));
            record = (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)))!;
            Assert.Equal((restriction.RestrictionId, since), (record.IncompleteRestrictionId, record.IncompleteSinceUtc));
            (access, detail) = await LastDeliveryAsync(ledger, runtime.Flow.Id);
            Assert.Equal(restriction.RestrictionId, access!["was"]!.GetValue<int>());
            Assert.Contains("still restricted until complete", detail, StringComparison.Ordinal);

            // A run that finds nothing changed sends nothing and leaves the access as it is.
            version = LatestVersion(id);
            await DeliverAsync(runtime);
            Assert.Equal(version, LatestVersion(id));
            Assert.Equal(restriction.RestrictionId, (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)))!.IncompleteRestrictionId);

            // The creator arriving gives the log its own access back, with the property.
            Change(tables, 0, "creator", "HAL");
            await DeliverAsync(runtime);
            Assert.True(Acl(id).SameAs(Acl(open)));
            Assert.Equal("HAL", _platform.Records[id]["data"]!["Source"]!.GetValue<string>());
            record = (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)))!;
            Assert.Equal((null, null, null, null), (record.IncompleteRestrictionId, record.IncompleteSinceUtc, record.IncompleteMissing, record.IncompleteHash));
            Assert.Equal(RecordStatus.Delivered, record.Status);
            (access, detail) = await LastDeliveryAsync(ledger, runtime.Flow.Id);
            Assert.False(access!["restricted"]!.GetValue<bool>());
            Assert.Equal(restriction.RestrictionId, access["was"]!.GetValue<int>());
            Assert.Contains("given its own access back", detail, StringComparison.Ordinal);
            Assert.Equal(0, (await ledger.StatsAsync(runtime.Flow.Id, Now)).RestrictedUntilComplete);
            Assert.Empty(await ledger.ListAsync(runtime.Flow.Id, new RecordQuery { Incomplete = true }));

            // Once no record is held under it, the restriction can be removed.
            await using (var db = _db.CreateDbContext())
            {
                Assert.Equal(0, Assert.Single(await Catalog.AccessRestrictionRegistry.ListAsync(db, "dev")).RestrictedUntilComplete);
                Assert.True(await Catalog.AccessRestrictionRegistry.RemoveAsync(db, restriction.RestrictionId));
            }
        }
    }

    [Fact]
    public async Task A_step_back_writes_the_earlier_version_under_the_restriction_and_refuses_one_no_longer_registered()
    {
        var tables = await EstateAsync();
        var restriction = await RegisterAsync(Restriction, Hold);
        var (runtime, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            // Delivered whole first, then without its creator: OSDU holds version 2 restricted.
            await DeliverAsync(runtime);
            var id = TargetOf(0);
            var own = Acl(id);
            var creator = _platform.Records[id]["data"]!["Source"]!.GetValue<string>();
            Change(tables, 0, "creator", null);
            await DeliverAsync(runtime);
            Assert.True(Acl(id).SameAs(restriction.Acl));
            Assert.False(own.SameAs(restriction.Acl));

            // The step back writes the version before with its creator, and still under the restriction's groups: the record
            // is never opened by a write back while the ledger says it is restricted until complete.
            var stepped = await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Previous);
            Assert.True(stepped.Failed == 0, string.Join("; ", stepped.Records.Select(r => r.Detail)));
            Assert.Equal(creator, _platform.Records[id]["data"]!["Source"]!.GetValue<string>());
            Assert.True(Acl(id).SameAs(restriction.Acl));
            Assert.Equal(restriction.RestrictionId, (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)))!.IncompleteRestrictionId);

            // A restriction taken out of the registry behind the ledger's back is not guessed at: nothing is written back.
            Change(tables, 0, "log_version", "9");
            await DeliverAsync(runtime);
            var version = LatestVersion(id);
            await using (var db = _db.CreateDbContext())
            {
                db.DeliveryAccessRestrictions.Remove(await db.DeliveryAccessRestrictions.FindAsync(restriction.RestrictionId) ?? throw new InvalidOperationException("the restriction is not registered"));
                await db.SaveChangesAsync();
            }

            var refused = await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Previous);
            Assert.Contains("no longer registered", Assert.Single(refused.Records).Detail, StringComparison.Ordinal);
            Assert.Equal(version, LatestVersion(id));
            Assert.True(Acl(id).SameAs(restriction.Acl));
        }
    }

    [Fact]
    public async Task A_run_whose_restriction_is_not_registered_or_could_never_be_lifted_sends_nothing()
    {
        var tables = await EstateAsync();
        Change(tables, 0, "creator", null);

        // No restriction under the name in the partition: the flow is refused as it opens.
        var unregistered = await Assert.ThrowsAsync<FlowValidationException>(() => RuntimeAsync(tables));
        Assert.Contains("partition 'dev' registers none under that name", unregistered.Message, StringComparison.Ordinal);

        // One the flow's identity is no owner of: the run stops before it plans anything, since a record restricted under it
        // could never be given its own access back.
        await RegisterAsync(Restriction, "data.someone-else@dev.dataservices.energy");
        var (runtime, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            var refused = await Assert.ThrowsAsync<DeliveryException>(() => DeliverAsync(runtime));
            Assert.Contains("could never be given its access back", refused.Message, StringComparison.Ordinal);
            Assert.Contains("Nothing was planned or sent", refused.Message, StringComparison.Ordinal);
            Assert.Empty(_platform.Records);
            Assert.Empty(await ledger.ListRecordsAsync(runtime.Flow.Id, null, 10));
        }
    }

    [Fact]
    public async Task Restricted_counts_and_lists_each_record_restricted_either_way_once_through_the_indexes_that_hold_them()
    {
        var ledger = _db.Ledger(_clock);
        var flowId = FlowId.Of("restricted-either-way-" + Guid.NewGuid().ToString("N")[..8]);
        await ledger.RegisterAsync(flowId);
        var own = new DeliveryKey(Guid.NewGuid());
        var incomplete = new DeliveryKey(Guid.NewGuid());
        var byOperator = new DeliveryKey(Guid.NewGuid());
        var both = new DeliveryKey(Guid.NewGuid());
        var heldIncomplete = new DeliveryKey(Guid.NewGuid());
        var stepHeld = new DeliveryKey(Guid.NewGuid());
        DeliveryKey[] all = [own, incomplete, byOperator, both, heldIncomplete, stepHeld];
        await ledger.UpsertPendingAsync(flowId, all.Select((key, i) => new RecordState
        {
            DeliveryKey = key,
            FlowId = flowId,
            SourceKey = $"restricted-{i}",
            Label = $"restricted-{i}",
            MappingName = "WellLog",
            Status = RecordStatus.Pending,
            TargetId = $"dev:work-product-component--WellLog:restricted-{i}",
            SourceKeyJson = $$"""["restricted-{{i}}"]""",
            PendingDocumentRef = "1:0:10",
            PendingMetadata = true,
        }).ToList());

        // What a delivery that sent them restricted, an operator's restriction and a step held leave on the records.
        const string Classification = "osdu.tags['dra.securityClassification']: dataset.class is empty";
        var group = ProblemSignature.Of(Classification);
        await using (var db = _db.CreateDbContext())
        {
            foreach (var key in new[] { incomplete, both, heldIncomplete })
            {
                await db.DeliveryRecords.Where(r => r.FlowId == flowId && r.DeliveryKey == key.Value).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.IncompleteRestrictionId, 7)
                    .SetProperty(r => r.IncompleteSinceUtc, Now)
                    .SetProperty(r => r.IncompleteMissing, Classification)
                    .SetProperty(r => r.IncompleteHash, group));
            }

            foreach (var key in new[] { byOperator, both })
            {
                await db.DeliveryRecords.Where(r => r.FlowId == flowId && r.DeliveryKey == key.Value).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.RestrictionId, 8)
                    .SetProperty(r => r.RestrictedUtc, Now));
            }

            await db.DeliveryRecords.Where(r => r.FlowId == flowId && r.DeliveryKey == heldIncomplete.Value).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "held"));
            await db.DeliveryRecords.Where(r => r.FlowId == flowId && r.DeliveryKey == stepHeld.Value).ExecuteUpdateAsync(s => s.SetProperty(r => r.HeldSteps, "grade"));
        }

        static DeliveryKey[] Sorted(IEnumerable<DeliveryKey> keys) => keys.OrderBy(k => k.Value).ToArray();
        async Task<DeliveryKey[]> ListedAsync(RecordQuery query) => Sorted((await ledger.ListAsync(flowId, query)).Select(r => r.DeliveryKey));

        // Restricted counts every record restricted either way once; each kind is counted as well.
        var stats = await ledger.StatsAsync(flowId, Now);
        Assert.Equal((4L, 2L, 3L, 1L), (stats.AccessRestricted, stats.Restricted, stats.RestrictedUntilComplete, stats.StepsHeld));

        // The flow's listing, its count and the keys a removal reaches take the same records.
        Assert.Equal(Sorted([incomplete, byOperator, both, heldIncomplete]), await ListedAsync(new RecordQuery { AccessRestricted = true }));
        Assert.Equal(Sorted([byOperator, both]), await ListedAsync(new RecordQuery { Restricted = true }));
        Assert.Equal(Sorted([incomplete, both, heldIncomplete]), await ListedAsync(new RecordQuery { Incomplete = true }));
        Assert.Equal(Sorted([incomplete, both, heldIncomplete]), await ListedAsync(new RecordQuery { Missing = group }));
        Assert.Empty(await ListedAsync(new RecordQuery { Missing = group + 1 }));
        Assert.Equal([stepHeld], await ListedAsync(new RecordQuery { StepHeld = true }));
        Assert.Equal([heldIncomplete], await ListedAsync(new RecordQuery { AccessRestricted = true, Status = RecordStatus.Held }));
        Assert.Equal([both], await ListedAsync(new RecordQuery { Restricted = true, Incomplete = true }));
        Assert.Equal(4, (await ledger.CountAsync(flowId, new RecordQuery { AccessRestricted = true }, 100)).Count);
        Assert.Equal(Sorted([incomplete, byOperator, both, heldIncomplete]), Sorted(await ledger.ListKeysAsync(flowId, new RecordQuery { AccessRestricted = true }, 100)));

        // The newest records across flows, read through the indexes, and a search across flows, over its candidates, narrow alike.
        async Task<DeliveryKey[]> RecentAsync(LookupNarrowing narrowing)
            => Sorted((await ledger.ListRecentAsync(50, flowId: flowId, narrowTo: narrowing)).Select(r => r.DeliveryKey));
        Assert.Equal(Sorted([incomplete, byOperator, both, heldIncomplete]), await RecentAsync(new LookupNarrowing(AccessRestricted: true)));
        Assert.Equal(Sorted([byOperator, both]), await RecentAsync(new LookupNarrowing(Restricted: true)));
        Assert.Equal(Sorted([incomplete, both, heldIncomplete]), await RecentAsync(new LookupNarrowing(Incomplete: true)));
        Assert.Equal([stepHeld], await RecentAsync(new LookupNarrowing(StepHeld: true)));
        Assert.Equal([heldIncomplete], Sorted((await ledger.ListRecentAsync(50, RecordStatus.Held, flowId, narrowTo: new LookupNarrowing(AccessRestricted: true))).Select(r => r.DeliveryKey)));
        Assert.Equal(4, (await ledger.CountRecentAsync(100, flowId: flowId, narrowTo: new LookupNarrowing(AccessRestricted: true))).Count);
        Assert.Equal(6, (await ledger.ListRecentAsync(50, flowId: flowId)).Count);
        Assert.Equal(
            Sorted([incomplete, byOperator, both, heldIncomplete]),
            Sorted((await ledger.LookupAsync("restricted", 50, flowId: flowId, narrowTo: new LookupNarrowing(AccessRestricted: true))).Select(r => r.DeliveryKey)));
    }

    /// <summary>The access block and the detail of the latest delivered try of the first sample log.</summary>
    private static async Task<(JsonNode? Access, string Detail)> LastDeliveryAsync(OsduLedger ledger, Guid flowId)
    {
        var attempt = (await ledger.ListAttemptsAsync(flowId, SampleEstate.Key(0), 20))
            .Where(a => a.Outcome == AttemptOutcome.Delivered)
            .MaxBy(a => a.AttemptId) ?? throw new InvalidOperationException("the log has no delivered try");
        var result = JsonNode.Parse(attempt.ResultJson!)!;
        return (result["access"], result["detail"]?.GetValue<string>() ?? string.Empty);
    }

    private async Task<AccessRestrictionState> RegisterAsync(string name, string group)
    {
        await using var db = _db.CreateDbContext();
        var row = new DeliveryAccessRestriction
        {
            Partition = "dev",
            Name = name,
            Viewers = AccessRestrictionState.GroupsJson([group]),
            Owners = AccessRestrictionState.GroupsJson([group]),
            CreatedUtc = Now,
            CreatedBy = "user:admin",
            UpdatedUtc = Now,
            UpdatedBy = "user:admin",
        };
        db.DeliveryAccessRestrictions.Add(row);
        await db.SaveChangesAsync();
        return AccessRestrictionState.Of(row);
    }

    private async Task<MemoryIngestionTables> EstateAsync() => await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);

    /// <summary>
    /// The sample flow over a copy of its mapping that marks <c>data.Source</c> <c>$restrictWhenMissing</c> under
    /// <c>restrictedAccess: legal-hold</c>, aimed at the stand-in OSDU under its platform root, failing fast.
    /// </summary>
    private async Task<FlowDefinition> FlowAsync()
    {
        var mappings = Path.Combine(_root, "restricting-mappings");
        Directory.CreateDirectory(mappings);
        var sample = (await File.ReadAllTextAsync(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml"))).ReplaceLineEndings("\n");
        const string Source = "    Source: { $from: creator }\n";
        const string Record = "\nrecord:\n";
        Assert.Contains(Source, sample, StringComparison.Ordinal);
        Assert.Contains(Record, sample, StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            Path.Combine(mappings, "WellLog@1.4.0.yaml"),
            sample
                .Replace(Source, "    Source: { $from: creator, $restrictWhenMissing: true }\n", StringComparison.Ordinal)
                .Replace(Record, $"\nrestrictedAccess: {Restriction}\n{Record}", StringComparison.Ordinal));

        var flow = Samples.LocalFlow(_root);
        return flow with
        {
            Render = flow.Render with { MappingsDirectory = mappings },
            Target = flow.Target with
            {
                Endpoint = FakeOsduPlatform.Endpoint,
                Auth = new TargetAuth { Type = TargetAuthType.None },
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" },
            },
            Reliability = flow.Reliability with { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            Parallelism = flow.Parallelism with { RecordsAtOnce = 4 },
        };
    }

    private async Task<(FlowRuntime Runtime, OsduLedger Ledger)> RuntimeAsync(MemoryIngestionTables tables)
    {
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock, protocols: _protocols, sources: tables);
        var runtime = await FlowRuntime.CreateAsync(engine, await FlowAsync(), SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        return (runtime, ledger);
    }

    /// <summary>A deliver run of the flow under a run id of its own, reading the whole scope.</summary>
    private async Task DeliverAsync(FlowRuntime runtime)
    {
        runtime.RunId = Guid.CreateVersion7();
        runtime.Actor = "schedule:test";
        runtime.Selection = SourceSelection.Full();
        runtime.SubmissionId = null;
        await runtime.RunAsync(force: false);
        _clock.Advance(TimeSpan.FromMinutes(1));
    }

    /// <summary>Changes one column of the sample log at <paramref name="index"/>, as the source would, at a later business version.</summary>
    private void Change(MemoryIngestionTables tables, int index, string column, string? value)
    {
        _changes++;
        SampleEstate.Change(tables.Records[index], column, value, Now, SampleWellLogs.UpdatedUtc(index).AddHours(_changes));
    }

    private static string TargetOf(int index) => "dev:work-product-component--WellLog:" + SampleEstate.Key(index).Value.ToString("N");

    private long LatestVersion(string id) => _platform.Records[id]["version"]!.GetValue<long>();

    private RecordAcl Acl(string id) => RecordAcl.OfRecord(_platform.Records[id]) ?? throw new InvalidOperationException($"{id} carries no ACL");
}
