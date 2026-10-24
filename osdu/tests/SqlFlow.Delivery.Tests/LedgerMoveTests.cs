using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The move of a renamed pipeline's ledger (<see cref="LedgerMoves"/>, osdu/docs/reference/flow/interfaces.md, Renaming a
/// flow) on SQL Server: every row of the old ledger goes under the new identity, the records the new one had only held give
/// way to the old ones, a ledger that claimed ids of its own is not moved into, work in flight holds a move back and keeps a
/// run of the renamed pipeline from reading half a ledger, and a move that stopped part way is taken up where it stopped.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class LedgerMoveTests : IDisposable
{
    private const string OldName = "welldb-welllog";
    private const string NewName = "welldb-welllog-v140";
    private static readonly Guid Old = FlowId.Of(OldName);
    private static readonly Guid New = FlowId.Of(NewName);

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();

    private OsduLedger Ledger => _db.Ledger(_clock);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public void Dispose() => _db.Dispose();

    /// <summary>Work for a record of <paramref name="flow"/> that queues a document, which claims its OSDU id.</summary>
    private static RecordState Work(Guid flow, string key, Guid submission) => new()
    {
        DeliveryKey = DeliveryKey.Derive("test", [key]),
        FlowId = flow,
        SourceKey = key,
        MappingName = "WellLog",
        TargetId = "dev:work-product-component--WellLog:" + key,
        LastSubmissionId = submission,
        PendingDocumentRef = "0:0:10",
        PendingRenderContext = "{}",
        PendingSourceFingerprint = "fp",
        PendingMetadataHash = "mh",
        PendingMetadata = true,
    };

    /// <summary>A record of <paramref name="flow"/> held for an id another record claimed: it claims nothing.</summary>
    private static RecordState Held(Guid flow, string key, Guid submission) => Work(flow, key, submission) with
    {
        TargetId = null,
        PendingDocumentRef = null,
        LastError = $"OSDU id dev:work-product-component--WellLog:{key} is already claimed by flow '{OldName}'",
    };

    /// <summary>Both ledgers in dev, the old one with records a and b claimed and a watermark, the new one with nothing yet.</summary>
    private async Task<Guid> OldLedgerAsync()
    {
        var ledger = Ledger;
        await ledger.RegisterAsync(Old, "dev", OldName);
        await ledger.RegisterAsync(New, "dev", NewName);
        var submission = Guid.NewGuid();
        Assert.Equal(2, (await ledger.UpsertPendingAsync(Old, [Work(Old, "a", submission), Work(Old, "b", submission)])).Staged);
        await ledger.SetWatermarkAsync(new SourceWatermark(Old, "logSource=STAT_COMP", Now.AddDays(-1), submission, Now.AddDays(-1), null));
        return submission;
    }

    private async Task RequestAsync()
    {
        await using var context = _db.CreateDbContext();
        var partition = await context.DeliveryLedgers.Where(l => l.FlowId == Old).Select(l => l.PartitionId).SingleAsync();
        Assert.Equal(LedgerMoveAsk.Asked, (await LedgerMoves.RequestAsync(
            context, new LedgerMoveRequest(partition, Old, OldName, OldName, New, NewName, string.Empty, NewName, Guid.NewGuid()), Now, CancellationToken.None)).Ask);
    }

    private async Task<long> RowsAsync(string table, string column, Guid flow)
    {
        await using var context = _db.CreateDbContext();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT_BIG(*) FROM [osdu].[{table}] WHERE [{column}] = @flow";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@flow";
        parameter.Value = flow;
        command.Parameters.Add(parameter);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var context = _db.CreateDbContext();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_move_takes_every_row_of_the_old_ledger_and_the_records_the_new_one_only_held_give_way()
    {
        var submission = await OldLedgerAsync();
        var ledger = Ledger;

        // The renamed pipeline ran before the sync paired the two: it held a, whose id the old ledger claimed, and c, held for
        // a reason of its own, and wrote watermarks of the same scope and of another.
        var held = Guid.NewGuid();
        await ledger.MarkHeldAsync(New, [Held(New, "a", held), Held(New, "c", held) with { LastError = "no wellbore" }]);
        await ledger.SetWatermarkAsync(new SourceWatermark(New, "logSource=STAT_COMP", Now, held, Now, null));
        await ledger.SetWatermarkAsync(new SourceWatermark(New, "logSource=STAT_CPI", Now, held, Now, null));
        await RequestAsync();

        var moved = Assert.Single(await ledger.CompleteLedgerMovesAsync(null));
        Assert.Equal(("completed", 2L, 1L, "dev"), (moved.Outcome, moved.Records, moved.Superseded, moved.Partition));

        // The old records are the new pipeline's, claims and all; the record it held for a reason of its own stays as it was.
        var records = (await ledger.ListAsync(New, new RecordQuery())).ToDictionary(r => r.SourceKey!);
        Assert.Equal(["a", "b", "c"], records.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("dev:work-product-component--WellLog:a", records["a"].ClaimedTargetId);
        Assert.Equal(submission, records["a"].LastSubmissionId);
        Assert.Equal("dev:work-product-component--WellLog:b", records["b"].ClaimedTargetId);
        Assert.Equal((RecordStatus.Held, (string?)null), (records["c"].Status, records["c"].ClaimedTargetId));

        // The hold the new pipeline wrote of a stays in a's history.
        Assert.Contains(await ledger.ListAttemptsAsync(New, DeliveryKey.Derive("test", ["a"]), 10), a => a.Outcome == AttemptOutcome.Held);

        // The old watermark of a scope both read is kept, so the next run reads what changed since the old name read; the
        // new pipeline's other scope is kept too.
        Assert.Equal(Now.AddDays(-1), (await ledger.GetWatermarkAsync(New, "logSource=STAT_COMP"))!.UpdatedThroughUtc);
        Assert.NotNull(await ledger.GetWatermarkAsync(New, "logSource=STAT_CPI"));

        // Nothing is left under the old identity, and the directory no longer names it.
        foreach (var table in LedgerMoves.KeyedTables)
        {
            Assert.Equal(0, await RowsAsync(table, "FlowId", Old));
        }

        foreach (var table in LedgerMoves.NamingTables)
        {
            Assert.Equal(0, await RowsAsync(table, "LedgerFlowId", Old));
        }

        Assert.Null(await ledger.GetLedgerAsync(Old));
        Assert.Equal("dev", (await ledger.GetLedgerAsync(New))!.Partition);

        // Each moved record names the move, and the audit trail says what it did; a second pass finds nothing to do.
        Assert.Equal(2, await RowsAsync("ActivityRecord", "FlowId", New));
        var activity = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = New, Kind = LedgerMoves.Kind }));
        Assert.Equal(("completed", LedgerMoves.Actor), (activity.Outcome, activity.Actor));
        Assert.Contains("moved 2 record(s) of 'welldb-welllog' to 'welldb-welllog-v140'", activity.Summary, StringComparison.Ordinal);
        Assert.Contains("1 record(s) 'welldb-welllog-v140' had held, which claimed no OSDU id, gave way to them", activity.Summary, StringComparison.Ordinal);
        Assert.Empty(await ledger.CompleteLedgerMovesAsync(null));
    }

    [Fact]
    public async Task Records_the_renamed_pipeline_delivered_under_keys_of_their_own_stay_beside_the_moved_ones()
    {
        await OldLedgerAsync();
        var ledger = Ledger;
        Assert.Equal(1, (await ledger.UpsertPendingAsync(New, [Work(New, "d", Guid.NewGuid())])).Staged);
        await RequestAsync();

        var moved = Assert.Single(await ledger.CompleteLedgerMovesAsync(null));
        Assert.Equal(("completed", 2L, 0L), (moved.Outcome, moved.Records, moved.Superseded));
        Assert.Equal(["a", "b", "d"], (await ledger.ListAsync(New, new RecordQuery())).Select(r => r.SourceKey!).Order(StringComparer.Ordinal));
        Assert.Empty(await ledger.ListAsync(Old, new RecordQuery()));
    }

    [Fact]
    public async Task A_ledger_whose_records_claimed_other_ids_under_the_old_keys_is_not_moved_into()
    {
        await OldLedgerAsync();
        var ledger = Ledger;
        Assert.Equal(1, (await ledger.UpsertPendingAsync(New, [Work(New, "a", Guid.NewGuid()) with { TargetId = "dev:work-product-component--WellLog:a-v2" }])).Staged);
        await RequestAsync();

        var refused = Assert.Single(await ledger.CompleteLedgerMovesAsync(null));
        Assert.Equal("failed", refused.Outcome);
        Assert.StartsWith(
            "not moved: 1 record(s) of 'welldb-welllog-v140' claimed OSDU ids under keys 'welldb-welllog' holds records of too",
            refused.Summary,
            StringComparison.Ordinal);
        Assert.Equal(2, (await ledger.ListAsync(Old, new RecordQuery())).Count);
        Assert.Single(await ledger.ListAsync(New, new RecordQuery()));
        Assert.Empty(await ledger.CompleteLedgerMovesAsync(New));
    }

    [Fact]
    public async Task A_move_waits_for_work_in_flight_and_a_run_of_the_renamed_pipeline_does_not_read_half_a_ledger()
    {
        await OldLedgerAsync();
        await using (var context = _db.CreateDbContext())
        {
            var partition = await context.DeliveryLedgers.Where(l => l.FlowId == Old).Select(l => l.PartitionId).SingleAsync();
            await ExecuteAsync(
                "INSERT INTO [osdu].[Lease] ([PartitionId], [Token], [FlowId], [Owner], [AcquiredUtc], [ExpiresUtc], [MaxAttempts], [ForRemoval]) VALUES (@partition, N'lease-1', @flow, N'test-worker', @now, @expires, 3, 0)",
                ("@partition", partition), ("@flow", Old), ("@now", Now), ("@expires", Now.AddMinutes(5)));
        }

        await RequestAsync();
        var ledger = Ledger;
        var waiting = Assert.Single(await ledger.CompleteLedgerMovesAsync(null));
        Assert.Equal(LedgerMoves.Running, waiting.Outcome);
        Assert.Contains("a run is working on 'welldb-welllog' or 'welldb-welllog-v140'", waiting.Summary, StringComparison.Ordinal);
        var refused = await Assert.ThrowsAsync<DeliveryException>(() => ledger.CompleteLedgerMovesAsync(New));
        Assert.Contains("nothing ran, since a run reads its ledger whole or not at all", refused.Message, StringComparison.Ordinal);
        Assert.Equal(2, (await ledger.ListAsync(Old, new RecordQuery())).Count);

        // The lease runs out: the run of the renamed pipeline makes the move itself and reads the whole ledger.
        _clock.Advance(TimeSpan.FromMinutes(10));
        var moved = Assert.Single(await ledger.CompleteLedgerMovesAsync(New));
        Assert.Equal(("completed", 2L), (moved.Outcome, moved.Records));
        Assert.Equal(2, (await ledger.ListAsync(New, new RecordQuery())).Count);
    }

    [Fact]
    public async Task A_move_waits_for_a_run_that_holds_a_submission_and_not_for_one_a_stopped_run_left()
    {
        var submission = await OldLedgerAsync();
        var ledger = Ledger;
        await ledger.RegisterSubmissionAsync(new SubmissionState
        {
            SubmissionId = submission,
            FlowId = Old,
            FlowName = OldName,
            MappingReference = "WellLog@1.4.0",
            RenderContext = "{}",
            SourceConnection = "${env:OSDU_DATA_DB}",
            SourceObject = "OsduData.arc.WellLog",
            RecordCount = 2,
        });
        await RequestAsync();

        // A run holds the submission it plans: the move waits for it.
        await using (await ledger.HoldSubmissionAsync(submission))
        {
            var waiting = Assert.Single(await ledger.CompleteLedgerMovesAsync(null));
            Assert.Equal(LedgerMoves.Running, waiting.Outcome);
            Assert.Contains("a submission it plans or sends", waiting.Summary, StringComparison.Ordinal);
        }

        // The run ended without closing it: nobody holds it, so the move is made.
        var moved = Assert.Single(await ledger.CompleteLedgerMovesAsync(null));
        Assert.Equal(("completed", 2L), (moved.Outcome, moved.Records));
        Assert.NotNull(await ledger.GetSubmissionAsync(submission));
        Assert.Equal(New, (await ledger.GetSubmissionAsync(submission))!.FlowId);
    }

    [Fact]
    public async Task A_move_that_stopped_part_way_is_taken_up_where_it_stopped()
    {
        await OldLedgerAsync();
        await RequestAsync();

        // A move that began, linked a's record and moved the watermark and the submissions, then stopped.
        long activityId;
        await using (var context = _db.CreateDbContext())
        {
            var activity = await context.DeliveryActivities.SingleAsync(a => a.FlowId == New && a.Kind == LedgerMoves.Kind);
            activityId = activity.ActivityId;
            await ExecuteAsync(
                "UPDATE [osdu].[Activity] SET [ParametersJson] = JSON_MODIFY([ParametersJson], '$.started', CAST(1 AS bit)) WHERE [ActivityId] = @activity",
                ("@activity", activityId));
            await ExecuteAsync(
                "INSERT INTO [osdu].[ActivityRecord] ([PartitionId], [FlowId], [DeliveryKey], [ActivityId]) VALUES (@partition, @flow, @key, @activity)",
                ("@partition", activity.PartitionId), ("@flow", Old), ("@key", DeliveryKey.Derive("test", ["a"]).Value), ("@activity", activityId));
            await ExecuteAsync("UPDATE [osdu].[SourceWatermark] SET [FlowId] = @to WHERE [FlowId] = @from", ("@to", New), ("@from", Old));
            await ExecuteAsync("UPDATE [osdu].[Submission] SET [FlowId] = @to WHERE [FlowId] = @from", ("@to", New), ("@from", Old));
        }

        var ledger = Ledger;
        var moved = Assert.Single(await ledger.CompleteLedgerMovesAsync(null));
        Assert.Equal(("completed", 2L, activityId), (moved.Outcome, moved.Records, moved.ActivityId));
        Assert.Equal(2, (await ledger.ListAsync(New, new RecordQuery())).Count);
        Assert.Equal(2, await RowsAsync("ActivityRecord", "FlowId", New));
        foreach (var table in LedgerMoves.KeyedTables)
        {
            Assert.Equal(0, await RowsAsync(table, "FlowId", Old));
        }
    }
}
