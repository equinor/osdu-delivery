using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>Where one dataset's files landed in the staging area the Dataset service handed out.</summary>
/// <param name="FileSource">For a single-file dataset: the file's staged path (<c>storageLocation.fileSource</c>).</param>
/// <param name="CollectionPath">For a file collection: the staged directory (<c>storageLocation.fileCollectionSource</c>).</param>
/// <param name="Files">Every file, with its name and size; in a collection the name is relative to the directory.</param>
public sealed record StagedDataset(string? FileSource, string? CollectionPath, IReadOnlyList<(string Name, long Size)> Files);

/// <summary>
/// The upload half of the Dataset service routes (osdu/specs/core/INTEGRATION.md section 2.5): a record's files put where
/// <c>storageInstructions</c> says, and the dataset record given the <c>DatasetProperties</c> that point at them. A single
/// file goes to the location's signed URL (<see cref="SignedUploads"/>); a file collection's files go under the staged
/// directory, the way the provider that signed it takes them. Each upload is a resumable step, as the file route's are.
/// </summary>
internal static class DatasetUploads
{
    public static string StorageStep(string payload) => "storage-" + payload;

    public static string UploadStep(string payload, int index) => "upload-" + payload + "-" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Uploads the files of one payload part for a dataset of <paramref name="entityType"/>, and returns where they are.
    /// A single-file entity type takes exactly one file. A retry reuses the location and the uploads an earlier try
    /// completed, as long as the location it recorded is still the one it uploaded to.
    /// </summary>
    public static async Task<StagedDataset> UploadAsync(
        OsduHttpClient client, DatasetService datasets, ProtocolOptions options, DeliveryWork work, string payload, IPayloadSource source, string entityType,
        long requestBodyCeiling, DeliverySteps steps, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(datasets);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(source);
        var chunks = await FileUploads.ListChunksAsync(work with { Payload = source }, ct).ConfigureAwait(false);
        var collection = DatasetService.IsCollectionType(entityType);
        if (!collection && chunks.Count != 1)
        {
            throw new RecordHeldException(
                string.Create(CultureInfo.InvariantCulture, $"payload '{payload}' holds {chunks.Count} files, and a {entityType} dataset holds one file; declare a dataset--FileCollection kind for several"));
        }

        var names = chunks.Select(c => FileUploads.FileName(c.Path)).ToList();
        if (collection && names.Distinct(StringComparer.Ordinal).Count() != names.Count)
        {
            throw new RecordHeldException($"payload '{payload}' holds two files of the same name, and a file collection keeps its files by name under one directory");
        }

        // A signed location is a credential, so a retry cannot reuse it: the step records only where the files landed,
        // and a retry that has not finished every upload asks for a new location and uploads them all again.
        var storageStep = StorageStep(payload);
        if (work.Completed(storageStep) is { } done
            && done.TryGetValue("complete", out var complete) && complete == "true"
            && (done.TryGetValue("fileSource", out var knownSource) | done.TryGetValue("collectionPath", out var knownPath)))
        {
            steps.Resumed(storageStep, done);
            return new StagedDataset(knownSource, knownPath, chunks.Select((c, i) => (names[i], c.Size)).ToList());
        }

        var started = steps.Now;
        var storage = await datasets.StorageAsync(entityType, ct).ConfigureAwait(false);
        StagedDataset staged;
        SignedTransfer? sent = null;
        if (collection)
        {
            staged = await DatasetCollections.UploadAsync(client, options, storage, source, chunks, names, ct, requestBodyCeiling: requestBodyCeiling).ConfigureAwait(false);
        }
        else
        {
            if (storage.Text("signedUrl") is null)
            {
                throw new RecordHeldException(
                    $"the dataset service handed out a file location this route cannot upload to (provider {storage.ProviderKey ?? "unnamed"}, with {Keys(storage)}); "
                    + "a single file goes to the signedUrl a location names (osdu/specs/core/INTEGRATION.md section 2.5.1)");
            }

            var signed = SignedUrl(storage, "signedUrl");
            var fileSource = storage.Text("fileSource")
                ?? throw new DeliveryException($"the dataset service's storage location for {entityType} names no fileSource to register the file under.");
            var chunk = chunks[0];
            sent = await SignedUploads.PutAsync(client, options, signed, source, chunk, requestBodyCeiling, wantsMd5: false, ct).ConfigureAwait(false);
            staged = new StagedDataset(fileSource, null, [(names[0], chunk.Size)]);
        }

        var returned = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["complete"] = "true",
            ["files"] = staged.Files.Count.ToString(CultureInfo.InvariantCulture),
        };
        if (staged.FileSource is { } landed)
        {
            returned["fileSource"] = landed;
        }

        if (staged.CollectionPath is { } directory)
        {
            returned["collectionPath"] = directory;
        }

        if (storage.ProviderKey is { } provider)
        {
            returned["providerKey"] = provider;
        }

        sent?.AddTo(returned);
        steps.Add(storageStep, started, null, returned);
        await work.ReportStepAsync(storageStep, returned, ct).ConfigureAwait(false);
        return staged;
    }

    /// <summary>
    /// <paramref name="record"/> pointing at its staged files: <c>FileSourceInfo</c> for a single file, and
    /// <c>FileCollectionPath</c> with a <c>FileSourceInfos</c> entry per file for a collection (the dataset schemas,
    /// osdu/specs/core/INTEGRATION.md section 3.3). What the record's mapping rendered under <c>DatasetProperties</c> is kept
    /// where the route does not set it; no checksum is written, since the Dataset service computes none and the schema's
    /// patterns take only the client's own.
    /// </summary>
    public static void Point(JsonObject record, StagedDataset staged)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(staged);
        if (record["data"] is not JsonObject data)
        {
            data = new JsonObject();
            record["data"] = data;
        }

        if (data["DatasetProperties"] is not JsonObject properties)
        {
            properties = new JsonObject();
            data["DatasetProperties"] = properties;
        }

        var total = staged.Files.Sum(f => f.Size);
        data["TotalSize"] ??= DatasetService.Size(total);
        if (staged.CollectionPath is { } directory)
        {
            properties["FileCollectionPath"] = directory;
            properties["FileSourceInfos"] = new JsonArray(staged.Files
                .Select(f => (JsonNode?)new JsonObject { ["FileSource"] = f.Name, ["Name"] = f.Name, ["FileSize"] = DatasetService.Size(f.Size) })
                .ToArray());
            properties.Remove("FileSourceInfo");
            return;
        }

        var (name, size) = staged.Files[0];
        data["Name"] ??= name;
        properties["FileSourceInfo"] = new JsonObject
        {
            ["FileSource"] = staged.FileSource ?? throw new DeliveryException("a single-file dataset was staged without a file source"),
            ["Name"] = name,
            ["FileSize"] = DatasetService.Size(size),
        };
    }

    /// <summary>The names of what a storage location holds, never its values, which carry credentials.</summary>
    public static string Keys(DatasetStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return storage.Location.Count == 0 ? "nothing" : string.Join(", ", storage.Location.Select(p => p.Key));
    }

    /// <summary>The storage location's signed URL, which must be an absolute http(s) URL; never logged, stored or returned.</summary>
    public static Uri SignedUrl(DatasetStorage storage, string property)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var text = storage.Text(property) ?? throw new DeliveryException($"the dataset service's storage location has no {property} to upload to.");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            throw new DeliveryException($"the dataset service's storage location has a {property} that is not an absolute http(s) URL.");
        }

        return url;
    }
}
