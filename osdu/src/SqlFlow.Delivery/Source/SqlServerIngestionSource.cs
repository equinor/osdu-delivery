using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Rendering;

namespace SqlFlow.Delivery.Source;

/// <summary>Opens a flow's ingestion tables on SQL Server or Azure SQL, with the flow's own connection reference.</summary>
public sealed class SqlServerIngestionSourceFactory : IIngestionSourceFactory
{
    private readonly ISecretResolver _secrets;

    public SqlServerIngestionSourceFactory(ISecretResolver secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = secrets;
    }

    public IIngestionSource Open(FlowDefinition flow, IReadOnlyDictionary<string, string> values, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(loggers);
        return new SqlServerIngestionSource(flow, values, _secrets, loggers.CreateLogger<SqlServerIngestionSource>());
    }
}

/// <summary>
/// Reads a flow's records out of the ingestion tables SQLFlow loads (osdu/docs/reference/concepts/change-detection.md,
/// Which rows a run reads; osdu/docs/reference/flow/delivery.md, source). A read fixes its window at open, pages its
/// candidates by the record table's identity primary key (or by the record key when the flow names none), and then
/// reads one page's rows as one command: the record rows and one result set per child dataset, under snapshot isolation
/// by default so a record and its child rows always describe the same moment. A fan-out cuts the candidates into ranges
/// of the primary key from a count per range of its values, so nothing ranks or sorts every candidate.
/// </summary>
public sealed class SqlServerIngestionSource : IIngestionSource
{
    private const int DeadlockError = 1205;
    private const int SnapshotNotAllowed = 3952;
    private const int SnapshotDisabled = 3951;
    private const int MaxDeadlockRetries = 3;

    /// <summary>The longest origin file name the ledger stores for a record.</summary>
    private const int MaxFileNameLength = 800;

    private readonly FlowDefinition _flow;
    private readonly IReadOnlyDictionary<string, string> _values;
    private readonly ISecretResolver _secrets;
    private readonly ILogger<SqlServerIngestionSource> _logger;
    private readonly KeyPaths _keys;
    private readonly Dictionary<string, IReadOnlyDictionary<string, SourceColumn>> _tables = new(StringComparer.OrdinalIgnoreCase);
    private IngestionLayout? _layout;
    private string? _fileNameColumn;
    private string? _rowNumberColumn;
    private string? _insertedColumn;

    public SqlServerIngestionSource(FlowDefinition flow, IReadOnlyDictionary<string, string> values, ISecretResolver secrets, ILogger<SqlServerIngestionSource> logger)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);
        _flow = flow;
        _keys = KeyPaths.Of(flow);
        _values = values;
        _secrets = secrets;
        _logger = logger;
    }

    public async Task VerifyAsync(CancellationToken ct = default)
    {
        await using var connection = await IngestionConnection.OpenAsync(_flow.Source.Connection, _flow.Name, _secrets, ct).ConfigureAwait(false);
        await BuildLayoutAsync(connection, ct).ConfigureAwait(false);
    }

    public async Task<SourceHeader> OpenAsync(SourceSelection selection, SourceWindow? stored, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        await using var connection = await IngestionConnection.OpenAsync(_flow.Source.Connection, _flow.Name, _secrets, ct).ConfigureAwait(false);
        var layout = await BuildLayoutAsync(connection, ct).ConfigureAwait(false);
        var upper = stored?.UpperUtc ?? await NowAsync(connection, ct).ConfigureAwait(false);
        var lower = stored is not null
            ? stored.LowerUtc
            : selection.Kind == SourceSelectionKind.Incremental ? selection.LowerUtc : null;
        var window = new SourceWindow(lower, upper);
        var columns = _tables.ToDictionary(t => t.Key, t => (IReadOnlySet<string>)t.Value.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<KeyTuple> missing = [];
        IReadOnlyList<KeyTuple> outOfScope = [];
        long candidates;
        if (selection.Kind == SourceSelectionKind.Keys)
        {
            var probe = await ProbeKeysAsync(connection, layout, selection.Keys, ct).ConfigureAwait(false);
            missing = probe.Missing;
            outOfScope = probe.OutOfScope;
            candidates = probe.InScope;
        }
        else
        {
            candidates = await CountAsync(connection, layout, selection, window, ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Source {Object} opened for {Selection}: window up to {Upper:O}, {Candidates} candidate record(s).",
            layout.Record.ToString(), selection.Describe(), window.UpperUtc, candidates);

        return new SourceHeader
        {
            Selection = selection,
            Window = window,
            Columns = columns,
            KeyColumns = layout.Key,
            PrimaryKey = layout.PrimaryKey?.Name,
            EstimatedCandidates = candidates,
            HasChanges = candidates > 0,
            MissingKeys = missing,
            OutOfScopeKeys = outOfScope,
        };
    }

    public async Task<IReadOnlyList<KeyRange>> SliceBoundsAsync(SourceHeader header, int slices, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentOutOfRangeException.ThrowIfLessThan(slices, 1);
        var layout = Layout();
        if (slices == 1 || header.EstimatedCandidates <= 1)
        {
            return [new KeyRange(0, null, null, layout.PrimaryKey?.Name)];
        }

        var primaryKey = layout.PrimaryKey
            ?? throw new DeliveryException(
                $"Flow '{_flow.Label}': a read is cut into slices on the record table's identity primary key, and the flow names none under {_keys.Name("source.record.primaryKey")}.");
        await using var connection = await IngestionConnection.OpenAsync(_flow.Source.Connection, _flow.Name, _secrets, ct).ConfigureAwait(false);
        await using var command = Command(connection, IngestionSql.SliceHistogram(layout, header.Selection.Kind, header.Window.LowerUtc is not null));
        Bind(command, layout, header, IngestionSql.KeyBounds.None, null, null);
        command.Parameters.Add(new SqlParameter(IngestionSql.BucketsParameter, SqlDbType.BigInt) { Value = (long)slices * KeySlices.BucketsPerSlice });

        long lowest;
        long width;
        var buckets = new List<(long Bucket, long Count)>();
        await using (var reader = await ExecuteReaderAsync(command, "count the candidates per range of the primary key", ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0))
            {
                // Nothing to cut: the candidates the read counted at open are gone.
                return [new KeyRange(0, null, null, primaryKey.Name)];
            }

            lowest = reader.GetInt64(0);
            width = reader.GetInt64(1);
            if (!await reader.NextResultAsync(ct).ConfigureAwait(false))
            {
                throw new DeliveryException($"Flow '{_flow.Label}': the source did not return how the candidates spread over {primaryKey.Name}.");
            }

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                buckets.Add((reader.GetInt64(0), reader.GetInt64(1)));
            }
        }

        var bounds = KeySlices.CutHistogram(lowest, width, buckets, slices);
        var ranges = new List<KeyRange>(bounds.Count + 1);
        KeyTuple? from = null;
        for (var i = 0; i < bounds.Count; i++)
        {
            var to = KeyTuple.Of(bounds[i].ToString(CultureInfo.InvariantCulture));
            ranges.Add(new KeyRange(i, from, to, primaryKey.Name));
            from = to;
        }

        ranges.Add(new KeyRange(bounds.Count, from, null, primaryKey.Name));
        _logger.LogInformation(
            "Cut {Candidates} candidate record(s) into {Slices} range(s) of {Column}, from {Buckets} counted range(s) of {Width} value(s) each.",
            buckets.Sum(b => b.Count), ranges.Count, primaryKey.Name, buckets.Count, width);
        return ranges;
    }

    public async IAsyncEnumerable<SourceRecord> ReadAsync(SourceHeader header, KeyRange? range, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(header);
        var layout = Layout();
        if (range is { } slice && !string.Equals(slice.On, layout.PrimaryKey?.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeliveryException(
                $"Flow '{_flow.Label}': slice {slice.Slice} of this submission was cut on {(slice.On is null ? "the record key" : $"column '{slice.On}'")}, "
                + $"and the flow now reads by {(layout.PrimaryKey is { } declared ? $"column '{declared.Name}'" : "the record key")}. Plan a new submission rather than re-running this one.");
        }

        var pageSize = _flow.Source.Incremental.PageSize;
        await using var connection = await IngestionConnection.OpenAsync(_flow.Source.Connection, _flow.Name, _secrets, ct).ConfigureAwait(false);
        var after = range?.From;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await CandidatePageAsync(connection, layout, header, after, range, pageSize, ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                yield break;
            }

            foreach (var record in await PageRecordsAsync(connection, layout, page, ct).ConfigureAwait(false))
            {
                yield return record;
            }

            if (page.Count < pageSize)
            {
                yield break;
            }

            after = page[^1];
        }
    }

    private IngestionLayout Layout()
        => _layout ?? throw new DeliveryException($"Flow '{_flow.Label}': the source has to be opened before it is read.");

    private SqlCommand Command(SqlConnection connection, string text)
    {
        var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = _flow.Source.Incremental.CommandTimeoutSeconds;
        return command;
    }

    public async Task<IReadOnlyList<SourceKeyColumn>> KeyColumnsAsync(CancellationToken ct = default)
    {
        await using var connection = await IngestionConnection.OpenAsync(_flow.Source.Connection, _flow.Name, _secrets, ct).ConfigureAwait(false);
        var layout = await BuildLayoutAsync(connection, ct, guardScope: false).ConfigureAwait(false);
        return layout.Key;
    }

    public async IAsyncEnumerable<IngestionKey> ReadKeysAsync(int pageSize, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        await using var connection = await IngestionConnection.OpenAsync(_flow.Source.Connection, _flow.Name, _secrets, ct).ConfigureAwait(false);
        var layout = await BuildLayoutAsync(connection, ct).ConfigureAwait(false);
        var keys = layout.Key.Count;
        KeyTuple? after = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = new List<(IngestionKey Key, KeyTuple Order)>(pageSize);
            await using (var command = Command(connection, IngestionSql.KeyPage(layout, after is not null)))
            {
                BindScope(command, layout);
                if (after is not null)
                {
                    BindOrder(command, layout, IngestionSql.AfterPrefix, after);
                }

                command.Parameters.Add(new SqlParameter(IngestionSql.PageParameter, SqlDbType.Int) { Value = pageSize });
                await using var reader = await ExecuteReaderAsync(command, "read a page of the record table's keys", ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var key = ReadKey(reader, layout);
                    var updated = SourceValues.Normalize(reader.GetValue(keys)) is DateTime moment ? DateTime.SpecifyKind(moment, DateTimeKind.Utc) : (DateTime?)null;
                    var order = new string[layout.Order.Count];
                    for (var i = 0; i < order.Length; i++)
                    {
                        order[i] = SourceRow.Stringify(SourceValues.Normalize(reader.GetValue(keys + 1 + i)))
                            ?? throw new DeliveryException($"The record table {layout.Record} returned a null in column '{layout.Order[i].Name}', which cannot place a record.");
                    }

                    page.Add((new IngestionKey(key, updated), new KeyTuple(order)));
                }
            }

            foreach (var (key, _) in page)
            {
                yield return key;
            }

            if (page.Count < pageSize)
            {
                yield break;
            }

            after = page[^1].Order;
        }
    }

    public async Task<IReadOnlyList<ScopeParameterValues>> ScopeValuesAsync(int max, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        await using var connection = await IngestionConnection.OpenAsync(_flow.Source.Connection, _flow.Name, _secrets, ct).ConfigureAwait(false);

        // What a scope can be given is listed before any value is: the check of a scope's rows is a run's, not this read's.
        var layout = await BuildLayoutAsync(connection, ct, guardScope: false).ConfigureAwait(false);
        var record = _tables[SourceDatasets.Record];
        var where = KeyPaths.Where(_flow);
        var listed = new List<ScopeParameterValues>(layout.Scope.Count);
        foreach (var (column, parameter) in layout.Scope)
        {
            var declared = Column(record, layout.Record, column, $"{where}: {_keys.Name("source.record.scope")} binds parameter '{parameter}' to column '{column}'");
            await using var command = Command(connection, IngestionSql.ScopeValues(layout, declared.Name));
            // One value past the ones listed says the column holds more.
            command.Parameters.Add(new SqlParameter(IngestionSql.TopParameter, SqlDbType.Int) { Value = max + 1 });
            var values = new List<ScopeValue>(Math.Min(max + 1, 1000));
            await using (var reader = await ExecuteReaderAsync(command, $"list the values of {declared.Name}", ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (SourceRow.Stringify(SourceValues.Normalize(reader.GetValue(0))) is { } text)
                    {
                        values.Add(new ScopeValue(text, reader.GetInt64(1)));
                    }
                }
            }

            listed.Add(new ScopeParameterValues(parameter, declared.Name, values.Take(max).ToList(), values.Count > max));
        }

        return listed;
    }

    private async Task<DateTime> NowAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = Command(connection, IngestionSql.UpperBound());
        var value = await ExecuteScalarAsync(command, "read the source database's clock", ct).ConfigureAwait(false);
        return value is DateTime moment
            ? DateTime.SpecifyKind(moment, DateTimeKind.Utc)
            : throw new DeliveryException($"Flow '{_flow.Label}': the source database did not answer with a moment for the read's upper bound.");
    }

    /// <summary>
    /// Reads the tables' shape and checks the flow's declarations against it. <paramref name="guardScope"/> false leaves out
    /// the check that no row of the run's scope has an unknown key, which reads the scope and so needs its values: for a
    /// caller that reads no row of a scope (the listing of the values a scope can be given).
    /// </summary>
    private async Task<IngestionLayout> BuildLayoutAsync(SqlConnection connection, CancellationToken ct, bool guardScope = true)
    {
        var source = _flow.Source;
        var where = KeyPaths.Where(_flow);
        var recordName = SourceObjectName.Parse(source.Record.Object);
        var record = await ReadColumnsAsync(connection, recordName, _keys.Name("source.record.object"), ct).ConfigureAwait(false);
        _tables[SourceDatasets.Record] = record;

        var key = new List<SourceKeyColumn>(source.Record.Key.Count);

        // The key columns whose declaration allows a null. SQLFlow's ingestion creates a target's data columns nullable
        // and never tightens them, so requiring the declaration to say NOT NULL would refuse every table its own flows
        // build. What a delivery actually needs is that no record it would deliver carries an unknown key, which is
        // checked against the rows below once the layout is known.
        var nullableKeys = new List<string>();
        foreach (var column in source.Record.Key)
        {
            var declared = Column(record, recordName, column, $"{where}: {_keys.Name("source.record.key")} names column '{column}'");
            if (declared.Nullable)
            {
                nullableKeys.Add(declared.Name);
            }

            if (!declared.Comparable)
            {
                throw new FlowValidationException(
                    $"{where}: {_keys.Name("source.record.key")} names column '{column}' of {recordName}, which is {declared.SqlType}. A key column has to be a comparable type (text with a length, a number, a date, a uniqueidentifier).");
            }

            key.Add(new SourceKeyColumn(column, declared.SqlType));
        }

        SourceKeyColumn? primaryKey = null;
        if (source.Record.PrimaryKey is { } primaryKeyName)
        {
            var declared = Column(record, recordName, primaryKeyName, $"{where}: {_keys.Name("source.record.primaryKey")} names column '{primaryKeyName}'");
            if (!declared.IsInteger)
            {
                throw new FlowValidationException(
                    $"{where}: {_keys.Name("source.record.primaryKey")} names column '{declared.Name}' of {recordName}, which is {declared.SqlType}. "
                    + "A read is paged and cut on an integer identity column (the ingestion flow's target.identityColumn).");
            }

            primaryKey = new SourceKeyColumn(declared.Name, declared.SqlType);
        }

        var updated = Column(record, recordName, source.SystemColumns.Updated, $"{where}: {_keys.Name("source.systemColumns.updated")} names column '{source.SystemColumns.Updated}'");
        if (!updated.IsMoment)
        {
            throw new FlowValidationException(
                $"{where}: {_keys.Name("source.systemColumns.updated")} names column '{source.SystemColumns.Updated}' of {recordName}, which is {updated.SqlType}. An incremental read windows on a date and time column (SQLFlow's UpdatedDate_DW).");
        }

        var deleted = Optional(record, source.SystemColumns.Deleted);
        if (source.SystemColumns.DeletedDeclared && source.SystemColumns.Deleted is { } declaredDeleted && deleted is null)
        {
            throw new FlowValidationException($"{where}: {_keys.Name("source.systemColumns.deleted")} names column '{declaredDeleted}', which the record table {recordName} does not hold.");
        }

        // A record's origin file and row are what the ledger traces it back to. A column the flow names itself has to be
        // there, so a misspelt name is refused rather than losing every record's origin without a word; one left at its
        // default is read when the table carries it.
        var fileName = Optional(record, source.SystemColumns.FileName);
        if (source.SystemColumns.FileNameDeclared && source.SystemColumns.FileName is { } declaredFileName && fileName is null)
        {
            throw MissingProvenance("source.systemColumns.fileName", declaredFileName, "origin file", recordName, record, where);
        }

        _fileNameColumn = fileName?.Name;
        if (fileName is not null && fileName.MaxCharacters > MaxFileNameLength)
        {
            throw new FlowValidationException(
                $"{where}: {_keys.Name("source.systemColumns.fileName")} names column '{fileName.Name}' of {recordName}, which holds up to {fileName.MaxCharacters.ToString(CultureInfo.InvariantCulture)} characters. "
                + $"The ledger stores a record's origin file in {MaxFileNameLength.ToString(CultureInfo.InvariantCulture)} characters; land the files under a shorter root, or opt out with {_keys.Name("source.systemColumns.fileName")}: ~.");
        }

        var rowNumber = Optional(record, source.SystemColumns.RowNumber);
        if (source.SystemColumns.RowNumberDeclared && source.SystemColumns.RowNumber is { } declaredRowNumber && rowNumber is null)
        {
            throw MissingProvenance("source.systemColumns.rowNumber", declaredRowNumber, "origin row", recordName, record, where);
        }

        if (rowNumber is not null && !rowNumber.IsWholeNumber)
        {
            throw new FlowValidationException(
                $"{where}: {_keys.Name("source.systemColumns.rowNumber")} names column '{rowNumber.Name}' of {recordName}, which is {rowNumber.SqlType}. "
                + $"A record's origin row is a whole number (SQLFlow's {FlowSystemColumns.DefaultRowNumber} is a bigint); name another, or opt out with {_keys.Name("source.systemColumns.rowNumber")}: ~.");
        }

        _rowNumberColumn = rowNumber?.Name;

        var inserted = Optional(record, source.SystemColumns.Inserted);
        if (source.SystemColumns.InsertedDeclared && source.SystemColumns.Inserted is { } declaredInserted && inserted is null)
        {
            throw new FlowValidationException($"{where}: {_keys.Name("source.systemColumns.inserted")} names column '{declaredInserted}', which the record table {recordName} does not hold.");
        }

        if (inserted is not null && !inserted.IsMoment)
        {
            throw new FlowValidationException(
                $"{where}: {_keys.Name("source.systemColumns.inserted")} names column '{inserted.Name}' of {recordName}, which is {inserted.SqlType}. "
                + $"The moment a row first reached the ingestion table is a date and time column (SQLFlow's {FlowSystemColumns.DefaultInserted}); name another, or opt out with {_keys.Name("source.systemColumns.inserted")}: ~.");
        }

        _insertedColumn = inserted?.Name;

        var datasets = new List<IngestionDataset>(source.Datasets.Count);
        foreach (var (name, dataset) in source.Datasets.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            var objectName = SourceObjectName.Parse(dataset.Object);
            var columns = await ReadColumnsAsync(connection, objectName, $"{_keys.Name("source.datasets")}.{name}.object", ct).ConfigureAwait(false);
            _tables[name] = columns;
            var join = new List<KeyValuePair<string, string>>(dataset.Join.Count);
            foreach (var recordColumn in source.Record.Key)
            {
                var childColumn = dataset.Join.FirstOrDefault(j => j.Value.Equals(recordColumn, StringComparison.OrdinalIgnoreCase)).Key
                    ?? throw new FlowValidationException($"{where}: {_keys.Name("source.datasets")}.{name}.join does not cover key column '{recordColumn}' of the record table.");
                Column(columns, objectName, childColumn, $"{where}: {_keys.Name("source.datasets")}.{name}.join names child column '{childColumn}'");
                join.Add(new KeyValuePair<string, string>(childColumn, recordColumn));
            }

            foreach (var order in dataset.OrderBy)
            {
                Column(columns, objectName, order, $"{where}: {_keys.Name("source.datasets")}.{name}.orderBy names column '{order}'");
            }

            datasets.Add(new IngestionDataset(
                name,
                objectName,
                join,
                dataset.OrderBy,
                Optional(columns, source.SystemColumns.Updated)?.Name,
                Optional(columns, source.SystemColumns.Deleted)?.Name,
                dataset.MaxRowsPerRecord));
        }

        _layout = new IngestionLayout
        {
            Record = recordName,
            Key = key,
            PrimaryKey = primaryKey,
            Updated = updated.Name,
            Deleted = deleted?.Name,
            Scope = source.Record.Scope.Select(s => new KeyValuePair<string, string>(s.Key, s.Value)).ToList(),
            Datasets = datasets,
        };

        if (primaryKey is not null)
        {
            await GuardPrimaryKeyAsync(connection, _layout, recordName, where, ct).ConfigureAwait(false);
        }

        if (guardScope && nullableKeys.Count > 0)
        {
            await GuardKnownKeysAsync(connection, _layout, recordName, nullableKeys, where, ct).ConfigureAwait(false);
        }

        return _layout;
    }

    /// <summary>
    /// Refuses a declared primary key the read cannot rely on. Ranges of it deal records to runs, so it has to be the
    /// table's own single-column primary key (unique, and the index every other index points at), an identity column (so
    /// the values ascend as rows arrive), and the record key has to be unique on its own: a record held by two rows could
    /// otherwise fall into two ranges and be planned by two runs.
    /// </summary>
    private async Task GuardPrimaryKeyAsync(SqlConnection connection, IngestionLayout layout, SourceObjectName recordName, string where, CancellationToken ct)
    {
        var primaryKey = layout.PrimaryKey!;
        await using var command = Command(connection, IngestionSql.PrimaryKeyProbe(recordName));
        command.Parameters.Add(new SqlParameter(IngestionSql.ObjectParameter, SqlDbType.NVarChar, 386) { Value = recordName.ToString() });
        command.Parameters.Add(new SqlParameter(IngestionSql.ColumnParameter, SqlDbType.NVarChar, 128) { Value = primaryKey.Name });
        command.Parameters.Add(new SqlParameter(IngestionSql.KeyNamesParameter, SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(layout.Key.Select(k => _tables[SourceDatasets.Record][k.Name].Name)) });
        bool identity;
        bool isPrimaryKey;
        bool keyUnique;
        await using (var reader = await ExecuteReaderAsync(command, "check the record table's primary key", ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new DeliveryException($"Flow '{_flow.Label}': the source did not say whether {recordName} has the primary key the flow names.");
            }

            identity = reader.GetInt32(0) == 1;
            isPrimaryKey = reader.GetInt32(1) == 1;
            keyUnique = reader.GetInt32(2) == 1;
        }

        var column = SourceObjectName.Quote(primaryKey.Name);
        if (!identity || !isPrimaryKey)
        {
            var missing = !identity && !isPrimaryKey
                ? "neither an identity column nor the table's primary key"
                : !identity ? "not an identity column" : "not the table's single-column primary key";
            throw new FlowValidationException(
                $"{where}: {_keys.Name("source.record.primaryKey")} names column '{primaryKey.Name}' of {recordName}, which is {missing}. "
                + $"The ingestion flow creates it with target.identityColumn: {primaryKey.Name} when it creates the table. An existing table needs it added once, "
                + $"which rewrites the table (drop a plain column of that name first): ALTER TABLE {recordName.Quoted} ADD {column} bigint IDENTITY(1, 1) NOT NULL "
                + $"CONSTRAINT {SourceObjectName.Quote("PK_" + recordName.Name)} PRIMARY KEY CLUSTERED; (NONCLUSTERED when the table already has a clustered index).");
        }

        if (!keyUnique)
        {
            throw new FlowValidationException(
                $"{where}: {_keys.Name("source.record.key")} ({string.Join(", ", layout.Key.Select(k => k.Name))}) has no unique index without a filter on {recordName}, so a record could be held by two rows "
                + "and a fan-out could deal them to two runs. The ingestion flow's load.keyColumns creates one (NCI_KeyColumn); a table that keeps history (SCD2) filters it and cannot be read by ranges of its primary key.");
        }
    }

    /// <summary>
    /// Refuses a run whose record table holds a row with an unknown key, naming the column. Two records with a null key
    /// would share one identity, so a delivery cannot tell them apart or address either of them; the run stops before
    /// anything is planned rather than delivering one of them under the other's id. Only the columns whose declaration
    /// allows a null are tested, so a table that already forbids them costs nothing.
    /// </summary>
    private async Task GuardKnownKeysAsync(
        SqlConnection connection, IngestionLayout layout, SourceObjectName recordName, IReadOnlyList<string> nullableKeys,
        string where, CancellationToken ct)
    {
        await using var command = Command(connection, IngestionSql.NullKeyProbe(layout, nullableKeys));
        BindScope(command, layout);
        await using var reader = await ExecuteReaderAsync(command, "check the record table's keys for unknown values", ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        var unknown = new List<string>(nullableKeys.Count);
        for (var i = 0; i < nullableKeys.Count; i++)
        {
            if (!reader.IsDBNull(i) && reader.GetInt32(i) == 1)
            {
                unknown.Add(nullableKeys[i]);
            }
        }

        throw new FlowValidationException(
            $"{where}: the record table {recordName} holds a row whose key column {(unknown.Count == 1 ? $"'{unknown[0]}' is" : $"{string.Join(" and ", unknown.Select(u => $"'{u}'"))} are")} null, "
            + "so that row has no identity to deliver under and could not be told apart from another like it. "
            + $"Give every row a key, or narrow {_keys.Name("source.record.scope")} to the rows that have one.");
    }

    private async Task<IReadOnlyDictionary<string, SourceColumn>> ReadColumnsAsync(SqlConnection connection, SourceObjectName table, string declaredAt, CancellationToken ct)
    {
        IReadOnlyDictionary<string, SourceColumn> columns;
        try
        {
            columns = await IngestionColumns.ReadAsync(connection, table, _flow.Source.Incremental.CommandTimeoutSeconds, ct).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            throw Failure(ex, $"read the columns of {table}");
        }

        if (columns.Count == 0)
        {
            throw new DeliveryException(
                $"Flow '{_flow.Label}': the table {table}, declared under {declaredAt}, was not found on the source database, or the identity this node connects with cannot see it. "
                + "Check the ingestion flow that loads it has run, and that the node's login is granted SELECT on it.");
        }

        return columns;
    }

    private static SourceColumn Column(IReadOnlyDictionary<string, SourceColumn> columns, SourceObjectName table, string name, string what)
        => columns.TryGetValue(name, out var column)
            ? column
            : throw new FlowValidationException($"{what}, which the table {table} does not hold. Columns: {string.Join(", ", columns.Keys.OrderBy(c => c, StringComparer.Ordinal))}.");

    private static SourceColumn? Optional(IReadOnlyDictionary<string, SourceColumn> columns, string? name)
        => name is not null && columns.TryGetValue(name, out var column) ? column : null;

    /// <summary>
    /// The refusal of a provenance column the flow named that the record table does not hold: every record read without it
    /// would reach the ledger with no <paramref name="what"/>, which is what traces a record back to where it came from.
    /// </summary>
    private FlowValidationException MissingProvenance(
        string key, string column, string what, SourceObjectName table, IReadOnlyDictionary<string, SourceColumn> columns, string where)
        => new(
            $"{where}: {_keys.Name(key)} names column '{column}', which the record table {table} does not hold, so every record would reach the ledger without its {what}. "
            + $"Columns: {string.Join(", ", columns.Keys.OrderBy(c => c, StringComparer.Ordinal))}. "
            + $"Name the column the ingestion flow lands it in, or opt out with {_keys.Name(key)}: ~.");

    private async Task<long> CountAsync(SqlConnection connection, IngestionLayout layout, SourceSelection selection, SourceWindow window, CancellationToken ct)
    {
        await using var command = Command(connection, IngestionSql.CandidateCount(layout, selection.Kind, window.LowerUtc is not null));
        BindWindow(command, layout, window);
        BindScope(command, layout);
        var value = await ExecuteScalarAsync(command, "count the candidate records", ct).ConfigureAwait(false);
        return value is long count ? count : 0;
    }

    private async Task<(long InScope, IReadOnlyList<KeyTuple> Missing, IReadOnlyList<KeyTuple> OutOfScope)> ProbeKeysAsync(
        SqlConnection connection, IngestionLayout layout, IReadOnlyList<KeyTuple> keys, CancellationToken ct)
    {
        if (keys.Count == 0)
        {
            return (0, [], []);
        }

        await using var command = Command(connection, IngestionSql.KeyProbe(layout));
        BindScope(command, layout);
        BindKeys(command, keys);
        var found = new HashSet<KeyTuple>();
        var outOfScope = new List<KeyTuple>();
        await using (var reader = await ExecuteReaderAsync(command, "look the named record keys up", ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var key = ReadKey(reader, layout);
                found.Add(key);
                if (reader.GetInt32(layout.Key.Count) == 0)
                {
                    outOfScope.Add(key);
                }
            }
        }

        var missing = keys.Where(k => !found.Contains(k)).ToList();
        return (found.Count - outOfScope.Count, missing, outOfScope);
    }

    /// <summary>One page of candidates, each as its value in the order the read pages in: its primary key, or its record key.</summary>
    private async Task<IReadOnlyList<KeyTuple>> CandidatePageAsync(
        SqlConnection connection, IngestionLayout layout, SourceHeader header, KeyTuple? after, KeyRange? range, int pageSize, CancellationToken ct)
    {
        var bounds = new IngestionSql.KeyBounds(after is not null, false, range?.To is not null);
        await using var command = Command(connection, IngestionSql.CandidatePage(layout, header.Selection.Kind, header.Window.LowerUtc is not null, bounds));
        Bind(command, layout, header, bounds, after, range?.To);
        command.Parameters.Add(new SqlParameter(IngestionSql.PageParameter, SqlDbType.Int) { Value = pageSize });

        var page = new List<KeyTuple>(pageSize);
        await using var reader = await ExecuteReaderAsync(command, "read a page of candidate record keys", ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            page.Add(ReadOrder(reader, layout));
        }

        return page;
    }

    private void Bind(SqlCommand command, IngestionLayout layout, SourceHeader header, IngestionSql.KeyBounds bounds, KeyTuple? after, KeyTuple? to)
    {
        if (header.Selection.Kind == SourceSelectionKind.Keys)
        {
            BindKeys(command, header.Selection.Keys);
        }
        else
        {
            BindWindow(command, layout, header.Window);
        }

        BindScope(command, layout);
        if (bounds.After && after is not null)
        {
            BindOrder(command, layout, IngestionSql.AfterPrefix, after);
        }

        if (bounds.To && to is not null)
        {
            BindOrder(command, layout, IngestionSql.ToPrefix, to);
        }
    }

    private void BindWindow(SqlCommand command, IngestionLayout layout, SourceWindow window)
    {
        var updated = _tables[SourceDatasets.Record][layout.Updated];
        if (window.LowerUtc is { } lower)
        {
            command.Parameters.Add(updated.Parameter(IngestionSql.LowerParameter, lower));
        }

        command.Parameters.Add(updated.Parameter(IngestionSql.UpperParameter, window.UpperUtc));
    }

    private void BindScope(SqlCommand command, IngestionLayout layout)
    {
        var record = _tables[SourceDatasets.Record];
        for (var i = 0; i < layout.Scope.Count; i++)
        {
            var (column, parameter) = layout.Scope[i];
            if (!_values.TryGetValue(parameter, out var value))
            {
                throw new DeliveryException(
                    $"Flow '{_flow.Label}': {_keys.Name("source.record.scope")} binds column '{column}' to parameter '{parameter}', and this run supplies no value for it. Supply it with --set {parameter}=value or the run's values.");
            }

            command.Parameters.Add(record[column].Parameter(IngestionSql.ScopePrefix + i.ToString(CultureInfo.InvariantCulture), value));
        }
    }

    /// <summary>Binds a value in the order the read pages in, one parameter per column of that order.</summary>
    private void BindOrder(SqlCommand command, IngestionLayout layout, string prefix, KeyTuple value)
    {
        var record = _tables[SourceDatasets.Record];
        if (value.Values.Count != layout.Order.Count)
        {
            throw new DeliveryException(
                $"Flow '{_flow.Label}': a page bound has {value.Values.Count} part(s), and the read pages by {string.Join(", ", layout.Order.Select(o => o.Name))}.");
        }

        for (var i = 0; i < layout.Order.Count; i++)
        {
            command.Parameters.Add(record[layout.Order[i].Name].Parameter(prefix + i.ToString(CultureInfo.InvariantCulture), value.Values[i]));
        }
    }

    private static void BindKeys(SqlCommand command, IReadOnlyList<KeyTuple> keys)
        => command.Parameters.Add(new SqlParameter(IngestionSql.KeysParameter, SqlDbType.NVarChar, -1)
        {
            Value = JsonSerializer.Serialize(keys.Select(k => k.Values).ToList()),
        });

    /// <summary>A candidate's value in the order the read pages in.</summary>
    private static KeyTuple ReadOrder(SqlDataReader reader, IngestionLayout layout)
    {
        var parts = new string[layout.Order.Count];
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = SourceRow.Stringify(SourceValues.Normalize(reader.GetValue(i)))
                ?? throw new DeliveryException($"The record table {layout.Record} returned a null in column '{layout.Order[i].Name}', which cannot place a record.");
        }

        return new KeyTuple(parts);
    }

    private static KeyTuple ReadKey(SqlDataReader reader, IngestionLayout layout)
    {
        var parts = new string[layout.Key.Count];
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = SourceRow.Stringify(SourceValues.Normalize(reader.GetValue(i)))
                ?? throw new DeliveryException($"The record table {layout.Record} returned a null in key column '{layout.Key[i].Name}', which cannot name a record.");
        }

        return new KeyTuple(parts);
    }

    private async Task<IReadOnlyList<SourceRecord>> PageRecordsAsync(SqlConnection connection, IngestionLayout layout, IReadOnlyList<KeyTuple> page, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ReadPageAsync(connection, layout, page, ct).ConfigureAwait(false);
            }
            catch (SqlException ex) when (ex.Number == DeadlockError && attempt <= MaxDeadlockRetries)
            {
                _logger.LogWarning(
                    "The source page of {Count} record(s) was chosen as a deadlock victim (attempt {Attempt} of {Attempts}); reading it again.",
                    page.Count, attempt, MaxDeadlockRetries + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<IReadOnlyList<SourceRecord>> ReadPageAsync(SqlConnection connection, IngestionLayout layout, IReadOnlyList<KeyTuple> page, CancellationToken ct)
    {
        var snapshot = _flow.Source.Incremental.Isolation == SourceIsolation.Snapshot;
        SqlTransaction transaction;
        try
        {
            transaction = (SqlTransaction)await connection.BeginTransactionAsync(
                snapshot ? IsolationLevel.Snapshot : IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (snapshot && ex.Number is SnapshotNotAllowed or SnapshotDisabled)
        {
            await PooledIsolation.RestoreAfterFailureAsync(connection, null).ConfigureAwait(false);
            throw SnapshotRefusal(ex);
        }

        // SQL Server keeps a session's isolation level when the pool lends the connection again, so a page read under
        // snapshot hands its connection back at read committed: a session left at snapshot would read the next
        // borrower's statements at snapshot too, and fail every one of them on a database that does not allow it.
        await using (transaction.ConfigureAwait(false))
        {
            try
            {
                var records = await ReadPageRowsAsync(connection, transaction, layout, page, snapshot, ct).ConfigureAwait(false);
                if (snapshot)
                {
                    await PooledIsolation.RestoreReadCommittedAsync(connection, transaction, ct).ConfigureAwait(false);
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return records;
            }
            catch (Exception) when (snapshot && connection.State == ConnectionState.Open)
            {
                await PooledIsolation.RestoreAfterFailureAsync(connection, transaction).ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task<IReadOnlyList<SourceRecord>> ReadPageRowsAsync(
        SqlConnection connection, SqlTransaction transaction, IngestionLayout layout, IReadOnlyList<KeyTuple> page, bool snapshot, CancellationToken ct)
    {
        await using var command = Command(connection, IngestionSql.PageRows(layout));
        command.Transaction = transaction;
        BindKeys(command, page);

        var rows = new Dictionary<KeyTuple, RecordBuilder>();
        var order = new List<KeyTuple>(page.Count);
        SqlDataReader reader;
        try
        {
            reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (snapshot && ex.Number is SnapshotNotAllowed or SnapshotDisabled)
        {
            throw SnapshotRefusal(ex);
        }
        catch (SqlException ex) when (ex.Number != DeadlockError)
        {
            throw Failure(ex, "read a page of record rows");
        }

        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var row = ReadRow(reader);
                var key = KeyOf(layout, row);
                if (rows.TryAdd(key, new RecordBuilder(row)))
                {
                    order.Add(key);
                }
            }

            foreach (var dataset in layout.Datasets)
            {
                if (!await reader.NextResultAsync(ct).ConfigureAwait(false))
                {
                    throw new DeliveryException($"Flow '{_flow.Label}': the source page did not return the rows of child dataset '{dataset.Name}'.");
                }

                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var row = ReadRow(reader);
                    var key = ChildKeyOf(dataset, row);
                    if (rows.TryGetValue(key, out var builder))
                    {
                        builder.Add(dataset, row);
                    }
                }
            }
        }

        return order.Where(rows.ContainsKey).Select(key => Build(layout, key, rows[key])).ToList();
    }

    private SourceRecord Build(IngestionLayout layout, KeyTuple key, RecordBuilder builder)
    {
        var updated = Moment(builder.Row.Get(layout.Updated));
        var datasets = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase);
        var versions = new Dictionary<string, DatasetVersion>(StringComparer.Ordinal);
        foreach (var dataset in layout.Datasets)
        {
            var rows = builder.Rows(dataset.Name);
            datasets[dataset.Name] = rows;
            DateTime? newest = null;
            if (dataset.Updated is { } column)
            {
                foreach (var row in rows)
                {
                    if (Moment(row.Get(column)) is { } moment && (newest is null || moment > newest))
                    {
                        newest = moment;
                    }
                }
            }

            versions[dataset.Name] = new DatasetVersion(rows.Count, newest);
        }

        return new SourceRecord
        {
            Row = builder.Row,
            Scopes = datasets,
            Origin = new SourceOrigin(
                _fileNameColumn is null ? null : builder.Row.GetString(_fileNameColumn),
                _rowNumberColumn is null ? null : RowNumber(builder.Row.Get(_rowNumberColumn)),
                updated,
                _insertedColumn is null ? null : Moment(builder.Row.Get(_insertedColumn))),
            Version = SourceVersion.Of(IngestionFingerprint.Of(updated, versions)),
            SourceKeyJson = key.Json,
            DeletedUtc = layout.Deleted is { } deleted ? Moment(builder.Row.Get(deleted)) : null,
            Hold = builder.Hold,
        };
    }

    private static KeyTuple KeyOf(IngestionLayout layout, SourceRow row)
        => new(layout.Key.Select(k => SourceRow.Stringify(row.Get(k.Name)) ?? string.Empty).ToList());

    private static KeyTuple ChildKeyOf(IngestionDataset dataset, SourceRow row)
        => new(dataset.Join.Select(j => SourceRow.Stringify(row.Get(j.Key)) ?? string.Empty).ToList());

    private static SourceRow ReadRow(SqlDataReader reader)
    {
        var values = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            values[reader.GetName(i)] = SourceValues.Normalize(reader.IsDBNull(i) ? null : reader.GetValue(i));
        }

        return new SourceRow(values);
    }

    /// <summary>
    /// A row's position in its file as the record's origin row: a whole number of any integer type (normalised to
    /// <see cref="long"/>), or of a decimal type without a scale, which the layout allows. Null for a row that holds none.
    /// </summary>
    private static long? RowNumber(object? value) => value switch
    {
        long whole => whole,
        decimal exact when exact == decimal.Truncate(exact) && exact is >= long.MinValue and <= long.MaxValue => (long)exact,
        _ => null,
    };

    private static DateTime? Moment(object? value) => value switch
    {
        DateTime dt => dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime(),
        DateTimeOffset dto => dto.UtcDateTime,
        _ => null,
    };

    private async Task<SqlDataReader> ExecuteReaderAsync(SqlCommand command, string what, CancellationToken ct)
    {
        try
        {
            return await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            throw Failure(ex, what);
        }
    }

    private async Task<object?> ExecuteScalarAsync(SqlCommand command, string what, CancellationToken ct)
    {
        try
        {
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value is DBNull ? null : value;
        }
        catch (SqlException ex)
        {
            throw Failure(ex, what);
        }
    }

    private DeliveryException Failure(SqlException ex, string what)
        => new($"Flow '{_flow.Label}': the source database refused to {what} for {_flow.Source.Record.Object} (SQL error {ex.Number}): {SecretHygiene.RedactedMessage(ex.Message)}", ex);

    private DeliveryException SnapshotRefusal(SqlException ex)
        => new(
            $"Flow '{_flow.Label}': the source database does not allow snapshot isolation, which is how a record and its child rows are read as one moment (SQL error {ex.Number}). "
            + $"Enable it with ALTER DATABASE [{_layout?.Record.Database ?? _flow.Source.Record.Object}] SET ALLOW_SNAPSHOT_ISOLATION ON, "
            + $"or read each result set as it is committed with {_keys.Name("source.incremental.isolation")}: readCommitted.",
            ex);

    /// <summary>One record's rows while a page is read: the record row, and the child rows met so far, per dataset.</summary>
    private sealed class RecordBuilder(SourceRow row)
    {
        private readonly Dictionary<string, List<SourceRow>> _children = new(StringComparer.OrdinalIgnoreCase);

        public SourceRow Row { get; } = row;

        /// <summary>Why this record cannot be delivered as it stands, or null; set when a child dataset overruns its ceiling.</summary>
        public string? Hold { get; private set; }

        public void Add(IngestionDataset dataset, SourceRow child)
        {
            if (!_children.TryGetValue(dataset.Name, out var rows))
            {
                rows = [];
                _children[dataset.Name] = rows;
            }

            if (rows.Count >= dataset.MaxRowsPerRecord)
            {
                Hold ??= $"child dataset '{dataset.Name}' holds more than {dataset.MaxRowsPerRecord.ToString(CultureInfo.InvariantCulture)} rows for this record, "
                    + "which is the maxRowsPerRecord its declaration allows; raise the ceiling deliberately, or split the record in the source";
                return;
            }

            rows.Add(child);
        }

        public IReadOnlyList<SourceRow> Rows(string dataset) => _children.TryGetValue(dataset, out var rows) ? rows : [];
    }
}

/// <summary>Collapses what the SQL Server client returns to the scalars a mapping renders from.</summary>
public static class SourceValues
{
    /// <summary>
    /// The value as the renderer sees it: whole numbers as <see cref="long"/>, exact numbers as <see cref="decimal"/>,
    /// approximate ones as <see cref="double"/>, moments as UTC, bytes as base64 text, and everything else as text.
    /// </summary>
    public static object? Normalize(object? value) => value switch
    {
        null or DBNull => null,
        string s => s,
        bool b => b,
        byte or sbyte or short or ushort or int or uint or long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        ulong ul => ul <= long.MaxValue ? (long)ul : (decimal)ul,
        float f => float.IsNaN(f) ? null : NumberValues.Widen(f),
        double d => double.IsNaN(d) ? null : d,
        decimal m => m,
        DateTime dt => dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime(),
        DateTimeOffset dto => dto.ToUniversalTime(),
        DateOnly day => day,
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        Guid g => g,
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => value.ToString(),
    };
}
