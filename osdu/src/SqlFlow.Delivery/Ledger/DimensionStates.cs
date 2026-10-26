using System.Globalization;

namespace SqlFlow.Delivery.Ledger;

/// <summary>What a dimension build comes to.</summary>
public static class DimensionRunStatus
{
    public const string Running = "running";

    /// <summary>Every value read, cleaned and written.</summary>
    public const string Completed = "completed";

    /// <summary>The build stopped before it wrote; the dimension holds what the build before it wrote.</summary>
    public const string Failed = "failed";

    public const string Cancelled = "cancelled";
}

/// <summary>
/// How a dimension build reads its dimension (osdu/docs/reference/flow/dimension-incremental.md, Full and incremental loads).
/// </summary>
public static class DimensionRunModes
{
    /// <summary>
    /// Every key the index holds, read whole: what adds and changes keys, and the only read that removes a key no record
    /// holds any more and lowers a count.
    /// </summary>
    public const string Full = "full";

    /// <summary>
    /// The keys of the records that changed in a window, and of the records their labels and attributes were read
    /// through, each read again whole: what adds and changes keys between full loads, and never removes one.
    /// </summary>
    public const string Incremental = "incremental";
}

/// <summary>Why an original belongs to no member, as its row keeps the reason.</summary>
public static class DimensionLeftOut
{
    /// <summary>Cleaning left nothing.</summary>
    public const string Empty = "empty";

    /// <summary>The clean value is longer than a member's may be.</summary>
    public const string TooLong = "tooLong";

    /// <summary>A map step left it out.</summary>
    public const string Dropped = "dropped";

    /// <summary>A step could not run on it.</summary>
    public const string Failed = "failed";
}

/// <summary>What a build changed of one original.</summary>
public static class DimensionChangeKinds
{
    /// <summary>It arrived, in a build after the dimension's first.</summary>
    public const string Added = "added";

    /// <summary>The build no longer found it.</summary>
    public const string Removed = "removed";

    /// <summary>It is under another member than before, or under none where it had one, or under one where it had none.</summary>
    public const string Moved = "moved";

    /// <summary>A build found it again after one had not.</summary>
    public const string Restored = "restored";
}

/// <summary>A dimension as a build registers it: the flow's ledger in a partition, and the declaration the build reads with.</summary>
public sealed record DimensionDeclaration
{
    /// <summary>The ledger identity of the dimension flow in the partition, which the build registered.</summary>
    public required Guid FlowId { get; init; }

    public required string FlowName { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string Kind { get; init; }

    /// <summary>The query as the build runs it, its tokens substituted; null for every record of the kind.</summary>
    public string? Query { get; init; }

    public required string Path { get; init; }

    /// <summary>The clean steps, as JSON.</summary>
    public required string CleanJson { get; init; }

    /// <summary>Where each key's label is read, as a JSON array of paths; null when keys are their own values.</summary>
    public string? LabelJson { get; init; }

    /// <summary>The attributes each key is read with, as a JSON array of <c>{ "name", "steps", "collect" }</c>; null when none.</summary>
    public string? AttributesJson { get; init; }

    /// <summary>The objects of a nested array each key's records hold, as JSON; null when the dimension reads none.</summary>
    public string? ElementsJson { get; init; }

    public required string DefinitionHash { get; init; }
}

/// <summary>How the index stores a dimension's field, as a build settled it from the templates or the record's own mapping.</summary>
/// <param name="Index">text, keyword, number, boolean or date.</param>
/// <param name="NestedPath">The nested array the field sits in, or null.</param>
/// <param name="AggregateBy">The field as the search's aggregateBy names it.</param>
/// <param name="Repeats">Whether one record can hold the field more than once.</param>
public sealed record DimensionFieldState(string Index, string? NestedPath, string AggregateBy, bool Repeats);

/// <summary>A dimension as the ledger holds it, with its current counts and its last build.</summary>
public sealed record DimensionState
{
    public required int DimensionId { get; init; }

    public required Guid FlowId { get; init; }

    public required string FlowName { get; init; }

    /// <summary>The data-partition-id the dimension is kept in.</summary>
    public string? Partition { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string Kind { get; init; }

    public string? Query { get; init; }

    public required string Path { get; init; }

    /// <summary>How the index stores the field; null until a build has settled it.</summary>
    public DimensionFieldState? Field { get; init; }

    public required string CleanJson { get; init; }

    /// <summary>Where each key's label is read, as a JSON array of paths; null when keys are their own values.</summary>
    public string? LabelJson { get; init; }

    /// <summary>The attributes each key is read with, as a JSON array of <c>{ "name", "steps", "collect" }</c>; null when none.</summary>
    public string? AttributesJson { get; init; }

    /// <summary>The objects of a nested array each key's records hold, as JSON; null when the dimension reads none.</summary>
    public string? ElementsJson { get; init; }

    /// <summary>What the last build read of each collected attribute, as JSON (<see cref="DimensionCollectedState"/>); null when none.</summary>
    public string? CollectedJson { get; init; }

    public required string DefinitionHash { get; init; }

    /// <summary>The dimension's table in the module's schema (<see cref="DimensionTables"/>); null until a build has written it.</summary>
    public string? TableName { get; init; }

    /// <summary>
    /// The column of the dimension's table that holds each key, as the table has it now
    /// (<see cref="Model.DimensionColumnNames"/>); null until the table has been made ready.
    /// </summary>
    public string? KeyColumn { get; init; }

    /// <summary>The column of the dimension's table that holds each key's value, as the table has it now; null until the table has been made ready.</summary>
    public string? ValueColumn { get; init; }

    public long Members { get; init; }

    public long Originals { get; init; }

    /// <summary>The build that last wrote or read the dimension; null when none has.</summary>
    public long? LastRunId { get; init; }

    public DateTime? LastBuiltUtc { get; init; }

    /// <summary>The last build that read the dimension in full and kept what its keys were read through; null when none has.</summary>
    public long? LastFullRunId { get; init; }

    /// <summary>When <see cref="LastFullRunId"/> completed.</summary>
    public DateTime? LastFullBuiltUtc { get; init; }

    public DateTime CreatedUtc { get; init; }
}

/// <summary>How a build read its dimension's values, and how complete that is.</summary>
public sealed record DimensionReadCounts
{
    public static DimensionReadCounts None { get; } = new();

    public long? Records { get; init; }

    public long? WithValue { get; init; }

    public long Nulls { get; init; }

    public long? TooLong { get; init; }

    public long Unreadable { get; init; }

    public int Aggregations { get; init; }

    public int Slices { get; init; }

    public int Splits { get; init; }

    public int ScannedSlices { get; init; }

    public int ScanPages { get; init; }

    public long ScannedUnits { get; init; }

    public int CountQueries { get; init; }

    /// <summary>Keys whose label the build read from the record they name.</summary>
    public long Labelled { get; init; }

    /// <summary>Keys the build could not label: a key naming no record, a record the search does not hold, or one without the path.</summary>
    public long Unlabelled { get; init; }

    /// <summary>Searches the build asked to read labels and attributes.</summary>
    public int LabelQueries { get; init; }

    /// <summary>The kinds the pattern matched and the template each was read against, as JSON; null when none was needed.</summary>
    public string? Templates { get; init; }

    /// <summary>What the build had to say, a line each.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>How the run read its dimension (<see cref="DimensionRunModes"/>).</summary>
    public string Mode { get; init; } = DimensionRunModes.Full;

    /// <summary>Where an incremental load's window began; null for a full load, and for a window open below.</summary>
    public DateTime? WindowFrom { get; init; }

    /// <summary>Up to when the run read what changed, its start less the flow's lag; null until it is known.</summary>
    public DateTime? WindowTo { get; init; }

    /// <summary>The records an incremental load found changed in its window; null for a full load.</summary>
    public long? ChangedRecords { get; init; }

    /// <summary>The keys an incremental load read again; null for a full load.</summary>
    public long? TouchedKeys { get; init; }

    /// <summary>
    /// The unique key the run kept the keys each record holds by (the flow's <c>incremental.keyColumns</c>, comma-separated);
    /// null for a run that kept none.
    /// </summary>
    public string? RecordKey { get; init; }
}

/// <summary>One build of one dimension as the ledger holds it.</summary>
public sealed record DimensionRunState
{
    public required long DimensionRunId { get; init; }

    public required int DimensionId { get; init; }

    public required Guid FlowId { get; init; }

    public Guid? RunId { get; init; }

    public required string Actor { get; init; }

    public required string Status { get; init; }

    public required string DefinitionHash { get; init; }

    public string? Query { get; init; }

    public string? AggregateBy { get; init; }

    public DimensionReadCounts Read { get; init; } = DimensionReadCounts.None;

    public long Members { get; init; }

    public long Originals { get; init; }

    public long LeftOut { get; init; }

    public long Unfilterable { get; init; }

    public DimensionChangeCounts Changes { get; init; } = DimensionChangeCounts.None;

    public required DateTime StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    public string? Error { get; init; }
}

/// <summary>What one build changed of a dimension.</summary>
public sealed record DimensionChangeCounts(
    long MembersAdded, long MembersRemoved, long MembersRestored, long OriginalsAdded, long OriginalsRemoved, long OriginalsMoved, long OriginalsRestored)
{
    public static DimensionChangeCounts None { get; } = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>True when the build changed nothing a filter or a page shows but counts.</summary>
    public bool IsNone => this == None;
}

/// <summary>
/// One original (a key) as a build found it: its member's clean value (the key's value) or why it has none, its count, the
/// label read for it and the record it was read from, the search filter finding its records, and its attributes.
/// </summary>
public sealed record DimensionOriginalWrite(
    string Original, string? CleanValue, string? LeftOut, string? Note, long Count, bool Filterable,
    string? Label = null, string? LabelFrom = null, string? Filter = null, IReadOnlyList<DimensionAttributeState>? Attributes = null);

/// <summary>
/// One value of one attribute of a key: its name, the value, where it was read (the id of the record it was read from, or
/// for a collected attribute the text the key's records hold), and for a collected attribute how many of the key's records hold it.
/// </summary>
public sealed record DimensionAttributeState(string Name, string Value, string? From, long? Records = null);

/// <summary>
/// What a build read of one collected attribute: its name and path, how the index stores the path, and the value records
/// holding none of its values were given (null when the dimension gives them none). The texts its values stand for are kept
/// apart (<see cref="DimensionCollectedText"/>), however many there are.
/// </summary>
public sealed record DimensionCollectedState(string Name, string Path, DimensionFieldState Field, string? Missing);

/// <summary>
/// One text a dimension's records hold at a collected attribute's path, exactly as the index holds it: the value it is shown
/// as (several texts shown alike are one value), and the records holding it. A search picking a value asks for its texts.
/// </summary>
public sealed record DimensionCollectedText(string Name, string Text, string Value, long Records);

/// <summary>
/// A value one attribute holds among a member's keys, as a page of members shows it: the value, and how many of the member's
/// keys hold it.
/// </summary>
public sealed record DimensionMemberAttributeValue(string Name, string Value, int Keys);

/// <summary>The values each attribute holds among one member's keys.</summary>
public sealed record DimensionMemberAttributes(long MemberId, IReadOnlyList<DimensionMemberAttributeValue> Attributes);

/// <summary>
/// A value an attribute holds among a dimension's keys: the value, the keys holding it, and their records (summed): every
/// record of each key for an attribute read through the record a key names, the records holding the value for a collected one.
/// </summary>
public sealed record DimensionAttributeValueState(string Value, int Keys, long Records);

/// <summary>How much of one attribute a build read: the keys a build finds now holding a value read for it, and how many values those are.</summary>
public sealed record DimensionAttributeCoverage(string Name, int Keys, int Values);

/// <summary>
/// Which values of one attribute a list reads: those among the keys a build finds now that hold every match of
/// <paramref name="Attributes"/> but one of the attribute itself, and belong to one of <paramref name="MemberIds"/> when
/// given. So each list of a cascade lists what the other picks leave, its own pick aside.
/// </summary>
/// <param name="Name">The attribute, as the dimension declares it.</param>
/// <param name="Search">Text the value contains, ignoring case; null reads every value.</param>
/// <param name="Limit">The most values the list holds.</param>
/// <param name="Attributes">The picks of the dimension's attributes; a match of <paramref name="Name"/> itself is passed over.</param>
/// <param name="MemberIds">Only the keys of these values of the dimension; null or empty reads the keys of every value.</param>
public sealed record DimensionAttributeValueQuery(
    string Name, string? Search, int Limit, IReadOnlyList<DimensionAttributeMatch>? Attributes = null, IReadOnlyCollection<long>? MemberIds = null);

/// <summary>
/// Keys an attribute has to hold: one of <paramref name="Values"/> under <paramref name="Name"/>, compared exactly. Several
/// matches are all to hold, by the same key.
/// </summary>
public sealed record DimensionAttributeMatch(string Name, IReadOnlyList<string> Values);

/// <summary>
/// A type of a cache flow that captures a dimension (<c>dimension: &lt;name&gt;, dimensionFlow: &lt;flow&gt;</c>) in the
/// dimension's partition, as the last repository sync recorded it.
/// </summary>
public sealed record DimensionCaptureState(string CacheFlow, string Type, string Partition);

/// <summary>
/// What removing a dimension took out of the ledger: the dimension, the rows kept of it in each table, and its own table
/// when no other partition keeps the dimension.
/// </summary>
public sealed record DimensionRemoved(
    int DimensionId, string Name, string FlowName, string? Partition, long Values, long Keys, long Builds, long Changes, long Attributes, long Texts,
    string? TableDropped = null)
{
    /// <summary>One line saying what went, for the audit trail and a terminal.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"Removed dimension {Name} of {FlowName}{(Partition is null ? string.Empty : $" in {Partition}")}: {Values} value(s), {Keys} key(s), {Attributes} attribute value(s), {Texts} collected text(s), {Changes} change(s) and {Builds} build(s){(TableDropped is null ? string.Empty : $"; dropped the table {DimensionTables.Shown(TableDropped)}")}.");
}

/// <summary>
/// Which rows of a dimension's table (<see cref="DimensionTables"/>) a page reads, and in what order.
/// </summary>
/// <param name="Search">Text the key, the value or any attribute contains, ignoring case; null reads every row.</param>
/// <param name="Attributes">Only the rows whose attribute holds one of the values, every attribute named; null or empty reads every row.</param>
/// <param name="OrderBy">
/// The column the rows are ordered by, by its name in the table (the key's, the value's, <c>records</c>, <c>id</c> or an
/// attribute's), or by the word <c>value</c> (the default) or <c>key</c>, which name those two columns whatever the
/// dimension calls them.
/// </param>
/// <param name="Descending">Largest first.</param>
/// <param name="Offset">The rows before the page.</param>
/// <param name="Limit">The most rows the page holds.</param>
public sealed record DimensionTableQuery(
    string? Search = null, IReadOnlyList<DimensionAttributeMatch>? Attributes = null, string? OrderBy = null, bool Descending = false, int Offset = 0, int Limit = 100);

/// <summary>
/// One row of a dimension's table: the row's number (what a table of facts joins on, the same for as long as the dimension
/// holds the row), the key's number (the same in every row of the key), the key, its value, its attributes in the order
/// of the table's attribute columns (null where the key has none), the records of the row, and the search filter finding
/// the key's records.
/// </summary>
public sealed record DimensionTableRow(long Id, long KeyId, string Key, string Value, IReadOnlyList<string?> Attributes, long Records, string? Filter);

/// <summary>
/// A dimension's table as a reader finds it: its name in the module's schema, the names of the columns that hold its key
/// and its value, and its attribute columns in order.
/// </summary>
public sealed record DimensionTableShape(string Table, string KeyColumn, string ValueColumn, IReadOnlyList<string> Attributes);

/// <summary>
/// A page of a dimension's table: the table it was read from, the names of the columns that hold its key and its value,
/// the attribute columns, the rows, whether more follow, and on a first page how many rows the query matches in all.
/// </summary>
public sealed record DimensionTablePage(
    string Table, string KeyColumn, string ValueColumn, IReadOnlyList<string> Attributes, IReadOnlyList<DimensionTableRow> Rows, bool More, long? Total);

/// <summary>A dimension whose table the database does not hold: no build has written it yet, or it was dropped by hand.</summary>
public sealed class DimensionTableMissingException : DeliveryException
{
    public DimensionTableMissingException(string message)
        : base(message)
    {
    }

    public DimensionTableMissingException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// A dimension's table whose key or value column a build renamed while the table was being read: the read named the
/// column as it was. Reading it again finds it under its name.
/// </summary>
public sealed class DimensionTableRenamedException : DeliveryException
{
    public DimensionTableRenamedException(string message)
        : base(message)
    {
    }

    public DimensionTableRenamedException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>One member as a build made it: its clean value, its count, and its filter.</summary>
public sealed record DimensionMemberWrite(string Value, long Records, bool RecordsExact, int Originals, int Unfilterable, string? Filter, int FilterParts);

/// <summary>
/// How a completed build's write merges what it staged (<see cref="DimensionStage"/>): whether it is partial, the field it
/// settled, what it read of its collected attributes, the dimension's table, whether it keeps the keys records hold, its counts,
/// and when it completed.
/// </summary>
public sealed record DimensionStagedWrite
{
    /// <summary>
    /// True for an incremental load: the keys staged as cleaned are the keys it read again, and the values laid out those they
    /// were and are under, each from all its keys; every other key and value stays as it is. A value left with no key is
    /// removed; no key is but one staged as gone. A text collected that the dimension does not hold yet is added, and the
    /// others stay. False for a full load, which holds every key and value: what it does not hold is removed.
    /// </summary>
    public bool Partial { get; init; }

    /// <summary>
    /// How the index stores the field, as the build settled it; null when it could not (no record of the kind for its templates
    /// to be read by), which leaves the field the dimension had.
    /// </summary>
    public required DimensionFieldState? Field { get; init; }

    /// <summary>What the build read of each collected attribute, as JSON (<see cref="DimensionCollectedState"/>); null when the dimension holds none.</summary>
    public string? CollectedJson { get; init; }

    /// <summary>
    /// The dimension's table, from the declaration the build read with: made when it is missing, given the column of an
    /// attribute it does not have, and brought to the rows the build found, in the write's transaction. Null writes no table.
    /// </summary>
    public DimensionTableSpec? Table { get; init; }

    /// <summary>
    /// Whether the write keeps the keys records held, by each record's unique key: a full load's replace every row the
    /// dimension kept, an incremental load's those of the records it read. False for a full load of a flow that loads in full,
    /// which leaves the dimension none.
    /// </summary>
    public bool KeepsRecords { get; init; }

    public required DimensionReadCounts Read { get; init; }

    public required DateTime CompletedUtc { get; init; }
}

/// <summary>
/// Everything one completed build writes of its dimension, held in memory: what a build whose rows are few hands the ledger
/// whole. The ledger stages the rows and merges them as any build's staging is merged.
/// </summary>
public sealed record DimensionWrite
{
    public required long DimensionRunId { get; init; }

    public required int DimensionId { get; init; }

    public required Guid FlowId { get; init; }

    /// <summary>
    /// True for an incremental load: <see cref="Originals"/> are the keys it read again and <see cref="Members"/> the values
    /// they were and are under, each laid out whole, and every other key and value stays as it is. A value left with no key
    /// is removed; no key is. A text collected that the dimension does not hold yet is added, and the others stay. False
    /// for a full load, which holds every key and value: what it does not hold is removed.
    /// </summary>
    public bool Partial { get; init; }

    /// <summary>
    /// The records each key's label and attributes were read through, found or not, by key: what a later incremental load
    /// finds the keys to read again by. The keys the write holds are given these and no others.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> KeyRecords { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>
    /// The keys records held when the load read them, by each record's unique key: every record a full load read, which
    /// replace those the dimension kept, or the records an incremental load found changed, whose rows alone are replaced.
    /// Null keeps none: a full load of a flow that loads in full leaves the dimension none.
    /// </summary>
    public DimensionRecordsWrite? Records { get; init; }

    /// <summary>
    /// For an incremental load, the keys it read again and found held by no record: they are removed, as a full load
    /// removes a key it no longer finds. Empty for a full load, which removes every key it does not hold.
    /// </summary>
    public IReadOnlyList<string> Removed { get; init; } = [];

    /// <summary>
    /// How the index stores the field, as the build settled it; null when it could not (no record of the kind for its templates
    /// to be read by), which leaves the field the dimension had.
    /// </summary>
    public required DimensionFieldState? Field { get; init; }

    /// <summary>What the build read of each collected attribute, as JSON (<see cref="DimensionCollectedState"/>); null when the dimension holds none.</summary>
    public string? CollectedJson { get; init; }

    /// <summary>Every text the build collected, with the value it is shown as; empty when the dimension collects nothing.</summary>
    public IReadOnlyList<DimensionCollectedText> CollectedTexts { get; init; } = [];

    /// <summary>
    /// The dimension's table, from the declaration the build read with: made when it is missing, given the column of an
    /// attribute it does not have, and brought to the rows the build found, in the write's transaction. Null writes no table.
    /// </summary>
    public DimensionTableSpec? Table { get; init; }

    public required IReadOnlyList<DimensionOriginalWrite> Originals { get; init; }

    public required IReadOnlyList<DimensionMemberWrite> Members { get; init; }

    public required DimensionReadCounts Read { get; init; }

    public required DateTime CompletedUtc { get; init; }

    /// <summary>How the write merges its rows once staged.</summary>
    public DimensionStagedWrite Staged() => new()
    {
        Partial = Partial,
        Field = Field,
        CollectedJson = CollectedJson,
        Table = Table,
        KeepsRecords = Records is not null,
        Read = Read,
        CompletedUtc = CompletedUtc,
    };
}

/// <summary>
/// The keys records held when a load read them (<see cref="DimensionWrite.Records"/>): the records read, each by the
/// SHA-256 of its unique key, and each key a record held, a pair each. A record that holds no key is read with no pair.
/// </summary>
public sealed record DimensionRecordsWrite(IReadOnlyCollection<byte[]> Read, IReadOnlyList<(byte[] Record, string Original)> Held);

/// <summary>A member as a page or a filter reads it.</summary>
public sealed record DimensionMemberState
{
    public required long MemberId { get; init; }

    public required int DimensionId { get; init; }

    public required string Value { get; init; }

    public long Records { get; init; }

    public bool RecordsExact { get; init; }

    public int Originals { get; init; }

    public int Unfilterable { get; init; }

    public string? Filter { get; init; }

    public int FilterParts { get; init; }

    public long FirstSeenRunId { get; init; }

    public DateTime FirstSeenUtc { get; init; }

    public long? RemovedRunId { get; init; }

    public DateTime? RemovedUtc { get; init; }

    /// <summary>
    /// The values each attribute holds among its keys, the most keys first and at most a few per attribute, as a page of
    /// members reads them; empty elsewhere.
    /// </summary>
    public IReadOnlyList<DimensionMemberAttributeValue> Attributes { get; init; } = [];
}

/// <summary>An original as a page or a filter reads it.</summary>
public sealed record DimensionValueState
{
    public required long ValueId { get; init; }

    public required int DimensionId { get; init; }

    public required string Original { get; init; }

    public long? MemberId { get; init; }

    /// <summary>The clean value of its member, when it has one.</summary>
    public string? MemberValue { get; init; }

    public string? LeftOut { get; init; }

    public string? Note { get; init; }

    /// <summary>The label read for it from the record it names, before cleaning; null when none was read.</summary>
    public string? Label { get; init; }

    /// <summary>The id of the record the label was read from.</summary>
    public string? LabelFrom { get; init; }

    /// <summary>The search filter finding the records holding it; null when no query can carry it.</summary>
    public string? Filter { get; init; }

    public long Count { get; init; }

    public bool Filterable { get; init; }

    public long FirstSeenRunId { get; init; }

    public DateTime FirstSeenUtc { get; init; }

    public long MemberSinceRunId { get; init; }

    public long? RemovedRunId { get; init; }

    public DateTime? RemovedUtc { get; init; }

    /// <summary>Its attributes, as its last build read them, in the order the dimension declares them.</summary>
    public IReadOnlyList<DimensionAttributeState> Attributes { get; init; } = [];
}

/// <summary>A change a build made to one original, as the change log reads it.</summary>
public sealed record DimensionChangeState(
    long ChangeId, long DimensionRunId, long ValueId, string Original, string Change, long? FromMemberId, string? FromValue, long? ToMemberId, string? ToValue,
    DateTime ChangedUtc);

/// <summary>Which changes a page of a dimension's change log reads, newest first.</summary>
/// <param name="DimensionRunId">Only the changes of this build.</param>
/// <param name="ValueId">Only the changes of this original.</param>
/// <param name="MemberId">Only the changes that took an original from this member or brought one to it.</param>
/// <param name="Change">Only changes of this kind (<see cref="DimensionChangeKinds"/>).</param>
/// <param name="Before">The change the page before ended at; null starts at the newest.</param>
/// <param name="Limit">The most changes the page holds.</param>
public sealed record DimensionChangeQuery(long? DimensionRunId, long? ValueId, long? MemberId, string? Change, long? Before, int Limit);

/// <summary>The order a page of members reads in.</summary>
public enum DimensionMemberOrder
{
    /// <summary>By clean value, in code point order: the order the index keeps the values in.</summary>
    Value,

    /// <summary>The members most records hold first, then by clean value.</summary>
    Records,
}

/// <summary>Where a page of members ended: its last member's clean value and records, which the next page starts after.</summary>
public sealed record DimensionMemberCursor(string Value, long Records);

/// <summary>Which members a page reads.</summary>
/// <param name="Search">Text the clean value or one of its originals contains, ignoring case; null reads every member.</param>
/// <param name="IncludeRemoved">Read the members no build finds any more as well.</param>
/// <param name="After">Where the page before ended, in the page's order; null starts at the first.</param>
/// <param name="Limit">The most members the page holds.</param>
/// <param name="Order">The order the page reads in.</param>
/// <param name="Attributes">Only the members holding a key that holds every match; null or empty reads every member.</param>
public sealed record DimensionMemberQuery(
    string? Search, bool IncludeRemoved, DimensionMemberCursor? After, int Limit, DimensionMemberOrder Order = DimensionMemberOrder.Value,
    IReadOnlyList<DimensionAttributeMatch>? Attributes = null);

/// <summary>The order a page of originals reads in.</summary>
public enum DimensionValueOrder
{
    /// <summary>In the order the builds found them.</summary>
    Arrival,

    /// <summary>The originals most records hold first, then in the order the builds found them.</summary>
    Count,
}

/// <summary>Where a page of originals ended: its last original's id and count, which the next page starts after.</summary>
public sealed record DimensionValueCursor(long ValueId, long Count);

/// <summary>Which originals a page reads.</summary>
/// <param name="Search">Text the original or its label contains, ignoring case; null reads every original.</param>
/// <param name="MemberId">Only the originals of this member.</param>
/// <param name="LeftOutOnly">Only the originals under no member.</param>
/// <param name="IncludeRemoved">Read the originals no build finds any more as well.</param>
/// <param name="After">Where the page before ended, in the page's order; null starts at the first.</param>
/// <param name="Limit">The most originals the page holds.</param>
/// <param name="Order">The order the page reads in.</param>
/// <param name="Attributes">Only the originals holding every match; null or empty reads every original.</param>
/// <param name="MemberIds">Only the originals of these members; null reads the originals of every member (and of none).</param>
public sealed record DimensionValueQuery(
    string? Search, long? MemberId, bool LeftOutOnly, bool IncludeRemoved, DimensionValueCursor? After, int Limit,
    DimensionValueOrder Order = DimensionValueOrder.Arrival, IReadOnlyList<DimensionAttributeMatch>? Attributes = null,
    IReadOnlyCollection<long>? MemberIds = null);
