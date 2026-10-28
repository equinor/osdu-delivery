using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SqlFlow.Core.Model;

namespace SqlFlow.Delivery.Storage;

/// <summary>
/// One rendered record waiting in a work batch: its key, its target id, its canonical document, and what its mapping's
/// assertions found of that document (osdu/docs/reference/flow/mapping-assertions.md) as compact JSON, or null when the
/// mapping states none. The findings sit beside the document they judge, so the check before sending reads the findings
/// of exactly the document it sends, and a release that accepts the document accepts them with it.
/// </summary>
/// <param name="Key">The record's delivery key.</param>
/// <param name="TargetId">The OSDU id the record is delivered under.</param>
/// <param name="Document">The record's canonical document.</param>
/// <param name="Assertions">What the mapping's assertions found of the document, as compact JSON, or null.</param>
/// <param name="Enrich">
/// The datasets the record's steps register (osdu/docs/reference/flow/enrich-records.md) and what the plan
/// decided a delivery does with them, as compact JSON, or
/// null for a record whose steps register none.
/// </param>
public sealed record WorkItem(Guid Key, string TargetId, string Document, string? Assertions = null, string? Enrich = null);

/// <summary>Where one document sits inside a work batch: the batch index and the byte range of its line.</summary>
public readonly record struct DocumentRef(int Batch, long Offset, int Length)
{
    /// <summary>The compact text the ledger stores: batch:offset:length.</summary>
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Batch}:{Offset}:{Length}");

    public static DocumentRef Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var parts = text.Split(':');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var batch)
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
        {
            throw new DeliveryException($"'{text}' is not a document reference (batch:offset:length).");
        }

        return new DocumentRef(batch, offset, length);
    }

    public static bool TryParse(string? text, out DocumentRef value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            value = Parse(text);
            return true;
        }
        catch (DeliveryException)
        {
            return false;
        }
    }
}

/// <summary>
/// The work batches of a submission (osdu/docs/reference/concepts/submissions.md, Work batches): JSON-lines files of
/// rendered documents the intake writes to the flow's work location and the drains read back, so the ledger holds a
/// reference per record instead of the document itself. A batch is one file, one claim, one unit of progress. Lines are
/// written as they render and read as they are needed: a batch of any size never sits in memory.
/// </summary>
public static class WorkBatchFile
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static string DirectoryFor(string workRoot, Guid submissionId)
        => Join(workRoot, submissionId.ToString("D"));

    public static string PathFor(string workRoot, Guid submissionId, int batch)
        => Join(DirectoryFor(workRoot, submissionId), "batch-" + batch.ToString("D6", CultureInfo.InvariantCulture) + ".jsonl");

    /// <summary>
    /// The bytes of one line (without the newline): key, target id, the document and, when there are any, the assertions'
    /// findings, as one JSON object.
    /// </summary>
    public static byte[] Encode(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var builder = new StringBuilder(item.Document.Length + 128 + (item.Assertions?.Length ?? 0));
        builder.Append("{\"key\":\"").Append(item.Key.ToString("D")).Append("\",\"targetId\":");
        builder.Append(JsonSerializer.Serialize(item.TargetId));
        builder.Append(",\"document\":").Append(item.Document);
        if (item.Assertions is { Length: > 0 } assertions)
        {
            builder.Append(",\"assertions\":").Append(assertions);
        }

        if (item.Enrich is { Length: > 0 } enrich)
        {
            builder.Append(",\"enrich\":").Append(enrich);
        }

        builder.Append('}');
        return Utf8.GetBytes(builder.ToString());
    }

    public static WorkItem Decode(ReadOnlySpan<byte> line)
    {
        using var document = JsonDocument.Parse(line.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("key", out var key) || !Guid.TryParse(key.GetString(), out var parsedKey)
            || !root.TryGetProperty("targetId", out var target) || target.GetString() is not { } targetId
            || !root.TryGetProperty("document", out var doc) || doc.ValueKind != JsonValueKind.Object)
        {
            throw new DeliveryException("A work batch line is not a work item (key, targetId, document).");
        }

        // A line written before a mapping could assert carries no findings, and its document is judged by its schema alone.
        var assertions = root.TryGetProperty("assertions", out var found) && found.ValueKind == JsonValueKind.Object ? found.GetRawText() : null;

        // A line written before steps registered files carries no parts.
        var enrich = root.TryGetProperty("enrich", out var parts) && parts.ValueKind == JsonValueKind.Object ? parts.GetRawText() : null;
        return new WorkItem(parsedKey, targetId, doc.GetRawText(), assertions, enrich);
    }

    /// <summary>
    /// Reads one document by its reference: a range read of the batch file. A document that is not there to read (the file
    /// is gone, ends before it, or holds no work item where it should) is <see cref="WorkDocumentMissingException"/>, which
    /// no retry mends; any other failure (the store did not answer) is thrown as it came, and a later try reads again.
    /// </summary>
    public static async Task<WorkItem> ReadOneAsync(FileStoreRegistry stores, string workRoot, Guid submissionId, DocumentRef reference, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stores);
        var path = PathFor(workRoot, submissionId, reference.Batch);
        Stream stream;
        try
        {
            stream = await stores.OpenRangeAsync(path, reference.Offset, reference.Length, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            throw new WorkDocumentMissingException($"{path}: the batch file holding document {reference} is gone; the work location was pruned.", ex);
        }

        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[reference.Length];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    throw new WorkDocumentMissingException($"{path}: the batch file ended before document {reference}; the work location was pruned or rewritten.");
                }

                read += n;
            }

            try
            {
                return Decode(buffer);
            }
            catch (Exception ex) when (ex is DeliveryException or JsonException)
            {
                throw new WorkDocumentMissingException($"{path}: document {reference} is not a work item any more; the work location was rewritten. {ex.Message}", ex);
            }
        }
    }

    /// <summary>Whether a store's failure says the file is not there, rather than that the store could not be reached.</summary>
    private static bool IsNotFound(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is FileNotFoundException or DirectoryNotFoundException || (e is global::Azure.RequestFailedException { Status: 404 }))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Streams every item of a batch file in order, with each line's reference. A batch that is not there to read (the file
    /// is gone, or a line of it is not a work item) is <see cref="WorkDocumentMissingException"/> once the items before it
    /// are read, which no retry mends; any other failure (the store did not answer) is thrown as it came.
    /// </summary>
    public static async IAsyncEnumerable<(WorkItem Item, DocumentRef Reference)> ReadAllAsync(
        FileStoreRegistry stores, string workRoot, Guid submissionId, int batch, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stores);
        var path = PathFor(workRoot, submissionId, batch);
        await using var stream = await OpenBatchAsync(stores, path, ct).ConfigureAwait(false);
        var buffer = new byte[1 << 16];
        var line = new MemoryStream();
        long offset = 0;
        long lineStart = 0;
        int read;
        while ((read = await ReadBatchAsync(stream, buffer, path, ct).ConfigureAwait(false)) > 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n')
                {
                    continue;
                }

                line.Write(buffer, start, i - start);
                if (line.Length > 0)
                {
                    var reference = new DocumentRef(batch, lineStart, (int)line.Length);
                    yield return (DecodeLine(line.GetBuffer().AsSpan(0, (int)line.Length), path, reference), reference);
                }

                line.SetLength(0);
                start = i + 1;
                lineStart = offset + start;
            }

            line.Write(buffer, start, read - start);
            offset += read;
        }

        if (line.Length > 0)
        {
            var reference = new DocumentRef(batch, lineStart, (int)line.Length);
            yield return (DecodeLine(line.GetBuffer().AsSpan(0, (int)line.Length), path, reference), reference);
        }
    }

    private static async Task<Stream> OpenBatchAsync(FileStoreRegistry stores, string path, CancellationToken ct)
    {
        try
        {
            return await stores.For(path).OpenReadAsync(new FileRef { Path = path, Name = Path.GetFileName(path) }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            throw new WorkDocumentMissingException($"{path}: the batch file is gone; the work location was pruned, or is on a node other than this one.", ex);
        }
    }

    private static async Task<int> ReadBatchAsync(Stream stream, byte[] buffer, string path, CancellationToken ct)
    {
        try
        {
            return await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            throw new WorkDocumentMissingException($"{path}: the batch file went while it was read; the work location was pruned.", ex);
        }
    }

    private static WorkItem DecodeLine(ReadOnlySpan<byte> line, string path, DocumentRef reference)
    {
        try
        {
            return Decode(line);
        }
        catch (Exception ex) when (ex is DeliveryException or JsonException)
        {
            throw new WorkDocumentMissingException($"{path}: document {reference} is not a work item any more; the work location was rewritten. {ex.Message}", ex);
        }
    }

    private static string Join(string root, string name) => FileStoreRegistry.Join(root, name);
}

/// <summary>Writes one work batch file, line by line, straight to storage; the batch is complete when disposed.</summary>
public sealed class WorkBatchWriter : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly int _batch;
    private long _offset;

    private WorkBatchWriter(Stream stream, int batch, string path)
    {
        _stream = stream;
        _batch = batch;
        Path = path;
    }

    public string Path { get; }

    public int Batch => _batch;

    public int Count { get; private set; }

    public long Bytes => _offset;

    public static async Task<WorkBatchWriter> OpenAsync(FileStoreRegistry stores, string workRoot, Guid submissionId, int batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stores);
        var path = WorkBatchFile.PathFor(workRoot, submissionId, batch);
        var stream = await stores.OpenWriteAsync(path, ct).ConfigureAwait(false);
        return new WorkBatchWriter(stream, batch, path);
    }

    /// <summary>Appends one item and returns where it landed.</summary>
    public async Task<DocumentRef> WriteAsync(WorkItem item, CancellationToken ct = default)
    {
        var bytes = WorkBatchFile.Encode(item);
        var reference = new DocumentRef(_batch, _offset, bytes.Length);
        await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _stream.WriteAsync(new[] { (byte)'\n' }, ct).ConfigureAwait(false);
        _offset += bytes.Length + 1;
        Count++;
        return reference;
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}

/// <summary>
/// A queued document that is not there to read: its work batch file is gone, ends before it, or holds something else where
/// it should be. No retry mends it; the record is planned again from its row instead.
/// </summary>
public sealed class WorkDocumentMissingException : DeliveryException
{
    public WorkDocumentMissingException(string message)
        : base(message)
    {
    }

    public WorkDocumentMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
