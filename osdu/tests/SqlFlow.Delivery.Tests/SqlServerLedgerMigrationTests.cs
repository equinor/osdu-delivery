using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The ledger's migrations run over a ledger written before them. <c>LedgerPerFlow</c>: records keyed by their flow, every
/// attempt given its record's flow, the OSDU ids already written claimed, an interrupted rollout's cursor completed, and
/// the ledgers it cannot convert refused with a message that says why. <c>LeasesAndRecordEvents</c>: the leases stopped
/// workers left behind kept as lease rows, and a revert refused while an appended event is not on its record. Each test
/// works in the suites' scratch database (<see cref="OsduScratchDatabase"/>), created empty and dropped afterwards: a
/// migration has to start from the schema it upgrades, and the suite's test database is already past it.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerLedgerMigrationTests
{
    private const string Before = "20260916105416_RemoveManualSubmission";

    private const string BeforeLeases = "20260916133609_CoverWorkerReads";

    private const string BeforeWaits = "20260916221306_DeliveryInterfaces";

    private const string BeforePartitionKeys = "20260928053849_PartitionRegistry";

    private const string BeforeIdle = "20260929012241_AssertionRuns";

    private const string BeforeDimensions = "20260929220041_ActivityIdle";

    /// <summary>The migration before dimensions kept each key's label and filter.</summary>
    private const string BeforeDimensionLabels = "20260930103543_DimensionFlows";

    /// <summary>The migration before dimension attributes, the last one a dimension's keys had no attributes in.</summary>
    private const string BeforeDimensionAttributes = "20260930175349_DimensionLabels";

    private const string BeforeCollectedAttributes = "20260930190046_DimensionAttributes";

    private const string BeforeCollectedTexts = "20261001084750_DimensionCollectedAttributes";

    /// <summary>The migration before dimensions had tables of their own and attributes had numbers.</summary>
    private const string BeforeDimensionTables = "20261001102750_DimensionCollectedTexts";

    /// <summary>The migration before a dimension's table named its key's and its value's columns after what the dimension reads.</summary>
    private const string BeforeDimensionColumnNames = "20261001163440_DimensionTables";

    /// <summary>The migration before runs and submissions could be reversed.</summary>
    private const string BeforeReversals = "20261004123427_RecordValidation";

    /// <summary>The migration before a record removed from OSDU could be deleted from the ledger.</summary>
    private const string BeforePurges = "20261005205855_RecordReversals";

    /// <summary>The migration before deliveries recorded what they created in OSDU, to undo it when they do not complete.</summary>
    private const string BeforeArtifacts = "20261007232509_PurgedRecordRecency";

    /// <summary>The migration before the search terms the syncs extract from mappings, and what people make of them.</summary>
    private const string BeforeSearchTerms = "20261008132311_InventoryRemovals";

    /// <summary>The migration before search terms were keyed by their source table: they were keyed by the mapping's source system.</summary>
    private const string BeforeSearchTermSources = "20261008170839_SearchTerms";

    /// <summary>The migration before an element's field kept up to 4,000 characters: it kept 256, as an attribute does.</summary>
    private const string BeforeElementValues = "20261009204033_DimensionViews";

    /// <summary>The migration before a dimension could be loaded incrementally: every build read it in full.</summary>
    private const string BeforeIncrementalLoads = "20261010152004_DimensionElementValues";

    /// <summary>The tables of reversals, each a ledger table keyed by the partition first.</summary>
    private static readonly string[] ReversalTables = ["Reversal", "ReversalItem"];

    /// <summary>The tables of dimension flows, each a ledger table keyed by the partition first.</summary>
    private static readonly string[] DimensionTables =
        ["Dimension", "DimensionRun", "DimensionMember", "DimensionValue", "DimensionChange", "DimensionAttribute", "DimensionCollectedText", "DimensionAttributeName", "DimensionElement",
            "DimensionKeyRecord", "DimensionRecord"];

    private static readonly Guid Mixed = FlowId.Of("wells-mixed-delivery");

    private static readonly Guid Retrieved = FlowId.Of("wells-retrieval");

    /// <summary>The ledger tables the partition leads the key of.</summary>
    private static readonly string[] LedgerTables =
        ["Record", "RecordIdentity", "Attempt", "Submission", "WorkBatch", "Lease", "RecordEvent", "SourceWatermark", "Activity", "Retrieval", "AssertionRun", "AssertionResult",
            .. DimensionTables];

    private static readonly DateTime Now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Guid Logs = FlowId.Of("welldb-welllog-03-header-delivery");

    private static readonly Guid Wellbores = FlowId.Of("wells-wellbore-03-header-delivery");

    /// <summary>
    /// The model the code builds is exactly the one the last migration leaves, so no change to it reaches a deployment
    /// without the migration that makes it. Comparing the model with the migrations' snapshot needs no connection.
    /// </summary>
    [Fact]
    public void The_model_is_the_one_the_last_migration_leaves()
    {
        using var db = new OsduDbContext(OsduDbContext.SqlServerOptions(OsduTestServer.LocalDefault));
        Assert.False(db.Database.HasPendingModelChanges(), "The module's model differs from its last migration's snapshot: add the migration that makes the change.");
    }

    [Fact]
    public async Task An_existing_ledger_is_keyed_per_flow_with_every_attempt_claim_and_cursor_placed()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.AllowSnapshotAsync();
        await database.MigrateAsync(Before);

        var submission = Guid.NewGuid();
        var delivered = Guid.NewGuid();
        var queued = Guid.NewGuid();
        var held = Guid.NewGuid();
        var unrecorded = Guid.NewGuid();
        await database.ExecuteAsync(
            """
            INSERT INTO [osdu].[Submission] ([SubmissionId], [FlowId], [FlowName], [MappingReference], [RenderContext], [ParametersJson], [RecordCount],
                [BatchCount], [Slices], [Status], [ReceivedUtc], [Planned], [SkippedUnchanged], [AwaitingApproval], [SkippedStale], [UnchangedAtPush],
                [Blocked], [Delivered], [Held], [Failed], [Untracked], [Kind], [SourceConnection], [SourceObject])
            VALUES (@submission, @logs, N'welldb-welllog-03-header-delivery', N'WellLog@1.4.0', N'{}', N'{}', 3, 1, 1, N'completed', @now, 2, 0, 0, 0, 0, 0, 1, 1, 0, 0,
                N'incremental', N'${env:OSDU_DATA_DB}', N'OsduData.silver.WellLog');
            INSERT INTO [osdu].[Record] ([DeliveryKey], [FlowId], [SourceKey], [MappingName], [Status], [AttemptCount], [PendingMetadata], [PendingPayload],
                [Blocked], [CreatedUtc], [UpdatedUtc], [TargetId], [LastDeliveredUtc], [PendingDocumentRef], [LastSubmissionId])
            VALUES
                (@delivered, @logs, N'wells:AREA_A_1/L-1001', N'WellLog', N'delivered', 0, 0, 0, 0, @now, @now, N'dev:work-product-component--WellLog:a', @now, NULL, @submission),
                (@queued, @logs, N'wells:AREA_A_1/L-1002', N'WellLog', N'pending', 0, 1, 0, 0, @now, @now, N'dev:work-product-component--WellLog:b', NULL, N'0:0:10', @submission),
                (@held, @wellbores, N'wells:WB-A', N'Wellbore', N'held', 0, 0, 0, 1, @now, @now, N'dev:master-data--Wellbore:c', NULL, NULL, NULL);
            INSERT INTO [osdu].[Attempt] ([DeliveryKey], [SubmissionId], [Worker], [StartedUtc], [CompletedUtc], [Outcome], [Phase])
            VALUES
                (@delivered, @submission, N'w', @now, @now, N'delivered', N'metadata'),
                (@held, NULL, N'intake', @now, @now, N'held', N'render'),
                (@unrecorded, @submission, N'w', @now, @now, N'failed', N'none');
            INSERT INTO [osdu].[Activity] ([FlowId], [FlowName], [Kind], [Actor], [StartedUtc], [Outcome], [DeliveryKey])
            VALUES (@logs, N'welldb-welllog-03-header-delivery', N'release', N'user:tahir', @now, N'completed', @delivered);
            INSERT INTO [osdu].[UpdateTag] ([Kind], [Scope], [TypeName], [ItemId], [Path], [Change], [ToVersion], [Mode], [Status], [SetIds],
                [AffectedRecords], [Processed], [DetectedUtc], [Cursor])
            VALUES (N'cache', N'dev', N'Wellbore', N'dev:master-data--Wellbore:x', N'Name', N'changed', N'v2', N'auto', N'rolling', N'1', 3, 1, @now, @held);
            """,
            ("submission", submission), ("delivered", delivered), ("queued", queued), ("held", held), ("unrecorded", unrecorded));

        await database.MigrateAsync(null);

        // Every attempt names its record's flow; one whose record is missing takes its submission's.
        await using (var db = database.Context())
        {
            var attempts = await db.DeliveryAttempts.AsNoTracking().ToDictionaryAsync(a => a.DeliveryKey, a => a.FlowId);
            Assert.Equal(Logs, attempts[delivered]);
            Assert.Equal(Wellbores, attempts[held]);
            Assert.Equal(Logs, attempts[unrecorded]);

            // The ids a record queued or delivered a document for are claimed; a record only ever held claims nothing.
            var claims = await db.DeliveryRecords.AsNoTracking().ToDictionaryAsync(r => r.DeliveryKey, r => r.ClaimedTargetId);
            Assert.Equal("dev:work-product-component--WellLog:a", claims[delivered]);
            Assert.Equal("dev:work-product-component--WellLog:b", claims[queued]);
            Assert.Null(claims[held]);

            // The rollout's cursor named a record by key, and now names its flow too.
            Assert.Equal(Wellbores, (await db.DeliveryUpdateTags.AsNoTracking().SingleAsync()).CursorFlowId);
        }

        // Every later migration kept the key per flow; the newest leads it with the partition the flow's ledger is kept under.
        Assert.Equal(["PartitionId", "FlowId", "DeliveryKey"], await database.KeyColumnsAsync());

        // The statistics view is back, and the upgraded ledger takes a second flow's record of the same row while it
        // keeps the first flow's OSDU id for that flow.
        var ledger = new OsduLedger(database.Context, TimeProvider.System);
        Assert.Equal((2L, 1L), ((await ledger.StatsAsync(Logs, Now)).Total, (await ledger.StatsAsync(Logs, Now)).Delivered));
        var second = await ledger.UpsertPendingAsync(Wellbores, [Pending(Wellbores, delivered, "dev:master-data--Wellbore:a")]);
        Assert.Equal((1, 0), (second.Staged, second.Conflicts.Count));
        var third = FlowId.Of("wells-welllog-copy");
        await ledger.RegisterAsync(third);
        var conflict = Assert.Single((await ledger.UpsertPendingAsync(third, [Pending(third, delivered, "dev:work-product-component--WellLog:a")])).Conflicts);
        Assert.Equal((Logs, "welldb-welllog-03-header-delivery"), (conflict.OwnerFlowId, conflict.OwnerFlowName));
        Assert.Equal(2, (await ledger.LookupAsync(delivered.ToString(), 10)).Count);

        // Back down is refused while two flows hold records of one key, because the earlier ledger keeps one per key. The
        // migrations back down run in one transaction, so a refusal leaves the ledger as it was, keys and all.
        var refused = await Assert.ThrowsAsync<SqlException>(() => database.MigrateAsync(Before));
        Assert.Contains("have records in more than one flow", refused.Message, StringComparison.Ordinal);
        Assert.Equal(["PartitionId", "FlowId", "DeliveryKey"], await database.KeyColumnsAsync());

        // With one record per key again, the migration goes back down to the key it replaced.
        await database.ExecuteAsync("DELETE FROM [osdu].[Record] WHERE [FlowId] = @wellbores AND [DeliveryKey] = @delivered;", ("delivered", delivered));
        await database.MigrateAsync(Before);
        Assert.Equal(["DeliveryKey"], await database.KeyColumnsAsync());
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM sys.views WHERE [name] = N'RecordCount' AND SCHEMA_NAME([schema_id]) = N'osdu';"));
    }

    [Fact]
    public async Task An_attempt_that_no_record_or_submission_places_stops_the_migration_and_says_how_to_find_it()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(Before);
        await database.ExecuteAsync(
            """
            INSERT INTO [osdu].[Attempt] ([DeliveryKey], [SubmissionId], [Worker], [StartedUtc], [CompletedUtc], [Outcome], [Phase])
            VALUES (@orphan, NULL, N'w', @now, @now, N'failed', N'none');
            """,
            ("orphan", Guid.NewGuid()));

        var failed = await Assert.ThrowsAsync<SqlException>(() => database.MigrateAsync(null));
        Assert.Contains("1 attempt(s) in [osdu].[Attempt] belong to no record and no submission", failed.Message, StringComparison.Ordinal);
        Assert.Contains("WHERE [FlowId] IS NULL", failed.Message, StringComparison.Ordinal);

        // The migration is undone as a whole: the ledger is still the one it started from.
        Assert.Equal(["DeliveryKey"], await database.KeyColumnsAsync());
        Assert.Equal(0L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[osdu].[Attempt]') AND [name] = N'FlowId';"));
    }

    [Fact]
    public async Task A_cache_declaration_written_before_origins_existed_is_an_osdu_type_afterwards_and_the_migration_goes_back()
    {
        const string BeforeLookups = "20260923132848_CacheSystemProperties";
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(BeforeLookups);
        var id = Guid.NewGuid();
        await database.ExecuteAsync(
            """
            INSERT INTO [osdu].[CacheDefinition] ([Id], [RepoId], [FlowName], [Scope], [Endpoint], [RelativePath], [Name], [EntityType], [Kind], [Query],
                [FieldsJson], [OnChange], [FirstSeenUtc], [LastSeenUtc])
            VALUES (@id, @logs, N'welldb-osdu-00-reference-cache', N'dev', N'${env:OSDU_URL}', N'cache/wells.yaml', N'UnitOfMeasure',
                N'reference-data--UnitOfMeasure', N'osdu:wks:reference-data--UnitOfMeasure:*', N'*', N'[]', N'auto', @now, @now);
            """,
            ("id", id));

        await database.MigrateAsync(null);
        await using (var db = database.Context())
        {
            var row = await db.DeliveryCacheDefinitions.SingleAsync(d => d.Id == id);
            Assert.Equal("osdu", row.Origin);
            Assert.Equal("${env:OSDU_URL}", row.Endpoint);
            Assert.Equal("osdu:wks:reference-data--UnitOfMeasure:*", row.Kind);
            Assert.Null(row.Connection);
            Assert.Null(row.SourceObject);
            Assert.Null(row.KeyField);
            Assert.Null(row.DictionaryPath);
        }

        // Going back drops the origin columns and keeps the declaration an OSDU type always was.
        await database.MigrateAsync(BeforeLookups);
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM [osdu].[CacheDefinition] WHERE [Kind] = N'osdu:wks:reference-data--UnitOfMeasure:*'"));
        Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[osdu].[CacheDefinition]') AND [name] = N'Origin'"));
    }

    [Fact]
    public async Task Two_records_holding_one_osdu_id_stop_the_migration()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(Before);
        await database.ExecuteAsync(
            """
            INSERT INTO [osdu].[Record] ([DeliveryKey], [FlowId], [SourceKey], [MappingName], [Status], [AttemptCount], [PendingMetadata], [PendingPayload],
                [Blocked], [CreatedUtc], [UpdatedUtc], [TargetId], [LastDeliveredUtc])
            VALUES
                (@first, @logs, N'a', N'WellLog', N'delivered', 0, 0, 0, 0, @now, @now, N'dev:work-product-component--WellLog:same', @now),
                (@second, @wellbores, N'b', N'WellLog', N'delivered', 0, 0, 0, 0, @now, @now, N'dev:work-product-component--WellLog:same', @now);
            """,
            ("first", Guid.NewGuid()), ("second", Guid.NewGuid()));

        var failed = await Assert.ThrowsAsync<SqlException>(() => database.MigrateAsync(null));
        Assert.Contains("1 OSDU id(s) are held by more than one record", failed.Message, StringComparison.Ordinal);
        Assert.Equal(["DeliveryKey"], await database.KeyColumnsAsync());
    }

    [Fact]
    public async Task Leases_in_flight_become_lease_rows_and_going_back_waits_until_every_appended_event_is_applied()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.AllowSnapshotAsync();
        await database.MigrateAsync(BeforeLeases);

        var submission = Guid.NewGuid();
        var run = Guid.NewGuid();
        var inBatch = Guid.NewGuid();
        var retried = Guid.NewGuid();
        var retriedLater = Guid.NewGuid();
        var idle = Guid.NewGuid();
        var batchToken = "node-a/" + Guid.NewGuid().ToString("N");
        var retryToken = "node-b/" + Guid.NewGuid().ToString("N");
        await database.ExecuteAsync(
            """
            INSERT INTO [osdu].[WorkBatch] ([SubmissionId], [Index], [FlowId], [Location], [RecordCount], [Status], [CreatedUtc], [StartedUtc], [RunId],
                [LeaseOwner], [LeaseExpiresUtc], [Delivered], [Held], [Failed], [Retrying])
            VALUES (@submission, 0, @logs, N'work/0', 1, N'running', @now, @now, @run, @batchToken, DATEADD(MINUTE, 5, @now), 0, 0, 0, 0);
            INSERT INTO [osdu].[Record] ([FlowId], [DeliveryKey], [SourceKey], [MappingName], [Status], [AttemptCount], [PendingMetadata], [PendingPayload],
                [Blocked], [CreatedUtc], [UpdatedUtc], [LastSubmissionId], [PendingDocumentRef], [WorkBatch], [LeaseOwner], [LeaseExpiresUtc])
            VALUES
                (@logs, @inBatch, N'a', N'WellLog', N'delivering', 1, 1, 0, 0, @now, @now, @submission, N'0:0:10', 0, @batchToken, DATEADD(MINUTE, 5, @now)),
                (@logs, @retried, N'b', N'WellLog', N'delivering', 2, 1, 0, 0, @now, @now, @submission, N'0:10:10', NULL, @retryToken, DATEADD(MINUTE, -1, @now)),
                (@logs, @retriedLater, N'c', N'WellLog', N'delivering', 2, 1, 0, 0, @now, DATEADD(SECOND, 1, @now), @submission, N'0:20:10', NULL, @retryToken, DATEADD(MINUTE, 1, @now)),
                (@logs, @idle, N'd', N'WellLog', N'pending', 0, 1, 0, 0, @now, @now, @submission, N'0:30:10', NULL, NULL, NULL);
            """,
            ("submission", submission), ("run", run), ("inBatch", inBatch), ("retried", retried), ("retriedLater", retriedLater), ("idle", idle),
            ("batchToken", batchToken), ("retryToken", retryToken));

        await database.MigrateAsync(null);

        // The batch's lease keeps its batch, run and expiry; the retry claim's records make one lease, expiring with the last.
        await using (var db = database.Context())
        {
            var leases = await db.DeliveryLeases.AsNoTracking().ToDictionaryAsync(l => l.Token);
            Assert.Equal(2, leases.Count);
            var batch = leases[batchToken];
            Assert.Equal(
                (Logs, (Guid?)submission, (int?)0, "node-a", (Guid?)run, Now, Now.AddMinutes(5)),
                (batch.FlowId, batch.SubmissionId, batch.WorkBatch, batch.Owner, batch.RunId, batch.AcquiredUtc, batch.ExpiresUtc));
            var retry = leases[retryToken];
            Assert.Equal(
                (Logs, (Guid?)null, (int?)null, "node-b", (Guid?)null, Now.AddSeconds(1), Now.AddMinutes(1)),
                (retry.FlowId, retry.SubmissionId, retry.WorkBatch, retry.Owner, retry.RunId, retry.AcquiredUtc, retry.ExpiresUtc));
        }

        Assert.Equal(0L, await database.ScalarAsync(
            "SELECT COUNT_BIG(*) FROM sys.columns WHERE [name] = N'LeaseExpiresUtc' AND [object_id] IN (OBJECT_ID(N'[osdu].[Record]'), OBJECT_ID(N'[osdu].[WorkBatch]'));"));

        // The ledger reads them as its own.
        var ledger = new OsduLedger(database.Context, TimeProvider.System);
        var leased = await ledger.GetRecordAsync(Logs, new DeliveryKey(retried));
        Assert.Equal((retryToken, (DateTime?)Now.AddMinutes(1)), (leased!.LeaseOwner, leased.LeaseExpiresUtc));
        Assert.Equal(Now.AddMinutes(1), await ledger.NextLeaseExpiryAsync(Logs, null));

        // A worker appends under the batch's lease, and going back is refused while that is not on the record.
        const string Step = "{\"upload\":{\"done\":\"1\"}}";
        await ledger.AppendAsync(Logs, batchToken, new LeaseAppend([new RecordStep(new DeliveryKey(inBatch), submission, "0:0:10", Step, Now)], []));
        var refused = await Assert.ThrowsAsync<SqlException>(() => database.MigrateAsync(BeforeLeases));
        Assert.Contains("1 delivery event(s) in osdu.RecordEvent have not been applied to their records", refused.Message, StringComparison.Ordinal);
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[RecordEvent];"));

        // Once the lease applies it, the migration goes back and puts each lease's expiry on its batch and its records.
        Assert.Equal(LeaseApplied.None, await ledger.CheckpointLeaseAsync(batchToken, Now));
        Assert.Equal(Step, (await ledger.GetRecordAsync(Logs, new DeliveryKey(inBatch)))!.PendingStepJson);
        await database.MigrateAsync(BeforeLeases);
        const string Expiry = "SELECT DATEDIFF(SECOND, @now, [LeaseExpiresUtc]) FROM [osdu].[Record] WHERE [DeliveryKey] = @key;";
        Assert.Equal(300L, await database.ScalarAsync(Expiry, ("key", inBatch)));
        Assert.Equal(60L, await database.ScalarAsync(Expiry, ("key", retried)));
        Assert.Equal(60L, await database.ScalarAsync(Expiry, ("key", retriedLater)));
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[Record] WHERE [DeliveryKey] = @key AND [LeaseExpiresUtc] IS NULL;", ("key", idle)));
        Assert.Equal(300L, await database.ScalarAsync("SELECT DATEDIFF(SECOND, @now, [LeaseExpiresUtc]) FROM [osdu].[WorkBatch] WHERE [SubmissionId] = @submission;", ("submission", submission)));
        Assert.Equal(0L, await database.ScalarAsync(
            "SELECT COUNT_BIG(*) FROM sys.tables WHERE [name] IN (N'Lease', N'RecordEvent') AND SCHEMA_NAME([schema_id]) = N'osdu';"));
    }

    /// <summary>
    /// The ledger asks nothing of the database beyond its own schema. It used to read every listing, wait and claim in
    /// a snapshot transaction, so a database that did not allow snapshot isolation stopped every node from claiming any
    /// work at all: a setting the product never set and never checked, on a database the control plane creates itself.
    /// A worker writes the record table only when it claims, checkpoints, closes or recovers a lease, and a record and
    /// its lease are read in one statement, so the reads need no isolation level of their own.
    /// </summary>
    [Fact]
    public async Task A_ledger_claims_delivers_and_lists_on_a_database_that_does_not_allow_snapshot_isolation()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(null);
        Assert.Equal(0L, await database.ScalarAsync(
            $"SELECT COUNT_BIG(*) FROM sys.databases WHERE [name] = N'{database.Name}' AND [snapshot_isolation_state] = 1;"));

        var ledger = new OsduLedger(database.Context, TimeProvider.System);
        await ledger.RegisterAsync(Logs);
        var key = Guid.NewGuid();
        Assert.Equal(1, (await ledger.UpsertPendingAsync(Logs, [Pending(Logs, key, "dev:work-product-component--WellLog:s")])).Staged);

        // The claim is the read path that used to fail first, before a node took any work.
        var claimed = await ledger.ClaimAsync(Logs, null, "w1", 10, TimeSpan.FromMinutes(5), Now);
        Assert.Single(claimed.Records);
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[Lease];"));

        // And the listing reads the record with the expiry of the lease that holds it, in one statement.
        var listed = await ledger.GetRecordAsync(Logs, new DeliveryKey(key));
        Assert.NotNull(listed);
        Assert.Equal(RecordStatus.Delivering, listed.Status);
        Assert.NotNull(listed.LeaseExpiresUtc);
    }

    [Fact]
    public async Task A_ledger_written_before_records_could_wait_takes_the_columns_and_waits_after_the_migration()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.AllowSnapshotAsync();
        await database.MigrateAsync(BeforeWaits);

        // A record written by the schema before waits existed: it refers to nothing, because nothing recorded what it refers to.
        var wellbore = Guid.NewGuid();
        await database.ExecuteAsync(
            """
            INSERT INTO [osdu].[Record] ([DeliveryKey], [FlowId], [SourceKey], [MappingName], [Status], [AttemptCount], [PendingMetadata], [PendingPayload],
                [Blocked], [CreatedUtc], [UpdatedUtc], [TargetId], [ClaimedTargetId], [PendingDocumentRef])
            VALUES (@wellbore, @wellbores, N'wells:WB-A', N'Wellbore', N'pending', 0, 1, 0, 0, @now, @now,
                N'dev:master-data--Wellbore:a', N'dev:master-data--Wellbore:a', N'0:0:10');
            """,
            ("wellbore", wellbore));

        await database.MigrateAsync(null);

        // The columns are there and empty, and the ledger waits on the upgraded database as it does on a new one.
        Assert.Equal(0L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[Record] WHERE [PendingReferences] IS NOT NULL OR [WaitingFor] IS NOT NULL;"));
        Assert.Equal(0L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[Submission] WHERE [Waiting] <> 0;"));
        var ledger = new OsduLedger(database.Context, TimeProvider.System);
        await ledger.RegisterAsync(Logs);
        var log = Guid.NewGuid();
        Assert.Equal(1, (await ledger.UpsertPendingAsync(
            Logs,
            [Pending(Logs, log, "dev:work-product-component--WellLog:s") with
            {
                PendingReferences = [new RecordReference("dev:master-data--Wellbore:a", "data.WellboreID")],
            }])).Staged);

        var claim = await ledger.ClaimAsync(Logs, null, "w1", 10, TimeSpan.FromMinutes(5), Now);
        Assert.Empty(claim.Records);
        Assert.Equal("dev:master-data--Wellbore:a", Assert.Single(claim.Waiting).WaitingFor);
        Assert.Equal(1, (await ledger.StatsAsync(Logs, Now)).Waiting);

        // The record written before the migration still delivers, and its delivery releases what waited for it.
        var wellboreClaim = await ledger.ClaimAsync(Wellbores, null, "w2", 10, TimeSpan.FromMinutes(5), Now);
        await ledger.CompleteAsync(Wellbores, new RecordCompletion
        {
            DeliveryKey = new DeliveryKey(wellbore),
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 1,
            Claimed = ClaimedWork.Of(Assert.Single(wellboreClaim.Records)),
            Attempt = new AttemptRecord
            {
                DeliveryKey = new DeliveryKey(wellbore),
                Worker = "w2",
                StartedUtc = Now,
                CompletedUtc = Now,
                Outcome = AttemptOutcome.Delivered,
                Phase = "metadata",
            },
        });

        Assert.Equal(RecordStatus.Pending, (await ledger.GetRecordAsync(Logs, new DeliveryKey(log)))!.Status);
    }

    /// <summary>
    /// <c>LedgerPartitions</c> over a ledger written before it: every ledger placed in the partition its interface rows name,
    /// else the one its records' OSDU ids name, else left unassigned for its next run; every row moved under its ledger's
    /// partition; every key and index rebuilt exactly as the model declares them; and back down to the keys it replaced,
    /// with nothing lost either way.
    /// </summary>
    [Fact]
    public async Task The_ledger_is_keyed_by_partition_with_every_ledger_placed_from_its_interfaces_or_its_records()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(BeforePartitionKeys);
        var logA = Guid.NewGuid();
        var logB = Guid.NewGuid();
        var wellbore = Guid.NewGuid();
        var mixedDev = Guid.NewGuid();
        var mixedTest = Guid.NewGuid();
        var submission = Guid.NewGuid();
        await database.ExecuteAsync(
            """
            INSERT INTO [osdu].[Interface] ([Id], [RepoId], [FlowName], [Interface], [Partition], [Ordinal], [LedgerFlowId], [LedgerName], [Route],
                [MappingReference], [Kind], [RecordObject], [AfterJson], [RelativePath], [Active], [FirstSeenUtc], [LastSeenUtc])
            VALUES
                (NEWID(), NEWID(), N'wells', N'welllogs', N'test', 0, @logs, N'wells/welllogs@test', N'storage', N'WellLog@1.4.0', N'', N'OsduData.silver.WellLog',
                    N'[]', N'flows/wells.yaml', 1, @now, @now),
                (NEWID(), NEWID(), N'wellbores', N'', N'', 0, @wellbores, N'wellbores', N'storage', N'Wellbore@1.0.0', N'', N'OsduData.silver.Wellbore',
                    N'[]', N'flows/wellbores.yaml', 1, @now, @now);
            INSERT INTO [osdu].[Record] ([DeliveryKey], [FlowId], [SourceKey], [MappingName], [Status], [AttemptCount], [PendingMetadata], [PendingPayload],
                [Blocked], [CreatedUtc], [UpdatedUtc], [TargetId], [ClaimedTargetId], [LastSubmissionId])
            VALUES
                (@logA, @logs, N'wells:L-1', N'WellLog', N'delivered', 0, 0, 0, 0, @now, @now, N'test:work-product-component--WellLog:a', N'test:work-product-component--WellLog:a', @submission),
                (@logB, @logs, N'wells:L-2', N'WellLog', N'delivered', 0, 0, 0, 0, @now, @now, N'test:work-product-component--WellLog:b', N'test:work-product-component--WellLog:b', @submission),
                (@wellbore, @wellbores, N'wells:WB-A', N'Wellbore', N'delivered', 0, 0, 0, 0, @now, @now, N'dev:master-data--Wellbore:a', N'dev:master-data--Wellbore:a', NULL),
                (@mixedDev, @mixed, N'wells:M-1', N'Wellbore', N'delivered', 0, 0, 0, 0, @now, @now, N'dev:master-data--Wellbore:m1', N'dev:master-data--Wellbore:m1', NULL),
                (@mixedTest, @mixed, N'wells:M-2', N'Wellbore', N'delivered', 0, 0, 0, 0, @now, @now, N'test:master-data--Wellbore:m2', N'test:master-data--Wellbore:m2', NULL);
            INSERT INTO [osdu].[Submission] ([SubmissionId], [FlowId], [FlowName], [MappingReference], [RenderContext], [ParametersJson], [RecordCount],
                [BatchCount], [Slices], [Status], [ReceivedUtc], [Planned], [SkippedUnchanged], [AwaitingApproval], [SkippedStale], [UnchangedAtPush],
                [Blocked], [Delivered], [Held], [Failed], [Untracked], [Kind], [SourceConnection], [SourceObject])
            VALUES (@submission, @logs, N'wells/welllogs@test', N'WellLog@1.4.0', N'{}', N'{}', 2, 1, 1, N'completed', @now, 2, 0, 0, 0, 0, 0, 2, 0, 0, 0,
                N'incremental', N'${env:OSDU_DATA_DB}', N'OsduData.silver.WellLog');
            INSERT INTO [osdu].[Attempt] ([DeliveryKey], [FlowId], [SubmissionId], [Worker], [StartedUtc], [CompletedUtc], [Outcome], [Phase])
            VALUES (@logA, @logs, @submission, N'w', @now, @now, N'delivered', N'metadata'), (@wellbore, @wellbores, NULL, N'w', @now, @now, N'delivered', N'metadata');
            INSERT INTO [osdu].[Activity] ([FlowId], [FlowName], [Kind], [Actor], [StartedUtc], [Outcome], [DeliveryKey])
            VALUES (@wellbores, N'wellbores', N'release', N'user:tahir', @now, N'completed', @wellbore), (@mixed, N'wells-mixed-delivery', N'deliver', N'service:schedule', @now, N'completed', NULL);
            INSERT INTO [osdu].[Retrieval] ([FlowId], [FlowName], [Actor], [Kinds], [Location], [Status], [Records], [Files], [Bytes], [StartedUtc])
            VALUES (@retrieved, N'wells-retrieval', N'service:schedule', N'master-data--Well:1.0.0', N'out/wells', N'done', 3, 1, 100, @now);
            """,
            ("logA", logA), ("logB", logB), ("wellbore", wellbore), ("mixedDev", mixedDev), ("mixedTest", mixedTest), ("submission", submission));

        await database.MigrateAsync(null);

        // Every ledger is in the directory: placed by its interface rows, else by its records' ids, else unassigned.
        var ledger = new OsduLedger(database.Context, TimeProvider.System);
        Assert.Equal("test", (await ledger.GetLedgerAsync(Logs))!.Partition);
        Assert.Equal("wells/welllogs@test", (await ledger.GetLedgerAsync(Logs))!.LedgerName);
        Assert.Equal("dev", (await ledger.GetLedgerAsync(Wellbores))!.Partition);
        Assert.Null((await ledger.GetLedgerAsync(Mixed))!.Partition);
        var retrieval = (await ledger.GetLedgerAsync(Retrieved))!;
        Assert.Equal((LedgerKinds.Retrieval, "wells-retrieval", (string?)null), (retrieval.Kind, retrieval.FlowName, retrieval.Partition));

        // Every row is under its ledger's partition, and each ledger reads as it did.
        Assert.Equal(2, (await ledger.StatsAsync(Logs, Now)).Total);
        Assert.Equal("test", (await ledger.GetRecordAsync(Logs, new DeliveryKey(logA)))!.Partition);
        Assert.Equal("test", (await ledger.GetSubmissionAsync(submission))!.Partition);
        Assert.Single(await ledger.ListAttemptsAsync(Logs, new DeliveryKey(logA), 10));
        Assert.Equal("dev", Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = Wellbores })).Partition);
        Assert.Equal(2, (await ledger.StatsAsync(Mixed, Now)).Total);
        Assert.Equal(0L, await database.ScalarAsync(
            "SELECT COUNT_BIG(*) FROM [osdu].[Record] AS r INNER JOIN [osdu].[Ledger] AS l ON l.[FlowId] = r.[FlowId] WHERE l.[PartitionId] <> r.[PartitionId];"));
        Assert.Single(await ledger.ListRecentAsync(10, partition: "dev"));
        Assert.Equal(2, (await ledger.ListRecentAsync(10, partition: "test")).Count);
        Assert.Equal(5, (await ledger.ListRecentAsync(10)).Count);

        // The unplaced ledger is adopted by the partition its records went to alone; it holds records of two, so neither.
        var refused = await Assert.ThrowsAsync<DeliveryException>(() => ledger.RegisterLedgerAsync(new LedgerEntry
        {
            FlowId = Mixed, Partition = "dev", Kind = LedgerKinds.Delivery, FlowName = "wells-mixed-delivery", LedgerName = "wells-mixed-delivery",
        }));
        Assert.Contains("1 record(s) delivered to 'test'", refused.Message, StringComparison.Ordinal);

        // Every key leads with the partition, and every index is exactly the one the model declares.
        Assert.Equal(["PartitionId", "FlowId", "DeliveryKey"], await database.KeyColumnsAsync());
        foreach (var table in LedgerTables)
        {
            Assert.Equal("PartitionId", (await database.PrimaryKeyAsync(table))[0]);
        }

        await using (var db = database.Context())
        {
            var tables = LedgerTables.Append("Ledger").Append("LedgerPartition").Append("UpdateTag").ToList();
            Assert.Equal(ModelIndexes(db, tables), await database.IndexesAsync(tables));
        }

        Assert.Equal(3L, await database.ScalarAsync(
            "SELECT COUNT_BIG(*) FROM sys.indexes WHERE [object_id] IN (OBJECT_ID(N'[osdu].[Attempt]'), OBJECT_ID(N'[osdu].[RecordEvent]')) AND [optimize_for_sequential_key] = 1;"));

        // Back down, the keys it replaced return with every row, and up again the ledger is placed the same way.
        await database.MigrateAsync(BeforePartitionKeys);
        Assert.Equal(["FlowId", "DeliveryKey"], await database.KeyColumnsAsync());
        Assert.Equal(0L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM sys.columns WHERE [name] = N'PartitionId' AND [object_id] = OBJECT_ID(N'[osdu].[Record]');"));
        Assert.Equal(0L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM sys.tables WHERE [name] IN (N'Ledger', N'LedgerPartition') AND SCHEMA_NAME([schema_id]) = N'osdu';"));
        Assert.Equal(5L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[Record];"));
        Assert.Equal(2L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[Attempt];"));

        await database.MigrateAsync(null);
        var again = new OsduLedger(database.Context, TimeProvider.System);
        Assert.Equal(("test", "dev"), ((await again.GetLedgerAsync(Logs))!.Partition, (await again.GetLedgerAsync(Wellbores))!.Partition));
        Assert.Equal(5, (await again.ListRecentAsync(10)).Count);
    }

    [Fact]
    public async Task A_run_that_changed_nothing_before_idle_was_recorded_is_marked_idle_from_what_the_ledger_says()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(BeforeIdle);
        var quiet = Guid.NewGuid();
        var busy = Guid.NewGuid();
        var resent = Guid.NewGuid();
        var resentRun = Guid.NewGuid();
        await database.ExecuteAsync(
            """
            INSERT INTO [osdu].[Submission] ([PartitionId], [SubmissionId], [FlowId], [FlowName], [MappingReference], [RenderContext], [ParametersJson],
                [RecordCount], [BatchCount], [Slices], [Status], [ReceivedUtc], [Planned], [SkippedUnchanged], [AwaitingApproval], [SkippedStale],
                [UnchangedAtPush], [Blocked], [Delivered], [Held], [Failed], [Untracked], [Kind], [SourceConnection], [SourceObject])
            VALUES
                (1, @quiet, @logs, N'logs', N'WellLog@1.4.0', N'{}', N'{}', 5, 0, 0, N'completed', @now, 0, 5, 0, 0, 0, 0, 0, 0, 0, 0,
                    N'full', N'${env:OSDU_DATA_DB}', N'OsduData.silver.WellLog'),
                (1, @busy, @logs, N'logs', N'WellLog@1.4.0', N'{}', N'{}', 2, 1, 1, N'completed', @now, 2, 0, 0, 0, 0, 0, 2, 0, 0, 0,
                    N'incremental', N'${env:OSDU_DATA_DB}', N'OsduData.silver.WellLog'),
                (1, @resent, @logs, N'logs', N'WellLog@1.4.0', N'{}', N'{}', 0, 0, 0, N'completed', @now, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                    N'incremental', N'${env:OSDU_DATA_DB}', N'OsduData.silver.WellLog');
            INSERT INTO [osdu].[Attempt] ([PartitionId], [DeliveryKey], [FlowId], [SubmissionId], [RunId], [Worker], [StartedUtc], [CompletedUtc], [Outcome], [Phase])
            VALUES (1, NEWID(), @logs, @busy, @resentRun, N'w', @now, @now, N'delivered', N'metadata');
            INSERT INTO [osdu].[Activity] ([PartitionId], [FlowId], [FlowName], [Kind], [Actor], [StartedUtc], [Outcome], [SubmissionId], [RunId], [Summary])
            VALUES
                (1, @logs, N'logs', N'deliver', N'unknown', @now, N'completed', @quiet, NEWID(), N'quiet'),
                (1, @logs, N'logs', N'deliver', N'unknown', @now, N'completed', @busy, NEWID(), N'busy'),
                (1, @logs, N'logs', N'deliver', N'unknown', @now, N'completed', @resent, @resentRun, N'resent'),
                (1, @logs, N'logs', N'deliver', N'unknown', @now, N'failed', @quiet, NEWID(), N'failed'),
                (1, @logs, N'logs', N'release', N'user:tahir', @now, N'completed', @quiet, NEWID(), N'released');
            """,
            ("quiet", quiet), ("busy", busy), ("resent", resent), ("resentRun", resentRun));

        await database.MigrateAsync(null);

        // Only the completed run whose submission did nothing, and under whose run no attempt was made, is idle: not the one
        // that delivered, nor the one that sent another submission's record, nor the failed run, nor the intervention.
        const string Idle = "SELECT COUNT_BIG(*) FROM [osdu].[Activity] WHERE [Idle] = 1;";
        Assert.Equal(1L, await database.ScalarAsync(Idle));
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[Activity] WHERE [Idle] = 1 AND [Summary] = N'quiet';"));
        await using (var db = database.Context())
        {
            Assert.Equal(ModelIndexes(db, ["Activity"]), await database.IndexesAsync(["Activity"]));
        }

        // Back down, the column and its index go with it; up again, the same run is marked.
        await database.MigrateAsync(BeforeIdle);
        Assert.Equal(0L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM sys.columns WHERE [name] = N'Idle' AND [object_id] = OBJECT_ID(N'[osdu].[Activity]');"));
        Assert.Equal(5L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[Activity];"));
        await database.MigrateAsync(null);
        Assert.Equal(1L, await database.ScalarAsync(Idle));
    }

    [Fact]
    public async Task The_tables_of_dimension_flows_are_added_keyed_by_partition_and_go_back_with_the_migration()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(BeforeDimensions);
        const string Tables = "SELECT COUNT_BIG(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'osdu' AND t.name LIKE N'Dimension%';";
        Assert.Equal(0L, await database.ScalarAsync(Tables));

        await database.MigrateAsync(null);

        Assert.Equal(13L, await database.ScalarAsync(Tables));
        foreach (var table in DimensionTables)
        {
            Assert.Equal("PartitionId", (await database.PrimaryKeyAsync(table))[0]);
        }

        await using (var db = database.Context())
        {
            Assert.Equal(ModelIndexes(db, DimensionTables), await database.IndexesAsync(DimensionTables));
        }

        // A clean value and an original compare exactly: GR and gr are two members, as a filter asks for them.
        const string Binary = "SELECT COUNT_BIG(*) FROM sys.columns WHERE [collation_name] = N'Latin1_General_100_BIN2' AND ([object_id] = OBJECT_ID(N'[osdu].[DimensionMember]') AND [name] = N'Value' OR [object_id] = OBJECT_ID(N'[osdu].[DimensionValue]') AND [name] = N'Original');";
        Assert.Equal(2L, await database.ScalarAsync(Binary));

        await database.MigrateAsync(BeforeDimensions);
        Assert.Equal(0L, await database.ScalarAsync(Tables));
        await database.MigrateAsync(null);
        Assert.Equal(13L, await database.ScalarAsync(Tables));
    }

    [Fact]
    public async Task A_dimension_built_before_incremental_loads_keeps_its_builds_as_full_loads_and_the_new_tables_go_back_with_the_migration()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(BeforeIncrementalLoads);
        await database.ExecuteAsync("""
            INSERT INTO [osdu].[DimensionRun] ([PartitionId], [DimensionId], [FlowId], [Actor], [Status], [DefinitionHash], [Nulls], [Unreadable], [Members], [Originals],
                [LeftOut], [Unfilterable], [MembersAdded], [MembersRemoved], [MembersRestored], [OriginalsAdded], [OriginalsRemoved], [OriginalsMoved], [OriginalsRestored],
                [Aggregations], [Slices], [Splits], [ScannedSlices], [ScanPages], [ScannedUnits], [CountQueries], [Labelled], [Unlabelled], [LabelQueries], [StartedUtc])
            VALUES (1, 1, NEWID(), N'tests', N'completed', N'0123456789abcdef', 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, SYSUTCDATETIME());
            """);

        await database.MigrateAsync(null);

        // A build made before is a full load, and kept no window, no record key and none of the records it read: an
        // incremental load cannot read on from it, so the dimension's next build loads it in full.
        Assert.Equal(1L, await database.ScalarAsync(
            "SELECT COUNT_BIG(*) FROM [osdu].[DimensionRun] WHERE [Mode] = N'full' AND [WindowTo] IS NULL AND [RecordKey] IS NULL AND [ChangedRecords] IS NULL AND [TouchedKeys] IS NULL;"));
        string[] tables = ["DimensionKeyRecord", "DimensionRecord"];
        foreach (var table in tables)
        {
            Assert.Equal("PartitionId", (await database.PrimaryKeyAsync(table))[0]);
        }

        await using (var db = database.Context())
        {
            Assert.Equal(ModelIndexes(db, [.. tables, "Dimension", "DimensionRun"]), await database.IndexesAsync([.. tables, "Dimension", "DimensionRun"]));
        }

        const string Columns = "SELECT COUNT_BIG(*) FROM sys.columns WHERE ([object_id] = OBJECT_ID(N'[osdu].[DimensionRun]') AND [name] IN (N'Mode', N'WindowFrom', N'WindowTo', N'ChangedRecords', N'TouchedKeys', N'RecordKey')) OR ([object_id] = OBJECT_ID(N'[osdu].[Dimension]') AND [name] IN (N'LastFullRunId', N'LastFullBuiltUtc'));";
        Assert.Equal(8L, await database.ScalarAsync(Columns));
        const string New = "SELECT COUNT_BIG(*) FROM sys.tables WHERE [name] IN (N'DimensionKeyRecord', N'DimensionRecord') AND [schema_id] = SCHEMA_ID(N'osdu');";
        Assert.Equal(2L, await database.ScalarAsync(New));

        await database.MigrateAsync(BeforeIncrementalLoads);
        Assert.Equal((0L, 0L), (await database.ScalarAsync(Columns), await database.ScalarAsync(New)));
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[DimensionRun];"));
        await database.MigrateAsync(null);
        Assert.Equal((8L, 2L), (await database.ScalarAsync(Columns), await database.ScalarAsync(New)));
    }

    [Fact]
    public async Task A_dimension_written_before_labels_keeps_its_keys_and_takes_the_label_and_filter_columns_empty()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(BeforeDimensionLabels);
        await database.ExecuteAsync("""
            INSERT INTO [osdu].[LedgerPartition] ([Name], [CreatedUtc]) VALUES (N'dev', SYSUTCDATETIME());
            DECLARE @p smallint = (SELECT [PartitionId] FROM [osdu].[LedgerPartition] WHERE [Name] = N'dev');
            INSERT INTO [osdu].[Dimension] ([PartitionId], [FlowId], [FlowName], [Name], [Kind], [Path], [Repeats], [CleanJson], [DefinitionHash], [Members], [Originals], [CreatedUtc])
            VALUES (@p, NEWID(), N'wells', N'Wellbore', N'osdu:wks:work-product-component--WellLog:1.4.0', N'data.WellboreID', 0, N'[]', N'0123456789abcdef', 1, 1, SYSUTCDATETIME());
            DECLARE @d int = SCOPE_IDENTITY();
            INSERT INTO [osdu].[DimensionValue] ([PartitionId], [DimensionId], [Original], [OriginalHash], [Count], [Filterable], [FirstSeenRunId], [FirstSeenUtc], [MemberSinceRunId])
            VALUES (@p, @d, N'dev:master-data--Wellbore:1:', HASHBYTES('SHA2_256', CAST(N'x' AS varbinary(max))), 4, 1, 1, SYSUTCDATETIME(), 1);
            """);

        await database.MigrateAsync(null);

        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[DimensionValue] WHERE [Label] IS NULL AND [LabelFrom] IS NULL AND [Filter] IS NULL AND [Count] = 4;"));
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[Dimension] WHERE [LabelJson] IS NULL;"));
        await using (var db = database.Context())
        {
            Assert.Equal(ModelIndexes(db, DimensionTables), await database.IndexesAsync(DimensionTables));
        }

        await database.MigrateAsync(BeforeDimensionLabels);
        Assert.Equal(0L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[osdu].[DimensionValue]') AND [name] IN (N'Label', N'LabelFrom', N'Filter');"));
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[DimensionValue];"));
    }

    [Fact]
    public async Task A_dimension_written_before_attributes_keeps_its_keys_and_takes_no_attribute_until_it_is_built_again()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(BeforeDimensionAttributes);
        await database.ExecuteAsync("""
            INSERT INTO [osdu].[LedgerPartition] ([Name], [CreatedUtc]) VALUES (N'dev', SYSUTCDATETIME());
            DECLARE @p smallint = (SELECT [PartitionId] FROM [osdu].[LedgerPartition] WHERE [Name] = N'dev');
            INSERT INTO [osdu].[Dimension] ([PartitionId], [FlowId], [FlowName], [Name], [Kind], [Path], [Repeats], [CleanJson], [LabelJson], [DefinitionHash], [Members], [Originals], [CreatedUtc])
            VALUES (@p, NEWID(), N'wells', N'Wellbore', N'osdu:wks:work-product-component--WellLog:1.4.0', N'data.WellboreID', 0, N'[]', N'["data.FacilityName"]', N'0123456789abcdef', 1, 1, SYSUTCDATETIME());
            DECLARE @d int = SCOPE_IDENTITY();
            INSERT INTO [osdu].[DimensionValue] ([PartitionId], [DimensionId], [Original], [OriginalHash], [Label], [Count], [Filterable], [FirstSeenRunId], [FirstSeenUtc], [MemberSinceRunId])
            VALUES (@p, @d, N'dev:master-data--Wellbore:1:', HASHBYTES('SHA2_256', CAST(N'x' AS varbinary(max))), N'WB A/1-A', 4, 1, 1, SYSUTCDATETIME(), 1);
            """);

        await database.MigrateAsync(null);

        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[DimensionValue] WHERE [Label] = N'WB A/1-A' AND [Count] = 4;"));
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[Dimension] WHERE [AttributesJson] IS NULL;"));
        Assert.Equal(0L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[DimensionAttribute];"));
        Assert.Equal("PartitionId", (await database.PrimaryKeyAsync("DimensionAttribute"))[0]);

        // An attribute's name and value compare exactly, as a lookup by them asks.
        const string Binary = "SELECT COUNT_BIG(*) FROM sys.columns WHERE [collation_name] = N'Latin1_General_100_BIN2' AND ([object_id] = OBJECT_ID(N'[osdu].[DimensionAttribute]') AND [name] = N'Value' OR [object_id] = OBJECT_ID(N'[osdu].[DimensionAttributeName]') AND [name] = N'Name');";
        Assert.Equal(2L, await database.ScalarAsync(Binary));
        await using (var db = database.Context())
        {
            Assert.Equal(ModelIndexes(db, DimensionTables), await database.IndexesAsync(DimensionTables));
        }

        await database.MigrateAsync(BeforeDimensionAttributes);
        Assert.Equal(0L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'osdu' AND t.name = N'DimensionAttribute';"));
        Assert.Equal(0L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[osdu].[Dimension]') AND [name] = N'AttributesJson';"));
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[DimensionValue];"));
    }

    [Fact]
    public async Task An_attribute_written_before_collected_attributes_keeps_its_value_and_a_key_then_holds_several_values_of_one()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(BeforeCollectedAttributes);
        await database.ExecuteAsync("""
            INSERT INTO [osdu].[LedgerPartition] ([Name], [CreatedUtc]) VALUES (N'dev', SYSUTCDATETIME());
            DECLARE @p smallint = (SELECT [PartitionId] FROM [osdu].[LedgerPartition] WHERE [Name] = N'dev');
            INSERT INTO [osdu].[Dimension] ([PartitionId], [FlowId], [FlowName], [Name], [Kind], [Path], [Repeats], [CleanJson], [AttributesJson], [DefinitionHash], [Members], [Originals], [CreatedUtc])
            VALUES (@p, NEWID(), N'wells', N'Wellbore', N'osdu:wks:work-product-component--WellLog:1.4.0', N'data.WellboreID', 0, N'[]', N'[{"name":"Country","steps":["data.Country"]}]', N'0123456789abcdef', 1, 1, SYSUTCDATETIME());
            DECLARE @d int = SCOPE_IDENTITY();
            INSERT INTO [osdu].[DimensionValue] ([PartitionId], [DimensionId], [Original], [OriginalHash], [Count], [Filterable], [FirstSeenRunId], [FirstSeenUtc], [MemberSinceRunId])
            VALUES (@p, @d, N'dev:master-data--Wellbore:1:', HASHBYTES('SHA2_256', CAST(N'x' AS varbinary(max))), 4, 1, 1, SYSUTCDATETIME(), 1);
            INSERT INTO [osdu].[DimensionAttribute] ([PartitionId], [DimensionId], [ValueId], [Name], [Value], [ValueFrom])
            VALUES (@p, @d, SCOPE_IDENTITY(), N'Country', N'United States', N'dev:master-data--GeoPoliticalEntity:US');
            """);

        await database.MigrateAsync(BeforeCollectedTexts);

        // The value kept, read from a record rather than collected; the key now names the value as well.
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[DimensionAttribute] WHERE [Value] = N'United States' AND [Records] IS NULL;"));
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[Dimension] WHERE [CollectedJson] IS NULL;"));
        Assert.Equal(["PartitionId", "DimensionId", "ValueId", "Name", "Value"], await database.PrimaryKeyAsync("DimensionAttribute"));
        await database.ExecuteAsync("""
            INSERT INTO [osdu].[DimensionAttribute] ([PartitionId], [DimensionId], [ValueId], [Name], [Value], [ValueFrom], [Records])
            SELECT [PartitionId], [DimensionId], [ValueId], N'Source', N'WELLDB', N'WELLDB', 3 FROM [osdu].[DimensionAttribute];
            INSERT INTO [osdu].[DimensionAttribute] ([PartitionId], [DimensionId], [ValueId], [Name], [Value], [ValueFrom], [Records])
            SELECT [PartitionId], [DimensionId], [ValueId], N'Source', N'PETREL', N'PETREL', 1 FROM [osdu].[DimensionAttribute] WHERE [Name] = N'Country';
            """);
        Assert.Equal(2L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[DimensionAttribute] WHERE [Name] = N'Source';"));
        await database.MigrateAsync(null);
        Assert.Equal(3L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[DimensionAttribute];"));
        await using (var db = database.Context())
        {
            Assert.Equal(ModelIndexes(db, DimensionTables), await database.IndexesAsync(DimensionTables));
        }

        // Back down, a key holds one value per attribute again: the collected values go, the value read stays.
        await database.MigrateAsync(BeforeCollectedAttributes);
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[DimensionAttribute] WHERE [Name] = N'Country' AND [Value] = N'United States';"));
        Assert.Equal(0L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[DimensionAttribute] WHERE [Name] = N'Source';"));
        Assert.Equal(["PartitionId", "DimensionId", "ValueId", "Name"], await database.PrimaryKeyAsync("DimensionAttribute"));
        Assert.Equal(0L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[osdu].[Dimension]') AND [name] = N'CollectedJson';"));
    }

    [Fact]
    public async Task Collected_texts_get_a_table_and_a_dimension_collected_before_it_is_built_again_before_a_search_picks_them()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(BeforeCollectedTexts);
        await database.ExecuteAsync("""
            INSERT INTO [osdu].[LedgerPartition] ([Name], [CreatedUtc]) VALUES (N'dev', SYSUTCDATETIME());
            DECLARE @p smallint = (SELECT [PartitionId] FROM [osdu].[LedgerPartition] WHERE [Name] = N'dev');
            INSERT INTO [osdu].[Dimension] ([PartitionId], [FlowId], [FlowName], [Name], [Kind], [Path], [Repeats], [CleanJson], [AttributesJson], [CollectedJson], [DefinitionHash], [Members], [Originals], [CreatedUtc])
            VALUES (@p, NEWID(), N'wells', N'Wellbore', N'osdu:wks:work-product-component--WellLog:1.4.0', N'data.WellboreID', 0, N'[]', N'[{"name":"Source","collect":"data.Source"}]',
                N'[{"name":"Source","path":"data.Source","field":{"index":"text","aggregateBy":"data.Source.keyword","repeats":false},"values":[{"value":"WELLDB","texts":["WELLDB"],"records":3}]}]',
                N'0123456789abcdef', 1, 1, SYSUTCDATETIME());
            """);

        await database.MigrateAsync(BeforeDimensionTables);

        // The texts a build kept in the dimension's row are not carried over: the next build writes them to the table.
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[Dimension] WHERE [CollectedJson] IS NULL AND [AttributesJson] IS NOT NULL;"));
        Assert.Equal(["PartitionId", "DimensionId", "Name", "TextHash"], await database.PrimaryKeyAsync("DimensionCollectedText"));
        const string Binary = "SELECT COUNT_BIG(*) FROM sys.columns WHERE [collation_name] = N'Latin1_General_100_BIN2' AND [object_id] = OBJECT_ID(N'[osdu].[DimensionCollectedText]') AND [name] IN (N'Name', N'Text', N'Value');";
        Assert.Equal(3L, await database.ScalarAsync(Binary));
        await database.MigrateAsync(null);
        await using (var db = database.Context())
        {
            Assert.Equal(ModelIndexes(db, DimensionTables), await database.IndexesAsync(DimensionTables));
        }

        await database.MigrateAsync(BeforeCollectedTexts);
        Assert.Equal(0L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'osdu' AND t.name = N'DimensionCollectedText';"));
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[Dimension];"));
    }

    [Fact]
    public async Task Attributes_get_numbers_and_identity_keys_keeping_every_row_and_a_dimensions_own_table_goes_back_with_the_migration()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(BeforeDimensionTables);
        await database.ExecuteAsync("""
            INSERT INTO [osdu].[LedgerPartition] ([Name], [CreatedUtc]) VALUES (N'dev', SYSUTCDATETIME());
            DECLARE @p smallint = (SELECT [PartitionId] FROM [osdu].[LedgerPartition] WHERE [Name] = N'dev');
            INSERT INTO [osdu].[Dimension] ([PartitionId], [FlowId], [FlowName], [Name], [Kind], [Path], [Repeats], [CleanJson], [AttributesJson], [DefinitionHash], [Members], [Originals], [CreatedUtc])
            VALUES (@p, NEWID(), N'wells', N'Wellbore', N'osdu:wks:work-product-component--WellLog:1.4.0', N'data.WellboreID', 0, N'[]',
                N'[{"name":"Country","steps":["data.Country"]},{"name":"Source","collect":"data.Source"}]', N'0123456789abcdef', 1, 1, SYSUTCDATETIME());
            DECLARE @d int = SCOPE_IDENTITY();
            INSERT INTO [osdu].[DimensionValue] ([PartitionId], [DimensionId], [Original], [OriginalHash], [Count], [Filterable], [FirstSeenRunId], [FirstSeenUtc], [MemberSinceRunId])
            VALUES (@p, @d, N'dev:master-data--Wellbore:1:', HASHBYTES('SHA2_256', CAST(N'x' AS varbinary(max))), 4, 1, 1, SYSUTCDATETIME(), 1);
            DECLARE @k bigint = SCOPE_IDENTITY();
            INSERT INTO [osdu].[DimensionAttribute] ([PartitionId], [DimensionId], [ValueId], [Name], [Value], [ValueFrom], [Records])
            VALUES (@p, @d, @k, N'Country', N'United States', N'dev:master-data--GeoPoliticalEntity:US', NULL),
                   (@p, @d, @k, N'Source', N'WELLDB', N'WELLDB', 3), (@p, @d, @k, N'Source', N'PETREL', N'PETREL', 1);
            INSERT INTO [osdu].[DimensionCollectedText] ([PartitionId], [DimensionId], [Name], [TextHash], [Text], [Value], [Records])
            VALUES (@p, @d, N'Source', HASHBYTES('SHA2_256', CAST(N'WELLDB' AS varbinary(max))), N'WELLDB', N'WELLDB', 3),
                   (@p, @d, N'Source', HASHBYTES('SHA2_256', CAST(N'PETREL' AS varbinary(max))), N'PETREL', N'PETREL', 1);
            """);

        await database.MigrateAsync(null);

        // Each attribute has its number, told collected or not by what its rows held; its place waits for the next build.
        const string Names = "SELECT COUNT_BIG(*) FROM [osdu].[DimensionAttributeName] WHERE [Ordinal] IS NULL AND ([Name] = N'Country' AND [Collected] = 0 OR [Name] = N'Source' AND [Collected] = 1);";
        Assert.Equal(2L, await database.ScalarAsync(Names));

        // Every row is kept, under its attribute's number, and keyed by a number of its own.
        const string Kept = """
            SELECT COUNT_BIG(*) FROM [osdu].[DimensionAttribute] AS a
            INNER JOIN [osdu].[DimensionAttributeName] AS n ON n.[PartitionId] = a.[PartitionId] AND n.[AttributeId] = a.[AttributeId]
            WHERE n.[Name] = N'Country' AND a.[Value] = N'United States' AND a.[Records] IS NULL
               OR n.[Name] = N'Source' AND a.[Value] = N'WELLDB' AND a.[Records] = 3
               OR n.[Name] = N'Source' AND a.[Value] = N'PETREL' AND a.[Records] = 1;
            """;
        Assert.Equal(3L, await database.ScalarAsync(Kept));
        const string Texts = """
            SELECT COUNT_BIG(*) FROM [osdu].[DimensionCollectedText] AS t
            INNER JOIN [osdu].[DimensionAttributeName] AS n ON n.[PartitionId] = t.[PartitionId] AND n.[AttributeId] = t.[AttributeId]
            WHERE n.[Name] = N'Source';
            """;
        Assert.Equal(2L, await database.ScalarAsync(Texts));

        // The number follows the partition and the dimension, so a dimension's rows are one range of each table.
        Assert.Equal(["PartitionId", "DimensionId", "AttributeValueId"], await database.PrimaryKeyAsync("DimensionAttribute"));
        Assert.Equal(["PartitionId", "DimensionId", "TextId"], await database.PrimaryKeyAsync("DimensionCollectedText"));
        Assert.Equal(["PartitionId", "DimensionId", "AttributeId"], await database.PrimaryKeyAsync("DimensionAttributeName"));
        const string Identities = "SELECT COUNT_BIG(*) FROM sys.identity_columns WHERE [object_id] IN (OBJECT_ID(N'[osdu].[DimensionAttribute]'), OBJECT_ID(N'[osdu].[DimensionCollectedText]'), OBJECT_ID(N'[osdu].[DimensionAttributeName]')) AND [name] IN (N'AttributeValueId', N'TextId', N'AttributeId');";
        Assert.Equal(3L, await database.ScalarAsync(Identities));
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[Dimension] WHERE [TableName] IS NULL;"));
        await using (var db = database.Context())
        {
            Assert.Equal(ModelIndexes(db, DimensionTables), await database.IndexesAsync(DimensionTables));
        }

        // A table a build made is no table of the model: going back down drops it with the column that names it, and
        // every attribute row takes its name again.
        await database.ExecuteAsync("""
            CREATE TABLE [osdu].[dim_Wellbore] ([id] bigint IDENTITY(1, 1) NOT NULL PRIMARY KEY, [partition] nvarchar(256) NOT NULL, [key] nvarchar(1024) NOT NULL);
            UPDATE [osdu].[Dimension] SET [TableName] = N'dim_Wellbore';
            """);
        await database.MigrateAsync(BeforeDimensionTables);
        const string Made = "SELECT COUNT_BIG(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'osdu' AND t.name IN (N'dim_Wellbore', N'DimensionAttributeName');";
        Assert.Equal(0L, await database.ScalarAsync(Made));
        Assert.Equal(3L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[DimensionAttribute] WHERE [Name] = N'Country' AND [Value] = N'United States' OR [Name] = N'Source' AND [Value] IN (N'WELLDB', N'PETREL');"));
        Assert.Equal(2L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[DimensionCollectedText] WHERE [Name] = N'Source';"));
        Assert.Equal(["PartitionId", "DimensionId", "ValueId", "Name", "Value"], await database.PrimaryKeyAsync("DimensionAttribute"));
        Assert.Equal(["PartitionId", "DimensionId", "Name", "TextHash"], await database.PrimaryKeyAsync("DimensionCollectedText"));
        Assert.Equal(0L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[osdu].[Dimension]') AND [name] = N'TableName';"));

        // And up again, the same rows under numbers again.
        await database.MigrateAsync(null);
        Assert.Equal(3L, await database.ScalarAsync(Kept));
        Assert.Equal(2L, await database.ScalarAsync(Texts));
    }

    [Fact]
    public async Task A_table_made_before_dimensions_named_their_columns_is_recorded_under_key_and_value_and_takes_them_back_going_down()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(BeforeDimensionColumnNames);
        await database.ExecuteAsync("""
            INSERT INTO [osdu].[LedgerPartition] ([Name], [CreatedUtc]) VALUES (N'dev', SYSUTCDATETIME());
            DECLARE @p smallint = (SELECT [PartitionId] FROM [osdu].[LedgerPartition] WHERE [Name] = N'dev');
            INSERT INTO [osdu].[Dimension] ([PartitionId], [FlowId], [FlowName], [Name], [Kind], [Path], [Repeats], [CleanJson], [DefinitionHash], [Members], [Originals], [CreatedUtc], [TableName])
            VALUES (@p, NEWID(), N'wells', N'Wellbore', N'osdu:wks:work-product-component--WellLog:1.4.0', N'data.WellboreID', 0, N'[]', N'0123456789abcdef', 1, 1, SYSUTCDATETIME(), N'dim_Wellbore'),
                   (@p, NEWID(), N'wells', N'Source', N'osdu:wks:work-product-component--WellLog:1.4.0', N'data.Source', 0, N'[]', N'0123456789abcdef', 0, 0, SYSUTCDATETIME(), NULL);
            CREATE TABLE [osdu].[dim_Wellbore] (
                [id] bigint IDENTITY(1, 1) NOT NULL PRIMARY KEY, [partition] nvarchar(256) NOT NULL, [key_id] bigint NOT NULL, [key] nvarchar(1024) NOT NULL,
                [value] nvarchar(256) NOT NULL, [records] bigint NOT NULL, [filter] nvarchar(4000) NULL);
            """);
        await database.ExecuteAsync("INSERT INTO [osdu].[dim_Wellbore] ([partition], [key_id], [key], [value], [records]) VALUES (N'dev', 1, N'dev:master-data--Wellbore:1:', N'A/1-F-1', 4);");

        // A table a build has made holds its key and its value under those two names, which is what its row records; a
        // dimension with no table records none until one is made.
        await database.MigrateAsync(null);
        const string Recorded = "SELECT COUNT_BIG(*) FROM [osdu].[Dimension] WHERE [TableName] = N'dim_Wellbore' AND [KeyColumn] = N'key' AND [ValueColumn] = N'value';";
        Assert.Equal(1L, await database.ScalarAsync(Recorded));
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[Dimension] WHERE [TableName] IS NULL AND [KeyColumn] IS NULL AND [ValueColumn] IS NULL;"));

        // A build then renames the two columns, and going back down gives them the names the code before reads them by.
        await database.ExecuteAsync("""
            EXEC sys.sp_rename N'[osdu].[dim_Wellbore].[key]', N'WellboreID', N'COLUMN';
            EXEC sys.sp_rename N'[osdu].[dim_Wellbore].[value]', N'FacilityName', N'COLUMN';
            UPDATE [osdu].[Dimension] SET [KeyColumn] = N'WellboreID', [ValueColumn] = N'FacilityName' WHERE [TableName] = N'dim_Wellbore';
            """);
        await database.MigrateAsync(BeforeDimensionColumnNames);
        const string Row = "SELECT COUNT_BIG(*) FROM [osdu].[dim_Wellbore] WHERE [id] = 1 AND [key] = N'dev:master-data--Wellbore:1:' AND [value] = N'A/1-F-1';";
        Assert.Equal(1L, await database.ScalarAsync(Row));
        Assert.Equal(0L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[osdu].[Dimension]') AND [name] IN (N'KeyColumn', N'ValueColumn');"));

        // And up again: the table as it is, recorded as it is.
        await database.MigrateAsync(null);
        Assert.Equal(1L, await database.ScalarAsync(Recorded));
        Assert.Equal(1L, await database.ScalarAsync(Row));
    }

    [Fact]
    public async Task The_reversal_tables_are_added_keyed_by_partition_with_one_reversal_to_a_source_and_go_back_with_the_migration()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(BeforeReversals);
        const string Tables = "SELECT COUNT_BIG(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'osdu' AND t.name IN (N'Reversal', N'ReversalItem');";
        Assert.Equal(0L, await database.ScalarAsync(Tables));

        await database.MigrateAsync(null);

        Assert.Equal(2L, await database.ScalarAsync(Tables));
        foreach (var table in ReversalTables)
        {
            Assert.Equal("PartitionId", (await database.PrimaryKeyAsync(table))[0]);
        }

        await using (var db = database.Context())
        {
            Assert.Equal(ModelIndexes(db, ReversalTables), await database.IndexesAsync(ReversalTables));
        }

        // An item's OSDU id compares exactly, as the record's claim of it does.
        const string Binary = "SELECT COUNT_BIG(*) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[osdu].[ReversalItem]') AND [name] = N'TargetId' AND [collation_name] = N'Latin1_General_100_BIN2';";
        Assert.Equal(1L, await database.ScalarAsync(Binary));

        // A source of a ledger has one reversal: asking again resumes it, and the database refuses a second one.
        const string Open = """
            INSERT INTO [osdu].[Reversal] ([PartitionId], [FlowId], [FlowName], [SourceKind], [SourceId], [SubmissionsJson], [Status], [RequestedBy], [RequestedUtc])
            VALUES (1, @logs, N'welldb-welllog-03-header-delivery', N'run', @run, N'[]', N'capturing', N'user:alice', @now);
            """;
        var run = Guid.NewGuid();
        await database.ExecuteAsync(Open, ("run", run));
        var second = await Assert.ThrowsAsync<SqlException>(() => database.ExecuteAsync(Open, ("run", run)));
        Assert.Equal(2601, second.Number);

        await database.MigrateAsync(BeforeReversals);
        Assert.Equal(0L, await database.ScalarAsync(Tables));
        await database.MigrateAsync(null);
        Assert.Equal(2L, await database.ScalarAsync(Tables));
    }

    [Fact]
    public async Task The_table_of_records_deleted_from_the_ledger_is_added_keyed_by_partition_and_goes_back_with_the_migration()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(BeforePurges);
        const string Table = "SELECT COUNT_BIG(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'osdu' AND t.name = N'PurgedRecord';";
        Assert.Equal(0L, await database.ScalarAsync(Table));

        await database.MigrateAsync(null);

        Assert.Equal(1L, await database.ScalarAsync(Table));
        Assert.Equal("PartitionId", (await database.PrimaryKeyAsync("PurgedRecord"))[0]);
        await using (var db = database.Context())
        {
            Assert.Equal(ModelIndexes(db, ["PurgedRecord"]), await database.IndexesAsync(["PurgedRecord"]));
        }

        // The OSDU id a deleted record had compares exactly, as the record's claim of it did.
        const string Binary = "SELECT COUNT_BIG(*) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[osdu].[PurgedRecord]') AND [name] = N'TargetId' AND [collation_name] = N'Latin1_General_100_BIN2';";
        Assert.Equal(1L, await database.ScalarAsync(Binary));

        await database.MigrateAsync(BeforePurges);
        Assert.Equal(0L, await database.ScalarAsync(Table));
        await database.MigrateAsync(null);
        Assert.Equal(1L, await database.ScalarAsync(Table));
    }

    [Fact]
    public async Task The_table_of_what_deliveries_created_is_added_keyed_by_partition_and_goes_back_with_the_migration()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(BeforeArtifacts);
        const string Table = "SELECT COUNT_BIG(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'osdu' AND t.name = N'Artifact';";
        Assert.Equal(0L, await database.ScalarAsync(Table));

        await database.MigrateAsync(null);

        Assert.Equal(1L, await database.ScalarAsync(Table));
        Assert.Equal("PartitionId", (await database.PrimaryKeyAsync("Artifact"))[0]);
        await using (var db = database.Context())
        {
            Assert.Equal(ModelIndexes(db, ["Artifact"]), await database.IndexesAsync(["Artifact"]));
        }

        // The OSDU id an artifact names compares exactly, as a record's claim of it does, so the inventory joins it to OSDU's ids.
        const string Binary = "SELECT COUNT_BIG(*) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[osdu].[Artifact]') AND [name] = N'TargetId' AND [collation_name] = N'Latin1_General_100_BIN2';";
        Assert.Equal(1L, await database.ScalarAsync(Binary));

        // A unit reports a slot once: a second row for the same unit and slot is refused.
        const string Insert = """
            INSERT INTO [osdu].[Artifact] ([PartitionId], [FlowId], [DeliveryKey], [UnitId], [UnitStartedUtc], [Slot], [Role], [State], [UndoAttempts], [CreatedUtc], [UpdatedUtc])
            VALUES (1, @flow, @key, @unit, SYSUTCDATETIME(), N'record', N'record', N'pending', 0, SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        var flow = Guid.NewGuid();
        var key = Guid.NewGuid();
        var unit = Guid.NewGuid();
        await database.ExecuteAsync(Insert, ("flow", flow), ("key", key), ("unit", unit));
        var second = await Assert.ThrowsAsync<SqlException>(() => database.ExecuteAsync(Insert, ("flow", flow), ("key", key), ("unit", unit)));
        Assert.Equal(2601, second.Number);

        await database.MigrateAsync(BeforeArtifacts);
        Assert.Equal(0L, await database.ScalarAsync(Table));
        await database.MigrateAsync(null);
        Assert.Equal(1L, await database.ScalarAsync(Table));
    }

    [Fact]
    public async Task The_tables_of_search_terms_and_their_refinements_are_added_and_go_back_with_the_migration()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(BeforeSearchTerms);
        const string Tables = "SELECT COUNT_BIG(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'osdu' AND t.name IN (N'SearchTerm', N'SearchTermRefinement');";
        Assert.Equal(0L, await database.ScalarAsync(Tables));

        await database.MigrateAsync(null);

        Assert.Equal(2L, await database.ScalarAsync(Tables));
        Assert.Equal("Id", (await database.PrimaryKeyAsync("SearchTerm"))[0]);
        Assert.Equal("TermId", (await database.PrimaryKeyAsync("SearchTermRefinement"))[0]);
        await using (var db = database.Context())
        {
            Assert.Equal(ModelIndexes(db, ["SearchTerm", "SearchTermRefinement"]), await database.IndexesAsync(["SearchTerm", "SearchTermRefinement"]));
        }

        // A repository gives a term once per entity type: a second row of the same term and type in the same repository is refused.
        const string Insert = """
            INSERT INTO [osdu].[SearchTerm] ([Id], [RepoId], [TermId], [TermKey], [Source], [EntityType], [Column], [RoutesJson], [FlowsJson], [FirstSeenUtc], [LastSeenUtc])
            VALUES (NEWID(), @repo, @term, N'osdudata.silver.welllog/log_run', N'OsduData.silver.WellLog', N'work-product-component--WellLog', N'log_run', N'[]', N'[]', SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        var repo = Guid.NewGuid();
        var term = Guid.NewGuid();
        await database.ExecuteAsync(Insert, ("repo", repo), ("term", term));
        var second = await Assert.ThrowsAsync<SqlException>(() => database.ExecuteAsync(Insert, ("repo", repo), ("term", term)));
        Assert.Equal(2601, second.Number);

        await database.MigrateAsync(BeforeSearchTerms);
        Assert.Equal(0L, await database.ScalarAsync(Tables));
        await database.MigrateAsync(null);
        Assert.Equal(2L, await database.ScalarAsync(Tables));
    }

    [Fact]
    public async Task Search_terms_keyed_by_their_source_table_empty_the_read_model_and_keep_what_people_made_of_them()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(BeforeSearchTermSources);
        await database.ExecuteAsync("""
            INSERT INTO [osdu].[SearchTerm] ([Id], [RepoId], [TermId], [TermKey], [System], [EntityType], [Dataset], [Column], [RoutesJson], [FlowsJson], [FirstSeenUtc], [LastSeenUtc])
            VALUES (NEWID(), NEWID(), NEWID(), N'welldb/work-product-component--WellLog//log_run', N'welldb', N'work-product-component--WellLog', NULL, N'log_run', N'[]', N'[]', SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO [osdu].[SearchTermRefinement] ([TermId], [TermKey], [EntityType], [Name], [Excluded], [UpdatedBy], [UpdatedUtc])
            VALUES (NEWID(), N'welldb/work-product-component--WellLog//log_run', N'work-product-component--WellLog', N'Run', 0, N'tester', SYSUTCDATETIME());
            """);

        await database.MigrateAsync(null);

        // The terms are written again from the pipelines when the control plane starts; the refinement waits for its term.
        Assert.Equal(0L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[SearchTerm];"));
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[SearchTermRefinement] WHERE [Name] = N'Run';"));
        const string Columns = "SELECT COUNT_BIG(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'[osdu].[SearchTerm]') AND name IN (N'System', N'Dataset');";
        Assert.Equal(0L, await database.ScalarAsync(Columns));
        await using (var db = database.Context())
        {
            Assert.Equal(ModelIndexes(db, ["SearchTerm"]), await database.IndexesAsync(["SearchTerm"]));
        }

        await database.MigrateAsync(BeforeSearchTermSources);
        Assert.Equal(2L, await database.ScalarAsync(Columns));
        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT_BIG(*) FROM [osdu].[SearchTermRefinement];"));
        await database.MigrateAsync(null);
    }

    [Fact]
    public async Task An_elements_field_keeps_up_to_4000_characters_and_the_values_kept_before_stay()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(BeforeElementValues);
        var before = new string('d', 256);
        await database.ExecuteAsync($"""
            INSERT INTO [osdu].[DimensionElement] ([PartitionId], [DimensionId], [ValueId], [Seq], [AttributeId], [Value])
            VALUES (1, 1, 1, 1, 1, N'{before}');
            """);

        await database.MigrateAsync(null);

        const string Type = """
            SELECT COUNT_BIG(*)
            FROM sys.columns AS c JOIN sys.types AS t ON t.[user_type_id] = c.[user_type_id]
            WHERE c.[object_id] = OBJECT_ID(N'[osdu].[DimensionElement]') AND c.[name] = N'Value'
              AND t.[name] = N'nvarchar' AND c.[max_length] = 8000 AND c.[collation_name] = N'Latin1_General_100_BIN2' AND c.[is_nullable] = 1;
            """;
        Assert.Equal(1L, await database.ScalarAsync(Type));
        Assert.Equal(256L, await database.ScalarAsync("SELECT CAST(LEN([Value]) AS bigint) FROM [osdu].[DimensionElement] WHERE [ValueId] = 1;"));
        var longest = new string('e', DeliveryDimensionElement.MaxValueLength);
        await database.ExecuteAsync($"""
            INSERT INTO [osdu].[DimensionElement] ([PartitionId], [DimensionId], [ValueId], [Seq], [AttributeId], [Value])
            VALUES (1, 1, 2, 1, 1, N'{longest}');
            """);
        Assert.Equal((long)DeliveryDimensionElement.MaxValueLength, await database.ScalarAsync("SELECT CAST(LEN([Value]) AS bigint) FROM [osdu].[DimensionElement] WHERE [ValueId] = 2;"));
        await using (var db = database.Context())
        {
            Assert.Equal(ModelIndexes(db, ["DimensionElement"]), await database.IndexesAsync(["DimensionElement"]));
        }
    }

    /// <summary>
    /// Every index the model declares on <paramref name="tables"/> of the module's schema, as
    /// <see cref="ScratchDatabase.IndexesAsync"/> describes one: read from the design-time model, which keeps the filters and
    /// included columns the runtime model leaves out.
    /// </summary>
    private static IReadOnlyList<string> ModelIndexes(OsduDbContext db, IReadOnlyCollection<string> tables)
    {
        var described = new List<string>();
        var model = db.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        foreach (var entity in model.GetEntityTypes().Where(e => e.GetSchema() == DeliveryModel.SchemaName && tables.Contains(e.GetTableName()!)))
        {
            var table = entity.GetTableName()!;
            var store = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, DeliveryModel.SchemaName);
            var key = entity.FindPrimaryKey()!;
            described.Add(Describe(table, key.GetName()!, true, key.Properties.Select(p => p.GetColumnName(store)!), [], null));
            foreach (var index in entity.GetIndexes())
            {
                var include = index.GetIncludeProperties() ?? [];
                described.Add(Describe(
                    table, index.GetDatabaseName()!, index.IsUnique, index.Properties.Select(p => p.GetColumnName(store)!),
                    include.Select(p => entity.FindProperty(p)!.GetColumnName(store)!), index.GetFilter()));
            }
        }

        return described.Order(StringComparer.Ordinal).ToList();
    }

    private static string Describe(string table, string name, bool unique, IEnumerable<string> columns, IEnumerable<string> include, string? filter)
        => $"{table}.{name} unique={unique} ({string.Join(",", columns)}) include ({string.Join(",", include.Order(StringComparer.Ordinal))}) where {filter ?? "-"}";

    private static RecordState Pending(Guid flow, Guid key, string targetId) => new()
    {
        DeliveryKey = new DeliveryKey(key),
        FlowId = flow,
        SourceKey = "wells:AREA_A_1/L-1001",
        MappingName = "Wellbore",
        TargetId = targetId,
        LastSubmissionId = Guid.NewGuid(),
        PendingDocumentRef = "0:0:10",
        PendingRenderContext = "{}",
        PendingMetadataHash = "mh",
        PendingMetadata = true,
    };

    /// <summary>The suites' scratch database, empty when the test takes it and dropped with everything in it when the test ends.</summary>
    private sealed class ScratchDatabase : IAsyncDisposable
    {
        private readonly OsduScratchDatabase _database;

        private ScratchDatabase(OsduScratchDatabase database) => _database = database;

        public string ConnectionString => _database.ConnectionString;

        public string Name => _database.Name;

        public static async Task<ScratchDatabase> CreateAsync() => new(await OsduScratchDatabase.CreateAsync());

        public OsduDbContext Context() => new(OsduDbContext.SqlServerOptions(ConnectionString));

        /// <summary>Lets the database run snapshot transactions, as the ledger's reads need.</summary>
        public Task AllowSnapshotAsync() => _database.AllowSnapshotIsolationAsync();

        /// <summary>Migrates to <paramref name="target"/>, up or down, or to the newest migration when it is null.</summary>
        public async Task MigrateAsync(string? target)
        {
            await using var db = Context();
            if (target is null)
            {
                await db.Database.MigrateAsync();
            }
            else
            {
                await db.GetService<IMigrator>().MigrateAsync(target);
            }
        }

        /// <summary>Runs a batch with the test's flows and clock as <c>@logs</c>, <c>@wellbores</c> and <c>@now</c>, and the given values.</summary>
        public async Task ExecuteAsync(string sql, params (string Name, object Value)[] values)
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = Command(connection, sql, values);
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>A number the query answers, with the same parameters as <see cref="ExecuteAsync"/>.</summary>
        public async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] values)
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = Command(connection, sql, values);
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static SqlCommand Command(SqlConnection connection, string sql, (string Name, object Value)[] values)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.Add(new SqlParameter("@logs", System.Data.SqlDbType.UniqueIdentifier) { Value = Logs });
            command.Parameters.Add(new SqlParameter("@wellbores", System.Data.SqlDbType.UniqueIdentifier) { Value = Wellbores });
            command.Parameters.Add(new SqlParameter("@now", System.Data.SqlDbType.DateTime2) { Value = Now });
            command.Parameters.Add(new SqlParameter("@mixed", System.Data.SqlDbType.UniqueIdentifier) { Value = Mixed });
            command.Parameters.Add(new SqlParameter("@retrieved", System.Data.SqlDbType.UniqueIdentifier) { Value = Retrieved });
            foreach (var (name, value) in values)
            {
                command.Parameters.Add(value switch
                {
                    Guid id => new SqlParameter("@" + name, System.Data.SqlDbType.UniqueIdentifier) { Value = id },
                    string text => new SqlParameter("@" + name, System.Data.SqlDbType.NVarChar, 4000) { Value = text },
                    _ => throw new ArgumentException($"The parameter @{name} is a {value.GetType().Name}; the scratch database takes ids and text.", nameof(values)),
                });
            }

            return command;
        }

        /// <summary>The columns of the record table's primary key, in key order.</summary>
        public async Task<IReadOnlyList<string>> KeyColumnsAsync()
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.[name]
                FROM sys.indexes AS i
                INNER JOIN sys.index_columns AS ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                INNER JOIN sys.columns AS c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                WHERE i.[object_id] = OBJECT_ID(N'[osdu].[Record]') AND i.[is_primary_key] = 1
                ORDER BY ic.[key_ordinal];
                """;
            var columns = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0));
            }

            return columns;
        }

        /// <summary>The columns of <paramref name="table"/>'s primary key, in key order.</summary>
        public async Task<IReadOnlyList<string>> PrimaryKeyAsync(string table)
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.[name]
                FROM sys.indexes AS i
                INNER JOIN sys.index_columns AS ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                INNER JOIN sys.columns AS c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                WHERE i.[object_id] = OBJECT_ID(N'[osdu].' + QUOTENAME(@table)) AND i.[is_primary_key] = 1
                ORDER BY ic.[key_ordinal];
                """;
            command.Parameters.Add(new SqlParameter("@table", System.Data.SqlDbType.NVarChar, 128) { Value = table });
            var columns = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0));
            }

            return columns;
        }

        /// <summary>
        /// Every index of <paramref name="tables"/> as the database holds it: its name, whether it is unique, its key columns
        /// in order, its included columns and its filter, described as the model's are, in name order.
        /// </summary>
        public async Task<IReadOnlyList<string>> IndexesAsync(IEnumerable<string> tables)
        {
            var wanted = tables.ToHashSet(StringComparer.Ordinal);
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT t.[name], i.[name], i.[is_unique], i.[filter_definition], c.[name], ic.[key_ordinal], ic.[is_included_column]
                FROM sys.indexes AS i
                INNER JOIN sys.tables AS t ON t.[object_id] = i.[object_id]
                INNER JOIN sys.index_columns AS ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
                INNER JOIN sys.columns AS c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                WHERE SCHEMA_NAME(t.[schema_id]) = N'osdu' AND i.[type] IN (1, 2)
                ORDER BY t.[name], i.[name], ic.[is_included_column], ic.[key_ordinal], c.[name];
                """;
            var indexes = new Dictionary<(string Table, string Name), (bool Unique, string? Filter, List<string> Columns, List<string> Include)>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!wanted.Contains(key.Item1))
                {
                    continue;
                }

                if (!indexes.TryGetValue(key, out var index))
                {
                    index = (reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3), [], []);
                    indexes[key] = index;
                }

                (reader.GetBoolean(6) ? index.Include : index.Columns).Add(reader.GetString(4));
            }

            // SQL Server keeps a filter as it normalized it, in parentheses; the model keeps it as it was written.
            return indexes
                .Select(i => Describe(i.Key.Table, i.Key.Name, i.Value.Unique, i.Value.Columns, i.Value.Include, Unwrap(i.Value.Filter)))
                .Order(StringComparer.Ordinal)
                .ToList();
        }

        private static string? Unwrap(string? filter)
            => filter is { Length: > 2 } && filter[0] == '(' && filter[^1] == ')' ? filter[1..^1] : filter;

        public ValueTask DisposeAsync() => _database.DisposeAsync();
    }
}
