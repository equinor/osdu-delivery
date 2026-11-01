using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Storage;

/// <summary>
/// One record's payload files as a protocol streams them: listed once from the record's payload location, each opened as
/// a fresh stream every time it is asked for, so a retried request re-reads the file rather than a spent stream.
/// </summary>
public sealed class StoragePayloadSource : IPayloadSource
{
    private readonly IPayloadFiles _files;
    private readonly PayloadLocation _location;
    private IReadOnlyList<PayloadFile>? _listed;

    public StoragePayloadSource(IPayloadFiles files, PayloadLocation location)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(location.Folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(location.Pattern);
        _files = files;
        _location = location;
    }

    public async Task<IReadOnlyList<PayloadFile>> ListChunksAsync(CancellationToken ct = default)
        => _listed ??= await _files.ListAsync(_location.Folder, _location.Pattern, ct).ConfigureAwait(false);

    public Task<Stream> OpenAsync(PayloadFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return _files.OpenAsync(file, ct);
    }

    public Task<PayloadCopySource?> CopySourceAsync(PayloadFile file, DateTimeOffset? until, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return _files.CopySourceAsync(file, until, ct);
    }
}
