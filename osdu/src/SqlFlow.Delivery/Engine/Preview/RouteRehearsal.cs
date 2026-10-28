using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Protocols.Dspdm;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Preview;

/// <summary>What a route does with one record, told without sending anything: the document it sends and its requests in order.</summary>
internal sealed record RehearsedRoute(JsonObject? Sent, IReadOnlyList<PreviewPlaceholder> Placeholders, IReadOnlyList<PreviewStep> Steps, IReadOnlyList<string> Notes);

/// <summary>
/// The requests a delivery of one record makes on the flow's route, in order, and the document the route sends where it adds
/// to the rendered one. Nothing is sent and nothing is asked of the platform: the paths are the flow's own
/// (<c>target.protocolOptions</c>) or the route's defaults, and the document goes through the same functions the route
/// builds it with. A value only the platform can give (a dataset id the File service mints when it registers a file) is a
/// placeholder in the document, named in <see cref="RehearsedRoute.Placeholders"/>, and a dataset id the route derives
/// from the record's own id is shown as it will be.
/// </summary>
internal static class RouteRehearsal
{
    /// <summary>
    /// Rehearses a delivery of the record in full: its document and its payload. <paramref name="payload"/> is the files the
    /// record's payload parts hold now, and <paramref name="targetState"/> what the ledger recorded of an earlier delivery
    /// (the dataset ids a metadata-only change writes the record with again). <paramref name="linkPaths"/> are the paths the
    /// mapping's <c>$link</c> nodes write the ids of its enrichments' records at, which an update merges where the flow
    /// preserves them.
    /// </summary>
    public static RehearsedRoute Rehearse(
        FlowDefinition flow,
        JsonObject rendered,
        string? targetId,
        IReadOnlyList<PreviewPayloadPart> payload,
        IReadOnlyDictionary<string, string>? targetState,
        IReadOnlyList<string>? linkPaths = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(rendered);
        ArgumentNullException.ThrowIfNull(payload);
        var options = flow.Target.ProtocolOptions;
        var steps = new Steps();
        var notes = new List<string>();
        var kind = rendered["kind"] is JsonValue k && k.TryGetValue<string>(out var text) ? text : null;
        switch (flow.Target.Protocol)
        {
            case DeliveryProtocol.Storage:
                StorageWrite(steps, options, "the record below");
                Preserved(notes, flow, linkPaths ?? []);
                return new RehearsedRoute(null, [], steps.All, notes);

            case DeliveryProtocol.File:
            {
                var (sent, placeholders) = WithFileDatasets(steps, options, rendered, Part(payload, null), targetState, notes);
                StorageWrite(steps, options, $"the record below, its data.{options.DatasetsProperty} listing the dataset records of its files");
                Preserved(notes, flow, linkPaths ?? []);
                return new RehearsedRoute(sent, placeholders, steps.All, notes);
            }

            case DeliveryProtocol.Dataset:
                return Dataset(steps, options, rendered, kind, targetId, Part(payload, null), notes);

            case DeliveryProtocol.Manifest:
            {
                var (sent, placeholders) = WithFileDatasets(steps, options, rendered, Part(payload, null), targetState, notes);
                Manifest(steps, options, notes);
                return new RehearsedRoute(sent, placeholders, steps.All, notes);
            }

            case DeliveryProtocol.Ddms:
                DdmsWrite(steps, flow, kind, "the record below");
                Bulk(steps, options, Part(payload, null), notes);
                return new RehearsedRoute(null, [], steps.All, notes);

            case DeliveryProtocol.FileAndDdms:
            {
                var (sent, placeholders) = WithFileDatasets(steps, options, rendered, Part(payload, PayloadParts.Files), targetState, notes);
                DdmsWrite(steps, flow, kind, $"the record below, its data.{options.DatasetsProperty} listing the dataset records of its files");
                Bulk(steps, options, Part(payload, PayloadParts.Bulk), notes);
                return new RehearsedRoute(sent, placeholders, steps.All, notes);
            }

            case DeliveryProtocol.ManifestAndDdms:
            {
                var (sent, placeholders) = WithFileDatasets(steps, options, rendered, Part(payload, PayloadParts.Files), targetState, notes);
                Manifest(steps, options, notes);
                Bulk(steps, options, Part(payload, PayloadParts.Bulk), notes);
                notes.Add("Ingestion writes the record through storage, past the DDMS, so a record that already holds bulk data carries the link its DDMS keeps to that data into the manifest.");
                return new RehearsedRoute(sent, placeholders, steps.All, notes);
            }

            case DeliveryProtocol.Workflow:
                Workflow(steps, flow, options);
                return new RehearsedRoute(null, [], steps.All, notes);

            case DeliveryProtocol.Dspdm:
                steps.Add("DSPDM", $"POST {DspdmService.CommonPath}", $"looks the business object's row up by the key attributes the record's data block holds ({BusinessObject(kind)})", "the row and its primary key, when DSPDM holds one");
                steps.Add("DSPDM", $"POST {DspdmService.SavePath}", "saves the row: its attributes are the record's data block, with null for an attribute that rendered empty; an update carries the row's primary key", "the saved row, read back; its version is when DSPDM last changed it");
                notes.Add("DSPDM draws a row's primary key when it inserts the row, so the route finds every row before it saves it, and a row its key finds that this record did not write holds the record unless the flow takes such rows over.");
                return new RehearsedRoute(null, [], steps.All, notes);

            case DeliveryProtocol.Etp:
            {
                var dataspace = rendered["data"]?["Dataspace"] is JsonValue d && d.TryGetValue<string>(out var named) && named.Length > 0 ? named : flow.Target.Etp.Dataspace;
                steps.Add("Reservoir DDMS", $"ETP 1.2 on a WebSocket at {flow.Target.Etp.Path}", "opens a session with the flow's credentials");
                steps.Add("Reservoir DDMS", "dataspace", $"creates the dataspace {dataspace ?? "the record names"} when it is missing, with the record's acl and legal tags");
                steps.Add("Reservoir DDMS", "transaction", "puts the object's XML (from the record's files payload, or data.Xml), then the arrays that fit a message, then commits", "the store's last write of the object");
                notes.Add("The object's identity is the uuid its own XML carries, and the record's target state keeps the URI it lives at.");
                return new RehearsedRoute(null, [], steps.All, notes);
            }

            default:
                notes.Add($"The route '{DeliveryProtocols.Name(flow.Target.Protocol)}' has no rehearsal; the rendered document is what the mapping produces.");
                return new RehearsedRoute(null, [], steps.All, notes);
        }
    }

    /// <summary>The payload part of the role, or the one payload set the route streams when the role is null.</summary>
    private static PreviewPayloadPart? Part(IReadOnlyList<PreviewPayloadPart> payload, string? role)
        => payload.FirstOrDefault(p => string.Equals(p.Role, role, StringComparison.Ordinal));

    /// <summary>The storage service's array write, as the record route and the file route's last step make it.</summary>
    private static void StorageWrite(Steps steps, ProtocolOptions options, string what)
    {
        var path = options.RecordPath ?? OsduRecordProtocol.DefaultRecordPath;
        var query = options.SkipDuplicates ? "?skipdupes=true" : string.Empty;
        steps.Add(
            "Storage",
            $"{options.RecordMethod ?? "PUT"} {path}{query}",
            string.Create(CultureInfo.InvariantCulture, $"{what}, in one array with the other records of its batch (up to {options.BatchSize})"),
            "the record's id and version");
    }

    private static void Preserved(List<string> notes, FlowDefinition flow, IReadOnlyList<string> linkPaths)
    {
        var preserved = flow.Target.ProtocolOptions.PreserveDataKeys;
        if (preserved.Count == 0)
        {
            return;
        }

        notes.Add($"An update copies data.{string.Join(", data.", preserved)} forward from the record OSDU holds (target.protocolOptions.preserveDataKeys), so those keys are sent as that record has them, not as rendered.");
        var linked = linkPaths
            .Where(link => link.Split('.') is ["data", var key] && preserved.Contains(key, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (linked.Count > 0)
        {
            notes.Add($"{string.Join(" and ", linked)} merges instead: the update keeps what OSDU holds there, adds the records the mapping's enrichments link there now ($link) and takes off those they no longer do.");
        }
    }

    /// <summary>
    /// The file service's part of a route that registers the record's files (file, fileAndDdms, manifest, manifestAndDdms):
    /// for each file a signed landing-zone location, the upload and the registration of its dataset record, whose id the
    /// service mints; then the record's dataset list pointing at those ids, as the route sets it.
    /// </summary>
    private static (JsonObject? Sent, IReadOnlyList<PreviewPlaceholder> Placeholders) WithFileDatasets(
        Steps steps, ProtocolOptions options, JsonObject rendered, PreviewPayloadPart? files, IReadOnlyDictionary<string, string>? targetState, List<string> notes)
    {
        var sent = (JsonObject)rendered.DeepClone();
        if (files is null || files.TotalFiles == 0)
        {
            // A record without files this time is written with the datasets its earlier delivery registered.
            var earlier = FileUploads.DatasetIds(targetState);
            if (earlier.Count == 0)
            {
                notes.Add(files?.Problem ?? "The record has no files to register, so its dataset list is sent as rendered.");
                return (null, []);
            }

            FileUploads.SetDatasets(sent, options.DatasetsProperty, earlier);
            notes.Add($"No file is registered this time: data.{options.DatasetsProperty} lists the {earlier.Count} dataset record(s) the record's earlier delivery registered.");
            return (sent, []);
        }

        var count = files.TotalFiles;
        // A part named files sits beside bulk data on a composed route, which uploads its files as the route's own rule says.
        var filesType = options.ForFiles(besideBulk: files.Role == PayloadParts.Files).PayloadContentType;
        var each = count == 1 ? "once" : string.Create(CultureInfo.InvariantCulture, $"once per file ({count} files)");
        steps.Add("File", $"GET {options.UploadUrlPath ?? FileUploads.DefaultUploadUrlPath}", "asks for a signed landing-zone location for the file", "a signed upload URL, used once and never stored, and the file's FileSource path", each);
        steps.Add("Landing zone", "PUT <signed URL>", $"streams the file as {filesType}", null, each);
        var first = files.Files.Count > 0 ? files.Files[0] : null;
        JsonObject? body = null;
        if (first is not null)
        {
            try
            {
                body = FileUploads.DatasetRecord(options, rendered, new UploadedFile(0, first.Name, first.Size, "<FileSource the File service returns>", null));
            }
            catch (DeliveryException ex)
            {
                notes.Add($"The route would hold the record before uploading: {ex.Message}.");
            }
        }

        steps.Add(
            "File",
            $"POST {options.FileMetadataPath ?? FileUploads.DefaultFileMetadataPath}",
            $"registers a {options.DatasetKind} record for the file, with the record's acl and legal tags{(first is null ? string.Empty : $" (the body shown is {first.Name}'s)")}",
            "the dataset record's id, which the File service mints",
            each,
            body);

        // The listing is bounded; every file the part holds gets a placeholder, named where the file is listed.
        var placeholders = new List<PreviewPlaceholder>(count);
        var ids = new List<string>(count);
        var existing = sent["data"]?[options.DatasetsProperty] is JsonArray rendersSome ? rendersSome.Count : 0;
        for (var i = 0; i < count; i++)
        {
            var name = i < files.Files.Count ? files.Files[i].Name : string.Create(CultureInfo.InvariantCulture, $"file {i + 1} of {count}");
            var id = $"<dataset id the File service returns for {name}>";
            ids.Add(id);
            placeholders.Add(new PreviewPlaceholder(
                string.Create(CultureInfo.InvariantCulture, $"data.{options.DatasetsProperty}[{existing + i}]"),
                $"the id of the {options.DatasetKind} record the File service registers for {name}, as a reference (the id and a colon)"));
        }

        FileUploads.SetDatasets(sent, options.DatasetsProperty, ids);
        return (sent, placeholders);
    }

    /// <summary>
    /// The dataset route: a record of a dataset kind is the dataset, registered under its own id with its files staged where
    /// the Dataset service says; any other record keeps its files in one dataset whose id derives from its own, and is written
    /// through storage pointing at it.
    /// </summary>
    private static RehearsedRoute Dataset(Steps steps, ProtocolOptions options, JsonObject rendered, string? kind, string? targetId, PreviewPayloadPart? files, List<string> notes)
    {
        var count = files?.TotalFiles ?? 0;
        var isDataset = kind is not null && OsduKind.EntityType(kind) is { } entityType && DatasetService.IsDatasetType(entityType);
        var stagedType = isDataset ? OsduKind.EntityType(kind!)! : TargetId.EntityTypeFromKind(options.DatasetKind);
        if (count > 0)
        {
            var each = count == 1 ? "once" : string.Create(CultureInfo.InvariantCulture, $"once per file ({count} files)");
            steps.Add("Dataset", $"POST {options.DatasetInstructionsPath ?? DatasetService.DefaultInstructionsPath}?kindSubType={stagedType}", "asks where files of this type are staged", "a signed staging location", each);
            steps.Add("Staging area", "PUT <signed location>", "streams the file", null, each);
        }

        if (isDataset)
        {
            steps.Add(
                "Dataset",
                $"PUT {options.DatasetRegisterPath ?? DatasetService.DefaultRegisterPath}",
                "registers the record as the dataset, under its own id, its data.DatasetProperties pointing at the staged files",
                "the dataset's version");
            steps.Add("Dataset", $"POST {options.DatasetRetrievalPath ?? DatasetService.DefaultRetrievalPath}", "checks the dataset answers retrieval instructions");
            notes.Add("The registration fills data.DatasetProperties from where the files were staged; a change to the record alone is written through storage with the dataset properties OSDU holds.");
            return new RehearsedRoute(null, [], steps.All, notes);
        }

        if (targetId is null)
        {
            notes.Add("The record has no id, so the dataset its files go in cannot be named.");
            return new RehearsedRoute(null, [], steps.All, notes);
        }

        var datasetId = OsduDatasetProtocol.DatasetIdFor(options, targetId);
        if (count > 0)
        {
            steps.Add("Dataset", $"PUT {options.DatasetRegisterPath ?? DatasetService.DefaultRegisterPath}", $"registers {datasetId} ({options.DatasetKind}) holding the files, with the record's acl and legal tags", "the dataset's version");
            steps.Add("Dataset", $"POST {options.DatasetRetrievalPath ?? DatasetService.DefaultRetrievalPath}", "checks the dataset answers retrieval instructions");
        }

        var sent = (JsonObject)rendered.DeepClone();
        FileUploads.SetDatasets(sent, options.DatasetsProperty, [datasetId]);
        StorageWrite(steps, options, $"the record below, its data.{options.DatasetsProperty} naming {datasetId}");
        notes.Add($"The dataset keeps its id on every delivery ({datasetId}, derived from the record's), so new files land on the same dataset.");
        return new RehearsedRoute(sent, [], steps.All, notes);
    }

    /// <summary>The ingestion workflow's part of a manifest route: the manifest, the run polled, and the records read back.</summary>
    private static void Manifest(Steps steps, ProtocolOptions options, List<string> notes)
    {
        steps.Add("Search", $"POST {options.SearchQueryPath ?? OsduManifestProtocol.DefaultSearchQueryPath}", string.Create(CultureInfo.InvariantCulture, $"waits until the search index lists the registered datasets (up to {options.DatasetIndexWaitSeconds} s), since ingestion checks references against it"));
        var run = (options.WorkflowRunPath ?? OsduManifestProtocol.DefaultWorkflowRunPath).Replace("{workflow}", options.WorkflowName, StringComparison.Ordinal);
        steps.Add("Workflow", $"POST {run}", $"hands the ingestion workflow one {options.ManifestKind} manifest carrying the record below with the other records of its batch", "the workflow run's id");
        var status = (options.WorkflowStatusPath ?? OsduManifestProtocol.DefaultWorkflowStatusPath).Replace("{workflow}", options.WorkflowName, StringComparison.Ordinal);
        steps.Add("Workflow", $"GET {status}", string.Create(CultureInfo.InvariantCulture, $"polls the run every {options.WorkflowPollSeconds} s until it finishes (at most {options.WorkflowTimeoutMinutes} min)"), "the run's status");
        steps.Add("Storage", $"POST {options.RecordQueryPath ?? OsduManifestProtocol.DefaultRecordQueryPath}", "reads the record back, so it settles on the version OSDU wrote, not on the run's status", "the record's version");
        if (options.ManifestByReference != ManifestReference.Never)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"A manifest over {options.ManifestInlineLimitKb} KB is stored as a dataset and handed to {options.ByReferenceWorkflowName} by reference."));
        }
    }

    /// <summary>A record written through the collection of its DDMS, where the flow's routing sends its kind.</summary>
    private static void DdmsWrite(Steps steps, FlowDefinition flow, string? kind, string what)
    {
        var where = kind is null ? "the record has no kind, so no DDMS collection takes it" : DdmsRouting.Of(flow).Explain(kind);
        steps.Add("DDMS", "the collection of the record's DDMS", $"{what}; {where}", "the record's version");
    }

    /// <summary>The bulk data a ddms route sends after the record: the payload files, in one request or a session.</summary>
    private static void Bulk(Steps steps, ProtocolOptions options, PreviewPayloadPart? bulk, List<string> notes)
    {
        if (bulk is null || bulk.TotalFiles == 0)
        {
            if (bulk?.Problem is { } problem)
            {
                notes.Add(problem);
            }

            return;
        }

        var session = bulk.TotalFiles > options.SessionThresholdChunks;
        steps.Add(
            "DDMS",
            session ? "a bulk data session" : "the bulk data of the record",
            string.Create(
                CultureInfo.InvariantCulture,
                $"sends the record's bulk data: {bulk.TotalFiles} file(s) as {options.PayloadContentType}{(session ? ", one chunk per file, then commits the session" : ", in one request")}"),
            "the version the bulk data was written at");
        notes.Add($"The DDMS keeps its link to the bulk data on the record ({string.Join(", ", OsduDdmsProtocol.LinkAttributes)}), which it writes itself when the bulk data lands.");
    }

    /// <summary>The workflow route: the anchor record, the workflow's inputs, each stage's run, and what the runs created.</summary>
    private static void Workflow(Steps steps, FlowDefinition flow, ProtocolOptions options)
    {
        steps.Add("Storage or Dataset", "the anchor", "writes the record first: registered with its files through the Dataset service, or written through storage", "the record's version");
        if (flow.Target.Workflow is not { } route)
        {
            steps.Add("Workflow", "none", "the flow declares no workflow, so a run of it fails before anything is sent");
            return;
        }

        if (route.Inputs.Count > 0)
        {
            steps.Add("Dataset", $"PUT {options.DatasetRegisterPath ?? DatasetService.DefaultRegisterPath}", string.Create(CultureInfo.InvariantCulture, $"registers the workflow's {route.Inputs.Count} input(s) under ids derived from the record's"), "each input dataset's version");
        }

        foreach (var stage in route.Stages)
        {
            var run = (options.WorkflowRunPath ?? WorkflowClient.DefaultRunPath).Replace("{workflow}", stage.Workflow, StringComparison.Ordinal);
            steps.Add("Workflow", $"POST {run}", $"triggers {stage.Workflow} with its execution context filled in{(stage.Contract is null ? string.Empty : $", checked against {stage.Contract}")}, then polls it", "what the run produced, which feeds the next stage");
        }

        steps.Add("Storage", "reads back what the runs created", "finds the records the route declares the runs create and reads them from storage, since a finished run is not a complete one");
    }

    private static string BusinessObject(string? kind)
        => kind is not null && kind.Split(':') is { Length: 4 } parts ? parts[2] : "the business object the kind names";

    /// <summary>The steps of a rehearsal as they are added, numbered in order.</summary>
    private sealed class Steps
    {
        private readonly List<PreviewStep> _steps = [];

        public IReadOnlyList<PreviewStep> All => _steps;

        public void Add(string service, string request, string what, string? returns = null, string? repeats = null, JsonObject? body = null)
            => _steps.Add(new PreviewStep
            {
                Order = _steps.Count + 1,
                Service = service,
                Request = request,
                What = what,
                Returns = returns,
                Repeats = repeats,
                Body = body,
            });
    }
}
