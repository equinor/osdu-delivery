namespace SqlFlow.Delivery.Data;

// The dimensions of dimension flows (osdu/docs/reference/concepts/dimension-tables.md): each dimension with its builds,
// and the views a dimension flow declares with what a build's check of each found.

/// <summary>
/// One dimension of a dimension flow in one partition (osdu/docs/reference/concepts/dimension-tables.md): the declaration
/// its last build read with, the field it asked the index for, and how many members and originals it holds now. Its
/// members, originals, builds and changes are rows of <see cref="DeliveryDimensionMember"/>,
/// <see cref="DeliveryDimensionValue"/>, <see cref="DeliveryDimensionRun"/> and <see cref="DeliveryDimensionChange"/>,
/// keyed by <see cref="DimensionId"/>.
/// </summary>
public sealed class DeliveryDimension
{
    /// <summary>The longest dimension name, as a run's payload and a page name it.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The longest path a dimension reads, which a query writes unquoted.</summary>
    public const int MaxPathLength = 512;

    /// <summary>The longest field the index is asked for: a keyword sub-field inside the service's nested form.</summary>
    public const int MaxAggregateByLength = 1100;

    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The ledger identity of the dimension flow in the partition.</summary>
    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>The kind the dimension reads, wildcards allowed per segment.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The query narrowing the records read, as the last build ran it; null for every record of the kind.</summary>
    public string? Query { get; set; }

    public string Path { get; set; } = string.Empty;

    /// <summary>How the index stores the field: text, keyword, number, boolean or date. Null until a build has read the templates.</summary>
    public string? FieldIndex { get; set; }

    /// <summary>The nested array the field sits in, or null.</summary>
    public string? NestedPath { get; set; }

    /// <summary>The field as the search's aggregateBy names it.</summary>
    public string? AggregateBy { get; set; }

    /// <summary>Whether one record can hold the field more than once, so members' counts need not be of records.</summary>
    public bool Repeats { get; set; }

    /// <summary>The clean steps, as JSON.</summary>
    public string CleanJson { get; set; } = "[]";

    /// <summary>
    /// Where each key's label is read, as a JSON array of paths: the record the key names, then each record a path before
    /// it refers to. Null when keys are their own values.
    /// </summary>
    public string? LabelJson { get; set; }

    /// <summary>
    /// The attributes each key is read with, as a JSON array of <c>{ "name", "steps", "collect" }</c>: the paths each is read
    /// from the record the key names, or the path of the dimension's own records whose values it collects. Null when
    /// the dimension reads none.
    /// </summary>
    public string? AttributesJson { get; set; }

    /// <summary>
    /// The objects of a nested array each key's records hold, as JSON <c>{ "path", "fields": [{ "name", "path", "keep" }] }</c>:
    /// a row each in the dimension's table, its fields kept as rows of <see cref="DeliveryDimensionElement"/>. Null when the
    /// dimension reads none.
    /// </summary>
    public string? ElementsJson { get; set; }

    /// <summary>
    /// What the last build that settled the field read of each collected attribute, as a JSON array: the attribute's name and
    /// path, how the index stores its field, and the value records holding none of its values were given. The texts its
    /// values stand for are rows of <see cref="DeliveryDimensionCollectedText"/>. Null when the dimension collects nothing.
    /// </summary>
    public string? CollectedJson { get; set; }

    /// <summary>The hash of the dimension's declaration as the last build read it.</summary>
    public string DefinitionHash { get; set; } = string.Empty;

    /// <summary>
    /// The dimension's table (<c>dim_&lt;dimension&gt;</c>), as the last build wrote it: the dimension as one table, a row
    /// per key and value it collects and a column per attribute, in the database and schema its flow's target names
    /// (<see cref="TableConnection"/>, <see cref="TableDatabase"/>, <see cref="TableSchema"/>), this schema when it names
    /// none. Builds make the table and widen it as the flow declares more; it is no table of the model, and no migration
    /// touches it. Null until a build has written it.
    /// </summary>
    public string? TableName { get; set; }

    /// <summary>
    /// The connection the table is kept on, as the flow's <c>target.connection</c> names it: a reference, never what it
    /// resolves to. Null for the module's own connection.
    /// </summary>
    public string? TableConnection { get; set; }

    /// <summary>The database on that connection's server the table is kept in; null for the connection's own.</summary>
    public string? TableDatabase { get; set; }

    /// <summary>The schema the table is kept in; null for the module's, <c>osdu</c>.</summary>
    public string? TableSchema { get; set; }

    /// <summary>
    /// The build whose rows the table holds: set when a build's write of the table commits. A build after one whose table
    /// write did not complete, or whose table moved to another target, lays out every key and brings the table to them.
    /// </summary>
    public long? TableRunId { get; set; }

    /// <summary>
    /// The column of the dimension's table that holds each key, as the table has it now: named after the property the
    /// dimension's path ends with (with <c>Key</c> at its end where the value's column has that name) unless its document
    /// names it, and <c>key</c> in a table made before dimensions named their columns, which the next build renames. Null
    /// until the table has been made ready.
    /// </summary>
    public string? KeyColumn { get; set; }

    /// <summary>
    /// The column of the dimension's table that holds each key's value, as the table has it now: named after the property
    /// the dimension's label ends with (or after the dimension, when it reads no label) unless its document names it, and
    /// <c>value</c> in a table made before dimensions named their columns. Null until the table has been made ready.
    /// </summary>
    public string? ValueColumn { get; set; }

    /// <summary>
    /// How many times a write changed the rows the dimension's table holds of the partition: a build's write that added,
    /// rewrote or deleted one, and a rewrite of the table outside a build. A view's check keeps the versions of the tables it
    /// read, so a build whose tables a view reads hold what they held at its last check keeps that check rather than reading
    /// the view again (<see cref="DeliveryDimensionViewCheck.ReadVersion"/>).
    /// </summary>
    public long TableVersion { get; set; }

    /// <summary>The members the dimension holds now.</summary>
    public long Members { get; set; }

    /// <summary>The originals the dimension holds now, those under no member included.</summary>
    public long Originals { get; set; }

    /// <summary>The build that last wrote or read the dimension.</summary>
    public long? LastRunId { get; set; }

    public DateTime? LastBuiltUtc { get; set; }

    /// <summary>
    /// The last build that read the dimension in full and kept the records each key's label and attributes were read
    /// through (<see cref="DeliveryDimensionKeyRecord"/>): what an incremental load reads on from. Null until one has.
    /// </summary>
    public long? LastFullRunId { get; set; }

    /// <summary>When <see cref="LastFullRunId"/> completed.</summary>
    public DateTime? LastFullBuiltUtc { get; set; }

    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// One build of one dimension: the declaration and the field it read with, how it read (aggregations, ranges, scans), what it
/// found and how complete that is, and what it changed.
/// </summary>
public sealed class DeliveryDimensionRun
{
    /// <summary>The longest unique key a load keeps records by: a few property paths.</summary>
    public const int MaxRecordKeyLength = 1600;

    public short PartitionId { get; set; }

    public long DimensionRunId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The ledger identity of the dimension flow in the partition.</summary>
    public Guid FlowId { get; set; }

    /// <summary>The platform run the build ran in.</summary>
    public Guid? RunId { get; set; }

    public string Actor { get; set; } = string.Empty;

    /// <summary>running, completed, failed or cancelled.</summary>
    public string Status { get; set; } = "running";

    /// <summary>
    /// How the run read its dimension: <c>full</c>, every key the index holds, or <c>incremental</c>, the keys of the
    /// records that changed in its window and of the records their labels and attributes were read through.
    /// </summary>
    public string Mode { get; set; } = "full";

    /// <summary>Where an incremental load's window began; null for a full load, and for a window open below.</summary>
    public DateTime? WindowFrom { get; set; }

    /// <summary>
    /// Up to when a completed run has read what changed, its start less the flow's lag: the next incremental load reads on
    /// from the latest of them. Null for a run that has not completed, and for one made before runs kept it.
    /// </summary>
    public DateTime? WindowTo { get; set; }

    /// <summary>The records an incremental load found changed in its window; null for a full load.</summary>
    public long? ChangedRecords { get; set; }

    /// <summary>The keys an incremental load read again; null for a full load.</summary>
    public long? TouchedKeys { get; set; }

    /// <summary>
    /// The unique key a load kept the keys each record holds by (<see cref="DeliveryDimensionRecord"/>): the flow's
    /// <c>incremental.keyColumns</c>, comma-separated. Null for a load that kept none, which no incremental load reads on from.
    /// </summary>
    public string? RecordKey { get; set; }

    public string DefinitionHash { get; set; } = string.Empty;

    /// <summary>The query as the build ran it, its tokens substituted.</summary>
    public string? Query { get; set; }

    public string? AggregateBy { get; set; }

    /// <summary>The kinds the pattern matched and the template each was read against, as JSON.</summary>
    public string? Templates { get; set; }

    /// <summary>The records the query matched; null when that could not be counted.</summary>
    public long? Records { get; set; }

    /// <summary>The records the exact field holds a value for; null when that could not be counted.</summary>
    public long? WithValue { get; set; }

    /// <summary>The records (or objects) whose value is null.</summary>
    public long Nulls { get; set; }

    /// <summary>The records holding only text values longer than the exact field keeps; null when not counted.</summary>
    public long? TooLong { get; set; }

    /// <summary>Values the index holds that are not of the field's type.</summary>
    public long Unreadable { get; set; }

    public long Members { get; set; }

    public long Originals { get; set; }

    /// <summary>Originals under no member: cleaned to nothing, to a clean value too long, dropped by a map, or failed by a step.</summary>
    public long LeftOut { get; set; }

    /// <summary>Originals no query can carry, which their members' filters leave out.</summary>
    public long Unfilterable { get; set; }

    public long MembersAdded { get; set; }

    public long MembersRemoved { get; set; }

    public long MembersRestored { get; set; }

    public long OriginalsAdded { get; set; }

    public long OriginalsRemoved { get; set; }

    /// <summary>Originals now under another member than before, or none where they had one.</summary>
    public long OriginalsMoved { get; set; }

    public long OriginalsRestored { get; set; }

    public int Aggregations { get; set; }

    /// <summary>Ranges an aggregation answered whole.</summary>
    public int Slices { get; set; }

    public int Splits { get; set; }

    public int ScannedSlices { get; set; }

    public int ScanPages { get; set; }

    public long ScannedUnits { get; set; }

    /// <summary>Counts of members' records the build asked the search for.</summary>
    public int CountQueries { get; set; }

    /// <summary>Keys whose label the build read from the record they name.</summary>
    public long Labelled { get; set; }

    /// <summary>Keys the build could not label: a key that names no record, a record the search does not hold, or one without the path.</summary>
    public long Unlabelled { get; set; }

    /// <summary>Searches the build asked to read labels.</summary>
    public int LabelQueries { get; set; }

    /// <summary>What the build had to say, as a JSON array of lines.</summary>
    public string? Notes { get; set; }

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// A view a dimension flow declares (osdu/docs/reference/flow/dimension-views.md), as its builds last wrote it: what the
/// module made in its schema, so a build writes only views it recorded and drops only those its flow no longer declares. A
/// view spans every partition its tables hold, so it belongs to no ledger partition; its checks do.
/// </summary>
public sealed class DeliveryDimensionView
{
    /// <summary>The longest name a view takes, as the document gives it.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The longest description of a view.</summary>
    public const int MaxDescriptionLength = 1000;

    public int ViewId { get; set; }

    /// <summary>The view's name as the document gives it, unique ignoring case among the views of the database.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The view's name in the module's schema (<c>v_dim_&lt;name&gt;</c>).</summary>
    public string ViewName { get; set; } = string.Empty;

    /// <summary>The dimension flow that declares the view, by its name.</summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>
    /// The connection the view is kept on, as its flow's <c>target.connection</c> names it, with the tables it reads: a
    /// reference, never what it resolves to. Null for the module's own connection.
    /// </summary>
    public string? TableConnection { get; set; }

    /// <summary>The database on that connection's server the view is kept in; null for the connection's own.</summary>
    public string? TableDatabase { get; set; }

    /// <summary>The schema the view is kept in; null for the module's, <c>osdu</c>.</summary>
    public string? TableSchema { get; set; }

    /// <summary>The ledger of the build that last wrote the view, which a removal of the view is recorded under.</summary>
    public Guid LedgerId { get; set; }

    public string? Description { get; set; }

    /// <summary>The view as its document declares it: its from dimension, its joins and its columns, as JSON.</summary>
    public string DeclarationJson { get; set; } = string.Empty;

    /// <summary>The statement the view was last written with.</summary>
    public string Sql { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of <see cref="Sql"/>, hex: a build that would write the same statement writes nothing. Null when the view is
    /// to be written again: a build renamed a column of a table it reads and dropped it.
    /// </summary>
    public string? SqlHash { get; set; }

    /// <summary>The dimension tables the view reads, its from table first, as a JSON list of names.</summary>
    public string TablesJson { get; set; } = string.Empty;

    /// <summary>The view's columns with their types, expressions and descriptions, as JSON.</summary>
    public string ColumnsJson { get; set; } = string.Empty;

    /// <summary>Why the view is not in the database now, when it is not: what dropped it, and what writes it again.</summary>
    public string? Note { get; set; }

    /// <summary>The platform run whose build last wrote the view.</summary>
    public Guid? WrittenRunId { get; set; }

    /// <summary>Who started that run.</summary>
    public string WrittenBy { get; set; } = string.Empty;

    public DateTime WrittenUtc { get; set; }

    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// What a build's check of a view found in one partition (osdu/docs/reference/flow/dimension-views.md): the rows, what each
/// join found, and the values each conversion could not read, with examples; or why the view could not be read. A view
/// keeps its newest checks of each partition.
/// </summary>
public sealed class DeliveryDimensionViewCheck
{
    /// <summary>The most checks a view keeps of each partition.</summary>
    public const int Kept = 50;

    public short PartitionId { get; set; }

    /// <summary>The row's own number.</summary>
    public long CheckId { get; set; }

    /// <summary>The view checked (<see cref="DeliveryDimensionView.ViewId"/>).</summary>
    public int ViewId { get; set; }

    /// <summary>The platform run whose build checked it.</summary>
    public Guid? RunId { get; set; }

    /// <summary><c>passed</c>, or <c>failed</c> when the view could not be read.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The rows the view holds of the partition.</summary>
    public long Rows { get; set; }

    /// <summary>What each join found: the rows that found their row, those whose value found none, and some of those values, as JSON.</summary>
    public string? JoinsJson { get; set; }

    /// <summary>The values each column holds, and the values each converted column could not read with examples, as JSON.</summary>
    public string? ColumnsJson { get; set; }

    /// <summary>Why the view could not be read, redacted.</summary>
    public string? Error { get; set; }

    public DateTime CheckedUtc { get; set; }

    /// <summary>How long the check read the view.</summary>
    public int DurationMs { get; set; }

    /// <summary>
    /// What the check read, as one text: SHA-256, hex, of the view's statement hash and the version of every dimension table
    /// it reads, and every table a view it joins reads, in the partition (<see cref="DeliveryDimension.TableVersion"/>). Two
    /// checks of the same text read the same rows. Null for a check made before checks kept it.
    /// </summary>
    public string? ReadVersion { get; set; }

    /// <summary>
    /// The check whose findings this one keeps, when nothing the view reads had changed since it (<see cref="ReadVersion"/>
    /// the same) and the view was not read again; null for a check that read the view.
    /// </summary>
    public long? ReusedCheckId { get; set; }
}
