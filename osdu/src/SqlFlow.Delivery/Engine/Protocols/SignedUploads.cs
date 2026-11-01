using System.Globalization;
using System.Web;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine.Protocols.Ddms;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>How one file reached a signed location, as the attempt's step keeps it.</summary>
/// <param name="How"><see cref="Whole"/>, <see cref="Streamed"/> or <see cref="ServerCopied"/>.</param>
/// <param name="Blocks">The blocks it went in.</param>
/// <param name="Md5">The file's MD5 in hex, when it is known.</param>
/// <param name="NotCopied">Why the store did not copy it from where it sits, when a copy was tried or could not be.</param>
/// <param name="Status">What the last request was answered with.</param>
internal sealed record SignedTransfer(string How, int Blocks, string? Md5, string? NotCopied, int Status)
{
    /// <summary>One request carrying the whole file.</summary>
    public const string Whole = "single";

    /// <summary>In blocks this process read and sent.</summary>
    public const string Streamed = "blocks";

    /// <summary>In blocks the store read from the file's own account.</summary>
    public const string ServerCopied = "serverCopy";

    public void AddTo(IDictionary<string, string> returned)
    {
        ArgumentNullException.ThrowIfNull(returned);
        returned["transfer"] = How;
        if (How != Whole)
        {
            returned["blocks"] = Blocks.ToString(CultureInfo.InvariantCulture);
        }

        if (Md5 is not null)
        {
            returned["md5"] = Md5;
        }

        if (NotCopied is not null)
        {
            returned["notCopied"] = NotCopied;
        }
    }
}

/// <summary>
/// A file put at a location a service signed (osdu/specs/core/INTEGRATION.md section 2.5.1). On Azure the location is a SAS
/// for one block blob, and one <c>PUT</c> holds at most 5000 MiB, so a file above <see cref="ProtocolOptions.UploadBlockMiB"/>
/// goes in blocks and a commit, as Microsoft's own guidance for Azure Data Manager for Energy has azcopy do. When the file
/// sits in Azure Storage the store reads the blocks from it itself, under a read signature this process signs for that one
/// file; a store that may not read it (a firewall, a private endpoint) answers on the first block, and the file is read here
/// and sent instead. Any other location takes one <c>PUT</c>.
/// </summary>
internal static class SignedUploads
{
    /// <summary>The oldest service version Put Block From URL takes blocks above 100 MiB under, and the one every request states at least.</summary>
    private const string MinimumVersion = "2020-04-08";

    /// <summary>How much of a refusal the attempt's step keeps.</summary>
    private const int MaxReason = 300;

    private const long MiB = 1024 * 1024;

    /// <summary>
    /// Sends <paramref name="file"/> to <paramref name="signed"/> and says how it went. <paramref name="wantsMd5"/> says the
    /// service registering the file reads the blob's content MD5, and computes it from the blob when it has none (the File
    /// service, within its request): a file with no MD5 of its own is then read here, which gives it one.
    /// </summary>
    public static async Task<SignedTransfer> PutAsync(
        OsduHttpClient client, ProtocolOptions options, Uri signed, IPayloadSource source, PayloadFile file, long requestBodyCeiling, bool wantsMd5, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(signed);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(file);
        var blockBytes = TakesBlocks(signed, options.UploadHeaders) ? BlockBytes(file, options.UploadBlockMiB, requestBodyCeiling) : long.MaxValue;
        if (file.Size <= blockBytes)
        {
            FileUploads.CheckCeiling(file, requestBodyCeiling);
            var result = await client.SendToSignedUrlAsync(
                HttpMethod.Put,
                signed,
                () => OsduDdmsProtocol.OpenSync(source, file),
                options.PayloadContentType,
                file.Size,
                FileUploads.SignedUploadHeaders(signed, options.UploadHeaders),
                ct).ConfigureAwait(false);
            return new SignedTransfer(SignedTransfer.Whole, 1, null, null, (int)result.Status);
        }

        var blob = Blob(client, signed);
        try
        {
            string? notCopied = null;
            if (options.UploadServerCopy)
            {
                var (copied, reason) = await TryCopyAsync(blob, options, signed, source, file, blockBytes, wantsMd5, ct).ConfigureAwait(false);
                if (copied is not null)
                {
                    return copied;
                }

                notCopied = reason;
            }

            await using var reader = await PartReader.OpenAsync(source, file, ct).ConfigureAwait(false);
            var blocks = await blob.SendAsync(file.Size, reader, (int)blockBytes, options.UploadConcurrency, ct).ConfigureAwait(false);
            await reader.EndAsync(ct).ConfigureAwait(false);
            var md5 = reader.Md5();
            var committed = await blob.CommitAsync(blocks, CommitHeaders(options, md5), ct).ConfigureAwait(false);
            return new SignedTransfer(SignedTransfer.Streamed, blocks, Convert.ToHexStringLower(md5), notCopied, (int)committed.Status);
        }
        catch (OsduStatusException ex) when (ex.StatusCode == 403 && Expiry(signed) is { } expired && expired <= DateTimeOffset.UtcNow)
        {
            throw new DeliveryException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The signed location ran out at {expired:u} before {FileUploads.FileName(file.Path)} ({file.Size} bytes) was all sent; a location cannot be extended, so the next try asks for a new one and starts the file again. The Dataset service's locations last one hour whatever is asked, so 16 GiB needs 37 Mbit/s or more: raise target.protocolOptions.uploadConcurrency, or send it from a node nearer the storage."),
                ex);
        }
    }

    /// <summary>
    /// The bytes one block of <paramref name="file"/> holds: the flow's size, no more than a request may carry, and more
    /// where the 50,000 blocks of a blob need it.
    /// </summary>
    public static long BlockBytes(PayloadFile file, int blockMiB, long requestBodyCeiling)
    {
        ArgumentNullException.ThrowIfNull(file);
        var size = blockMiB * MiB;
        if (requestBodyCeiling > 0 && requestBodyCeiling < size)
        {
            size = requestBodyCeiling;
        }

        var needed = (file.Size + BlockBlob.MaxBlocks - 1) / BlockBlob.MaxBlocks;
        if (needed <= size)
        {
            return size;
        }

        if (requestBodyCeiling > 0 && requestBodyCeiling < needed)
        {
            throw new RecordHeldException(string.Create(
                CultureInfo.InvariantCulture,
                $"payload chunk {file.Index} is {file.Size} bytes, more than {BlockBlob.MaxBlocks} blocks of the target's declared request body ceiling of {requestBodyCeiling} bytes hold (reliability.maxRequestBodyBytes)"));
        }

        return (needed + MiB - 1) / MiB * MiB;
    }

    /// <summary>Whether a signed location takes blocks: an Azure blob, unless the flow declares it another kind of blob.</summary>
    public static bool TakesBlocks(Uri signed, IReadOnlyDictionary<string, string> declared)
    {
        ArgumentNullException.ThrowIfNull(signed);
        ArgumentNullException.ThrowIfNull(declared);
        return IsAzureBlob(signed)
               && (!declared.TryGetValue(FileUploads.AzureBlobTypeHeader, out var type) || string.Equals(type, "BlockBlob", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a signed location is an Azure blob: its host is a Blob endpoint in any Azure cloud (<c>.blob.core.</c>) or an
    /// account in an Azure DNS zone (<c>.blob.storage.azure.net</c>), or its query is a SAS for one blob, whatever the host
    /// (a custom domain, a private name).
    /// </summary>
    public static bool IsAzureBlob(Uri signed)
    {
        ArgumentNullException.ThrowIfNull(signed);
        if (signed.Host.Contains(".blob.core.", StringComparison.OrdinalIgnoreCase)
            || signed.Host.EndsWith(".blob.storage.azure.net", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var query = HttpUtility.ParseQueryString(signed.Query);
        return query["sr"] == "b" && query["sv"] is not null && query["sig"] is not null;
    }

    /// <summary>When a SAS says it ends, or null when it says nothing the service would read.</summary>
    public static DateTimeOffset? Expiry(Uri signed)
    {
        ArgumentNullException.ThrowIfNull(signed);
        return DateTimeOffset.TryParse(HttpUtility.ParseQueryString(signed.Query)["se"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var end)
            ? end
            : null;
    }

    /// <summary>
    /// Has the store copy the file block by block, with the first block on its own: a store that may not read the source
    /// refuses that one at once, and nothing else has been staged or spent. A 4xx there is a reason to read the file here,
    /// not a failure; what comes after the first block fails the upload as any request does.
    /// </summary>
    private static async Task<(SignedTransfer? Copied, string? Reason)> TryCopyAsync(
        BlockBlob blob, ProtocolOptions options, Uri signed, IPayloadSource source, PayloadFile file, long blockBytes, bool wantsMd5, CancellationToken ct)
    {
        PayloadCopySource? copy;
        try
        {
            copy = await source.CopySourceAsync(file, Expiry(signed), ct).ConfigureAwait(false);
        }
        catch (DeliveryException ex)
        {
            return (null, Reason(ex));
        }

        if (copy is null)
        {
            return (null, null);
        }

        if (wantsMd5 && copy.ContentMd5 is not { Length: > 0 })
        {
            return (null, "the file holds no content MD5, and the service registering it would read all of it to compute one");
        }

        if (copy.Length != file.Size)
        {
            throw new DeliveryException(string.Create(
                CultureInfo.InvariantCulture,
                $"The payload file {FileUploads.FileName(file.Path)} holds {copy.Length} bytes, and it was listed with {file.Size}; it changed before it was sent."));
        }

        try
        {
            await blob.CopyBlockAsync(copy.Url, 0, 0, blockBytes, ct).ConfigureAwait(false);
        }
        catch (OsduStatusException ex) when (ex.StatusCode is >= 400 and < 500)
        {
            return (null, Reason(ex));
        }

        var blocks = (int)((file.Size + blockBytes - 1) / blockBytes);
        await Parallel.ForEachAsync(
            Enumerable.Range(1, blocks - 1),
            new ParallelOptions { MaxDegreeOfParallelism = options.UploadConcurrency, CancellationToken = ct },
            async (index, token) =>
            {
                var offset = index * blockBytes;
                await blob.CopyBlockAsync(copy.Url, index, offset, Math.Min(blockBytes, file.Size - offset), token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        // A block read from a file that changed mid-copy would be committed with no error, so its version is compared before the commit.
        var after = await source.CopySourceAsync(file, Expiry(signed), ct).ConfigureAwait(false);
        if (!string.Equals(after?.ETag, copy.ETag, StringComparison.Ordinal))
        {
            throw new DeliveryException($"The payload file {FileUploads.FileName(file.Path)} changed while it was being copied; nothing was committed, and the next try copies it again.");
        }

        var committed = await blob.CommitAsync(blocks, CommitHeaders(options, copy.ContentMd5), ct).ConfigureAwait(false);
        var md5 = copy.ContentMd5 is { Length: > 0 } hash ? Convert.ToHexStringLower(hash) : null;
        return (new SignedTransfer(SignedTransfer.ServerCopied, blocks, md5, null, (int)committed.Status), null);
    }

    /// <summary>What a commit gives the blob: the flow's upload headers, the file's media type, and its MD5 when it is known.</summary>
    private static List<(string Name, string Value)> CommitHeaders(ProtocolOptions options, byte[]? md5)
    {
        var headers = options.UploadHeaders
            .Where(h => !string.Equals(h.Key, FileUploads.AzureBlobTypeHeader, StringComparison.OrdinalIgnoreCase))
            .Select(h => (h.Key, h.Value))
            .ToList();
        headers.Add(("x-ms-blob-content-type", options.PayloadContentType));
        if (md5 is { Length: > 0 })
        {
            headers.Add(("x-ms-blob-content-md5", Convert.ToBase64String(md5)));
        }

        return headers;
    }

    /// <summary>The blob a SAS signs, reached under it: neither the flow's auth nor its headers go with a request.</summary>
    private static BlockBlob Blob(OsduHttpClient client, Uri signed)
    {
        var stated = HttpUtility.ParseQueryString(signed.Query)["sv"] ?? BlockBlob.DefaultVersion;
        var version = string.CompareOrdinal(stated, MinimumVersion) < 0 ? MinimumVersion : stated;
        return new BlockBlob(
            (query, content, headers) =>
            {
                var request = new HttpRequestMessage(HttpMethod.Put, new UriBuilder(signed) { Query = query + "&" + signed.Query.TrimStart('?') }.Uri) { Content = content };
                request.Headers.TryAddWithoutValidation("x-ms-version", version);
                foreach (var (name, value) in headers)
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }

                return request;
            },
            (build, ct) => client.SendAsIsAsync(build, null, idempotent: true, ct));
    }

    private static string Reason(Exception ex)
    {
        var text = HeaderRedaction.RedactMessage(ex.Message);
        return text.Length <= MaxReason ? text : text[..MaxReason] + "...";
    }
}
