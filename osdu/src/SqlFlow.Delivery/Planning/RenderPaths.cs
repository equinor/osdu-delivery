using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Planning;

/// <summary>How a path of a document differs between two renders of a record.</summary>
public enum PathChangeKind
{
    /// <summary>The newer render holds the path and the earlier one did not.</summary>
    Added,

    /// <summary>The earlier render held the path and the newer one does not.</summary>
    Removed,

    /// <summary>Both hold the path, with different values.</summary>
    Changed,
}

/// <summary>One path of a record's document whose value differs between two renders, without either value.</summary>
public sealed record PathChange(string Path, PathChangeKind Change);

/// <summary>
/// The paths whose values differ between the render OSDU holds and the one being sent (osdu/docs/reference/concepts/change-detection.md,
/// Why a record was delivered): the first <see cref="RenderPaths.MaxListed"/> in path order, and how many of each kind in all. Or,
/// when there was nothing to compare against, why not: <see cref="NotCompared"/>.
/// </summary>
public sealed record PathChanges
{
    /// <summary>Why the renders were not compared (no earlier render the ledger kept the paths of), or null when they were.</summary>
    public string? NotCompared { get; init; }

    /// <summary>The paths that differ, in path order, at most <see cref="RenderPaths.MaxListed"/>.</summary>
    public IReadOnlyList<PathChange> Listed { get; init; } = [];

    public int Added { get; init; }

    public int Removed { get; init; }

    public int Changed { get; init; }

    public bool Compared => NotCompared is null;

    /// <summary>How many paths differ in all.</summary>
    public int Total => Added + Removed + Changed;

    /// <summary>How many paths differ beyond those listed.</summary>
    public int More => Total - Listed.Count;

    /// <summary>The renders could not be compared, for <paramref name="why"/>.</summary>
    public static PathChanges Without(string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);
        return new PathChanges { NotCompared = why };
    }
}

/// <summary>
/// What a rendered document holds path by path, without its values: each path that ends in a value, with a 64-bit hash of
/// that value's canonical JSON. The ledger keeps it for the document OSDU holds of each record (<c>osdu.Record.RenderPaths</c>),
/// so a later delivery can name the paths its document changed (osdu/docs/reference/concepts/delivery-reasons.md) while the
/// ledger keeps no value a restricted record could leak.
/// </summary>
/// <remarks>
/// <para>A path descends through objects only. An array is one value, so <c>acl.viewers</c> or <c>data.Curves</c> is one path
/// however many items it holds: a change to an item, or to their order, changes that one path. That keeps the count of paths
/// bounded by the template's properties rather than by the data (a log's thousand curves stay one path), and names a change
/// at the property an operator reads it by. An empty object is a value of its own. Names are written as every record path
/// is (<see cref="RecordPathNotation"/>): <c>acl.viewers</c>, <c>tags['dra.lraID']</c>.</para>
/// <para>The hash is XXH3's 64 bits over the value's canonical bytes, read straight out of the canonical document, so two
/// renders compare equal exactly when the bytes of the value do. It names a change and guards nothing (the document's own
/// SHA-256 decides what is sent), so it is chosen for speed: one pass over the document, a fraction of what rendering it costs.
/// Two different values share a hash with a chance of about one in 2^64 for each path compared: a change named as unchanged
/// that rarely, and never a change claimed that did not happen.</para>
/// <para>The stored form is compact binary, about 25 bytes a path for a typical record: a format byte, the count, then for each
/// path in ordinal order the bytes it shares with the path before it, the rest of its UTF-8, and its hash (front coding).</para>
/// </remarks>
public sealed class RenderPaths
{
    /// <summary>The most changed paths a delivery's reason names; the rest are counted.</summary>
    public const int MaxListed = 50;

    /// <summary>The format of <see cref="Encode"/>'s first byte; a form with another is not read.</summary>
    public const byte Format = 1;

    private readonly string[] _paths;
    private readonly ulong[] _hashes;

    private RenderPaths(string[] paths, ulong[] hashes)
    {
        _paths = paths;
        _hashes = hashes;
    }

    /// <summary>How many paths the document holds.</summary>
    public int Count => _paths.Length;

    /// <summary>The paths, in ordinal order.</summary>
    public IReadOnlyList<string> Paths => _paths;

    /// <summary>The hash of the value at <paramref name="path"/>, or null when the document holds no such path.</summary>
    public ulong? HashOf(string path)
    {
        var at = Array.BinarySearch(_paths, path, StringComparer.Ordinal);
        return at >= 0 ? _hashes[at] : null;
    }

    /// <summary>
    /// The paths of a canonical document (<see cref="CanonicalJson"/>) and the hashes of their values. A document that is not
    /// a JSON object is refused: every record is one.
    /// </summary>
    public static RenderPaths Of(string canonicalDocument)
    {
        ArgumentNullException.ThrowIfNull(canonicalDocument);
        var bytes = Encoding.UTF8.GetBytes(canonicalDocument);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 512 });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new ArgumentException("A rendered document is a JSON object.", nameof(canonicalDocument));
        }

        var found = new List<(string Path, ulong Hash)>();
        Walk(ref reader, string.Empty, bytes, found);

        // The canonical text sorts each object's keys, but a path is ordered as a whole, and a quoted name sorts apart
        // from a bare one, so the whole list is sorted once.
        found.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new RenderPaths([.. found.Select(f => f.Path)], [.. found.Select(f => f.Hash)]);
    }

    /// <summary>
    /// The paths whose values differ from <paramref name="before"/> to <paramref name="after"/>, the first
    /// <paramref name="max"/> of them listed in path order: one merge of two sorted lists.
    /// </summary>
    public static PathChanges Compare(RenderPaths before, RenderPaths after, int max = MaxListed)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentOutOfRangeException.ThrowIfNegative(max);
        var listed = new List<PathChange>(Math.Min(max, 16));
        int added = 0, removed = 0, changed = 0;
        int i = 0, j = 0;
        while (i < before._paths.Length || j < after._paths.Length)
        {
            var order = i == before._paths.Length ? 1
                : j == after._paths.Length ? -1
                : string.CompareOrdinal(before._paths[i], after._paths[j]);
            PathChange? change = null;
            if (order < 0)
            {
                removed++;
                change = new PathChange(before._paths[i++], PathChangeKind.Removed);
            }
            else if (order > 0)
            {
                added++;
                change = new PathChange(after._paths[j++], PathChangeKind.Added);
            }
            else
            {
                if (before._hashes[i] != after._hashes[j])
                {
                    changed++;
                    change = new PathChange(after._paths[j], PathChangeKind.Changed);
                }

                i++;
                j++;
            }

            if (change is not null && listed.Count < max)
            {
                listed.Add(change);
            }
        }

        return new PathChanges { Listed = listed, Added = added, Removed = removed, Changed = changed };
    }

    /// <summary>The compact form the ledger keeps (see the remarks).</summary>
    public byte[] Encode()
    {
        using var stream = new MemoryStream(capacity: 16 + (_paths.Length * 28));
        stream.WriteByte(Format);
        WriteCount(stream, _paths.Length);
        var previous = Array.Empty<byte>();
        Span<byte> hash = stackalloc byte[8];
        foreach (var (path, value) in _paths.Zip(_hashes))
        {
            var bytes = Encoding.UTF8.GetBytes(path);
            var shared = 0;
            var most = Math.Min(previous.Length, bytes.Length);
            while (shared < most && previous[shared] == bytes[shared])
            {
                shared++;
            }

            WriteCount(stream, shared);
            WriteCount(stream, bytes.Length - shared);
            stream.Write(bytes, shared, bytes.Length - shared);
            BinaryPrimitives.WriteUInt64LittleEndian(hash, value);
            stream.Write(hash);
            previous = bytes;
        }

        return stream.ToArray();
    }

    /// <summary>
    /// The compact form in base64: how a work item carries it, and how the ledger's records hold it, as text, so two reads of
    /// the same record compare equal (a record's equality compares an array by reference, a text by its characters).
    /// </summary>
    public string EncodeText() => Convert.ToBase64String(Encode());

    /// <summary>The paths <see cref="EncodeText"/> wrote, or null with why when <paramref name="text"/> is not base64 of them.</summary>
    public static RenderPaths? DecodeText(string text, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] stored;
        try
        {
            stored = Convert.FromBase64String(text);
        }
        catch (FormatException ex)
        {
            problem = $"the path hashes the ledger keeps cannot be read: they are not base64 ({ex.Message})";
            return null;
        }

        return Decode(stored, out problem);
    }

    /// <summary>
    /// The paths <see cref="Encode"/> wrote, or null with why when <paramref name="stored"/> cannot be read as them: written in
    /// another format, cut short, or not in path order.
    /// </summary>
    public static RenderPaths? Decode(byte[] stored, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(stored);
        problem = null;
        if (stored.Length == 0 || stored[0] != Format)
        {
            problem = stored.Length == 0
                ? "the path hashes the ledger keeps are empty"
                : $"the path hashes the ledger keeps are in format {stored[0]}, which this version does not read";
            return null;
        }

        try
        {
            var at = 1;
            var count = ReadCount(stored, ref at);
            var paths = new string[count];
            var hashes = new ulong[count];
            var previous = Array.Empty<byte>();
            for (var n = 0; n < count; n++)
            {
                var shared = ReadCount(stored, ref at);
                var rest = ReadCount(stored, ref at);
                if (shared > previous.Length || at + rest + 8 > stored.Length)
                {
                    throw new FormatException("an entry runs past the end of the paths");
                }

                var bytes = new byte[shared + rest];
                Array.Copy(previous, bytes, shared);
                Array.Copy(stored, at, bytes, shared, rest);
                at += rest;
                paths[n] = Encoding.UTF8.GetString(bytes);
                hashes[n] = BinaryPrimitives.ReadUInt64LittleEndian(stored.AsSpan(at, 8));
                at += 8;
                if (n > 0 && string.CompareOrdinal(paths[n - 1], paths[n]) >= 0)
                {
                    throw new FormatException($"path {paths[n]} is not after {paths[n - 1]}");
                }

                previous = bytes;
            }

            if (at != stored.Length)
            {
                throw new FormatException("bytes follow the last path");
            }

            return new RenderPaths(paths, hashes);
        }
        catch (FormatException ex)
        {
            problem = $"the path hashes the ledger keeps cannot be read: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Walks the object the reader stands on, adding each path that ends in a value with its hash. Returns whether the object
    /// held any property, so an empty object can be a value of its own.
    /// </summary>
    private static bool Walk(ref Utf8JsonReader reader, string prefix, byte[] bytes, List<(string Path, ulong Hash)> found)
    {
        var any = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var path = RecordPathNotation.Append(prefix, reader.GetString()!);
            reader.Read();
            any = true;
            var start = (int)reader.TokenStartIndex;
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                if (Walk(ref reader, path, bytes, found))
                {
                    continue;
                }
            }
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                reader.Skip();
            }

            found.Add((path, Hash(bytes.AsSpan(start, (int)reader.BytesConsumed - start))));
        }

        return any;
    }

    private static ulong Hash(ReadOnlySpan<byte> value) => XxHash3.HashToUInt64(value);

    private static void WriteCount(Stream stream, int value)
    {
        var rest = (uint)value;
        while (rest >= 0x80)
        {
            stream.WriteByte((byte)(rest | 0x80));
            rest >>= 7;
        }

        stream.WriteByte((byte)rest);
    }

    private static int ReadCount(byte[] stored, ref int at)
    {
        var value = 0;
        for (var shift = 0; shift < 32; shift += 7)
        {
            if (at >= stored.Length)
            {
                throw new FormatException("a count runs past the end of the paths");
            }

            var b = stored[at++];
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value >= 0 ? value : throw new FormatException("a count is out of range");
            }
        }

        throw new FormatException("a count is longer than five bytes");
    }
}
