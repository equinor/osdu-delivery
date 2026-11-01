using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Text;
using System.Web;
using System.Xml.Linq;
using SqlFlow.Delivery.Engine.Protocols;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The block blob side of a signed staging location, as real Azure Storage answered it under a <c>sp=cw</c> SAS: blocks
/// staged by id, of one length per blob, each checked against its <c>Content-MD5</c>; a list committed, which is what makes
/// the blob; a block read from another account by Put Block From URL, or refused with <c>CannotVerifyCopySource</c> when
/// that account may not be read; and one <c>PUT</c> of more than Put Blob holds refused with <c>RequestBodyTooLarge</c>.
/// </summary>
public sealed partial class FakeOsduPlatform
{
    private readonly Dictionary<string, Dictionary<string, byte[]>> _blocks = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Path, string Id), int> _blockAttempts = [];
    private readonly Dictionary<long, int> _appendAttempts = [];

    /// <summary>What a committed blob was given, by path.</summary>
    public Dictionary<string, (string? ContentType, string? Md5)> Committed { get; } = new(StringComparer.Ordinal);

    /// <summary>The block ids a blob was last committed from, in order, by path.</summary>
    public Dictionary<string, List<string>> CommittedBlocks { get; } = new(StringComparer.Ordinal);

    /// <summary>The most one <c>PUT</c> of a whole blob holds: 5000 MiB on Azure, lowered to show what a larger file meets.</summary>
    public long MaxPutBlobBytes { get; set; } = 5000L * 1024 * 1024;

    /// <summary>Whether the File service's landing zone is a blob SAS, as it is on Azure, rather than a location of another store.</summary>
    public bool LandingIsAzureBlob { get; set; }

    /// <summary>The end a blob SAS states, when it states one.</summary>
    public string? SignedUntil { get; set; }

    /// <summary>Whether the blob SAS has run out: the store refuses it as it refuses a signature past its end.</summary>
    public bool SignedExpired { get; set; }

    /// <summary>The files another account holds, by their URL without a signature, for Put Block From URL to read.</summary>
    public Dictionary<string, byte[]> CopySources { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether the account holding a copy source refuses the store, as a firewall or a private endpoint does.</summary>
    public bool CopyRefused { get; set; }

    /// <summary>The status a block is answered with instead, by its index and how many times it was tried.</summary>
    public Func<int, int, HttpStatusCode?>? BlockFails { get; set; }

    /// <summary>The status a Data Lake append is answered with instead, by its position and how many times it was tried.</summary>
    public Func<long, int, HttpStatusCode?>? AppendFails { get; set; }

    private string BlobSas => "sv=2021-06-08&sr=b&sp=cw" + (SignedUntil is { } end ? "&se=" + Uri.EscapeDataString(end) : string.Empty) + "&sig=secret";

    private HttpResponseMessage? AppendFailure(long position)
    {
        var attempt = _appendAttempts[position] = _appendAttempts.GetValueOrDefault(position) + 1;
        return AppendFails?.Invoke(position, attempt) is { } status ? StoreError(status, "InternalError", "The server encountered an internal error.") : null;
    }

    private HttpResponseMessage? BlobOperation(string method, string path, NameValueCollection query, byte[] bytes, HttpRequestMessage request)
    {
        if (method != "PUT" || query["sr"] != "b")
        {
            return null;
        }

        if (SignedExpired)
        {
            return StoreError(HttpStatusCode.Forbidden, "AuthenticationFailed", "Server failed to authenticate the request. Signed expiry time must be after signed start time");
        }

        return query["comp"] switch
        {
            "block" => PutBlock(path, query["blockid"]!, bytes, request),
            "blocklist" => PutBlockList(path, bytes, request),
            _ => bytes.LongLength > MaxPutBlobBytes ? StoreError(HttpStatusCode.RequestEntityTooLarge, "RequestBodyTooLarge", "The request body is too large and exceeds the maximum permissible limit.") : null,
        };
    }

    private HttpResponseMessage PutBlock(string path, string id, byte[] bytes, HttpRequestMessage request)
    {
        if (!request.Headers.Contains("x-ms-version"))
        {
            return StoreError(HttpStatusCode.BadRequest, "MissingRequiredHeader", "x-ms-version");
        }

        if (!_blocks.TryGetValue(path, out var blocks))
        {
            blocks = _blocks[path] = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        }

        if (blocks.Count > 0 && blocks.Keys.First().Length != id.Length)
        {
            return StoreError(HttpStatusCode.BadRequest, "InvalidBlobOrBlock", "The specified blob or block content is invalid.");
        }

        var attempt = _blockAttempts[(path, id)] = _blockAttempts.GetValueOrDefault((path, id)) + 1;
        var index = int.Parse(Encoding.ASCII.GetString(Convert.FromBase64String(id)), CultureInfo.InvariantCulture);
        if (BlockFails?.Invoke(index, attempt) is { } failing)
        {
            return StoreError(failing, "InternalError", "The server encountered an internal error.");
        }

        byte[] block;
        if (request.Headers.TryGetValues("x-ms-copy-source", out var sources))
        {
            if (bytes.Length != 0)
            {
                return StoreError(HttpStatusCode.BadRequest, "InvalidHeaderValue", "Content-Length must be 0");
            }

            var source = new Uri(sources.Single());
            if (CopyRefused || HttpUtility.ParseQueryString(source.Query)["sig"] is null)
            {
                return StoreError(HttpStatusCode.Forbidden, "CannotVerifyCopySource", "This request is not authorized to perform this operation.");
            }

            if (!CopySources.TryGetValue(source.GetLeftPart(UriPartial.Path), out var whole))
            {
                return StoreError(HttpStatusCode.NotFound, "CannotVerifyCopySource", "The specified blob does not exist.");
            }

            var range = request.Headers.GetValues("x-ms-source-range").Single()["bytes=".Length..].Split('-');
            var from = long.Parse(range[0], CultureInfo.InvariantCulture);
            var to = long.Parse(range[1], CultureInfo.InvariantCulture);
            block = whole[(int)from..(int)(to + 1)];
        }
        else
        {
            if (request.Content?.Headers.ContentMD5 is { } md5 && !md5.SequenceEqual(ObjectBody.Md5Of(bytes)))
            {
                return StoreError(HttpStatusCode.BadRequest, "Md5Mismatch", "The MD5 value specified in the request did not match with the MD5 value calculated by the server.");
            }

            block = bytes;
        }

        blocks[id] = block;
        return new HttpResponseMessage(HttpStatusCode.Created);
    }

    private HttpResponseMessage PutBlockList(string path, byte[] bytes, HttpRequestMessage request)
    {
        var ids = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements("Latest").Select(e => e.Value).ToList();
        if (!_blocks.TryGetValue(path, out var blocks) || ids.Any(id => !blocks.ContainsKey(id)))
        {
            return StoreError(HttpStatusCode.BadRequest, "InvalidBlockList", "The specified block list is invalid.");
        }

        Staged[path] = ids.SelectMany(id => blocks[id]).ToArray();
        CommittedBlocks[path] = ids;
        Committed[path] = (Header(request, "x-ms-blob-content-type"), Header(request, "x-ms-blob-content-md5"));
        return new HttpResponseMessage(HttpStatusCode.Created);
    }

    private static string? Header(HttpRequestMessage request, string name)
        => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private static HttpResponseMessage StoreError(HttpStatusCode status, string code, string message)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent($"<Error><Code>{code}</Code><Message>{message}</Message></Error>", Encoding.UTF8, "application/xml") };
        response.Headers.TryAddWithoutValidation("x-ms-error-code", code);
        return response;
    }
}
