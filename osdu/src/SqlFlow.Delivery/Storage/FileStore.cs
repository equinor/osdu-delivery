using Azure;
using SqlFlow.Azure;
using SqlFlow.Azure.Storage;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;
using SqlFlow.Sources;

namespace SqlFlow.Delivery.Storage;

/// <summary>
/// Writes a file (create or overwrite) and opens a location for streamed writing, for the locations one file store
/// family handles. The platform's <see cref="IFileStore"/> is read-only by design (the engine only ever reads sources);
/// the intake's work batches and retrieval files are what the delivery domain writes, so the write side lives here,
/// next to the reads it pairs with.
/// </summary>
public interface IFileWriter
{
    bool CanHandle(string location);

    Task WriteAsync(string location, Stream content, CancellationToken ct = default);

    /// <summary>
    /// Opens a location for streamed writing: bytes go to storage as they are written, so a work batch of any size
    /// never sits in memory. The content becomes visible (and complete) when the stream is disposed.
    /// </summary>
    Task<Stream> OpenWriteAsync(string location, CancellationToken ct = default);
}

/// <summary>
/// The two reads a streaming engine needs beyond the platform's forward-only <see cref="IFileStore.OpenReadAsync"/>:
/// a seekable stream (parquet reads its footer first and then row groups on demand, so a seekable blob stream
/// avoids buffering the whole file) and a byte range (one document out of a work batch, without the rest).
/// </summary>
public interface IFileReader
{
    bool CanHandle(string location);

    Task<Stream> OpenSeekableAsync(FileRef file, CancellationToken ct = default);

    Task<Stream> OpenRangeAsync(string path, long offset, long length, CancellationToken ct = default);
}

/// <summary>
/// Picks the store (and the writer and reader) for a location: the platform's local and Azure blob stores for
/// listings and forward reads, the matching writers and readers for the few writes and the seekable and range
/// reads. One registry per host, shared by the payload files, the work batches and the retrievals.
/// </summary>
public sealed class FileStoreRegistry
{
    private readonly IReadOnlyList<IFileStore> _stores;
    private readonly IReadOnlyList<IFileWriter> _writers;
    private readonly IReadOnlyList<IFileReader> _readers;

    public FileStoreRegistry(IEnumerable<IFileStore> stores, IEnumerable<IFileWriter> writers, IEnumerable<IFileReader>? readers = null)
    {
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(writers);
        _stores = stores.ToList();
        _writers = writers.ToList();
        _readers = (readers ?? []).ToList();
    }

    /// <summary>Joins a name onto a location, whichever store the location belongs to (a URI gets '/', a local path the platform separator).</summary>
    public static string Join(string root, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return root.Contains("://", StringComparison.Ordinal) ? root.TrimEnd('/') + "/" + name : Path.Combine(root, name);
    }

    /// <summary>
    /// The store for a location. A location that does not exist lists as empty rather than failing: the callers (a
    /// payload listing, a work batch read) turn "nothing there" into their own precise message, exactly as a blob prefix
    /// with no blobs already does.
    /// </summary>
    public IFileStore For(string location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        var store = _stores.FirstOrDefault(s => s.CanHandle(location))
            ?? throw new DeliveryException($"No file store handles location '{location}'. Use a local path, or an abfss:// or https://<account>.blob.core.windows.net URI.");
        return new AbsentAsEmptyStore(store);
    }

    public Task WriteAsync(string location, Stream content, CancellationToken ct = default)
        => Writer(location).WriteAsync(location, content, ct);

    public Task<Stream> OpenWriteAsync(string location, CancellationToken ct = default)
        => Writer(location).OpenWriteAsync(location, ct);

    /// <summary>
    /// A seekable stream over a file: the reader's own when one is registered for the location, otherwise the
    /// store's forward stream spilled to a temporary file (disk, never memory).
    /// </summary>
    public async Task<Stream> OpenSeekableAsync(FileRef file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (_readers.FirstOrDefault(r => r.CanHandle(file.Path)) is { } reader)
        {
            return await reader.OpenSeekableAsync(file, ct).ConfigureAwait(false);
        }

        var forward = await For(file.Path).OpenReadAsync(file, ct).ConfigureAwait(false);
        return await ParquetFiles.EnsureSeekableAsync(forward, ct).ConfigureAwait(false);
    }

    /// <summary>A byte range of a file (one document out of a work batch). Falls back to a forward read and a skip.</summary>
    public async Task<Stream> OpenRangeAsync(string path, long offset, long length, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (_readers.FirstOrDefault(r => r.CanHandle(path)) is { } reader)
        {
            return await reader.OpenRangeAsync(path, offset, length, ct).ConfigureAwait(false);
        }

        var forward = await For(path).OpenReadAsync(new FileRef { Path = path, Name = Path.GetFileName(path) }, ct).ConfigureAwait(false);
        try
        {
            await SkipAsync(forward, offset, ct).ConfigureAwait(false);
            return new BoundedReadStream(forward, length);
        }
        catch
        {
            await forward.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task SkipAsync(Stream stream, long count, CancellationToken ct)
    {
        if (stream.CanSeek)
        {
            stream.Position = count;
            return;
        }

        var buffer = new byte[1 << 16];
        while (count > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new DeliveryException("The stream ended before the requested range.");
            }

            count -= read;
        }
    }

    private IFileWriter Writer(string location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        return _writers.FirstOrDefault(w => w.CanHandle(location))
            ?? throw new DeliveryException($"No file writer handles location '{location}'. Snapshots, work batches and known-state publications go to a local path or an Azure Storage URI.");
    }
}

/// <summary>A store whose listing of a local path that is not there is empty (the platform store reports it as an error).</summary>
internal sealed class AbsentAsEmptyStore : IFileStore
{
    private readonly IFileStore _inner;

    public AbsentAsEmptyStore(IFileStore inner)
    {
        _inner = inner;
    }

    public bool CanHandle(string location) => _inner.CanHandle(location);

    public Task<IReadOnlyList<FileRef>> ListAsync(string location, FileDiscovery discovery, CancellationToken ct = default)
        => IsAbsentLocalPath(location)
            ? Task.FromResult<IReadOnlyList<FileRef>>([])
            : _inner.ListAsync(location, discovery, ct);

    public Task<Stream> OpenReadAsync(FileRef file, CancellationToken ct = default) => _inner.OpenReadAsync(file, ct);

    private static bool IsAbsentLocalPath(string location)
        => !location.Contains("://", StringComparison.Ordinal) && !File.Exists(location) && !Directory.Exists(location);
}

/// <summary>Reads at most <c>length</c> bytes of an inner stream, then reports end of stream; disposes the inner stream.</summary>
internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private long _remaining;

    public BoundedReadStream(Stream inner, long length)
    {
        _inner = inner;
        _remaining = length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var read = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
        _remaining -= read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var read = await _inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
        _remaining -= read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Writes to the local and UNC file system, atomically (write to a sibling temp file, then move into place).</summary>
public sealed class LocalFileWriter : IFileWriter
{
    private readonly LocalFileStore _store = new();

    public bool CanHandle(string location) => _store.CanHandle(location);

    public async Task WriteAsync(string location, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        await using var target = await OpenWriteAsync(location, ct).ConfigureAwait(false);
        await content.CopyToAsync(target, ct).ConfigureAwait(false);
    }

    public Task<Stream> OpenWriteAsync(string location, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        var directory = Path.GetDirectoryName(location);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = location + ".tmp";
        var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
        return Task.FromResult<Stream>(new MoveOnDisposeStream(stream, temp, location));
    }

    /// <summary>The temp file becomes the target when the stream closes, so a reader never sees a half-written file.</summary>
    private sealed class MoveOnDisposeStream : Stream
    {
        private readonly FileStream _inner;
        private readonly string _temp;
        private readonly string _target;
        private bool _done;

        public MoveOnDisposeStream(FileStream inner, string temp, string target)
        {
            _inner = inner;
            _temp = temp;
            _target = target;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _inner.Length;

        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_done)
            {
                _done = true;
                _inner.Dispose();
                File.Move(_temp, _target, overwrite: true);
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_done)
            {
                _done = true;
                await _inner.DisposeAsync().ConfigureAwait(false);
                File.Move(_temp, _target, overwrite: true);
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Seekable and range reads over the local file system.</summary>
public sealed class LocalFileReader : IFileReader
{
    private readonly LocalFileStore _store = new();

    public bool CanHandle(string location) => _store.CanHandle(location);

    public Task<Stream> OpenSeekableAsync(FileRef file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return Task.FromResult<Stream>(new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous));
    }

    public Task<Stream> OpenRangeAsync(string path, long offset, long length, CancellationToken ct = default)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
        stream.Position = offset;
        return Task.FromResult<Stream>(new BoundedReadStream(stream, length));
    }
}

/// <summary>
/// Writes to Azure Storage through SQLFlow's <see cref="AzureStorageAccess"/>, the same access and credential the
/// platform's <see cref="AzureBlobFileStore"/> reads with: a snapshot store, a work location or a known-state location
/// on the lake needs no second identity, and is reached over the account's Blob or Data Lake endpoint, whichever this
/// host can reach.
/// </summary>
public sealed class AzureBlobFileWriter : IFileWriter
{
    /// <summary>The size of one block (Blob endpoint) or append (Data Lake endpoint) of a streamed write.</summary>
    private const int BufferSize = 4 * 1024 * 1024;

    private readonly AzureStorageAccess _storage;

    public AzureBlobFileWriter(AzureStorageAccess storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public bool CanHandle(string location) => AzureBlobLocation.IsAzureStorageUri(location);

    public async Task WriteAsync(string location, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var parsed = AzureBlobLocation.Parse(location);
        try
        {
            await _storage.Container(parsed).UploadAsync(parsed.BlobPath, content, overwrite: true, contentHash: null, ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex)
        {
            throw new DeliveryException($"Could not write '{location}'{Over(ex)} (status {ex.Status}): {ex.Message}", ex);
        }
    }

    /// <summary>Written in 4 MB parts as bytes arrive; committed when the stream is disposed.</summary>
    public async Task<Stream> OpenWriteAsync(string location, CancellationToken ct = default)
    {
        var parsed = AzureBlobLocation.Parse(location);
        try
        {
            return await _storage.Container(parsed).OpenWriteAsync(parsed.BlobPath, BufferSize, ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex)
        {
            throw new DeliveryException($"Could not open '{location}' for writing{Over(ex)} (status {ex.Status}): {ex.Message}", ex);
        }
    }

    /// <summary>" over the dfs endpoint" (or blob) when the storage layer recorded where the failed request went.</summary>
    internal static string Over(Exception ex)
        => AzureStorageFailures.EndpointOf(ex) is { } endpoint ? $" over the {endpoint.HostLabel()} endpoint" : string.Empty;
}

/// <summary>
/// Seekable and range reads over Azure Storage: a seekable stream fetches ranges on demand (parquet footers and row
/// groups without the rest of the file), and a range read fetches one slice of a work batch. Both go through SQLFlow's
/// <see cref="AzureStorageAccess"/>, over the account's Blob or Data Lake endpoint, whichever this host can reach.
/// </summary>
public sealed class AzureBlobFileReader : IFileReader
{
    private const int BufferSize = 4 * 1024 * 1024;

    private readonly AzureStorageAccess _storage;

    public AzureBlobFileReader(AzureStorageAccess storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    public bool CanHandle(string location) => AzureBlobLocation.IsAzureStorageUri(location);

    public async Task<Stream> OpenSeekableAsync(FileRef file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var parsed = AzureBlobLocation.Parse(file.Path);
        try
        {
            return await _storage.Container(parsed).OpenSeekableReadAsync(parsed.BlobPath, BufferSize, ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex)
        {
            throw new DeliveryException($"Could not open '{file.Path}'{AzureBlobFileWriter.Over(ex)} (status {ex.Status}): {ex.Message}", ex);
        }
    }

    public async Task<Stream> OpenRangeAsync(string path, long offset, long length, CancellationToken ct = default)
    {
        var parsed = AzureBlobLocation.Parse(path);
        try
        {
            return await _storage.Container(parsed).OpenReadAsync(parsed.BlobPath, offset, length, ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex)
        {
            throw new DeliveryException($"Could not read '{path}' at {offset}{AzureBlobFileWriter.Over(ex)} (status {ex.Status}): {ex.Message}", ex);
        }
    }
}
