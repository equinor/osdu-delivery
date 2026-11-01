using SqlFlow.Core.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Storage;

/// <summary>
/// Where one record's payload files are listed from: a folder (a local path or a storage URI) and the file name glob
/// under it. Stored on a record as one text, the folder and the pattern joined, which <see cref="Parse"/> splits again.
/// </summary>
public readonly record struct PayloadLocation(string Folder, string Pattern)
{
    /// <summary>The folder and the pattern as one location: the pattern is the last segment.</summary>
    public override string ToString() => FileStoreRegistry.Join(Folder, Pattern);

    /// <summary>Splits a stored location into its folder and its pattern; throws <see cref="DeliveryException"/> when it has no folder.</summary>
    public static PayloadLocation Parse(string stored)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stored);
        var cut = stored.LastIndexOfAny(['/', '\\']);
        if (cut <= 0 || cut == stored.Length - 1)
        {
            throw new DeliveryException($"'{stored}' is not a payload location: it names a folder and a file pattern, separated by '/'.");
        }

        return new PayloadLocation(stored[..cut], stored[(cut + 1)..]);
    }
}

/// <summary>Lists and opens payload files wherever they sit, with the node's own identity.</summary>
public interface IPayloadFiles
{
    /// <summary>The files directly under <paramref name="folder"/> that match <paramref name="pattern"/>, in file name order.</summary>
    Task<IReadOnlyList<PayloadFile>> ListAsync(string folder, string pattern, CancellationToken ct = default);

    /// <summary>A fresh forward stream over one file.</summary>
    Task<Stream> OpenAsync(PayloadFile file, CancellationToken ct = default);

    /// <summary>Where another storage account can read one file from itself; null when none can (<see cref="IPayloadSource.CopySourceAsync"/>).</summary>
    Task<PayloadCopySource?> CopySourceAsync(PayloadFile file, DateTimeOffset? until, CancellationToken ct = default) => Task.FromResult<PayloadCopySource?>(null);
}

/// <summary>The payload files over the host's file stores: local and UNC paths, and Azure Storage.</summary>
public sealed class StoragePayloadFiles : IPayloadFiles
{
    private readonly FileStoreRegistry _stores;

    public StoragePayloadFiles(FileStoreRegistry stores)
    {
        ArgumentNullException.ThrowIfNull(stores);
        _stores = stores;
    }

    public async Task<IReadOnlyList<PayloadFile>> ListAsync(string folder, string pattern, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        var files = await _stores.For(folder).ListAsync(folder, new FileDiscovery { Pattern = pattern }, ct).ConfigureAwait(false);
        return files
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select((f, i) => new PayloadFile(i, f.Path, f.Size, f.Modified))
            .ToList();
    }

    public Task<Stream> OpenAsync(PayloadFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return _stores.For(file.Path).OpenReadAsync(new FileRef { Path = file.Path, Name = System.IO.Path.GetFileName(file.Path), Size = file.Size }, ct);
    }

    public Task<PayloadCopySource?> CopySourceAsync(PayloadFile file, DateTimeOffset? until, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return _stores.SignReadAsync(file.Path, until, ct);
    }
}
