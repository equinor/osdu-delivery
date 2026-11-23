using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The writes an intake decides record by record (a hold, a skip of a record it rendered) against the writes that reach
/// the same records from elsewhere while it plans (osdu/docs/reference/concepts/ledger.md, Writes at scale): a part of a
/// split delivery holds records while another part's landing releases them or asks for them to be planned again. Each
/// intake write takes its records in the order of the table's key, in one statement, before it decides anything, so it
/// never holds one record while it waits for another that a set-based statement holds the other way round. On SQL Server,
/// in the suites' test database.
/// </summary>
/// <remarks>
/// Before the writes took their records that way, the first round of either race ended with the intake's write chosen as
/// the victim of a deadlock on two keys of <c>PK_Record</c>, which failed the part that made it.
/// </remarks>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public class IntakeLedgerWriteTests
{
    private const int Records = 40;

    private const int Rounds = 100;

    private static readonly Lazy<Task> Migrated = new(async () =>
    {
        await using var db = Database();
        await db.Database.MigrateAsync();
    });

    private readonly TestClock _clock = new();
    private readonly Guid _flow = FlowId.Of("intake-ledger-" + Guid.NewGuid().ToString("N"));
    private readonly string _run = Guid.NewGuid().ToString("N");

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private static OsduDbContext Database() => new(OsduDbContext.SqlServerOptions(OsduTestServer.ConnectionString));

    private async Task<OsduLedger> LedgerAsync()
    {
        await Migrated.Value;
        var ledger = new OsduLedger(Database, _clock);
        await ledger.RegisterAsync(_flow);
        return ledger;
    }

    private DeliveryKey Key(string name) => DeliveryKey.Derive("intake-ledger-test", [_run, name]);

    /// <summary>A record a render held because the parent it reads is not in OSDU, as the intake hands it to the ledger.</summary>
    private RecordState Held(string name, Guid submission) => new()
    {
        DeliveryKey = Key(name),
        FlowId = _flow,
        SourceKey = _run + "/" + name,
        SourceKeyJson = $"[\"{_run}\",\"{name}\"]",
        MappingName = "Thing",
        TargetId = "dev:x:" + _run + name,
        LastSubmissionId = submission,
        LastError = "the parent record is not in OSDU",
        ParentId = "dev:parent:" + _run,
        PendingSourceFileName = "welllog_20260901.csv",
        PendingSourceRowNumber = 1,
    };

    /// <summary>The same record, rendered by a later plan and found to send nothing.</summary>
    private SkippedRecord Rendered(string name) => new()
    {
        DeliveryKey = Key(name),
        Kind = SkipKind.Rendered,
        Reason = "metadata and payload hashes unchanged",
        SourceKeyJson = $"[\"{_run}\",\"{name}\"]",
        Origin = new RecordOrigin("welllog_20260901.csv", 1, Now),
    };

    [Fact]
    public async Task A_hold_and_a_release_of_the_same_records_at_once_never_deadlock_on_sql_server()
    {
        var ledger = await LedgerAsync();
        var submission = Guid.NewGuid();
        var held = Enumerable.Range(0, Records).Select(i => Held($"h{i:D3}", submission)).ToList();
        Assert.Equal(Records, (await ledger.MarkHeldAsync(_flow, held)).Count);
        var keys = held.Select(h => h.DeliveryKey).ToList();

        // A part holds the records again while a landing elsewhere releases them: the two writes of the incident, over and
        // over, each given the records in an order of its own.
        var lost = await RaceAsync(
            () => ledger.MarkHeldAsync(_flow, held),
            now => ledger.ReleaseAsync(_flow, keys.AsEnumerable().Reverse(), now));

        Assert.Empty(lost);

        // Every round left each record as one of the two writes leaves it, never as a mix of both: held and blocked by its
        // problem, or released and asking to be planned again.
        foreach (var key in keys)
        {
            var record = (await ledger.GetRecordAsync(_flow, key))!;
            Assert.Equal(RecordStatus.Held, record.Status);
            Assert.True(record.Blocked ? record.PlanRequestedUtc is null : record.PlanRequestedUtc is not null, $"{key}: blocked {record.Blocked}, plan requested {record.PlanRequestedUtc:o}");
        }
    }

    [Fact]
    public async Task A_skip_of_rendered_records_and_a_release_of_them_at_once_never_deadlock_on_sql_server()
    {
        var ledger = await LedgerAsync();
        var submission = Guid.NewGuid();
        var held = Enumerable.Range(0, Records).Select(i => Held($"s{i:D3}", submission)).ToList();
        await ledger.MarkHeldAsync(_flow, held);
        var keys = held.Select(h => h.DeliveryKey).ToList();
        var skipped = Enumerable.Range(0, Records).Select(i => Rendered($"s{i:D3}")).ToList();

        var lost = await RaceAsync(
            () => ledger.MarkSkippedAsync(_flow, skipped, Guid.NewGuid()),
            now => ledger.ReleaseAsync(_flow, keys.AsEnumerable().Reverse(), now));

        Assert.Empty(lost);
        foreach (var key in keys)
        {
            Assert.Equal(RecordStatus.Held, (await ledger.GetRecordAsync(_flow, key))!.Status);
        }
    }

    [Fact]
    public async Task Two_holds_of_new_records_at_once_leave_each_record_once_on_sql_server()
    {
        // Two parts whose rows give the same records hold them at the same moment: neither finds them, both insert, and the
        // table's key refuses the second, which decides again from the rows the first left.
        var ledger = await LedgerAsync();
        var submission = Guid.NewGuid();
        for (var round = 0; round < 10; round++)
        {
            var held = Enumerable.Range(0, Records).Select(i => Held($"n{round:D2}-{i:D3}", submission) with { Basis = PlanBasis.Of(null) }).ToList();
            await Task.WhenAll(
                Task.Run(() => ledger.MarkHeldAsync(_flow, held)),
                Task.Run(() => ledger.MarkHeldAsync(_flow, held.AsEnumerable().Reverse().ToList())));

            foreach (var record in held)
            {
                Assert.Equal(RecordStatus.Held, (await ledger.GetRecordAsync(_flow, record.DeliveryKey))!.Status);
            }
        }
    }

    [Fact]
    public async Task Every_write_of_named_records_seeks_each_one_in_the_order_of_the_key_on_sql_server()
    {
        var ledger = await LedgerAsync();
        var submission = Guid.NewGuid();
        var held = Enumerable.Range(0, Records).Select(i => Held($"p{i:D3}", submission)).ToList();
        var keys = held.Select(h => h.DeliveryKey).Reverse().ToList();
        await ClearPlansAsync();

        // A hold, a release, a hold again and a redelivery: each statement that takes named records, in a plan of its own.
        await ledger.MarkHeldAsync(_flow, held);
        await ledger.ReleaseAsync(_flow, keys, Now);
        await ledger.MarkHeldAsync(_flow, held);
        await ledger.ForceRedeliverAsync(_flow, keys, RedeliverScope.Metadata, Now);

        var (statements, unordered) = await NamedRecordPlansAsync();
        Assert.True(statements >= 3, $"the named-record writes are in the plan cache ({statements} found)");
        Assert.True(unordered.Count == 0, "Statements that do not take their records one seek at a time in the order given:\n" + string.Join("\n", unordered));
    }

    private static readonly System.Xml.Linq.XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    /// <summary>Empties the plans SQL Server keeps for the test database, so the plans read after the writes are theirs.</summary>
    private static async Task ClearPlansAsync()
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(OsduTestServer.ConnectionString);
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand("ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;", connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The plans SQL Server holds in this database for the statements that join named keys to the record table
    /// (<see cref="SqlServerLedgerBulk.NamedKeys"/>), and those of them that could take the records in an order of their
    /// own: a hash or merge join, an aggregate, or a read of the record table that is not a seek of its key.
    /// </summary>
    private static async Task<(int Statements, List<string> Unordered)> NamedRecordPlansAsync()
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(OsduTestServer.ConnectionString);
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(
            """
            SELECT SUBSTRING(st.[text], qs.[statement_start_offset] / 2 + 1,
                       ((CASE qs.[statement_end_offset] WHEN -1 THEN DATALENGTH(st.[text]) ELSE qs.[statement_end_offset] END)
                        - qs.[statement_start_offset]) / 2 + 1),
                   qp.[query_plan]
            FROM sys.dm_exec_query_stats AS qs
            CROSS APPLY sys.dm_exec_sql_text(qs.[sql_handle]) AS st
            CROSS APPLY sys.dm_exec_text_query_plan(qs.[plan_handle], qs.[statement_start_offset], qs.[statement_end_offset]) AS qp
            CROSS APPLY (SELECT CONVERT(int, pa.[value]) AS [dbid] FROM sys.dm_exec_plan_attributes(qs.[plan_handle]) AS pa WHERE pa.[attribute] = N'dbid') AS a
            WHERE a.[dbid] = DB_ID() AND st.[text] LIKE N'%OPENJSON(@keys) WITH (%' AND qp.[query_plan] IS NOT NULL;
            """,
            connection);
        var statements = 0;
        var unordered = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var statement = string.Join(' ', reader.GetString(0).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (!statement.Contains("[osdu].[Record] AS r", StringComparison.Ordinal) || !statement.Contains("AS n", StringComparison.Ordinal))
            {
                continue;
            }

            statements++;
            var plan = System.Xml.Linq.XDocument.Parse(reader.GetString(1));
            var shown = statement.Length > 160 ? statement[..160] + "..." : statement;
            foreach (var op in plan.Descendants(Showplan + "RelOp").Select(r => r.Attribute("PhysicalOp")?.Value).Where(o => o is "Hash Match" or "Merge Join" or "Stream Aggregate"))
            {
                unordered.Add($"{op} in: {shown}");
            }

            foreach (var read in plan.Descendants(Showplan + "IndexScan"))
            {
                var table = read.Element(Showplan + "Object");
                if (table?.Attribute("Table")?.Value != "[Record]")
                {
                    continue;
                }

                if (table.Attribute("Index")?.Value != "[PK_Record]" || !read.Elements(Showplan + "SeekPredicates").Any())
                {
                    unordered.Add($"[Record].{table.Attribute("Index")?.Value} is read past its key by: {shown}");
                }
            }
        }

        return (statements, unordered);
    }

    /// <summary>
    /// Runs the two writes against each other <see cref="Rounds"/> times, a second of the clock apart, and returns the rounds
    /// either of them lost, each with why.
    /// </summary>
    private async Task<List<string>> RaceAsync(Func<Task> intake, Func<DateTime, Task> elsewhere)
    {
        var lost = new List<string>();
        for (var round = 0; round < Rounds; round++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            var now = Now;
            var first = Task.Run(intake);
            var second = Task.Run(() => elsewhere(now));
            try
            {
                await Task.WhenAll(first, second);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Microsoft.Data.SqlClient.SqlException)
            {
                lost.Add($"round {round}: {ex.GetType().Name}: {ex.GetBaseException().Message}");
            }
        }

        return lost;
    }
}
