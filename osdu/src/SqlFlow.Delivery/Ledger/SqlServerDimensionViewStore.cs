using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// Writes, checks, drops and reads the views dimension flows declare (osdu/docs/reference/flow/dimension-views.md). A
/// view is written in one transaction under an application lock of its own: the rows of the tables it reads given their
/// key's hash, the view written with its column list when its statement changed, and its record. A build writes only a
/// view it recorded, or a name no object of the schema holds, and drops only the views its flow made and no longer
/// declares. The check reads the view's rows of the run's partition after the write, outside it.
/// </summary>
internal static class SqlServerDimensionViewStore
{
    /// <summary>How long one statement may run: a view may read millions of rows.</summary>
    private const int CommandTimeoutSeconds = 900;

    /// <summary>How long a write waits for another build writing the same view.</summary>
    private const int LockTimeoutMs = 120_000;

    /// <summary>How many times a write the database chose as a deadlock victim is made again.</summary>
    private const int DeadlockAttempts = 3;

    /// <summary>The checks a view's page shows.</summary>
    private const int ShownChecks = 20;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private const string LockSql = """
        DECLARE @granted int;
        EXEC @granted = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = @timeout;
        SELECT @granted;
        """;

    /// <summary>The application lock a view is written, dropped and renamed under, whichever build or removal holds it.</summary>
    internal static string LockResource(string name) => "osdu-dimension-view:" + name.ToUpperInvariant();

    /// <summary>The view's name as a statement names it: <c>[osdu].[v_dim_...]</c>.</summary>
    private static string Qualified(string viewName) => DimensionTables.Qualified(viewName);

    /// <summary>
    /// Writes the views <paramref name="write"/> carries and checks each in its partition (whose ledger number is
    /// <paramref name="partitionId"/>), then drops every view its flow made and no longer declares. A view that cannot be
    /// written keeps its last definition and says why; the others are still written.
    /// </summary>
    public static async Task<DimensionViewsWritten> WriteAsync(OsduDbContext db, DimensionViewWrite write, short partitionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(write);
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            var outcomes = new List<DimensionViewOutcome>(write.Views.Count);
            var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in write.Views)
            {
                ct.ThrowIfCancellationRequested();

                // A view that joins a view this build could not write would read one that is not as its document says, or
                // none at all: it keeps its last definition, and says which view it waits on.
                if (item.View.Definition.JoinedViews.FirstOrDefault(failed.Contains) is { } waits)
                {
                    failed.Add(item.View.ViewName);
                    outcomes.Add(new DimensionViewOutcome(item.View.Name, item.View.ViewName, DimensionViewWriteStatus.Failed,
                        $"View {item.View.Name} joins {DimensionTables.Shown(waits)}, which this build could not write, so it was not written either and keeps its last definition; it is written once the view it joins is.",
                        null));
                    continue;
                }

                var outcome = await WriteOneAsync(connection, write, item, partitionId, ct).ConfigureAwait(false);
                if (outcome.Status == DimensionViewWriteStatus.Failed)
                {
                    failed.Add(item.View.ViewName);
                }

                outcomes.Add(outcome);
            }

            var dropped = await RemoveUndeclaredAsync(connection, write.Flow, write.Views.Select(v => v.View.Name).ToList(), ct).ConfigureAwait(false);
            return new DimensionViewsWritten(outcomes, dropped);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task<DimensionViewOutcome> WriteOneAsync(
        SqlConnection connection, DimensionViewWrite write, DimensionViewToWrite item, short partitionId, CancellationToken ct)
    {
        var view = item.View;
        string status;
        int viewId;
        try
        {
            // The tables a view reads are settled before its transaction, as a build's are: one no build has made yet is
            // made empty, and one made before tables kept a key's hash is given the column.
            foreach (var table in item.Tables)
            {
                await SqlServerDimensionStore.EnsureViewTableAsync(connection, table, ct).ConfigureAwait(false);
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    (status, viewId) = await WriteViewAsync(connection, write, item, ct).ConfigureAwait(false);
                    break;
                }
                catch (SqlException ex) when (attempt < DeadlockAttempts && SqlServerLedgerBulk.IsDeadlock(ex))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(50, 200) * attempt), ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is DeliveryException or SqlException or InvalidOperationException)
        {
            var error = ex is SqlException sql
                ? $"View {view.Name} could not be written as {DimensionTables.Shown(view.ViewName)}, and keeps its last definition: {SecretHygiene.RedactedMessage(sql.Message.Trim())}"
                : SecretHygiene.RedactedMessage(ex);
            return new DimensionViewOutcome(view.Name, view.ViewName, DimensionViewWriteStatus.Failed, error, null);
        }

        var check = await CheckAsync(connection, write, view, viewId, partitionId, ct).ConfigureAwait(false);
        return new DimensionViewOutcome(view.Name, view.ViewName, status, null, check);
    }

    // The view's record, and the object of its name the schema holds now, with its type.
    private const string RecordSql = """
        SELECT v.[ViewId], v.[Name], v.[FlowName], v.[SqlHash], v.[ViewName]
        FROM [osdu].[DimensionView] AS v
        WHERE UPPER(v.[Name]) = UPPER(@name);

        SELECT o.[type] FROM sys.objects AS o WHERE o.[object_id] = OBJECT_ID(@view);
        """;

    private static async Task<(string Status, int ViewId)> WriteViewAsync(SqlConnection connection, DimensionViewWrite write, DimensionViewToWrite item, CancellationToken ct)
    {
        var view = item.View;
        var definition = view.Definition;
        var shown = DimensionTables.Shown(view.ViewName);
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await LockAsync(connection, transaction, view.Name, $"while it wrote view {view.Name}", ct).ConfigureAwait(false);

            int? recordId = null;
            string? recordFlow = null;
            string? recordHash = null;
            string? recordViewName = null;
            string? objectType = null;
            await using (var read = Command(connection, transaction, RecordSql))
            {
                read.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, DeliveryDimensionView.MaxNameLength) { Value = view.Name });
                read.Parameters.Add(new SqlParameter("@view", SqlDbType.NVarChar, 300) { Value = Qualified(view.ViewName) });
                await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    recordId = reader.GetInt32(0);
                    recordFlow = reader.GetString(2);
                    recordHash = await reader.IsDBNullAsync(3, ct).ConfigureAwait(false) ? null : reader.GetString(3);
                    recordViewName = reader.GetString(4);
                }

                await reader.NextResultAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    objectType = reader.GetString(0).Trim();
                }
            }

            if (recordFlow is not null && !string.Equals(recordFlow, write.Flow, StringComparison.OrdinalIgnoreCase))
            {
                throw new DeliveryException(
                    $"View {view.Name} is declared by dimension flow '{recordFlow}' as well, and a view's name is unique among the flows of a database, so {shown} was not written. Rename one of them, or remove the other flow's view (sqlflow dimensions remove-view {view.Name}) when that flow is gone.");
            }

            if (objectType is not null && (recordId is null || objectType != "V"))
            {
                throw new DeliveryException(recordId is null
                    ? $"{shown} is in the database, and no build of a dimension flow made it, so it is not written over. Drop it, or name the view otherwise."
                    : $"{shown} is in the database as an object of type {objectType}, not the view a build made, so it is not written over. Drop it so the next build writes the view.");
            }

            // The rows of the tables a view reads carry their key's hash, which its joins find them by; a table written
            // before tables kept one is given them, in every partition when the view is written anew, else in the run's.
            var writes = recordId is null || recordHash != definition.Hash || objectType is null;
            foreach (var table in item.Tables)
            {
                await using var fill = Command(connection, transaction, DimensionTables.FillKeyHashSql(table.Name, table.KeyColumn, onePartition: !writes));
                fill.Parameters.Add(new SqlParameter("@partition", SqlDbType.NVarChar, 256) { Value = write.Partition });
                await fill.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (writes)
            {
                // A view a build wrote under the module's former name for views moves to its name now, in the same transaction.
                if (recordViewName is not null && !string.Equals(recordViewName, view.ViewName, StringComparison.OrdinalIgnoreCase))
                {
                    var former = Qualified(recordViewName);
                    await using var drop = Command(connection, transaction, $"IF OBJECT_ID(N'{former.Replace("'", "''", StringComparison.Ordinal)}', N'V') IS NOT NULL DROP VIEW {former};");
                    await drop.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using var create = Command(connection, transaction, definition.CreateSql);
                await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var declaration = JsonSerializer.Serialize(
                new ViewDeclaration(view.From, view.Joins.Select(j => new DimensionViewJoinState(j.As, j.To, j.On, j.View ? DimensionViewSpec.Prefix + j.To : DimensionTables.NameOf(j.To))).ToList(),
                    view.Columns.Count == 0 ? null : view.Columns, view.Where, view.GroupBy.Count == 0 ? null : view.GroupBy),
                Json);
            var columns = JsonSerializer.Serialize(
                definition.Columns.Select(c => new DimensionViewColumnState(c.Name, c.Type, c.Expression, c.DataType, c.Description)).ToList(), Json);
            var tables = JsonSerializer.Serialize(definition.Tables, Json);
            const string Upsert = """
                IF @id IS NULL
                BEGIN
                    INSERT INTO [osdu].[DimensionView] ([Name], [ViewName], [FlowName], [LedgerId], [Description], [DeclarationJson], [Sql], [SqlHash], [TablesJson],
                        [ColumnsJson], [Note], [WrittenRunId], [WrittenBy], [WrittenUtc], [CreatedUtc])
                    VALUES (@name, @viewName, @flow, @ledger, @description, @declaration, @sql, @hash, @tables, @columns, NULL, @run, @actor, @now, @now);
                    SELECT CAST(SCOPE_IDENTITY() AS int);
                END
                ELSE
                BEGIN
                    UPDATE [osdu].[DimensionView] SET [Name] = @name, [ViewName] = @viewName, [FlowName] = @flow, [LedgerId] = @ledger, [Description] = @description, [DeclarationJson] = @declaration,
                        [Sql] = @sql, [SqlHash] = @hash, [TablesJson] = @tables, [ColumnsJson] = @columns, [Note] = NULL,
                        [WrittenRunId] = CASE WHEN @writes = 1 THEN @run ELSE [WrittenRunId] END,
                        [WrittenBy] = CASE WHEN @writes = 1 THEN @actor ELSE [WrittenBy] END,
                        [WrittenUtc] = CASE WHEN @writes = 1 THEN @now ELSE [WrittenUtc] END
                    WHERE [ViewId] = @id;
                    SELECT @id;
                END
                """;
            int viewId;
            await using (var upsert = Command(connection, transaction, Upsert))
            {
                upsert.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = (object?)recordId ?? DBNull.Value });
                upsert.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, DeliveryDimensionView.MaxNameLength) { Value = view.Name });
                upsert.Parameters.Add(new SqlParameter("@viewName", SqlDbType.NVarChar, 128) { Value = view.ViewName });
                upsert.Parameters.Add(new SqlParameter("@flow", SqlDbType.NVarChar, DeliveryLedger.MaxFlowNameLength) { Value = write.Flow });
                upsert.Parameters.Add(new SqlParameter("@ledger", SqlDbType.UniqueIdentifier) { Value = write.FlowLedgerId });
                upsert.Parameters.Add(new SqlParameter("@description", SqlDbType.NVarChar, DeliveryDimensionView.MaxDescriptionLength) { Value = (object?)view.Description ?? DBNull.Value });
                upsert.Parameters.Add(new SqlParameter("@declaration", SqlDbType.NVarChar, -1) { Value = declaration });
                upsert.Parameters.Add(new SqlParameter("@sql", SqlDbType.NVarChar, -1) { Value = definition.CreateSql });
                upsert.Parameters.Add(new SqlParameter("@hash", SqlDbType.NVarChar, 64) { Value = definition.Hash });
                upsert.Parameters.Add(new SqlParameter("@tables", SqlDbType.NVarChar, -1) { Value = tables });
                upsert.Parameters.Add(new SqlParameter("@columns", SqlDbType.NVarChar, -1) { Value = columns });
                upsert.Parameters.Add(new SqlParameter("@run", SqlDbType.UniqueIdentifier) { Value = (object?)write.RunId ?? DBNull.Value });
                upsert.Parameters.Add(new SqlParameter("@actor", SqlDbType.NVarChar, 200) { Value = OsduLedger.Truncate(write.Actor, 200) });
                upsert.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = write.Now });
                upsert.Parameters.Add(new SqlParameter("@writes", SqlDbType.Bit) { Value = writes });
                viewId = Convert.ToInt32(await upsert.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return (writes ? DimensionViewWriteStatus.Written : DimensionViewWriteStatus.Unchanged, viewId);
        }
    }

    /// <summary>What a view's record keeps of its declaration.</summary>
    private sealed record ViewDeclaration(
        string From, IReadOnlyList<DimensionViewJoinState> Joins, IReadOnlyList<DimensionViewColumnSpec>? Columns, string? Where = null, IReadOnlyList<string>? GroupBy = null);

    /// <summary>
    /// Reads the view's rows of the partition (osdu/docs/reference/flow/dimension-views.md) and keeps what it found:
    /// the rows, what each join found, the values each column holds and those each conversion could not read, with
    /// examples. A view that cannot be read is checked column by column to name the column it fails at.
    /// </summary>
    private static async Task<DimensionViewCheckState> CheckAsync(
        SqlConnection connection, DimensionViewWrite write, DimensionViewSpec view, int viewId, short partitionId, CancellationToken ct)
    {
        var definition = view.Definition;
        var stopwatch = Stopwatch.StartNew();
        var status = DimensionViewCheckStatus.Passed;
        long rows = 0;
        var joins = new List<DimensionViewJoinCheck>();
        var columns = new List<DimensionViewColumnCheck>();
        string? error = null;
        try
        {
            var converted = definition.Columns.Where(c => c.InputSql is not null).ToList();
            var counts = new long[1 + (2 * definition.Joins.Count) + converted.Count + definition.Columns.Count];
            await using (var command = new SqlCommand(DimensionViews.CheckSql(definition), connection) { CommandTimeout = CommandTimeoutSeconds })
            {
                command.Parameters.Add(new SqlParameter("@partition", SqlDbType.NVarChar, 256) { Value = write.Partition });
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    for (var i = 0; i < counts.Length; i++)
                    {
                        counts[i] = reader.GetInt64(i);
                    }
                }
            }

            rows = counts[0];
            var at = 1;
            foreach (var join in definition.Joins)
            {
                var matched = counts[at++];
                var unmatched = counts[at++];
                var examples = unmatched > 0 ? await TextsAsync(connection, DimensionViews.UnmatchedSql(definition, join), write.Partition, ct).ConfigureAwait(false) : [];
                joins.Add(new DimensionViewJoinCheck(join.Alias, join.To, join.On, matched, unmatched, examples));
            }

            var unconverted = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var column in converted)
            {
                unconverted[column.Name] = counts[at++];
            }

            foreach (var column in definition.Columns)
            {
                var values = counts[at++];
                long? failed = unconverted.TryGetValue(column.Name, out var n) ? n : null;
                var examples = failed > 0 ? await ExamplesAsync(connection, DimensionViews.UnconvertedSql(definition, column), write.Partition, ct).ConfigureAwait(false) : [];
                columns.Add(new DimensionViewColumnCheck(column.Name, values, failed, examples));
            }
        }
        catch (SqlException ex) when (!ct.IsCancellationRequested)
        {
            status = DimensionViewCheckStatus.Failed;
            var failing = await FailingColumnAsync(connection, definition, write.Partition, ct).ConfigureAwait(false);
            var message = SecretHygiene.RedactedMessage(ex.Message.Trim());
            error = failing is null
                ? $"View {view.Name} could not be read in partition '{write.Partition}': {message}"
                : $"Column {failing} of view {view.Name} could not be computed for every row of partition '{write.Partition}': {message} The view keeps its definition, and the build fails so the pipelines ordered after the flow do not read it; a value that no longer fails, or an expression written around it, lets the next build pass.";
        }

        stopwatch.Stop();
        var checkedUtc = write.Now;
        var duration = (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds);
        long checkId;
        const string Insert = """
            INSERT INTO [osdu].[DimensionViewCheck] ([PartitionId], [ViewId], [RunId], [Status], [Rows], [JoinsJson], [ColumnsJson], [Error], [CheckedUtc], [DurationMs])
            VALUES (@p, @view, @run, @status, @rows, @joins, @columns, @error, @checked, @duration);
            SELECT CAST(SCOPE_IDENTITY() AS bigint);

            DELETE FROM [osdu].[DimensionViewCheck]
            WHERE [ViewId] = @view AND [PartitionId] = @p
              AND [CheckId] NOT IN (
                  SELECT TOP (@kept) c.[CheckId] FROM [osdu].[DimensionViewCheck] AS c
                  WHERE c.[ViewId] = @view AND c.[PartitionId] = @p
                  ORDER BY c.[CheckedUtc] DESC, c.[CheckId] DESC);
            """;
        await using (var insert = new SqlCommand(Insert, connection) { CommandTimeout = CommandTimeoutSeconds })
        {
            insert.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = partitionId });
            insert.Parameters.Add(new SqlParameter("@view", SqlDbType.Int) { Value = viewId });
            insert.Parameters.Add(new SqlParameter("@run", SqlDbType.UniqueIdentifier) { Value = (object?)write.RunId ?? DBNull.Value });
            insert.Parameters.Add(new SqlParameter("@status", SqlDbType.NVarChar, 16) { Value = status });
            insert.Parameters.Add(new SqlParameter("@rows", SqlDbType.BigInt) { Value = rows });
            insert.Parameters.Add(new SqlParameter("@joins", SqlDbType.NVarChar, -1) { Value = joins.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(joins, Json) });
            insert.Parameters.Add(new SqlParameter("@columns", SqlDbType.NVarChar, -1) { Value = columns.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(columns, Json) });
            insert.Parameters.Add(new SqlParameter("@error", SqlDbType.NVarChar, 4000) { Value = (object?)OsduLedger.Truncate(error, 4000) ?? DBNull.Value });
            insert.Parameters.Add(new SqlParameter("@checked", SqlDbType.DateTime2) { Value = checkedUtc });
            insert.Parameters.Add(new SqlParameter("@duration", SqlDbType.Int) { Value = duration });
            insert.Parameters.Add(new SqlParameter("@kept", SqlDbType.Int) { Value = DeliveryDimensionViewCheck.Kept });
            checkId = Convert.ToInt64(await insert.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        return new DimensionViewCheckState(checkId, write.Partition, write.RunId, status, rows, joins, columns, error, checkedUtc, duration);
    }

    /// <summary>The first column of the view that cannot be computed for every row of the partition; null when each can alone.</summary>
    private static async Task<string?> FailingColumnAsync(SqlConnection connection, DimensionViewDefinition definition, string partition, CancellationToken ct)
    {
        foreach (var column in definition.Columns)
        {
            try
            {
                await using var command = new SqlCommand(DimensionViews.ColumnSql(definition, column), connection) { CommandTimeout = CommandTimeoutSeconds };
                command.Parameters.Add(new SqlParameter("@partition", SqlDbType.NVarChar, 256) { Value = partition });
                _ = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }
            catch (SqlException) when (!ct.IsCancellationRequested)
            {
                return column.Name;
            }
        }

        return null;
    }

    private static async Task<IReadOnlyList<string>> TextsAsync(SqlConnection connection, string sql, string partition, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
        command.Parameters.Add(new SqlParameter("@partition", SqlDbType.NVarChar, 256) { Value = partition });
        var texts = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            texts.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty);
        }

        return texts;
    }

    private static async Task<IReadOnlyList<DimensionViewExample>> ExamplesAsync(SqlConnection connection, string sql, string partition, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
        command.Parameters.Add(new SqlParameter("@partition", SqlDbType.NVarChar, 256) { Value = partition });
        var examples = new List<DimensionViewExample>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            examples.Add(new DimensionViewExample(reader.GetInt64(0), await reader.IsDBNullAsync(1, ct).ConfigureAwait(false) ? string.Empty : reader.GetString(1)));
        }

        return examples;
    }

    /// <summary>Drops every view <paramref name="flow"/> made and no longer declares, each under its lock, and its record with its checks.</summary>
    private static async Task<IReadOnlyList<string>> RemoveUndeclaredAsync(SqlConnection connection, string flow, IReadOnlyList<string> declared, CancellationToken ct)
    {
        var made = new List<(string Name, string ViewName)>();
        await using (var read = new SqlCommand("SELECT [Name], [ViewName], [FlowName] FROM [osdu].[DimensionView];", connection) { CommandTimeout = CommandTimeoutSeconds })
        {
            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(2), flow, StringComparison.OrdinalIgnoreCase)
                    && !declared.Contains(reader.GetString(0), StringComparer.OrdinalIgnoreCase))
                {
                    made.Add((reader.GetString(0), reader.GetString(1)));
                }
            }
        }

        var dropped = new List<string>(made.Count);
        foreach (var (name, viewName) in made.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            _ = await RemoveViewAsync(connection, name, viewName, flow, ct).ConfigureAwait(false);
            dropped.Add(name);
        }

        return dropped;
    }

    // A view the module made, dropped with its record and checks; the object is dropped only when it is a view.
    private const string RemoveViewSql = """
        DECLARE @dropped bit = 0;
        IF OBJECT_ID(@view, N'V') IS NOT NULL
        BEGIN
            DECLARE @drop nvarchar(400) = N'DROP VIEW ' + @view + N';';
            EXEC sys.sp_executesql @drop;
            SET @dropped = 1;
        END;

        DECLARE @id int = (SELECT [ViewId] FROM [osdu].[DimensionView] WHERE UPPER([Name]) = UPPER(@name) AND UPPER([FlowName]) = UPPER(@flow));
        DELETE FROM [osdu].[DimensionViewCheck] WHERE [ViewId] = @id;
        DECLARE @checks bigint = @@ROWCOUNT;
        DELETE FROM [osdu].[DimensionView] WHERE [ViewId] = @id;
        SELECT @dropped, @checks, CASE WHEN @id IS NULL THEN 0 ELSE 1 END;
        """;

    private static async Task<(bool Dropped, long Checks, bool Found)> RemoveViewAsync(SqlConnection connection, string name, string viewName, string flow, CancellationToken ct)
    {
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await LockAsync(connection, transaction, name, $"while it dropped view {name}", ct).ConfigureAwait(false);
            await using var command = Command(connection, transaction, RemoveViewSql);
            command.Parameters.Add(new SqlParameter("@view", SqlDbType.NVarChar, 300) { Value = Qualified(viewName) });
            command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, DeliveryDimensionView.MaxNameLength) { Value = name });
            command.Parameters.Add(new SqlParameter("@flow", SqlDbType.NVarChar, DeliveryLedger.MaxFlowNameLength) { Value = flow });
            (bool, long, bool) result = (false, 0, false);
            await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    result = (reader.GetBoolean(0), reader.GetInt64(1), reader.GetInt32(2) == 1);
                }
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
    }

    /// <summary>
    /// Drops, in <paramref name="transaction"/>, every view a build made that reads <paramref name="table"/>, and marks its
    /// record to be written again with <paramref name="note"/> saying why: what a build that renames a column of the table
    /// does before it renames (osdu/docs/reference/flow/dimension-views.md). A view naming the old column would fail, or,
    /// where two columns swap names, read the other one; the run's view step writes it from the document again. Each view's
    /// lock is taken before the rename touches the table, so a build writing a view and one renaming a column it reads never
    /// wait on each other.
    /// </summary>
    /// <returns>The names of the views dropped.</returns>
    internal static async Task<IReadOnlyList<string>> ClearViewsReadingAsync(
        SqlConnection connection, SqlTransaction transaction, string table, string note, CancellationToken ct)
    {
        var reading = new List<(string Name, string ViewName)>();
        await using (var read = Command(connection, transaction, "SELECT [Name], [ViewName], [TablesJson] FROM [osdu].[DimensionView];"))
        {
            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (TablesOf(reader.GetString(2)).Contains(table, StringComparer.OrdinalIgnoreCase))
                {
                    reading.Add((reader.GetString(0), reader.GetString(1)));
                }
            }
        }

        foreach (var (name, viewName) in reading.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            await LockAsync(connection, transaction, name, $"while a build renamed a column of {DimensionTables.Shown(table)}", ct).ConfigureAwait(false);
            await using var drop = Command(
                connection, transaction,
                """
                IF OBJECT_ID(@view, N'V') IS NOT NULL
                BEGIN
                    DECLARE @drop nvarchar(400) = N'DROP VIEW ' + @view + N';';
                    EXEC sys.sp_executesql @drop;
                END;

                UPDATE [osdu].[DimensionView] SET [SqlHash] = NULL, [Note] = @note WHERE UPPER([Name]) = UPPER(@name);
                """);
            drop.Parameters.Add(new SqlParameter("@view", SqlDbType.NVarChar, 300) { Value = Qualified(viewName) });
            drop.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, DeliveryDimensionView.MaxNameLength) { Value = name });
            drop.Parameters.Add(new SqlParameter("@note", SqlDbType.NVarChar, 1000) { Value = OsduLedger.Truncate(note, 1000) });
            await drop.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return reading.Select(r => r.Name).ToList();
    }

    /// <summary>Removes a view for good: the view from the database when it is there, and its record with its checks. Null when no view of the name is recorded.</summary>
    public static async Task<DimensionViewRemoved?> RemoveAsync(OsduDbContext db, string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var records = await db.DeliveryDimensionViews.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        if (records.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } record)
        {
            return null;
        }

        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            var (dropped, checks, found) = await RemoveViewAsync(connection, record.Name, record.ViewName, record.FlowName, ct).ConfigureAwait(false);
            return found ? new DimensionViewRemoved(record.Name, record.ViewName, record.FlowName, dropped, checks) : null;
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The views recorded, of one flow when <paramref name="flow"/> names one, by name.</summary>
    public static async Task<IReadOnlyList<DimensionViewState>> ListAsync(OsduDbContext db, string? flow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await db.DeliveryDimensionViews.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return rows
            .Where(r => flow is null || string.Equals(r.FlowName, flow, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToState)
            .ToList();
    }

    /// <summary>The view named <paramref name="name"/>, ignoring case, with its newest checks; null when none is recorded.</summary>
    public static async Task<DimensionViewDetail?> GetAsync(OsduDbContext db, string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await db.DeliveryDimensionViews.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        if (rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } record)
        {
            return null;
        }

        var checks = await db.DeliveryDimensionViewChecks.AsNoTracking()
            .Where(c => c.ViewId == record.ViewId)
            .OrderByDescending(c => c.CheckedUtc).ThenByDescending(c => c.CheckId)
            .Take(ShownChecks)
            .ToListAsync(ct).ConfigureAwait(false);
        var partitions = await db.DeliveryLedgerPartitions.AsNoTracking().ToDictionaryAsync(p => p.PartitionId, p => p.Name, ct).ConfigureAwait(false);
        return new DimensionViewDetail(ToState(record), checks.Select(c => ToState(c, partitions.GetValueOrDefault(c.PartitionId) ?? string.Empty)).ToList());
    }

    /// <summary>The views a build made that read <paramref name="table"/>, by name.</summary>
    public static async Task<IReadOnlyList<string>> ReadingAsync(OsduDbContext db, string table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await db.DeliveryDimensionViews.AsNoTracking().Select(v => new { v.Name, v.TablesJson }).ToListAsync(ct).ConfigureAwait(false);
        return rows.Where(r => TablesOf(r.TablesJson).Contains(table, StringComparer.OrdinalIgnoreCase)).Select(r => r.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// What a build of <paramref name="flow"/> would do with each of <paramref name="views"/>, read and written nowhere: a name
    /// another flow's view holds, or an object no build made, stops it; a table no build has made yet is made empty.
    /// </summary>
    public static async Task<IReadOnlyList<DimensionViewProbe>> ProbeAsync(OsduDbContext db, string flow, IReadOnlyList<DimensionViewSpec> views, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(views);
        var records = await db.DeliveryDimensionViews.AsNoTracking().Select(v => new { v.Name, v.FlowName, v.SqlHash }).ToListAsync(ct).ConfigureAwait(false);
        var names = views.Select(v => v.ViewName).Concat(views.SelectMany(v => v.Definition.Tables)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var objects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            foreach (var name in names)
            {
                await using var command = new SqlCommand("SELECT o.[type] FROM sys.objects AS o WHERE o.[object_id] = OBJECT_ID(@name);", connection) { CommandTimeout = CommandTimeoutSeconds };
                command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 300) { Value = DimensionTables.Qualified(name) });
                if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string type)
                {
                    objects[name] = type.Trim();
                }
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        var probes = new List<DimensionViewProbe>(views.Count);
        foreach (var view in views)
        {
            var problems = new List<string>();
            var notes = new List<string>();
            var record = records.FirstOrDefault(r => string.Equals(r.Name, view.Name, StringComparison.OrdinalIgnoreCase));
            var shown = DimensionTables.Shown(view.ViewName);
            if (record is not null && !string.Equals(record.FlowName, flow, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"View {view.Name} is declared by dimension flow '{record.FlowName}' as well; a view's name is unique among the flows of a database.");
            }
            else if (objects.TryGetValue(view.ViewName, out var type) && (record is null || type != "V"))
            {
                problems.Add($"{shown} is in the database, and no build of a dimension flow made it, so a build would not write over it.");
            }
            else if (record is null || !objects.ContainsKey(view.ViewName))
            {
                notes.Add($"A build writes {shown}.");
            }
            else
            {
                notes.Add(record.SqlHash == view.Definition.Hash ? $"{shown} is as the document declares it; a build checks it." : $"A build writes {shown} again: its document changed.");
            }

            foreach (var table in view.Definition.Tables.Where(t => !objects.ContainsKey(t)))
            {
                notes.Add($"{DimensionTables.Shown(table)} is not in the database yet; a build makes it empty, and its join finds nothing until its dimension is built.");
            }

            probes.Add(new DimensionViewProbe(view.Name, problems, notes));
        }

        return probes;
    }

    /// <summary>The database <paramref name="db"/> connects to, as its server names it.</summary>
    public static async Task<DatabaseIdentity> IdentityAsync(OsduDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            return await IdentityAsync((SqlConnection)db.Database.GetDbConnection(), ct).ConfigureAwait(false);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The database an open <paramref name="connection"/> is on: the server's name, the database's, and when the database was made.</summary>
    public static async Task<DatabaseIdentity> IdentityAsync(SqlConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new SqlCommand(
            "SELECT CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)), DB_NAME(), (SELECT d.[create_date] FROM sys.databases AS d WHERE d.[database_id] = DB_ID());",
            connection) { CommandTimeout = 60 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new DatabaseIdentity(reader.IsDBNull(0) ? string.Empty : reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? DateTime.MinValue : reader.GetDateTime(2))
            : throw new DeliveryException("The database did not say which it is.");
    }

    private static IReadOnlyList<string> TablesOf(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static DimensionViewState ToState(DeliveryDimensionView row)
    {
        ViewDeclaration? declaration = null;
        IReadOnlyList<DimensionViewColumnState> columns = [];
        try
        {
            declaration = JsonSerializer.Deserialize<ViewDeclaration>(row.DeclarationJson, Json);
            columns = JsonSerializer.Deserialize<List<DimensionViewColumnState>>(row.ColumnsJson, Json) ?? [];
        }
        catch (JsonException)
        {
            // A record written by a build this one cannot read shows its names and SQL; its next build rewrites the rest.
        }

        return new DimensionViewState(
            row.ViewId, row.Name, row.ViewName, row.FlowName, row.LedgerId, row.Description, declaration?.From ?? string.Empty, declaration?.Joins ?? [], columns,
            TablesOf(row.TablesJson), row.Sql, row.SqlHash is not null, row.Note, row.WrittenRunId, row.WrittenBy, row.WrittenUtc, row.CreatedUtc, declaration?.Where,
            declaration?.GroupBy);
    }

    private static DimensionViewCheckState ToState(DeliveryDimensionViewCheck row, string partition)
    {
        IReadOnlyList<DimensionViewJoinCheck> joins = [];
        IReadOnlyList<DimensionViewColumnCheck> columns = [];
        try
        {
            joins = row.JoinsJson is null ? [] : JsonSerializer.Deserialize<List<DimensionViewJoinCheck>>(row.JoinsJson, Json) ?? [];
            columns = row.ColumnsJson is null ? [] : JsonSerializer.Deserialize<List<DimensionViewColumnCheck>>(row.ColumnsJson, Json) ?? [];
        }
        catch (JsonException)
        {
            // A check written by a build this one cannot read shows its counts and its error.
        }

        return new DimensionViewCheckState(row.CheckId, partition, row.RunId, row.Status, row.Rows, joins, columns, row.Error, row.CheckedUtc, row.DurationMs);
    }

    private static async Task LockAsync(SqlConnection connection, SqlTransaction transaction, string name, string doing, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, LockSql);
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = LockResource(name) });
        command.Parameters.Add(new SqlParameter("@timeout", SqlDbType.Int) { Value = LockTimeoutMs });
        var granted = Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (granted < 0)
        {
            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                $"Another build held view {name}'s lock for more than {LockTimeoutMs / 1000} seconds {doing} (sp_getapplock answered {granted}), so nothing was written. Run the flow again when the other has finished."));
        }
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string sql)
        => new(sql, connection, transaction) { CommandTimeout = CommandTimeoutSeconds };
}
