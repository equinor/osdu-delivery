using System.Globalization;
using System.Text;
using System.Xml.Linq;
using SqlFlow.Delivery.Engine.Protocols.Ddms;
using SqlFlow.Delivery.Http;
using static SqlFlow.Delivery.Engine.Protocols.ObjectBody;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// One Azure block blob written block by block: each block staged (Put Block, or Put Block From URL when the store reads it
/// from another account itself), then the list committed, which is what makes the blob appear. A block's id is its place,
/// so a repeated block replaces itself and a repeated commit finds the same blocks. The callers say how a request is built
/// and sent: under a signature for one blob (<see cref="SignedUploads"/>), or for a container (the Seismic Store route).
/// </summary>
/// <param name="request">Builds a <c>PUT</c> on the blob from its query, its body and its headers; runs for every attempt.</param>
/// <param name="send">Sends what the builder makes.</param>
internal sealed class BlockBlob(
    Func<string, HttpContent?, IReadOnlyList<(string Name, string Value)>, HttpRequestMessage> request,
    Func<Func<HttpRequestMessage>, CancellationToken, Task<HttpFetchResult>> send)
{
    /// <summary>The most blocks one block blob is committed from (Put Block List).</summary>
    public const int MaxBlocks = 50_000;

    /// <summary>The service version a request states when its SAS names none.</summary>
    public const string DefaultVersion = "2021-06-08";

    /// <summary>A block's id: its place in the blob, the same length for every block as the service requires.</summary>
    public static string Id(int index) => Convert.ToBase64String(Encoding.ASCII.GetBytes(index.ToString("D6", CultureInfo.InvariantCulture)));

    /// <summary>
    /// Stages <paramref name="length"/> bytes of <paramref name="reader"/> as blocks of at most
    /// <paramref name="blockBytes"/>, each with its <c>Content-MD5</c>, which the service checks; returns how many.
    /// </summary>
    public Task<int> SendAsync(long length, PartReader reader, int blockBytes, int concurrency, CancellationToken ct)
        => UploadParts.SendAsync(
            reader,
            length,
            blockBytes,
            concurrency,
            (index, _, part, token) =>
            {
                var checksum = Md5Of(part.Span);
                return send(() => request(Block(index), Bytes(part, checksum), []), token);
            },
            ct);

    /// <summary>
    /// Stages a block the store reads itself from <paramref name="source"/> (Put Block From URL): no byte of it passes
    /// through this process, and the call answers once the block is staged, so a commit that follows finds it.
    /// </summary>
    public Task CopyBlockAsync(Uri source, int index, long offset, long length, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        var range = string.Create(CultureInfo.InvariantCulture, $"bytes={offset}-{offset + length - 1}");
        return UploadParts.RepeatedAsync(
            () => send(() => request(Block(index), Bytes(ReadOnlyMemory<byte>.Empty, null), [("x-ms-copy-source", source.AbsoluteUri), ("x-ms-source-range", range)]), ct),
            ct);
    }

    /// <summary>
    /// Commits <paramref name="blocks"/> blocks, in order, with <paramref name="headers"/> as the blob's properties. Without a
    /// <c>x-ms-blob-content-md5</c> the blob holds no content MD5, whatever the blob it replaces held.
    /// </summary>
    public Task<HttpFetchResult> CommitAsync(int blocks, IReadOnlyList<(string Name, string Value)> headers, CancellationToken ct)
    {
        var list = new XElement("BlockList", Enumerable.Range(0, blocks).Select(index => new XElement("Latest", Id(index))));
        var body = Encoding.UTF8.GetBytes(new XDeclaration("1.0", "utf-8", null) + list.ToString(SaveOptions.DisableFormatting));
        return send(() => request("comp=blocklist", Xml(body), headers), ct);
    }

    private static string Block(int index) => "comp=block&blockid=" + Uri.EscapeDataString(Id(index));
}
