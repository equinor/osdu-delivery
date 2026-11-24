using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace SqlFlow.Delivery.Engine.Protocols.Etp;

/// <summary>
/// One ETP 1.2 message: what protocol and message type its header carries, and how its body is written. The bodies are
/// the records of <c>EtpMessages.*.cs</c>, a file per protocol, generated from the pinned protocol
/// (osdu/specs/reservoir-ddms/etp-1.2.avpr).
/// </summary>
public interface IEtpMessage
{
    /// <summary>The ETP protocol number the header carries (Core 0, Discovery 3, Store 4, DataArray 9, Transaction 18, Dataspace 24, DataspaceOSDU 2424).</summary>
    int Protocol { get; }

    /// <summary>The message type inside that protocol.</summary>
    int MessageType { get; }

    /// <summary>The message's name in the pinned protocol, for logs and errors.</summary>
    string MessageName { get; }

    /// <summary>Writes the body, the header having been written already.</summary>
    void Write(EtpWriter writer);
}

/// <summary>
/// The branch of <see cref="DataValue"/> a value carries. The ordinals are the union's branch order in the pinned
/// protocol, which is what goes on the wire (osdu/specs/reservoir-ddms/INTEGRATION.md section 2.4).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "The branches are named as the pinned ETP protocol names them, which is what makes the branch order on the wire readable.")]
public enum DataValueKind
{
    Null = 0,
    Boolean = 1,
    Int = 2,
    Long = 3,
    Float = 4,
    Double = 5,
    String = 6,
    BooleanArray = 7,
    NullableBooleanArray = 8,
    IntArray = 9,
    NullableIntArray = 10,
    LongArray = 11,
    NullableLongArray = 12,
    FloatArray = 13,
    DoubleArray = 14,
    StringArray = 15,
    BytesArray = 16,
    Bytes = 17,
    SparseArray = 18,
}

/// <summary>
/// The <c>Energistics.Etp.v12.Datatypes.DataValue</c> union: the value type of every ETP metadata map, from a
/// dataspace's legal tags and ACLs to a session's capabilities. The branch is kept explicitly, because the branch
/// index is what the wire carries and a reader has nothing else to go on.
/// </summary>
public sealed class DataValue
{
    /// <summary>The union's null branch, which ETP sends for an absent value.</summary>
    public static readonly DataValue Null = new(DataValueKind.Null, null);

    private readonly object? _item;

    private DataValue(DataValueKind kind, object? item)
    {
        Kind = kind;
        _item = item;
    }

    public DataValueKind Kind { get; }

    public static DataValue Of(bool value) => new(DataValueKind.Boolean, value);

    public static DataValue Of(int value) => new(DataValueKind.Int, value);

    public static DataValue Of(long value) => new(DataValueKind.Long, value);

    public static DataValue Of(float value) => new(DataValueKind.Float, value);

    public static DataValue Of(double value) => new(DataValueKind.Double, value);

    public static DataValue Of(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new DataValue(DataValueKind.String, value);
    }

    /// <summary>A list of strings, the branch the ETP server stores a dataspace's viewers, owners and legal tags in.</summary>
    public static DataValue Of(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new DataValue(DataValueKind.StringArray, new ArrayOfString { Values = values });
    }

    /// <summary>The value as text, for the branches that carry one: a string, or a number or flag written out.</summary>
    public string? Text => Kind switch
    {
        DataValueKind.String => (string)_item!,
        DataValueKind.Boolean => ((bool)_item!) ? "true" : "false",
        DataValueKind.Int => ((int)_item!).ToString(CultureInfo.InvariantCulture),
        DataValueKind.Long => ((long)_item!).ToString(CultureInfo.InvariantCulture),
        DataValueKind.Float => ((float)_item!).ToString(CultureInfo.InvariantCulture),
        DataValueKind.Double => ((double)_item!).ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>The value as a flag, when it carries one.</summary>
    public bool? Flag => Kind == DataValueKind.Boolean ? (bool)_item! : null;

    /// <summary>The value as a whole number, when it carries one.</summary>
    public long? Number => Kind switch
    {
        DataValueKind.Int => (int)_item!,
        DataValueKind.Long => (long)_item!,
        _ => null,
    };

    /// <summary>
    /// The value as a list of strings: the list branch as it stands, and a single string as a list of one, which is how
    /// the ETP server accepts a legal tag written either way.
    /// </summary>
    public IReadOnlyList<string>? Strings => Kind switch
    {
        DataValueKind.StringArray => ((ArrayOfString)_item!).Values,
        DataValueKind.String => [(string)_item!],
        _ => null,
    };

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUnion((int)Kind);
        switch (Kind)
        {
            case DataValueKind.Null: break;  // An Avro null is no bytes at all; the branch before it is what the reader sees.
            case DataValueKind.Boolean: writer.WriteBoolean((bool)_item!); break;
            case DataValueKind.Int: writer.WriteInt((int)_item!); break;
            case DataValueKind.Long: writer.WriteLong((long)_item!); break;
            case DataValueKind.Float: writer.WriteFloat((float)_item!); break;
            case DataValueKind.Double: writer.WriteDouble((double)_item!); break;
            case DataValueKind.String: writer.WriteString((string)_item!); break;
            case DataValueKind.BooleanArray: ((ArrayOfBoolean)_item!).Write(writer); break;
            case DataValueKind.NullableBooleanArray: ((ArrayOfNullableBoolean)_item!).Write(writer); break;
            case DataValueKind.IntArray: ((ArrayOfInt)_item!).Write(writer); break;
            case DataValueKind.NullableIntArray: ((ArrayOfNullableInt)_item!).Write(writer); break;
            case DataValueKind.LongArray: ((ArrayOfLong)_item!).Write(writer); break;
            case DataValueKind.NullableLongArray: ((ArrayOfNullableLong)_item!).Write(writer); break;
            case DataValueKind.FloatArray: ((ArrayOfFloat)_item!).Write(writer); break;
            case DataValueKind.DoubleArray: ((ArrayOfDouble)_item!).Write(writer); break;
            case DataValueKind.StringArray: ((ArrayOfString)_item!).Write(writer); break;
            case DataValueKind.BytesArray: ((ArrayOfBytes)_item!).Write(writer); break;
            case DataValueKind.Bytes: writer.WriteBytes(((ReadOnlyMemory<byte>)_item!).Span); break;
            case DataValueKind.SparseArray: ((AnySparseArray)_item!).Write(writer); break;
            default: throw new EtpFormatException($"A DataValue carries branch {(int)Kind}, which the pinned schema does not define.");
        }
    }

    public static DataValue Read(ref EtpReader reader)
    {
        var branch = (DataValueKind)reader.ReadUnion(19);
        return branch switch
        {
            DataValueKind.Null => Null,
            DataValueKind.Boolean => new DataValue(branch, reader.ReadBoolean()),
            DataValueKind.Int => new DataValue(branch, reader.ReadInt()),
            DataValueKind.Long => new DataValue(branch, reader.ReadLong()),
            DataValueKind.Float => new DataValue(branch, reader.ReadFloat()),
            DataValueKind.Double => new DataValue(branch, reader.ReadDouble()),
            DataValueKind.String => new DataValue(branch, reader.ReadString()),
            DataValueKind.BooleanArray => new DataValue(branch, ArrayOfBoolean.Read(ref reader)),
            DataValueKind.NullableBooleanArray => new DataValue(branch, ArrayOfNullableBoolean.Read(ref reader)),
            DataValueKind.IntArray => new DataValue(branch, ArrayOfInt.Read(ref reader)),
            DataValueKind.NullableIntArray => new DataValue(branch, ArrayOfNullableInt.Read(ref reader)),
            DataValueKind.LongArray => new DataValue(branch, ArrayOfLong.Read(ref reader)),
            DataValueKind.NullableLongArray => new DataValue(branch, ArrayOfNullableLong.Read(ref reader)),
            DataValueKind.FloatArray => new DataValue(branch, ArrayOfFloat.Read(ref reader)),
            DataValueKind.DoubleArray => new DataValue(branch, ArrayOfDouble.Read(ref reader)),
            DataValueKind.StringArray => new DataValue(branch, ArrayOfString.Read(ref reader)),
            DataValueKind.BytesArray => new DataValue(branch, ArrayOfBytes.Read(ref reader)),
            DataValueKind.Bytes => new DataValue(branch, reader.ReadBytes()),
            _ => new DataValue(branch, AnySparseArray.Read(ref reader)),
        };
    }
}

/// <summary>
/// The <c>Energistics.Etp.v12.Datatypes.AnyArray</c> union: the bulk payload of every array ETP carries. Its branch
/// order is exactly the <see cref="AnyArrayType"/> symbol order, which is the transport type the server stores
/// (osdu/specs/reservoir-ddms/INTEGRATION.md section 4.6).
/// </summary>
public sealed class AnyArray
{
    private readonly object _item;

    private AnyArray(AnyArrayType kind, object item)
    {
        Kind = kind;
        _item = item;
    }

    /// <summary>The transport type of the array, which is also its union branch.</summary>
    public AnyArrayType Kind { get; }

    /// <summary>How many elements the array carries, whatever its transport type.</summary>
    public int ElementCount => Kind switch
    {
        AnyArrayType.ArrayOfBoolean => ((ArrayOfBoolean)_item).Values.Length,
        AnyArrayType.ArrayOfInt => ((ArrayOfInt)_item).Values.Length,
        AnyArrayType.ArrayOfLong => ((ArrayOfLong)_item).Values.Length,
        AnyArrayType.ArrayOfFloat => ((ArrayOfFloat)_item).Values.Length,
        AnyArrayType.ArrayOfDouble => ((ArrayOfDouble)_item).Values.Length,
        AnyArrayType.ArrayOfString => ((ArrayOfString)_item).Values.Count,
        _ => ((ReadOnlyMemory<byte>)_item).Length,
    };

    /// <summary>
    /// What this array will take on the wire, near enough to plan messages by: the fixed-width types exactly, and the
    /// variable-length ones at their worst case, so a slice planned against a budget never overruns it.
    /// </summary>
    public long EstimatedBytes => Kind switch
    {
        AnyArrayType.ArrayOfBoolean => ((ArrayOfBoolean)_item).Values.Length,
        AnyArrayType.ArrayOfInt => (long)((ArrayOfInt)_item).Values.Length * 5,
        AnyArrayType.ArrayOfLong => (long)((ArrayOfLong)_item).Values.Length * 10,
        AnyArrayType.ArrayOfFloat => (long)((ArrayOfFloat)_item).Values.Length * sizeof(float),
        AnyArrayType.ArrayOfDouble => (long)((ArrayOfDouble)_item).Values.Length * sizeof(double),
        AnyArrayType.ArrayOfString => ((ArrayOfString)_item).Values.Sum(value => (long)System.Text.Encoding.UTF8.GetByteCount(value) + 5),
        _ => ((ReadOnlyMemory<byte>)_item).Length + 5L,
    };

    /// <summary>
    /// The elements from <paramref name="start"/>, as an array of the same transport type. A slice of a row-major
    /// array whose trailing dimensions are whole is a contiguous run of elements, which is what the fill of a large
    /// array sends (osdu/specs/reservoir-ddms/INTEGRATION.md section 5.4).
    /// </summary>
    public AnyArray Slice(int start, int length) => Kind switch
    {
        AnyArrayType.ArrayOfBoolean => Of(((ArrayOfBoolean)_item).Values.Slice(start, length)),
        AnyArrayType.ArrayOfInt => Of(((ArrayOfInt)_item).Values.Slice(start, length)),
        AnyArrayType.ArrayOfLong => Of(((ArrayOfLong)_item).Values.Slice(start, length)),
        AnyArrayType.ArrayOfFloat => Of(((ArrayOfFloat)_item).Values.Slice(start, length)),
        AnyArrayType.ArrayOfDouble => Of(((ArrayOfDouble)_item).Values.Slice(start, length)),
        AnyArrayType.ArrayOfString => Of(((ArrayOfString)_item).Values.Skip(start).Take(length).ToList()),
        _ => OfBytes(((ReadOnlyMemory<byte>)_item).Slice(start, length)),
    };

    /// <summary>The bytes one element of this transport type occupies on the wire, or null when elements vary in size.</summary>
    public int? ElementBytes => Kind switch
    {
        AnyArrayType.ArrayOfBoolean => 1,
        AnyArrayType.ArrayOfFloat => sizeof(float),
        AnyArrayType.ArrayOfDouble => sizeof(double),
        AnyArrayType.Bytes => 1,
        _ => null,
    };

    public static AnyArray Of(ReadOnlyMemory<bool> values) => new(AnyArrayType.ArrayOfBoolean, new ArrayOfBoolean { Values = values });

    public static AnyArray Of(ReadOnlyMemory<int> values) => new(AnyArrayType.ArrayOfInt, new ArrayOfInt { Values = values });

    public static AnyArray Of(ReadOnlyMemory<long> values) => new(AnyArrayType.ArrayOfLong, new ArrayOfLong { Values = values });

    public static AnyArray Of(ReadOnlyMemory<float> values) => new(AnyArrayType.ArrayOfFloat, new ArrayOfFloat { Values = values });

    public static AnyArray Of(ReadOnlyMemory<double> values) => new(AnyArrayType.ArrayOfDouble, new ArrayOfDouble { Values = values });

    public static AnyArray Of(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new AnyArray(AnyArrayType.ArrayOfString, new ArrayOfString { Values = values });
    }

    /// <summary>The <c>bytes</c> branch, which ETP uses for a byte-per-element array.</summary>
    public static AnyArray OfBytes(ReadOnlyMemory<byte> values) => new(AnyArrayType.Bytes, values);

    public ReadOnlyMemory<bool> Booleans => Kind == AnyArrayType.ArrayOfBoolean ? ((ArrayOfBoolean)_item).Values : throw Mismatch(AnyArrayType.ArrayOfBoolean);

    public ReadOnlyMemory<int> Ints => Kind == AnyArrayType.ArrayOfInt ? ((ArrayOfInt)_item).Values : throw Mismatch(AnyArrayType.ArrayOfInt);

    public ReadOnlyMemory<long> Longs => Kind == AnyArrayType.ArrayOfLong ? ((ArrayOfLong)_item).Values : throw Mismatch(AnyArrayType.ArrayOfLong);

    public ReadOnlyMemory<float> Floats => Kind == AnyArrayType.ArrayOfFloat ? ((ArrayOfFloat)_item).Values : throw Mismatch(AnyArrayType.ArrayOfFloat);

    public ReadOnlyMemory<double> Doubles => Kind == AnyArrayType.ArrayOfDouble ? ((ArrayOfDouble)_item).Values : throw Mismatch(AnyArrayType.ArrayOfDouble);

    public IReadOnlyList<string> Strings => Kind == AnyArrayType.ArrayOfString ? ((ArrayOfString)_item).Values : throw Mismatch(AnyArrayType.ArrayOfString);

    public ReadOnlyMemory<byte> Bytes => Kind == AnyArrayType.Bytes ? (ReadOnlyMemory<byte>)_item : throw Mismatch(AnyArrayType.Bytes);

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUnion((int)Kind);
        switch (Kind)
        {
            case AnyArrayType.ArrayOfBoolean: ((ArrayOfBoolean)_item).Write(writer); break;
            case AnyArrayType.ArrayOfInt: ((ArrayOfInt)_item).Write(writer); break;
            case AnyArrayType.ArrayOfLong: ((ArrayOfLong)_item).Write(writer); break;
            case AnyArrayType.ArrayOfFloat: ((ArrayOfFloat)_item).Write(writer); break;
            case AnyArrayType.ArrayOfDouble: ((ArrayOfDouble)_item).Write(writer); break;
            case AnyArrayType.ArrayOfString: ((ArrayOfString)_item).Write(writer); break;
            case AnyArrayType.Bytes: writer.WriteBytes(((ReadOnlyMemory<byte>)_item).Span); break;
            default: throw new EtpFormatException($"An AnyArray carries branch {(int)Kind}, which the pinned schema does not define.");
        }
    }

    public static AnyArray Read(ref EtpReader reader)
    {
        var branch = (AnyArrayType)reader.ReadUnion(7);
        return branch switch
        {
            AnyArrayType.ArrayOfBoolean => new AnyArray(branch, ArrayOfBoolean.Read(ref reader)),
            AnyArrayType.ArrayOfInt => new AnyArray(branch, ArrayOfInt.Read(ref reader)),
            AnyArrayType.ArrayOfLong => new AnyArray(branch, ArrayOfLong.Read(ref reader)),
            AnyArrayType.ArrayOfFloat => new AnyArray(branch, ArrayOfFloat.Read(ref reader)),
            AnyArrayType.ArrayOfDouble => new AnyArray(branch, ArrayOfDouble.Read(ref reader)),
            AnyArrayType.ArrayOfString => new AnyArray(branch, ArrayOfString.Read(ref reader)),
            _ => new AnyArray(branch, reader.ReadBytes()),
        };
    }

    private EtpFormatException Mismatch(AnyArrayType asked)
        => new($"An ETP array carries {Kind} and was read as {asked}.");
}
