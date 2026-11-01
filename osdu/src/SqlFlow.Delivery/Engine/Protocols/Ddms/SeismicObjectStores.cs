using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SqlFlow.Delivery.Hashing;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Protocols;
using static SqlFlow.Delivery.Engine.Protocols.ObjectBody;

namespace SqlFlow.Delivery.Engine.Protocols.Ddms;

/// <summary>
/// A payload file read front to back in parts, every byte hashed as it is read: the MD5 Seismic Store's clients keep for
/// a file, and which Azure keeps as each object's content MD5. A part stays valid until the next read.
/// </summary>
internal sealed class PartReader : IAsyncDisposable
{
    private const int SkipBytes = 1 << 20;

    private readonly Stream _stream;
    private readonly IncrementalHash _md5;
    private readonly string _name;
    private byte[] _buffer = [];

    private PartReader(Stream stream, PayloadFile file)
    {
        _stream = stream;
        _name = FileUploads.FileName(file.Path);
        Length = file.Size;
        _md5 = CreateMd5();
    }

    /// <summary>The bytes read so far.</summary>
    public long Position { get; private set; }

    /// <summary>The bytes the file was listed with.</summary>
    public long Length { get; }

    public static async Task<PartReader> OpenAsync(IPayloadSource source, PayloadFile file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(file);
        var stream = await source.OpenAsync(file, ct).ConfigureAwait(false);
        try
        {
            return new PartReader(stream, file);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The next <paramref name="count"/> bytes, which the file must still hold.</summary>
    public async Task<ReadOnlyMemory<byte>> ReadAsync(int count, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_buffer.Length < count)
        {
            _buffer = new byte[count];
        }

        await ReadIntoAsync(_buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        return _buffer.AsMemory(0, count);
    }

    /// <summary>Fills <paramref name="target"/> with the next bytes, which the file must still hold.</summary>
    public async Task ReadIntoAsync(Memory<byte> target, CancellationToken ct)
    {
        var read = await _stream.ReadAtLeastAsync(target, target.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        if (read < target.Length)
        {
            throw new DeliveryException(string.Create(
                CultureInfo.InvariantCulture,
                $"The payload file {_name} ended after {Position + read} bytes, and it was listed with {Length}; it changed while it was being sent."));
        }

        _md5.AppendData(target.Span);
        Position += target.Length;
    }

    /// <summary>Reads past <paramref name="count"/> bytes an earlier try sent, hashing them as if they were sent again.</summary>
    public async Task SkipAsync(long count, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        while (count > 0)
        {
            var part = (int)Math.Min(SkipBytes, count);
            await ReadAsync(part, ct).ConfigureAwait(false);
            count -= part;
        }
    }

    /// <summary>Checks the file ends where it was listed to.</summary>
    public async Task EndAsync(CancellationToken ct)
    {
        var probe = new byte[1];
        if (await _stream.ReadAsync(probe, ct).ConfigureAwait(false) > 0)
        {
            throw new DeliveryException(string.Create(
                CultureInfo.InvariantCulture,
                $"The payload file {_name} holds more than the {Length} bytes it was listed with; it changed while it was being sent."));
        }
    }

    /// <summary>The MD5 of everything read so far.</summary>
    public byte[] Md5() => _md5.GetCurrentHash();

    public async ValueTask DisposeAsync()
    {
        _md5.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "The MD5 Seismic Store's clients keep, and Azure's content MD5, are integrity checksums the stores define; no security rests on them.")]
    private static IncrementalHash CreateMd5() => IncrementalHash.CreateHash(HashAlgorithmName.MD5);
}

/// <summary>What Seismic Store answers for upload credentials (<c>AccessToken</c>): a SAS URL, a bearer token or a key triple, by provider.</summary>
/// <param name="TokenType">The service's <c>token_type</c>: <c>SasUrl</c> or <c>Bearer</c>.</param>
/// <param name="AccessToken">The credential itself; never logged, stored or returned.</param>
internal sealed record SeismicCredential(string TokenType, string AccessToken)
{
    /// <summary>A credential never shows in a log or an attempt.</summary>
    public override string ToString() => $"({TokenType} credential)";
}

/// <summary>
/// An object store a Seismic Store dataset's objects go to, with the credentials the service issued for the dataset
/// (osdu/specs/seismic-ddms/INTEGRATION.md sections 4.3 and 4.4). Every request goes through the delivery's reliability
/// stack without the flow's auth or headers, and nothing an attempt records names a credential. A request the store
/// refuses because the credential expired (401, 403, or S3's <c>ExpiredToken</c>) is sent once more with a credential
/// the service issues afresh, so a long upload outlives the lifetime of any one credential.
/// </summary>
internal abstract class SeismicObjectStore(OsduHttpClient client, Func<CancellationToken, Task<SeismicCredential>> renew)
{
    /// <summary>The smallest part S3 takes, other than the last; an object up to the part size, and never smaller than this, goes in one request.</summary>
    public const long MinS3PartBytes = 5L * 1024 * 1024;

    /// <summary>The most parts an S3 upload takes.</summary>
    public const int MaxS3Parts = 10_000;

    /// <summary>What Google Cloud Storage takes a resumable upload's pieces in multiples of.</summary>
    public const int GooglePieceQuantum = 256 * 1024;

    /// <summary>How many times one request is sent with a renewed credential before its refusal stands.</summary>
    private const int Renewals = 1;

    /// <summary>How the attempt names the store: its kind and place, never a credential.</summary>
    public abstract string Describe { get; }

    /// <summary>
    /// Writes the object <paramref name="key"/> (under the dataset's location) of <paramref name="length"/> bytes, read
    /// from <paramref name="reader"/> in pieces of at most <paramref name="partBytes"/>; <paramref name="md5"/> gives the
    /// MD5 the store is to keep for it once its last byte is read.
    /// </summary>
    public abstract Task PutAsync(string key, long length, PartReader reader, int partBytes, Func<byte[]> md5, CancellationToken ct);

    /// <summary>Removes the object <paramref name="key"/>; one that is not there is removed already.</summary>
    public abstract Task DeleteAsync(string key, CancellationToken ct);

    /// <summary>Takes a credential the service issued afresh; throws when it is for another place than this store's.</summary>
    protected abstract void Accept(SeismicCredential credential);

    /// <summary>
    /// Sends a request built from the current credential, and once more with a renewed one when the store refuses the
    /// credential. <paramref name="build"/> runs for every attempt.
    /// </summary>
    protected async Task<HttpFetchResult> SendAsync(Func<HttpRequestMessage> build, IReadOnlySet<int>? allow, bool idempotent, CancellationToken ct)
    {
        for (var renewed = 0; ; renewed++)
        {
            try
            {
                return await client.SendAsIsAsync(build, allow, idempotent, ct).ConfigureAwait(false);
            }
            catch (OsduStatusException ex) when (renewed < Renewals && Expired(ex))
            {
                Accept(await renew(ct).ConfigureAwait(false));
            }
        }
    }

    /// <summary>An object key as a path below the location: each segment escaped.</summary>
    protected static string EscapeKey(string key) => string.Join('/', key.Split('/').Select(AwsSigV4.Encode));

    /// <summary>Whether a refusal says the credential no longer holds: 401, 403, or S3's 400 for an expired session.</summary>
    private static bool Expired(OsduStatusException ex)
        => ex.StatusCode is 401 or 403
           || (ex.StatusCode == 400 && (ex.Message.Contains("ExpiredToken", StringComparison.Ordinal) || ex.Message.Contains("TokenRefreshRequired", StringComparison.Ordinal)));
}

/// <summary>
/// Azure Blob Storage under the SAS Seismic Store signs for a dataset's container (and folder, on a uniform subproject):
/// each object a block blob of blocks of at most the part size, committed with the running MD5 as its content MD5, as
/// sdutil writes them (osdu/specs/seismic-ddms/INTEGRATION.md section 4.4), through <see cref="BlockBlob"/>. Every request
/// states the service version the SAS was signed with. Staged blocks outlive a renewed SAS, so a renewal resumes at the
/// block it failed on; the blocks go one at a time, so a renewal never races another block.
/// </summary>
internal sealed class AzureBlobStore : SeismicObjectStore
{
    private readonly string _location;
    private string _sas;
    private string _version;

    public AzureBlobStore(OsduHttpClient client, SeismicCredential credential, Func<CancellationToken, Task<SeismicCredential>> renew)
        : base(client, renew)
    {
        ArgumentNullException.ThrowIfNull(credential);
        (_location, _sas, _version) = Parse(credential);
        Describe = $"Azure Blob Storage ({new Uri(_location).Host}{new Uri(_location).AbsolutePath})";
    }

    public override string Describe { get; }

    public override async Task PutAsync(string key, long length, PartReader reader, int partBytes, Func<byte[]> md5, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partBytes, 1);
        if (length == 0)
        {
            var empty = Convert.ToBase64String(md5());
            await SendAsync(() => Request(HttpMethod.Put, key, null, Bytes(ReadOnlyMemory<byte>.Empty, null), ("x-ms-blob-type", "BlockBlob"), ("x-ms-blob-content-md5", empty)), null, idempotent: true, ct).ConfigureAwait(false);
            return;
        }

        var blob = new BlockBlob(
            (query, content, headers) => Request(HttpMethod.Put, key, query, content, [.. headers]),
            (build, token) => SendAsync(build, null, idempotent: true, token));
        var blocks = await blob.SendAsync(length, reader, partBytes, concurrency: 1, ct).ConfigureAwait(false);
        await blob.CommitAsync(blocks, [("x-ms-blob-content-md5", Convert.ToBase64String(md5()))], ct).ConfigureAwait(false);
    }

    public override async Task DeleteAsync(string key, CancellationToken ct)
        => await SendAsync(() => Request(HttpMethod.Delete, key, null, null), new HashSet<int> { 404 }, idempotent: true, ct).ConfigureAwait(false);

    protected override void Accept(SeismicCredential credential)
    {
        var (location, sas, version) = Parse(credential);
        if (!string.Equals(location, _location, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeliveryException($"Seismic Store renewed the upload credential of {Describe} for another location; the next try starts the upload again.");
        }

        (_sas, _version) = (sas, version);
    }

    private static (string Location, string Sas, string Version) Parse(SeismicCredential credential)
    {
        if (!Uri.TryCreate(credential.AccessToken, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp) || url.Query.Length < 2)
        {
            throw new RecordHeldException("Seismic Store issued a SasUrl credential that is not an absolute http(s) URL with a signature");
        }

        var sas = url.Query.TrimStart('?');
        return (url.GetLeftPart(UriPartial.Path).TrimEnd('/'), sas, System.Web.HttpUtility.ParseQueryString(sas)["sv"] ?? BlockBlob.DefaultVersion);
    }

    private HttpRequestMessage Request(HttpMethod method, string key, string? query, HttpContent? content, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, new Uri($"{_location}/{EscapeKey(key)}?{_sas}{(query is null ? string.Empty : "&" + query)}")) { Content = content };
        request.Headers.TryAddWithoutValidation("x-ms-version", _version);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }
}

/// <summary>
/// Google Cloud Storage with the downscoped bearer token Seismic Store issues on gc: each object a resumable upload in
/// pieces of the part size, its <c>crc32c</c> compared with the bytes sent, as sdutil checks it
/// (osdu/specs/seismic-ddms/INTEGRATION.md section 4.4). A piece whose answer is lost is settled by asking the session how
/// much it holds, and what it lacks is sent again.
/// </summary>
internal sealed class GoogleStore : SeismicObjectStore
{
    /// <summary>Where Google Cloud Storage's JSON API is, unless the flow names another endpoint.</summary>
    public const string DefaultEndpoint = "https://storage.googleapis.com";

    private const int PieceFailures = 3;

    private readonly string _endpoint;
    private readonly string _bucket;
    private readonly string _prefix;
    private string _token;

    public GoogleStore(OsduHttpClient client, Uri endpoint, string bucket, string prefix, SeismicCredential credential, Func<CancellationToken, Task<SeismicCredential>> renew)
        : base(client, renew)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(credential);
        _endpoint = endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
        _bucket = bucket;
        _prefix = prefix.Trim('/');
        _token = Token(credential);
        Describe = $"Google Cloud Storage (gs://{bucket}/{_prefix})";
    }

    public override string Describe { get; }

    public override async Task PutAsync(string key, long length, PartReader reader, int partBytes, Func<byte[]> md5, CancellationToken ct)
    {
        var name = Name(key);
        var start = new Uri($"{_endpoint}/upload/storage/v1/b/{AwsSigV4.Encode(_bucket)}/o?uploadType=resumable&name={AwsSigV4.Encode(name)}");
        var opened = await SendAsync(
            () =>
            {
                var request = Request(HttpMethod.Post, start);
                request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "application/octet-stream");
                request.Headers.TryAddWithoutValidation("X-Upload-Content-Length", length.ToString(CultureInfo.InvariantCulture));
                request.Content = new ByteArrayContent([]);
                return request;
            },
            null,
            idempotent: true,
            ct).ConfigureAwait(false);
        var session = opened.Headers.Location
            ?? throw new DeliveryException($"{Describe} answered the start of the upload of {name} without the session to send it to.");
        if (!session.IsAbsoluteUri)
        {
            session = new Uri(new Uri(_endpoint + "/"), session);
        }

        uint crc = 0;
        JsonElement? stored = null;
        if (length == 0)
        {
            stored = await PieceAsync(session, ReadOnlyMemory<byte>.Empty, 0, 0, name, ct).ConfigureAwait(false);
        }

        var piece = Math.Max(GooglePieceQuantum, partBytes / GooglePieceQuantum * GooglePieceQuantum);
        for (long sent = 0; sent < length;)
        {
            var count = (int)Math.Min(piece, length - sent);
            var part = await reader.ReadAsync(count, ct).ConfigureAwait(false);
            crc = Crc32C.Append(crc, part.Span);
            stored = await PieceAsync(session, part, sent, length, name, ct).ConfigureAwait(false);
            sent += count;
        }

        if (stored is not { ValueKind: JsonValueKind.Object } done)
        {
            throw new DeliveryException($"{Describe} did not answer the last piece of {name} with the object it stored; the next try sends the object again.");
        }

        var expected = Crc32C.ToBase64(crc);
        var answered = done.TryGetProperty("crc32c", out var checksum) && checksum.ValueKind == JsonValueKind.String ? checksum.GetString() : null;
        if (!string.Equals(answered, expected, StringComparison.Ordinal))
        {
            throw new DeliveryException($"{Describe} stored {name} with the checksum {answered ?? "(none)"}, and the bytes sent have {expected}; the next try sends the object again.");
        }
    }

    public override async Task DeleteAsync(string key, CancellationToken ct)
    {
        var url = new Uri($"{_endpoint}/storage/v1/b/{AwsSigV4.Encode(_bucket)}/o/{AwsSigV4.Encode(Name(key))}");
        await SendAsync(() => Request(HttpMethod.Delete, url), new HashSet<int> { 404 }, idempotent: true, ct).ConfigureAwait(false);
    }

    protected override void Accept(SeismicCredential credential) => _token = Token(credential);

    /// <summary>
    /// Sends one piece starting at byte <paramref name="offset"/> of <paramref name="total"/>; returns the stored object
    /// after the last piece and null before it. What the session did not take is sent again, as long as it takes
    /// something each time; a piece that fails in transit is settled by asking the session what it holds.
    /// </summary>
    private async Task<JsonElement?> PieceAsync(Uri session, ReadOnlyMemory<byte> part, long offset, long total, string name, CancellationToken ct)
    {
        var from = 0;
        var failures = 0;
        while (true)
        {
            var slice = part[from..];
            var first = offset + from;
            var range = total == 0
                ? "bytes */0"
                : string.Create(CultureInfo.InvariantCulture, $"bytes {first}-{first + slice.Length - 1}/{total}");
            long held;
            bool afterFailure;
            try
            {
                var result = await SendAsync(
                    () =>
                    {
                        var request = Request(HttpMethod.Put, session);
                        var content = Bytes(slice, null);
                        content.Headers.TryAddWithoutValidation("Content-Range", range);
                        request.Content = content;
                        return request;
                    },
                    new HashSet<int> { 308 },
                    idempotent: false,
                    ct).ConfigureAwait(false);
                if ((int)result.Status != 308)
                {
                    return OsduHttpClient.ParseJson(result, Bare(session));
                }

                held = Held(result);
                afterFailure = false;
            }
            catch (Exception ex) when (ex is DeliveryException { InnerException: HttpRequestException or IOException or TaskCanceledException } or OsduStatusException { StatusCode: >= 500 }
                                       && ++failures < PieceFailures)
            {
                var status = await StatusAsync(session, total, ct).ConfigureAwait(false);
                if (status.Stored is { } stored)
                {
                    return stored;
                }

                held = status.Held;
                afterFailure = true;
            }

            if (held == offset + part.Length - 1)
            {
                return null;
            }

            // The session holds less than the piece: what it lacks goes again, as long as each answer shows it took bytes
            // (a failed piece may be sent again whole, as often as failures are allowed).
            var next = held + 1 - offset;
            if (next < from || next > part.Length || (next == from && !afterFailure))
            {
                throw new DeliveryException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Describe} holds {held + 1} bytes of {name} after a piece from byte {first} to {first + slice.Length - 1}; the next try sends the object again."));
            }

            from = (int)next;
        }
    }

    /// <summary>What the session holds: the last byte it stored, or the object when it is complete.</summary>
    private async Task<(long Held, JsonElement? Stored)> StatusAsync(Uri session, long total, CancellationToken ct)
    {
        var result = await SendAsync(
            () =>
            {
                var request = Request(HttpMethod.Put, session);
                var content = new ByteArrayContent([]);
                content.Headers.TryAddWithoutValidation("Content-Range", string.Create(CultureInfo.InvariantCulture, $"bytes */{total}"));
                request.Content = content;
                return request;
            },
            new HashSet<int> { 308 },
            idempotent: true,
            ct).ConfigureAwait(false);
        return (int)result.Status == 308 ? (Held(result), null) : (total - 1, OsduHttpClient.ParseJson(result, Bare(session)));
    }

    /// <summary>A session address without its query, which names the upload and is not to appear in a message.</summary>
    private static Uri Bare(Uri session) => new(session.GetLeftPart(UriPartial.Path));

    /// <summary>The last byte a 308 says the session holds (<c>Range: bytes=0-N</c>), or -1 when it holds none.</summary>
    private static long Held(HttpFetchResult result)
    {
        if (!result.Headers.TryGetValues("Range", out var values) || values.FirstOrDefault() is not { } text)
        {
            return -1;
        }

        var dash = text.LastIndexOf('-');
        return dash > 0 && long.TryParse(text[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var last) ? last : -1;
    }

    private static string Token(SeismicCredential credential)
        => string.IsNullOrWhiteSpace(credential.AccessToken)
            ? throw new RecordHeldException("Seismic Store issued an empty bearer credential for Google Cloud Storage")
            : credential.AccessToken;

    private string Name(string key) => _prefix.Length == 0 ? key : _prefix + "/" + key;

    private HttpRequestMessage Request(HttpMethod method, Uri url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }
}

/// <summary>
/// An S3-compatible store (Seismic Store on anthos and IBM) at the endpoint the flow names, with the temporary key triple
/// the service issues: path-style addresses, every request signed with SigV4, an object up to the part size in one
/// request and a larger one as a multipart upload, each part with its <c>Content-MD5</c>
/// (osdu/specs/seismic-ddms/INTEGRATION.md sections 4.3 and 4.4). An upload whose parts are staged survives a renewed
/// key triple; one that fails or stops is aborted, since its parts would otherwise keep occupying the bucket.
/// </summary>
internal sealed class S3Store : SeismicObjectStore
{
    private const string Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    private readonly string _endpoint;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly string _region;
    private readonly TimeProvider _time;
    private AwsSigV4.Credentials _credentials;

    public S3Store(
        OsduHttpClient client, Uri endpoint, string bucket, string prefix, SeismicCredential credential, string region, TimeProvider time,
        Func<CancellationToken, Task<SeismicCredential>> renew)
        : base(client, renew)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentNullException.ThrowIfNull(time);
        _endpoint = endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
        _bucket = bucket;
        _prefix = prefix.Trim('/');
        _region = region;
        _time = time;
        _credentials = Triple(credential);
        Describe = $"S3 ({endpoint.Host}, bucket {bucket}, {_prefix})";
    }

    public override string Describe { get; }

    /// <summary>The key triple Seismic Store issues on anthos and IBM (<c>AccessKeyId:SecretAccessKey:SessionToken</c>).</summary>
    public static AwsSigV4.Credentials Triple(SeismicCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var parts = credential.AccessToken.Split(':', 3);
        return parts.Length == 3 && parts.All(p => p.Length > 0)
            ? new AwsSigV4.Credentials(parts[0], parts[1], parts[2])
            : throw new RecordHeldException("Seismic Store issued a bearer credential that is not the key triple an S3 store takes (AccessKeyId:SecretAccessKey:SessionToken)");
    }

    /// <summary>The part size an object of <paramref name="length"/> goes up in: the flow's, or larger where S3's part limit needs it, and never under S3's minimum.</summary>
    public static long PartSize(long length, int partBytes)
    {
        const long MiB = 1024 * 1024;
        var needed = (length + MaxS3Parts - 1) / MaxS3Parts;
        var size = Math.Max(Math.Max(partBytes, MinS3PartBytes), needed);
        return (size + MiB - 1) / MiB * MiB;
    }

    public override async Task PutAsync(string key, long length, PartReader reader, int partBytes, Func<byte[]> md5, CancellationToken ct)
    {
        if (length <= Math.Max(partBytes, MinS3PartBytes))
        {
            var whole = await reader.ReadAsync((int)length, ct).ConfigureAwait(false);
            var checksum = Md5Of(whole.Span);
            await SignedAsync(HttpMethod.Put, key, null, () => Bytes(whole, checksum), null, ct).ConfigureAwait(false);
            return;
        }

        var size = PartSize(length, partBytes);
        var started = await SignedAsync(HttpMethod.Post, key, "uploads", null, null, ct).ConfigureAwait(false);
        var uploadId = Element(started, "UploadId")
            ?? throw new DeliveryException($"{Describe} answered the start of the multipart upload of {key} without its upload id.");
        try
        {
            var parts = new List<(int Number, string ETag)>();
            for (long sent = 0; sent < length;)
            {
                var count = (int)Math.Min(size, length - sent);
                var part = await reader.ReadAsync(count, ct).ConfigureAwait(false);
                var checksum = Md5Of(part.Span);
                var number = parts.Count + 1;
                var result = await SignedAsync(
                    HttpMethod.Put,
                    key,
                    string.Create(CultureInfo.InvariantCulture, $"partNumber={number}&uploadId={AwsSigV4.Encode(uploadId)}"),
                    () => Bytes(part, checksum),
                    null,
                    ct).ConfigureAwait(false);
                var etag = ETag(result)
                    ?? throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"{Describe} answered part {number} of {key} without its ETag."));
                parts.Add((number, etag));
                sent += count;
            }

            XNamespace ns = Namespace;
            var complete = new XElement(
                ns + "CompleteMultipartUpload",
                parts.Select(p => new XElement(ns + "Part", new XElement(ns + "PartNumber", p.Number), new XElement(ns + "ETag", p.ETag))));
            var body = Encoding.UTF8.GetBytes(complete.ToString(SaveOptions.DisableFormatting));
            var done = await SignedAsync(HttpMethod.Post, key, "uploadId=" + AwsSigV4.Encode(uploadId), () => Xml(body), null, ct).ConfigureAwait(false);

            // S3 answers a completion that fails late with 200 and an error document.
            if (Element(done, "Code") is { } code)
            {
                throw new DeliveryException($"{Describe} refused to complete the multipart upload of {key}: {code} {Element(done, "Message")}");
            }
        }
        catch
        {
            await AbortAsync(key, uploadId).ConfigureAwait(false);
            throw;
        }
    }

    public override async Task DeleteAsync(string key, CancellationToken ct)
        => await SignedAsync(HttpMethod.Delete, key, null, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);

    protected override void Accept(SeismicCredential credential) => _credentials = Triple(credential);

    private async Task AbortAsync(string key, string uploadId)
    {
        try
        {
            await SignedAsync(HttpMethod.Delete, key, "uploadId=" + AwsSigV4.Encode(uploadId), null, new HashSet<int> { 404 }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeliveryException or HttpRequestException or IOException)
        {
            // The upload's parts then expire under the bucket's lifecycle rules; the failure that led here is the one reported.
        }
    }

    private Task<HttpFetchResult> SignedAsync(HttpMethod method, string key, string? query, Func<HttpContent>? content, IReadOnlySet<int>? allow, CancellationToken ct)
    {
        var path = _prefix.Length == 0 ? key : _prefix + "/" + key;
        var url = new Uri($"{_endpoint}/{AwsSigV4.Encode(_bucket)}/{EscapeKey(path)}{(query is null ? string.Empty : "?" + query)}");
        return SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(method, url) { Content = content?.Invoke() };
                AwsSigV4.Sign(request, _credentials, _region, "s3", _time.GetUtcNow());
                return request;
            },
            allow,
            idempotent: true,
            ct);
    }

    /// <summary>A part's ETag as S3 answers it, quoted; a store that answers it bare has its value quoted here.</summary>
    private static string? ETag(HttpFetchResult result)
    {
        if (result.Headers.ETag?.Tag is { } tag)
        {
            return tag;
        }

        return result.Headers.TryGetValues("ETag", out var values) && values.FirstOrDefault() is { Length: > 0 } raw
            ? "\"" + raw.Trim('"') + "\""
            : null;
    }

    private string? Element(HttpFetchResult result, string name)
    {
        if (result.Body.Length == 0)
        {
            return null;
        }

        try
        {
            return XDocument.Parse(result.BodyText).Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
        }
        catch (System.Xml.XmlException ex)
        {
            throw new DeliveryException($"{Describe} answered with a body that is not XML: {ex.Message}", ex);
        }
    }
}
