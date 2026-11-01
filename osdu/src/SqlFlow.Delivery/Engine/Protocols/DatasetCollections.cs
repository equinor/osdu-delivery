using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Web;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine.Protocols.Ddms;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using static SqlFlow.Delivery.Engine.Protocols.ObjectBody;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// The files of a file collection, put under the staging directory the Dataset service handed out, the way the provider
/// that signed it takes them (osdu/specs/core/INTEGRATION.md section 2.5.1). Every upload goes to a location the
/// storage instructions signed, so the flow's auth and headers never go with it, and the signed location is never
/// logged, stored or returned.
/// <list type="bullet">
/// <item>Azure (<c>signedUrl</c>, a directory SAS on the Data Lake host): each file created, appended and flushed under
/// the directory, as the File service's own Azure test uploads a collection with the Data Lake client (project 90 at
/// <c>d7c25c2d7f5d2f42bed901c68a407098195389bb</c>, <c>testing/file-test-azure/.../Helper/DataLakeHelper.java:15-26</c>).</item>
/// <item>Core-plus on MinIO or S3 (<c>url</c> and <c>signingOptions</c> holding a presigned POST policy): each file sent
/// as a multipart form with the policy's fields and its key under the directory (project 1441 at
/// <c>c389fbea3de45d24c4f4847107dd3057bc988724</c>, <c>MinioFullFlowIT.java:64-98</c>, <c>S3FullFlowIT.java:73-102</c>).</item>
/// <item>Core-plus on Google Cloud Storage (<c>signingOptions</c> holding a bucket, a folder and a token scoped to it):
/// each file sent as a media upload under the folder (project 1475 at <c>abb8463cb22db1d1314452d421387a04cafc7407</c>,
/// <c>GcsFullFlowIT.java:53-61, 93-116</c>).</item>
/// </list>
/// IBM hands out temporary credentials for an object store endpoint it does not name, so a collection there is not
/// uploaded here, and the record says why.
/// </summary>
internal static class DatasetCollections
{
    /// <summary>The Google Cloud Storage media upload endpoint the OSDU driver tests upload a collection's files to.</summary>
    public const string GcsUploadEndpoint = "https://storage.googleapis.com/upload/storage/v1/b/";

    public static async Task<StagedDataset> UploadAsync(
        OsduHttpClient client, ProtocolOptions options, DatasetStorage storage, IPayloadSource source, IReadOnlyList<PayloadFile> chunks, IReadOnlyList<string> names, CancellationToken ct, int? appendBytes = null, long requestBodyCeiling = 0)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count != chunks.Count)
        {
            throw new ArgumentException("every payload file needs its name", nameof(names));
        }

        // The way to upload is read from the location first: a location with temporary credentials (IBM) names neither
        // an endpoint nor a directory, and holding the record says why, where a missing directory alone would not.
        var signing = storage.Location["signingOptions"] as JsonObject;
        var uploader = storage.Text("signedUrl") is not null ? Uploader.DataLake
            : signing is not null && Text(signing, "policy") is not null ? Uploader.PostPolicy
            : signing is not null && Text(signing, "connectionString") is not null ? Uploader.Google
            : throw new RecordHeldException(
                $"the dataset service handed out a collection location this route cannot upload to (provider {storage.ProviderKey ?? "unnamed"}, with {DatasetUploads.Keys(storage)}); "
                + "a location with temporary credentials names no endpoint to use them with (osdu/specs/core/INTEGRATION.md section 2.5.1)");
        var directory = storage.Text("fileCollectionSource")
            ?? throw new DeliveryException("the dataset service's storage location for a file collection names no fileCollectionSource to register it under.");
        var files = new List<(string Name, long Size)>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var name = names[i];
            switch (uploader)
            {
                case Uploader.DataLake:
                    var part = appendBytes ?? (int)SignedUploads.BlockBytes(chunk, options.UploadBlockMiB, requestBodyCeiling);
                    await AzureAsync(client, DatasetUploads.SignedUrl(storage, "signedUrl"), name, source, chunk, part, ct).ConfigureAwait(false);
                    break;
                case Uploader.PostPolicy:
                    FileUploads.CheckCeiling(chunk, requestBodyCeiling);
                    await PostPolicyAsync(client, storage, signing!, directory, name, source, chunk, options.PayloadContentType, ct).ConfigureAwait(false);
                    break;
                default:
                    FileUploads.CheckCeiling(chunk, requestBodyCeiling);
                    await GoogleAsync(client, signing!, name, source, chunk, options.PayloadContentType, ct).ConfigureAwait(false);
                    break;
            }

            files.Add((name, chunk.Size));
        }

        // Azure names the directory with its leading slash; core-plus names it bare, and its copy reads the path as the
        // text after the bucket, so the path is given its slashes (osdu/specs/core/INTEGRATION.md section 2.5.1).
        var path = directory.StartsWith('/') ? directory : "/" + directory.TrimEnd('/') + "/";
        return new StagedDataset(null, path, files);
    }

    /// <summary>
    /// One file under an Azure Data Lake directory SAS: created (<c>PUT ?resource=file</c>), appended in parts
    /// (<c>PATCH ?action=append&amp;position=</c>) and flushed (<c>PATCH ?action=flush&amp;position=</c>), as the Data Lake
    /// client the File service's test uses sends them (Azure Data Lake SDK 12.27.0, <c>PathsImpl.java:100-133, 750-875</c>).
    /// The file is read front to back once and its parts go in order, each repeated by itself, since an append's position
    /// says where it lands. The request states the service version the SAS was signed with. The file is created over
    /// whatever a failed try left, since the directory is new for every try.
    /// </summary>
    private static async Task AzureAsync(OsduHttpClient client, Uri directory, string name, IPayloadSource source, PayloadFile chunk, int appendBytes, CancellationToken ct)
    {
        var query = HttpUtility.ParseQueryString(directory.Query);
        var version = query["sv"] ?? BlockBlob.DefaultVersion;
        var file = FileUrl(directory, name);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-ms-version"] = version };
        await client.SendToSignedUrlAsync(HttpMethod.Put, WithQuery(file, "resource=file"), () => new MemoryStream([], writable: false), "application/octet-stream", 0, headers, ct).ConfigureAwait(false);

        await using var reader = await PartReader.OpenAsync(source, chunk, ct).ConfigureAwait(false);
        await UploadParts.SendAsync(
            reader,
            chunk.Size,
            appendBytes,
            concurrency: 1,
            (_, offset, part, token) => client.SendAsIsAsync(
                () =>
                {
                    var request = new HttpRequestMessage(new HttpMethod("PATCH"), WithQuery(file, "action=append&position=" + offset.ToString(CultureInfo.InvariantCulture))) { Content = Bytes(part, null) };
                    request.Headers.TryAddWithoutValidation("x-ms-version", version);
                    return request;
                },
                null,
                idempotent: true,
                token),
            ct).ConfigureAwait(false);
        await reader.EndAsync(ct).ConfigureAwait(false);
        await client.SendToSignedUrlAsync(
            new HttpMethod("PATCH"),
            WithQuery(file, "action=flush&position=" + chunk.Size.ToString(CultureInfo.InvariantCulture)),
            () => new MemoryStream([], writable: false),
            "application/octet-stream",
            0,
            headers,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One file sent with a presigned POST policy (MinIO, S3): the policy's fields, the key the file takes under the
    /// directory, and the file last, as the OSDU object store driver tests send it. MinIO's fields name no key, and the
    /// key is the directory and the name; S3's name the directory, and the name is appended to it.
    /// </summary>
    private static async Task PostPolicyAsync(
        OsduHttpClient client, DatasetStorage storage, JsonObject signing, string directory, string name, IPayloadSource source, PayloadFile chunk, string contentType, CancellationToken ct)
    {
        var target = DatasetUploads.SignedUrl(storage, "url");
        var fields = new List<KeyValuePair<string, string>>();
        string? key = null;
        foreach (var (field, value) in signing)
        {
            if (value is not JsonValue text || !text.TryGetValue<string>(out var content))
            {
                continue;
            }

            if (string.Equals(field, "key", StringComparison.Ordinal))
            {
                key = content + name;
                continue;
            }

            fields.Add(new(field, content));
        }

        fields.Add(new("key", key ?? directory.Trim('/') + "/" + name));
        await client.SendFormToSignedUrlAsync(target, fields, "file", name, () => OsduDdmsProtocol.OpenSync(source, chunk), chunk.Size, contentType, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One file sent as a Google Cloud Storage media upload under the folder the downscoped token covers, as the OSDU
    /// Google driver test sends it; the token is the one the storage instructions scoped to the folder.
    /// </summary>
    private static async Task GoogleAsync(OsduHttpClient client, JsonObject signing, string name, IPayloadSource source, PayloadFile chunk, string contentType, CancellationToken ct)
    {
        var bucket = Text(signing, "bucket") ?? throw new DeliveryException("the dataset service's Google storage location names no bucket.");
        var folder = Text(signing, "filepath") ?? string.Empty;
        var token = Text(signing, "connectionString")!;
        var url = new Uri(GcsUploadEndpoint + Uri.EscapeDataString(bucket) + "/o?uploadType=media&name=" + Uri.EscapeDataString(folder + name));
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer " + token };
        await client.SendToSignedUrlAsync(HttpMethod.Post, url, () => OsduDdmsProtocol.OpenSync(source, chunk), contentType, chunk.Size, headers, ct).ConfigureAwait(false);
    }

    /// <summary>The URL of a file under a directory URL, keeping the directory's query (its SAS).</summary>
    private static Uri FileUrl(Uri directory, string name)
    {
        var builder = new UriBuilder(directory) { Path = directory.AbsolutePath.TrimEnd('/') + "/" + Uri.EscapeDataString(name) };
        return builder.Uri;
    }

    private static Uri WithQuery(Uri url, string parameters)
    {
        var builder = new UriBuilder(url);
        var query = builder.Query.TrimStart('?');
        builder.Query = parameters + (query.Length == 0 ? string.Empty : "&" + query);
        return builder.Uri;
    }

    private static string? Text(JsonObject node, string name)
        => node[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text) ? text : null;

    /// <summary>How a collection location takes its files.</summary>
    private enum Uploader
    {
        /// <summary>A Data Lake directory SAS (Azure).</summary>
        DataLake,

        /// <summary>A presigned POST policy (MinIO, S3).</summary>
        PostPolicy,

        /// <summary>A token scoped to a Google Cloud Storage folder.</summary>
        Google,
    }
}
