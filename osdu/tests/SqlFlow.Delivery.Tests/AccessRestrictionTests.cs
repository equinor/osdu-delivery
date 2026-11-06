using System.Net;
using System.Text.Json.Nodes;
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
/// Restricting records' access in OSDU end to end (osdu/docs/reference/concepts/access-restriction.md): the sample well logs
/// delivered through the ddms route to a stand-in OSDU under its platform root, the module's database on SQL Server, and a
/// registered restriction whose groups replace the records' ACL through storage's metadata patch.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class AccessRestrictionTests : IDisposable
{
    private const string Hold = "data.restricted.owners@dev.dataservices.energy";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();
    private readonly FakeOsduPlatform _platform = new();
    private readonly FakeOsduProtocols _protocols;

    public AccessRestrictionTests()
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
    public async Task A_restriction_replaces_the_acl_in_place_keeps_the_record_out_of_every_run_and_lifting_gives_it_back()
    {
        var tables = await EstateAsync();
        var (runtime, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverAsync(runtime);
            var restriction = await RegisterAsync("legal-hold");
            var id = TargetOf(0);
            var version = LatestVersion(id);
            var original = Acl(id);
            Assert.False(original.SameAs(restriction.Acl));

            runtime.RunId = Guid.CreateVersion7();
            runtime.Actor = "user:steward";
            var summary = await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Restrict, restriction: "legal-hold");

            Assert.True(summary.Failed == 0, string.Join("; ", summary.Records.Select(r => r.Detail)));
            Assert.Equal((1, 1, 0, 0), (summary.Selected, summary.Restricted, summary.Skipped, summary.Failed));
            Assert.Contains("1 restricted under 'legal-hold'", summary.Describe(), StringComparison.Ordinal);

            // OSDU holds the record under the restriction's groups, at the version it had, every version governed by them.
            Assert.True(Acl(id).SameAs(restriction.Acl));
            Assert.Equal(version, LatestVersion(id));
            Assert.All(_platform.History[id], v => Assert.True(RecordAcl.OfRecord(v)!.SameAs(restriction.Acl)));
            var patch = Assert.Single(_platform.AclPatches);
            Assert.Equal([id], patch.Ids);

            // The ledger keeps the restriction and the ACL to give back, with an attempt naming both, under who asked.
            var record = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal((restriction.RestrictionId, RecordStatus.Delivered, version), (record!.RestrictionId, record.Status, record.TargetVersion));
            Assert.NotNull(record.RestrictedUtc);
            Assert.True(RecordAcl.Parse(record.RestrictedFromAcl!).SameAs(original));
            var attempt = (await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(0), 20)).First(a => a.Phase == AttemptPhases.Restrict);
            Assert.Equal((AttemptOutcome.Restricted, "user:steward", runtime.RunId), (attempt.Outcome, attempt.Worker, attempt.RunId));
            Assert.Contains("\"name\":\"legal-hold\"", attempt.ResultJson!, StringComparison.Ordinal);
            Assert.Contains(Hold, attempt.ResultJson!, StringComparison.Ordinal);
            var activity = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, Kind = AttemptPhases.Restrict }));
            Assert.Equal(("completed", "user:steward"), (activity.Outcome, activity.Actor));
            Assert.Contains("\"restriction\":\"legal-hold\"", activity.ParametersJson!, StringComparison.Ordinal);

            // A changed row is not delivered while the record is restricted, whatever a run reads, and nothing writes it.
            ChangeRows(tables, "HAL", 0);
            await DeliverAsync(runtime, force: true);
            Assert.Equal(version, LatestVersion(id));
            Assert.True(Acl(id).SameAs(restriction.Acl));
            Assert.Equal(restriction.RestrictionId, (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)))!.RestrictionId);

            // Nor does a step back to the previous version, or asking to restrict it again.
            var stepBack = await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Previous);
            Assert.Contains("restricted", Assert.Single(stepBack.Records).Detail, StringComparison.Ordinal);
            var again = await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Restrict, restriction: "legal-hold");
            Assert.Equal(("skipped", 1), (Assert.Single(again.Records).Outcome, _platform.AclPatches.Count));

            // Lifting it gives the record back the ACL it had, and the run after delivers the row that changed meanwhile.
            runtime.RunId = Guid.CreateVersion7();
            var lifted = await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Unrestrict);
            Assert.Equal((1, 0), (lifted.Lifted, lifted.Failed));
            Assert.True(Acl(id).SameAs(original));
            Assert.Equal(version, LatestVersion(id));
            var free = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal((null, null, null), (free!.RestrictionId, free.RestrictedUtc, free.RestrictedFromAcl));
            Assert.NotNull(free.PlanRequestedUtc);
            Assert.Equal(
                AttemptOutcome.Unrestricted,
                (await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(0), 30)).First(a => a.Phase == AttemptPhases.LiftRestriction).Outcome);

            await DeliverAsync(runtime);
            Assert.True(LatestVersion(id) > version);
            Assert.True(Acl(id).SameAs(original));

            // Every call the restriction, the lift and the runs made is one the pinned contracts describe, as they describe it.
            OsduContracts.AssertConform(
                _platform.Calls, null, OsduContracts.Storage, OsduContracts.Entitlements, OsduContracts.Legal, OsduContracts.WellboreDdms);
        }
    }

    [Fact]
    public async Task Restricting_drops_queued_work_and_no_release_sends_a_restricted_record()
    {
        var tables = await EstateAsync();
        var (runtime, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverAsync(runtime);
            var restriction = await RegisterAsync("quarantine");
            var id = TargetOf(1);
            var version = LatestVersion(id);

            // A changed row is planned and queued, and not sent yet.
            ChangeRows(tables, "HAL", 1);
            runtime.RunId = Guid.CreateVersion7();
            runtime.Selection = SourceSelection.Full();
            runtime.SubmissionId = null;
            await runtime.IntakeAsync(false);
            Assert.Equal(RecordStatus.Pending, (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(1)))!.Status);

            var summary = await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(1)]), RemovalChoice.Restrict, restriction: "quarantine");
            Assert.Equal(1, summary.Restricted);
            var record = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(1));
            Assert.Equal((RecordStatus.Delivered, false, (string?)null), (record!.Status, record.HasPendingWork, record.PendingDocumentRef));

            // Nothing a drain, a release or a redelivery does sends it.
            await runtime.WorkAsync(once: true);
            Assert.Equal(0, await ledger.ReleaseAsync(runtime.Flow.Id, [SampleEstate.Key(1)], Now));
            Assert.Equal(1, await runtime.RedeliverAsync([SampleEstate.Key(1)], RedeliverScope.All));
            await DeliverAsync(runtime);
            Assert.Equal(version, LatestVersion(id));
            Assert.True(Acl(id).SameAs(restriction.Acl));
        }
    }

    [Fact]
    public async Task A_restriction_the_flow_could_never_lift_is_refused_before_anything_changes()
    {
        var tables = await EstateAsync();
        var (runtime, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverAsync(runtime);
            await RegisterAsync("foreign", "data.someone-else@dev.dataservices.energy");
            var id = TargetOf(0);
            var acl = Acl(id);

            var refused = await Assert.ThrowsAsync<DeliveryException>(
                () => runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Restrict, restriction: "foreign"));
            Assert.Contains("could never be given its access back", refused.Message, StringComparison.Ordinal);
            Assert.Contains(_platform.MemberEmail, refused.Message, StringComparison.Ordinal);
            Assert.Empty(_platform.AclPatches);
            Assert.True(Acl(id).SameAs(acl));
            Assert.Null((await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)))!.RestrictionId);
            Assert.Equal("failed", Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, Kind = AttemptPhases.Restrict })).Outcome);

            // A data manager may change any record, so the same restriction is liftable by one.
            _platform.CallerGroups.Add("users.data.root@dev.dataservices.energy");
            Assert.Equal(1, (await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Restrict, restriction: "foreign")).Restricted);

            // A name the partition has not registered is refused, naming the partition.
            var unknown = await Assert.ThrowsAsync<DeliveryException>(
                () => runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(1)]), RemovalChoice.Restrict, restriction: "nowhere"));
            Assert.Contains("No access restriction named 'nowhere' is registered for partition 'dev'", unknown.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_restriction_whose_answer_is_lost_stays_in_the_ledger_with_the_acl_the_record_had()
    {
        var tables = await EstateAsync();
        var (runtime, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverAsync(runtime);
            var restriction = await RegisterAsync("legal-hold");
            var id = TargetOf(2);
            var original = Acl(id);

            _platform.AclPatchAnswerLost = true;
            var lost = await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(2)]), RemovalChoice.Restrict, restriction: "legal-hold");
            Assert.Equal((0, 1), (lost.Restricted, lost.Failed));
            Assert.Contains("kept restricted here", Assert.Single(lost.Records).Detail, StringComparison.Ordinal);

            // OSDU took it, the ledger does not know that, and keeps the record out with the ACL it had.
            Assert.True(Acl(id).SameAs(restriction.Acl));
            var asked = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(2));
            Assert.Equal(restriction.RestrictionId, asked!.RestrictionId);
            Assert.Null(asked.RestrictedUtc);
            Assert.True(RecordAcl.Parse(asked.RestrictedFromAcl!).SameAs(original));

            // Asked again, it is confirmed, and the ACL to give back is still the one it had, not the restriction's.
            _platform.AclPatchAnswerLost = false;
            Assert.Equal(1, (await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(2)]), RemovalChoice.Restrict, restriction: "legal-hold")).Restricted);
            var confirmed = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(2));
            Assert.NotNull(confirmed!.RestrictedUtc);
            Assert.True(RecordAcl.Parse(confirmed.RestrictedFromAcl!).SameAs(original));

            Assert.Equal(1, (await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(2)]), RemovalChoice.Unrestrict)).Lifted);
            Assert.True(Acl(id).SameAs(original));
        }
    }

    [Fact]
    public async Task A_restriction_storage_refuses_is_withdrawn_and_the_record_follows_its_source_again()
    {
        var tables = await EstateAsync();
        var (runtime, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverAsync(runtime);
            await RegisterAsync("legal-hold");
            var id = TargetOf(3);
            var original = Acl(id);

            _platform.AclPatchRefused = HttpStatusCode.BadRequest;
            var refused = await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(3)]), RemovalChoice.Restrict, restriction: "legal-hold");
            Assert.Equal((0, 1), (refused.Restricted, refused.Failed));
            Assert.Contains("kept the record's access as it was", Assert.Single(refused.Records).Detail, StringComparison.Ordinal);
            Assert.True(Acl(id).SameAs(original));
            var record = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(3));
            Assert.Equal((null, null), (record!.RestrictionId, record.RestrictedFromAcl));
            Assert.NotNull(record.PlanRequestedUtc);
        }
    }

    [Fact]
    public async Task A_restricted_record_removed_from_osdu_stays_in_the_ledger_and_keeps_the_ledger_from_being_deleted()
    {
        var tables = await EstateAsync();
        var (runtime, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverAsync(runtime);
            await RegisterAsync("legal-hold");
            Assert.Equal(1, (await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Restrict, restriction: "legal-hold")).Restricted);

            // Removed with the ledger step: out of OSDU, and kept in the ledger, so nothing delivers it again.
            var removal = await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Record, purgeLedger: true);
            Assert.Equal((1, 0), (removal.Removed, removal.Purged));
            Assert.Contains("its access is restricted", Assert.Single(removal.Records).Detail, StringComparison.Ordinal);
            var removed = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal(RecordStatus.Deleted, removed!.Status);
            Assert.NotNull(removed.RestrictionId);
            Assert.Equal(0, (await runtime.PurgeFromLedgerAsync([SampleEstate.Key(0)])).Purged);
            await DeliverAsync(runtime, force: true);
            Assert.Contains(TargetOf(0), _platform.Removed);

            // Deleting the ledger would deliver it again, open to everyone, so it is refused before anything is removed.
            var refused = await Assert.ThrowsAsync<DeliveryException>(() => runtime.DeleteLedgerAsync("dev"));
            Assert.Contains("have their access restricted in OSDU", refused.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(TargetOf(1), _platform.Removed);

            // Lifted, nothing is asked of OSDU, and the record follows its source: the next run delivers it again.
            var patches = _platform.AclPatches.Count;
            Assert.Equal(1, (await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Unrestrict)).Lifted);
            Assert.Equal(patches, _platform.AclPatches.Count);
            await DeliverAsync(runtime);
            Assert.DoesNotContain(TargetOf(0), _platform.Removed);
            Assert.Equal(RecordStatus.Delivered, (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)))!.Status);
        }
    }

    [Fact]
    public async Task A_restriction_carried_by_records_is_not_removed_from_the_registry()
    {
        var tables = await EstateAsync();
        var (runtime, _) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverAsync(runtime);
            var restriction = await RegisterAsync("legal-hold");
            Assert.Equal(1, (await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Restrict, restriction: "legal-hold")).Restricted);

            await using (var db = _db.CreateDbContext())
            {
                var listed = Assert.Single(await Catalog.AccessRestrictionRegistry.ListAsync(db, "dev"));
                Assert.Equal(1, listed.Records);
                var inUse = await Assert.ThrowsAsync<Catalog.AccessRestrictionInUseException>(() => Catalog.AccessRestrictionRegistry.RemoveAsync(db, restriction.RestrictionId));
                Assert.Contains("1 record(s) carry access restriction 'legal-hold'", inUse.Message, StringComparison.Ordinal);
            }

            Assert.Equal(1, (await runtime.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Unrestrict)).Lifted);
            await using (var db = _db.CreateDbContext())
            {
                Assert.True(await Catalog.AccessRestrictionRegistry.RemoveAsync(db, restriction.RestrictionId));
                Assert.Empty(await Catalog.AccessRestrictionRegistry.ListAsync(db, "dev"));
            }
        }
    }

    [Fact]
    public async Task The_registry_takes_only_data_groups_and_one_name_per_partition()
    {
        await using var db = _db.CreateDbContext();
        db.DeliveryPartitions.Add(new DeliveryPartition { Name = "dev", CreatedBy = "test", UpdatedBy = "test", CreatedUtc = Now, UpdatedUtc = Now, IsDefault = true });
        await db.SaveChangesAsync();

        var added = await Catalog.AccessRestrictionRegistry.AddAsync(db, "dev", "Legal hold", "Records under legal review", [Hold], [Hold], "user:admin", Now);
        Assert.Equal(("dev", "Legal hold"), (added.Partition, added.Name));

        var taken = await Assert.ThrowsAsync<DeliveryException>(() => Catalog.AccessRestrictionRegistry.AddAsync(db, "dev", "legal HOLD", null, [Hold], [Hold], "user:admin", Now));
        Assert.Contains("already", taken.Message, StringComparison.Ordinal);
        var notGroup = await Assert.ThrowsAsync<DeliveryException>(() => Catalog.AccessRestrictionRegistry.AddAsync(db, "dev", "other", null, ["restricted"], [Hold], "user:admin", Now));
        Assert.Contains("restricted is not a data group", notGroup.Message, StringComparison.Ordinal);
        var empty = await Assert.ThrowsAsync<DeliveryException>(() => Catalog.AccessRestrictionRegistry.AddAsync(db, "dev", "other", null, [Hold], [], "user:admin", Now));
        Assert.Contains("at least one group as its owners", empty.Message, StringComparison.Ordinal);
        var unregistered = await Assert.ThrowsAsync<DeliveryException>(() => Catalog.AccessRestrictionRegistry.AddAsync(db, "prod", "hold", null, [Hold], [Hold], "user:admin", Now));
        Assert.Contains("Partition 'prod' is not registered", unregistered.Message, StringComparison.Ordinal);

        var described = await Catalog.AccessRestrictionRegistry.DescribeAsync(db, added.RestrictionId, "Kept while counsel reviews", "user:admin", Now);
        Assert.Equal("Kept while counsel reviews", described!.Description);
        Assert.True(described.Acl.SameAs(added.Acl));
    }

    private async Task<AccessRestrictionState> RegisterAsync(string name, string group = Hold)
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

    /// <summary>The sample flow, aimed at the stand-in OSDU under its platform root, failing fast.</summary>
    private FlowDefinition Flow()
    {
        var flow = Samples.LocalFlow(_root);
        return flow with
        {
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
        var runtime = await FlowRuntime.CreateAsync(engine, Flow(), SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        return (runtime, ledger);
    }

    /// <summary>A deliver run of the flow under a run id of its own, reading the whole scope; returns the run.</summary>
    private async Task<Guid> DeliverAsync(FlowRuntime runtime, bool force = false)
    {
        var run = Guid.CreateVersion7();
        runtime.RunId = run;
        runtime.Actor = "schedule:test";
        runtime.Selection = SourceSelection.Full();
        runtime.SubmissionId = null;
        await runtime.RunAsync(force);
        _clock.Advance(TimeSpan.FromMinutes(1));
        return run;
    }

    /// <summary>Changes the creator of the sample records at <paramref name="indexes"/>, as the source would, at a later business version.</summary>
    private void ChangeRows(MemoryIngestionTables tables, string value, params int[] indexes)
    {
        _changes++;
        foreach (var index in indexes)
        {
            SampleEstate.Change(tables.Records[index], "creator", value, Now, SampleWellLogs.UpdatedUtc(index).AddHours(_changes));
        }
    }

    private int _changes;

    private static string TargetOf(int index) => "dev:work-product-component--WellLog:" + SampleEstate.Key(index).Value.ToString("N");

    private long LatestVersion(string id) => _platform.Records[id]["version"]!.GetValue<long>();

    private RecordAcl Acl(string id) => RecordAcl.OfRecord(_platform.Records[id]) ?? throw new InvalidOperationException($"{id} carries no ACL");
}
