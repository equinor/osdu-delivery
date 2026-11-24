namespace SqlFlow.Delivery.Data;

// What a dimension holds (osdu/docs/reference/concepts/dimension-tables.md): its members and originals, the attributes
// and elements of each key, the texts it collects, the records each key was read through and held by, and every change
// a build made.

/// <summary>
/// One member of a dimension: a clean value, with how many records hold any of its originals, and the search filter that
/// finds them. A member a build no longer finds is marked removed and kept, so its id stays its own if it comes back. A build
/// writes a row only when something of it changed, so every member the dimension holds now was seen by its last build.
/// </summary>
public sealed class DeliveryDimensionMember
{
    public short PartitionId { get; set; }

    public long MemberId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The clean value, trimmed, compared exactly.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>The records holding any of its originals (or the sum of its originals' counts when <see cref="RecordsExact"/> is false).</summary>
    public long Records { get; set; }

    public bool RecordsExact { get; set; }

    /// <summary>The originals under it.</summary>
    public int Originals { get; set; }

    /// <summary>Its originals no query can carry, which the filter leaves out.</summary>
    public int Unfilterable { get; set; }

    /// <summary>The search filter finding every record holding a filterable original, when it fits one query; null otherwise.</summary>
    public string? Filter { get; set; }

    /// <summary>How many queries the filter takes: 1 when <see cref="Filter"/> holds it, more when its originals pass one query's clauses, 0 with none filterable.</summary>
    public int FilterParts { get; set; }

    /// <summary>The build that first found it. One the dimension holds now was found by the dimension's last build.</summary>
    public long FirstSeenRunId { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    /// <summary>The build that no longer found it, or null while it is there.</summary>
    public long? RemovedRunId { get; set; }

    public DateTime? RemovedUtc { get; set; }
}

/// <summary>
/// One original of a dimension: a value exactly as the index holds it, the member it belongs to or why it belongs to none, and
/// how many records (or objects of a nested array) hold it.
/// </summary>
public sealed class DeliveryDimensionValue
{
    /// <summary>The longest original kept in the columns that copy one (a change's, a dimension table's key column).</summary>
    public const int MaxOriginalLength = 1024;

    /// <summary>
    /// The longest key a dimension keeps, and the longest record id or record key it finds a key by: each is unique by its
    /// text, and a unique index over the partition, the dimension and a text this long fits SQL Server's 1,700 bytes of an
    /// index key. A longer key is counted and left out by the build. A key is an OSDU id, well under it.
    /// </summary>
    public const int MaxKeyLength = 800;

    /// <summary>The longest label kept; a longer one is cut, with a note.</summary>
    public const int MaxLabelLength = 1024;

    /// <summary>The longest filter of one original: the original quoted and escaped, inside the field's nested form.</summary>
    public const int MaxFilterLength = 4000;

    public short PartitionId { get; set; }

    public long ValueId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The value exactly as the index holds it, compared exactly: what an original is unique by in its dimension.</summary>
    public string Original { get; set; } = string.Empty;

    /// <summary>
    /// The length of <see cref="Original"/> in bytes, computed by the database: SQL Server compares text as though the
    /// shorter were padded with spaces, so an original is unique by its text and its length (<c>GR</c> and <c>GR </c> are two).
    /// </summary>
    public short OriginalBytes { get; private set; }

    /// <summary>
    /// SHA-256 of the objects of the key's nested array as the build that last wrote them staged them (each object's fields
    /// in order, as JSON): a build that stages the same objects leaves the key's elements as they are. Null for a key the
    /// last build wrote no object of.
    /// </summary>
    public byte[]? ElementsHash { get; set; }

    /// <summary>
    /// SHA-256 of the attribute values read through the record the key names, as the build that last wrote them staged them
    /// (each name, value and where it was read, as JSON): a build that reads the same leaves those values as they are. Null
    /// for a key the last build read none of.
    /// </summary>
    public byte[]? AttributesHash { get; set; }

    /// <summary>The member it belongs to, or null when cleaning left it out of every member.</summary>
    public long? MemberId { get; set; }

    /// <summary>Why it belongs to no member: empty, tooLong, dropped or failed; null when it has a member.</summary>
    public string? LeftOut { get; set; }

    /// <summary>What cleaning had to say about it.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// The label the dimension read for it from the record it names (<c>Wellbore A-1</c> for a wellbore's id), before
    /// cleaning; null for a dimension that reads no label, or when the build could not read one.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>The id of the record the label was read from: the record the original names, or the last one a label's steps reached.</summary>
    public string? LabelFrom { get; set; }

    /// <summary>The search filter finding the records holding it, exactly as the index holds it; null when no query can carry it.</summary>
    public string? Filter { get; set; }

    public long Count { get; set; }

    /// <summary>Whether a query can carry it, so its member's filter finds its records.</summary>
    public bool Filterable { get; set; }

    /// <summary>The build that first found it. One the dimension holds now was found by the dimension's last build.</summary>
    public long FirstSeenRunId { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    /// <summary>The build that no longer found it, or null while it is there.</summary>
    public long? RemovedRunId { get; set; }

    public DateTime? RemovedUtc { get; set; }

    /// <summary>The build since which it belongs to its member (or to none).</summary>
    public long MemberSinceRunId { get; set; }
}

/// <summary>
/// One value of one attribute of one original (a key). An attribute read through the record the key names holds one value
/// a key, with the record it was read from; a collected attribute holds every value the key's own records hold, each with how
/// many of them hold it. A row is rewritten when a build reads it otherwise, and removed when a build finds the key
/// without it; a key no build finds any more keeps what its last build read.
/// </summary>
public sealed class DeliveryDimensionAttributeValue
{
    /// <summary>The longest attribute name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The longest attribute value kept; a longer one is cut by the build, with a note.</summary>
    public const int MaxValueLength = 256;

    public short PartitionId { get; set; }

    /// <summary>The row's own number, which the table is stored in the order of.</summary>
    public long AttributeValueId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The original the attribute is of (<see cref="DeliveryDimensionValue.ValueId"/>).</summary>
    public long ValueId { get; set; }

    /// <summary>The attribute, by its number (<see cref="DeliveryDimensionAttributeName.AttributeId"/>).</summary>
    public int AttributeId { get; set; }

    /// <summary>The value read, trimmed, compared exactly.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Where it was read: the id of the record the original names, or of the last one the attribute's steps reached; for a
    /// collected attribute, the text the key's records hold, as the index holds it (the first, ordinally, when several are shown
    /// as one value). Null for the value a key holding none is given.
    /// </summary>
    public string? ValueFrom { get; set; }

    /// <summary>
    /// For a collected attribute, how many of the key's records hold the value; null for an attribute read through the record the
    /// key names, which is every record of the key's.
    /// </summary>
    public long? Records { get; set; }
}

/// <summary>
/// One field of one object of a nested array a key's records hold (a curve of a well log): the key, the object's place among
/// the key's (from 1, in the order of the records' ids and then of the objects in each record), the field, and the value
/// read. An object holding none of its fields is one row of its first field with no value, so it is still a row of the
/// dimension's table. A build rewrites the rows of the keys it found; a key no build finds any more keeps what its last
/// build read.
/// </summary>
public sealed class DeliveryDimensionElement
{
    /// <summary>The longest value of a field kept; a longer one is cut by the build, with a note. No index holds it, so it is longer than an attribute's.</summary>
    public const int MaxValueLength = 4000;

    public short PartitionId { get; set; }

    /// <summary>The row's own number, which the table is stored in the order of.</summary>
    public long ElementValueId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The original the object belongs to (<see cref="DeliveryDimensionValue.ValueId"/>).</summary>
    public long ValueId { get; set; }

    /// <summary>The object's place among the original's, from 1.</summary>
    public int Seq { get; set; }

    /// <summary>The field, by its number (<see cref="DeliveryDimensionAttributeName.AttributeId"/>), as an attribute is named.</summary>
    public int AttributeId { get; set; }

    /// <summary>The value read, as the field keeps it, compared exactly; null for an object holding none of its fields.</summary>
    public string? Value { get; set; }
}

/// <summary>
/// One text a dimension's records hold at a collected attribute's path, as the last build that settled the field read it:
/// the text exactly as the index holds it, the value it is shown as (several texts shown alike are one value), and the
/// records holding it. A search picking a collected value asks for every text shown as it; a pick of the value for what is
/// not read asks for the records holding none of them. A build replaces the dimension's rows with what it read.
/// </summary>
public sealed class DeliveryDimensionCollectedText
{
    public short PartitionId { get; set; }

    /// <summary>The row's own number, which the table is stored in the order of.</summary>
    public long TextId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The collected attribute, by its number (<see cref="DeliveryDimensionAttributeName.AttributeId"/>).</summary>
    public int AttributeId { get; set; }

    /// <summary>The text exactly as the index holds it, compared exactly: what the row is unique by for its attribute.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The length of <see cref="Text"/> in bytes, computed by the database, which the row is unique by beside it, as a key is.</summary>
    public short TextBytes { get; private set; }

    /// <summary>The value it is shown as, compared exactly.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>The records holding it.</summary>
    public long Records { get; set; }
}

/// <summary>
/// An attribute of a dimension, under the number its values are kept and joined by: its name as the dimension declares
/// it, whether it is collected from the dimension's own records, and its place among the attributes the dimension declares
/// now, which is the order of its columns as the dimension's table is read. An attribute the dimension no longer
/// declares keeps its number and has no place.
/// </summary>
public sealed class DeliveryDimensionAttributeName
{
    public short PartitionId { get; set; }

    public int AttributeId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The attribute's name, as the dimension declares it, compared exactly.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Its place among the attributes the dimension declares now, from 1. Null for an attribute the dimension no longer declares.</summary>
    public short? Ordinal { get; set; }

    /// <summary>Whether its values are collected from the dimension's own records, so a key holds several.</summary>
    public bool Collected { get; set; }
}

/// <summary>
/// A record one key's label and attributes were read through: the record the key names and every record a step reached or
/// looked for, found or not. A build rewrites them for every key it reads; an incremental load finds the keys to read
/// again because a record changed by looking the record up here.
/// </summary>
public sealed class DeliveryDimensionKeyRecord
{
    /// <summary>The longest entity type a record id names.</summary>
    public const int MaxEntityTypeLength = 256;

    public short PartitionId { get; set; }

    /// <summary>The row's own number, which the table is stored in the order of.</summary>
    public long KeyRecordId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The original read through the record (<see cref="DeliveryDimensionValue.ValueId"/>).</summary>
    public long ValueId { get; set; }

    /// <summary>The record's id without its version, compared exactly: what a changed record is looked up by.</summary>
    public string RecordId { get; set; } = string.Empty;

    /// <summary>The entity type the id names (<c>master-data--Wellbore</c>): the kinds an incremental load asks the search what changed in.</summary>
    public string EntityType { get; set; } = string.Empty;
}

/// <summary>
/// A key one record of a dimension held when a load last read the record, the record named by its unique key (the flow's
/// <c>incremental.keyColumns</c>): what an incremental load reads again when the record changes, besides the keys it holds
/// now. A full load of a flow that loads incrementally rewrites them all; an incremental load rewrites those of the records
/// it found changed. A record that left the index keeps its rows until the next full load.
/// </summary>
public sealed class DeliveryDimensionRecord
{
    public short PartitionId { get; set; }

    /// <summary>The row's own number, which the table is stored in the order of.</summary>
    public long DimensionRecordId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The record's unique key, its columns' values joined, compared exactly.</summary>
    public string RecordKey { get; set; } = string.Empty;

    /// <summary>The original the record held (<see cref="DeliveryDimensionValue.ValueId"/>).</summary>
    public long ValueId { get; set; }
}

/// <summary>
/// A change a build made to one original: it arrived (after the dimension's first build), left, came back, or moved from one
/// member to another. The first build's originals are its arrivals, told by their first build. A dimension keeps only its
/// latest state, so the original and the values it left and went to are kept here as they read when the change was made:
/// the change still says what it was once the next build has deleted a removed original and its value.
/// </summary>
public sealed class DeliveryDimensionChange
{
    public short PartitionId { get; set; }

    public long ChangeId { get; set; }

    public int DimensionId { get; set; }

    public long DimensionRunId { get; set; }

    public long ValueId { get; set; }

    /// <summary>added, removed, moved or restored.</summary>
    public string Change { get; set; } = string.Empty;

    public long? FromMemberId { get; set; }

    public long? ToMemberId { get; set; }

    /// <summary>The original, as the index held it when the change was made.</summary>
    public string Original { get; set; } = string.Empty;

    /// <summary>The value the original was under before the change, as it read then; null when it was under none.</summary>
    public string? FromValue { get; set; }

    /// <summary>The value the original is under after the change, as it read then; null when it is under none.</summary>
    public string? ToValue { get; set; }

    public DateTime ChangedUtc { get; set; }
}
