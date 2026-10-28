using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols.Ddms;

/// <summary>One row a record's rows file gives, in the order rows are posted: its table, the row it belongs to, and its columns.</summary>
/// <param name="Index">Its place in the order rows are posted.</param>
/// <param name="Table">The table it goes to.</param>
/// <param name="Parent">The place of the row it belongs to, or null for a row directly under the header record.</param>
/// <param name="Values">The columns the file gives it.</param>
/// <param name="Where">Where the file gives it, for a message (<c>rows.json: phi-k-synthesis-rt[0].phi-k-synthesis-phi-k[3]</c>).</param>
internal sealed record ReservoirManagementRow(int Index, ReservoirManagementTable Table, int? Parent, JsonObject Values, string Where);

/// <summary>What the Reservoir Management shape checked before its first request: the header collection, and the record's rows.</summary>
internal sealed record ReservoirManagementPlan(ReservoirManagementHeader Header, IReadOnlyList<ReservoirManagementRow> Rows, string Fingerprint);

/// <summary>
/// The Reservoir Management DDMS shape (osdu/specs/reservoir-management-ddms/INTEGRATION.md, section 8 in particular). A
/// record of one of the service's nine header kinds is a Storage record, written with <c>PUT /records</c>; the service's
/// own write is never called, since it sends the records to Storage without their ids, nor its delete, which purges.
///
/// <list type="number">
/// <item>The record's rows are the payload: a JSON file of the tables below the header collection, each an array of rows,
/// and each row an object of its columns and of the tables below it. Everything the service would refuse is checked
/// first: tables and columns it has, the columns a table requires, values of each column's type, and the columns the
/// route fills (the row's key, its parents' keys, the header's parent, the forecast base) left out.</item>
/// <item>Rows are posted to a header row the service holds in its own database, which only its list call creates, from
/// the records Search serves. So a record with rows is first taken in: the service's copy is read
/// (<c>GET /ddms/{collection}/{id}?catalog_entity_id=</c>), and while it is missing the list call runs
/// (<c>GET /ddms/{collection}/?parent_type=</c>), for at most <c>settleSeconds</c>, <c>pollSeconds</c> apart. The step
/// <c>sync</c> records the copy's parent and forecast base, which the rows take.</item>
/// <item>Each row is posted alone (<c>POST /ddms/{table}</c>) and the key the service answers with is fed to the rows below
/// it. A post is not idempotent, so the keys are recorded every 50 rows (steps <c>rows-{n}</c>), after a step
/// <c>rows-begin</c> that marks the posting as started; a later try takes the recorded keys, finds rows an earlier try
/// posted after its last record among the rows the service holds under the same parent, by their values, and posts the
/// rest.</item>
/// <item>The rows an earlier delivery of the record posted (the record's target state) are deleted once the new ones are
/// all posted (<c>DELETE /ddms/{table}/{key}?catalog_entity_id=</c>), the rows below before the rows above.</item>
/// </list>
///
/// Removal: the record scope soft-deletes the record in Storage and leaves the service's rows and its copy; the history
/// scope purges the record's earlier versions; everything deletes the rows, then purges the record. The service's copy
/// of the record stays in its database, since only its purging delete removes it.
/// </summary>
internal sealed class ReservoirManagementShape(DdmsShapeContext context) : IDdmsShape
{
    public const string SyncStep = "sync";
    public const string RowsBeginStep = "rows-begin";
    public const string RowsDoneStep = "rows-done";
    public const string RowsStepPrefix = "rows-";

    /// <summary>The rows of the record's delivery, as the target state keeps them: <c>table=key,key-key;table=key</c>, in the order they were posted.</summary>
    public const string RowsKey = "reservoirManagement.rows";

    /// <summary>The step a row whose post failed is recorded under, with the rows posted since the last block, before the try fails.</summary>
    public const string RowsPartialStep = "rows-partial";

    /// <summary>The step marking that every row is posted and the earlier delivery's rows are being deleted.</summary>
    public const string RowsReplaceStep = "rows-replace";

    // The artifacts of a Reservoir Management delivery (osdu/docs/reference/concepts/artifacts-and-undo.md,
    // Reservoir Management): each block of rows posted, a row whose post was not answered, and the replacement of
    // the earlier delivery's rows once every row is posted.
    private const string RowsSlotPrefix = "rows:";
    private const string PendingRowSlotPrefix = "rows-pending:";
    private const string ReplaceSlot = "rows-replace";

    /// <summary>The longest run of row keys one artifact's locator holds, so a locator is never cut short.</summary>
    private const int MaxRowsLocator = 900;

    /// <summary>The parent the service's copy of the record names, which the rows took.</summary>
    public const string ParentKey = "reservoirManagement.parent";

    /// <summary>How many rows a step records the keys of.</summary>
    public const int RowsPerStep = 50;

    /// <summary>The most rows one record posts: each is a request of its own.</summary>
    public const int MaxRows = 100_000;

    /// <summary>The largest rows file read.</summary>
    public const long MaxRowsFileBytes = 64L * 1024 * 1024;

    /// <summary>The parent types the service's list call takes (<c>parent_type</c>).</summary>
    public static readonly IReadOnlyList<string> ParentTypes = ["Reservoir", "Segment", "Sector"];

    private const string StorageVersionPath = "recordIdVersions[0]";

    /// <summary>The pattern the service reads a record's id by, per entity type (<c>catalog_entity_id</c>, [app/core/constants.py:27-39]).</summary>
    private static readonly IReadOnlyDictionary<string, Regex> IdPatterns = ReservoirManagementTables.Headers
        .Select(h => KindType(h.Kind))
        .Distinct(StringComparer.Ordinal)
        .ToDictionary(t => t, t => new Regex($@"^[\w\-\.]+:{Regex.Escape(t)}:[\w\-\.\:\%]+$", RegexOptions.CultureInvariant), StringComparer.Ordinal);

    private readonly OsduHttpClient _client = context.Client;
    private readonly ProtocolOptions _options = context.Options;
    private readonly DdmsRouting _routing = context.Routing;
    private readonly ILogger _logger = context.Logger;
    private readonly TimeProvider _time = context.Time;

    public async Task<object?> PrepareAsync(DeliveryWork work, DdmsRecordPaths paths, CancellationToken ct)
    {
        var route = RouteOf(paths);
        var header = HeaderOf(route);
        if (!work.DeliverPayload)
        {
            return new ReservoirManagementPlan(header, [], string.Empty);
        }

        if (header.Tables.Count == 0)
        {
            throw new RecordHeldException($"{Describe(route)} keeps no rows for {header.Segment} records, and the record comes with a rows file");
        }

        var payload = work.Payload ?? throw new RecordHeldException("the record needs its rows file but none is attached");
        var rows = await ReadRowsAsync(payload, header, ct).ConfigureAwait(false);
        if (rows.Count > 0)
        {
            _ = Partition(route);
            if (SyncProblem(route, header, work.TargetId, work.Document) is { } problem)
            {
                throw new RecordHeldException(problem);
            }
        }

        return new ReservoirManagementPlan(header, rows, Fingerprint(rows));
    }

    public async Task<DeliveryOutcome> SendAsync(DeliveryWork work, DdmsRecordPaths paths, object? prepared, CancellationToken ct)
    {
        var route = RouteOf(paths);
        var plan = prepared as ReservoirManagementPlan
            ?? throw new InvalidOperationException("The Reservoir Management shape was handed work another shape prepared.");
        var steps = new DeliverySteps(_time);
        var returned = new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = work.TargetId };
        var version = work.ExistingVersion;
        if (work.DeliverMetadata)
        {
            version = await WriteRecordAsync(work, route, steps, rowsFollow: work.DeliverPayload && plan.Rows.Count > 0, ct).ConfigureAwait(false);
        }

        string? detail = null;
        var sent = 0;
        if (work.DeliverPayload)
        {
            (sent, detail) = await WriteRowsAsync(work, route, plan, steps, returned, ct).ConfigureAwait(false);
        }

        if (version is { } v)
        {
            returned["version"] = v.ToString(CultureInfo.InvariantCulture);
        }

        return new DeliveryOutcome
        {
            MetadataDelivered = work.DeliverMetadata,
            PayloadDelivered = work.DeliverPayload,
            TargetVersion = version,
            ChunksSent = sent,
            Detail = detail,
            Returned = returned,
            Steps = steps.Steps,
        };
    }

    public Task<VerifyResult> VerifyAsync(DdmsRecordPaths paths, string targetId, long? expectedVersion, CancellationToken ct)
        => RecordWriter.VerifyAsync(_client, RouteOf(paths).RecordPath, targetId, expectedVersion, ct);

    public Task<JsonObject?> ReadAsync(DdmsRecordPaths paths, string targetId, CancellationToken ct)
        => RecordWriter.ReadAsync(_client, RouteOf(paths).RecordPath, targetId, ct);

    /// <summary>
    /// The record scope soft-deletes the record in Storage; the history scope purges its earlier versions; everything
    /// deletes the rows its deliveries posted, the rows below first, then purges the record. The service's copy of the
    /// record stays in its database, since the service removes it only with a purge of its own.
    /// </summary>
    public async Task<DeleteOutcome> DeleteAsync(DdmsRecordPaths paths, string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState, CancellationToken ct)
    {
        var route = RouteOf(paths);
        var storagePath = scope switch
        {
            RemovalScope.Record => _routing.StorageDeletePath,
            RemovalScope.History => _routing.HistoryPath,
            _ => _routing.StoragePurgePath,
        } ?? throw new RecordHeldException(
            $"{Describe(route)} keeps its records in Storage, and this flow does not say where the storage service is. Give the DDMS its root under target.ddms, "
            + "the flow's endpoint being the OSDU platform root.");
        var removed = 0;
        if (scope == RemovalScope.Everything)
        {
            removed = await RemoveRowsAsync(route, ReservoirManagementRows.Parse(targetState?.GetValueOrDefault(RowsKey)), ct).ConfigureAwait(false);
        }

        var outcome = await RecordWriter.DeleteAsync(_client, new RemovalPaths(storagePath, storagePath, storagePath), targetId, scope, ct).ConfigureAwait(false);
        return scope switch
        {
            RemovalScope.Record => outcome with { Detail = outcome.Detail + "; the service's rows and its copy of the record stay, since it has no reversible delete" },
            RemovalScope.Everything => outcome with
            {
                Deleted = outcome.Deleted || removed > 0,
                AlreadyGone = outcome.AlreadyGone && removed == 0,
                Detail = string.Create(CultureInfo.InvariantCulture, $"{removed} row(s) deleted from the Reservoir Management DDMS; ")
                    + (outcome.AlreadyGone ? "the record was already gone from OSDU" : "the record purged from OSDU")
                    + "; the service's copy of the record stays in its database, since only its own purge removes it",
            },
            _ => outcome,
        };
    }

    /// <summary>The service keeps no link on the record, so a rewritten record needs none carried.</summary>
    public bool CarryLink(JsonObject? stored, JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return true;
    }

    /// <summary>
    /// Why the service could not take <paramref name="document"/> into its database, which a record's rows need, or null:
    /// the service searches one kind per collection, reads the row's parent from <c>data.ParentObjectID</c>, and reads
    /// the record by an id of the collection's pattern.
    /// </summary>
    internal static string? SyncProblem(DdmsRoute route, ReservoirManagementHeader header, string targetId, JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(document);
        var kind = Text(document["kind"]);
        if (!string.Equals(kind, header.Kind, StringComparison.Ordinal))
        {
            return $"{Describe(route)} takes only {header.Kind} records into its database, where a {header.Segment} record's rows go, and the record's kind is '{kind}'; "
                + "deliver the record without its rows, or with that kind";
        }

        if (Text(document["data"]?["ParentObjectID"]) is null)
        {
            return $"the record names no data.ParentObjectID, which {Describe(route)} takes its copy's parent from; a record without one is never taken in, so its rows cannot be posted";
        }

        var type = KindType(header.Kind);
        return IdPatterns[type].IsMatch(targetId)
            ? null
            : $"the id '{targetId}' is not one {Describe(route)} reads a {header.Segment} record by (<partition>:{type}:<key>, the key of letters, digits, '_', '-', '.', ':' and '%')";
    }

    /// <summary>The parent type the list call is asked with: the first the parent id names, as the service's filter reads it.</summary>
    internal static string ParentTypeOf(string? parentObjectId)
        => ParentTypes.FirstOrDefault(t => parentObjectId?.Contains(t + ":", StringComparison.Ordinal) == true) ?? ParentTypes[0];

    /// <summary>The fingerprint of rows: their tables, parents and values, in order.</summary>
    internal static string Fingerprint(IReadOnlyList<ReservoirManagementRow> rows)
        => Hashing.ContentHash.OfParts([.. rows.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Table.Segment}|{r.Parent}|{r.Values.ToJsonString()}"))]);

    /// <summary>
    /// Reads the rows file of a record: JSON objects whose properties are tables below the header collection, each an
    /// array of rows, a row's properties being its columns and the tables below its table. Rows are ordered as posted: a
    /// row, then the rows below it.
    /// </summary>
    internal static async Task<IReadOnlyList<ReservoirManagementRow>> ReadRowsAsync(IPayloadSource payload, ReservoirManagementHeader header, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(header);
        var files = await payload.ListChunksAsync(ct).ConfigureAwait(false);
        if (files.Count == 0)
        {
            throw new RecordHeldException($"no rows file was found for the record (a .json file of the tables below {header.Segment}: {string.Join(", ", header.Tables)})");
        }

        var rows = new List<ReservoirManagementRow>();
        foreach (var file in files)
        {
            var name = FileUploads.FileName(file.Path);
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                throw new RecordHeldException($"the rows file {name} is not a .json file; the Reservoir Management DDMS takes rows as JSON");
            }

            var root = await ParseAsync(payload, file, name, ct).ConfigureAwait(false);
            if (root is not JsonObject tables)
            {
                throw new RecordHeldException($"the rows file {name} is not a JSON object of the tables below {header.Segment}");
            }

            AddTables(tables, header.Segment, null, rows, name + ": ", name);
        }

        return rows;
    }

    private static async Task<JsonNode?> ParseAsync(IPayloadSource payload, PayloadFile file, string name, CancellationToken ct)
    {
        if (file.Size > MaxRowsFileBytes)
        {
            throw new RecordHeldException(string.Create(CultureInfo.InvariantCulture, $"the rows file {name} is {file.Size} bytes, above the {MaxRowsFileBytes} a rows file may be"));
        }

        await using var stream = await payload.OpenAsync(file, ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxRowsFileBytes)
            {
                throw new RecordHeldException(string.Create(CultureInfo.InvariantCulture, $"the rows file {name} holds more than the {MaxRowsFileBytes} bytes a rows file may be"));
            }

            buffer.Write(chunk, 0, read);
        }

        try
        {
            return JsonNode.Parse(buffer.ToArray());
        }
        catch (JsonException ex)
        {
            throw new RecordHeldException($"the rows file {name} is not JSON: {ex.Message}", ex);
        }
    }

    /// <summary>Adds the rows of the tables <paramref name="node"/> gives below <paramref name="level"/>, in the file's order.</summary>
    private static void AddTables(JsonObject node, string level, int? parent, List<ReservoirManagementRow> rows, string where, string file)
    {
        var below = ReservoirManagementTables.Below(level);
        foreach (var (name, value) in node)
        {
            var table = below.FirstOrDefault(t => string.Equals(t.Segment, name, StringComparison.Ordinal))
                ?? throw new RecordHeldException(
                    $"{where}'{name}' is not a table below {level}; the tables there are {(below.Count == 0 ? "none" : string.Join(", ", below.Select(t => t.Segment)))}");
            if (value is not JsonArray array)
            {
                throw new RecordHeldException($"{where}{name} is not an array of rows");
            }

            for (var i = 0; i < array.Count; i++)
            {
                var place = string.Create(CultureInfo.InvariantCulture, $"{where}{name}[{i}]");
                if (array[i] is not JsonObject row)
                {
                    throw new RecordHeldException($"{place} is not an object of columns");
                }

                AddRow(row, table, parent, rows, place, file);
            }
        }
    }

    private static void AddRow(JsonObject row, ReservoirManagementTable table, int? parent, List<ReservoirManagementRow> rows, string place, string file)
    {
        if (rows.Count >= MaxRows)
        {
            throw new RecordHeldException(string.Create(CultureInfo.InvariantCulture, $"{file} holds more than the {MaxRows} rows a record posts, each in a request of its own"));
        }

        var values = new JsonObject();
        var index = rows.Count;
        var children = new JsonObject();
        rows.Add(new ReservoirManagementRow(index, table, parent, values, place));
        var owned = table.RouteColumns;
        var below = ReservoirManagementTables.Below(table.Segment);
        foreach (var (name, value) in row)
        {
            if (below.Any(t => string.Equals(t.Segment, name, StringComparison.Ordinal)))
            {
                children[name] = value?.DeepClone();
                continue;
            }

            if (owned.Contains(name, StringComparer.Ordinal))
            {
                throw new RecordHeldException($"{place} gives {name}, which the route fills from the rows above it; leave it out");
            }

            if (!table.Columns.TryGetValue(name, out var type))
            {
                throw new RecordHeldException(
                    $"{place} gives {name}, which is not a column of {table.Segment}"
                    + (below.Count > 0 ? $" nor a table below it ({string.Join(", ", below.Select(t => t.Segment))})" : string.Empty)
                    + $"; its columns are {string.Join(", ", table.Columns.Keys)}");
            }

            if (TypeProblem(value, type) is { } problem)
            {
                throw new RecordHeldException($"{place} gives {name} {problem}");
            }

            values[name] = value?.DeepClone();
        }

        foreach (var required in table.Required)
        {
            if (values[required] is null)
            {
                throw new RecordHeldException($"{place} gives no {required}, which every {table.Segment} row needs");
            }
        }

        AddTables(children, table.Segment, index, rows, place + ".", file);
    }

    /// <summary>What is wrong with a value for a column of <paramref name="type"/>, or null.</summary>
    private static string? TypeProblem(JsonNode? value, ReservoirManagementColumn type)
    {
        if (value is null)
        {
            return null;
        }

        if (value is not JsonValue scalar)
        {
            return "as an object or an array, and the column takes one value";
        }

        var kind = scalar.GetValueKind();
        return type switch
        {
            ReservoirManagementColumn.Number when kind != JsonValueKind.Number => "as something other than a number",
            ReservoirManagementColumn.WholeNumber when kind != JsonValueKind.Number || !scalar.TryGetValue<JsonElement>(out var element) || !element.TryGetInt64(out _)
                => "as something other than a whole number",
            ReservoirManagementColumn.Text when kind != JsonValueKind.String => "as something other than a string",
            ReservoirManagementColumn.Boolean when kind is not (JsonValueKind.True or JsonValueKind.False) => "as something other than true or false",
            ReservoirManagementColumn.Scalar when kind is not (JsonValueKind.String or JsonValueKind.Number) => "as something other than a string or a number",
            _ => null,
        };
    }

    /// <summary>
    /// Writes the record through Storage, with the data keys OSDU owns carried from the stored record. When rows follow, the
    /// record is the unit's until they are posted.
    /// </summary>
    private async Task<long?> WriteRecordAsync(DeliveryWork work, DdmsRoute route, DeliverySteps steps, bool rowsFollow, CancellationToken ct)
    {
        if (work.Completed(OsduDdmsProtocol.MetadataStep) is { } done)
        {
            steps.Resumed(OsduDdmsProtocol.MetadataStep, done);
            return done.TryGetValue("version", out var text) ? RecordWriter.ParseVersion(text) ?? work.ExistingVersion : work.ExistingVersion;
        }

        var document = (JsonObject)work.Document.DeepClone();
        if (_options.PreserveDataKeys.Count > 0 && work.ExistingVersion is not null)
        {
            await RecordWriter.PreserveAsync(_client, route.RecordPath, work.TargetId, document, _options.PreserveDataKeys, StepLinks.Of(work), ct).ConfigureAwait(false);
        }

        var started = steps.Now;
        var (version, status) = await RecordWriter.SendAsync(_client, _options with { VersionPath = StorageVersionPath }, route.RecordsPath, "PUT", document, ct).ConfigureAwait(false);
        if (version is null)
        {
            version = (await RecordWriter.VerifyAsync(_client, route.RecordPath, work.TargetId, null, ct).ConfigureAwait(false)).ObservedVersion;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = work.TargetId };
        if (version is { } v)
        {
            values["version"] = v.ToString(CultureInfo.InvariantCulture);
        }

        steps.Add(OsduDdmsProtocol.MetadataStep, started, status, values);
        IReadOnlyList<TargetArtifact> recordItself = rowsFollow ? [TargetArtifact.RecordWritten(work.TargetId, version, work.ExistingVersion)] : [];
        await work.ReportStepAsync(OsduDdmsProtocol.MetadataStep, values, recordItself, ct).ConfigureAwait(false);
        return version;
    }

    /// <summary>The service's copy of a header record, as the rows below it name it.</summary>
    private sealed record HeaderRow(string ParentObjectId, long? ForecastBase);

    /// <summary>
    /// Posts the record's rows, the recorded ones taken as posted and the ones an earlier try posted after its last
    /// record found by their values, then deletes the rows the record's earlier delivery posted. Returns how many rows
    /// this try posted, and what the outcome says.
    /// </summary>
    private async Task<(int Sent, string Detail)> WriteRowsAsync(
        DeliveryWork work, DdmsRoute route, ReservoirManagementPlan plan, DeliverySteps steps, Dictionary<string, string> returned, CancellationToken ct)
    {
        var earlier = ReservoirManagementRows.Parse(work.TargetState.GetValueOrDefault(RowsKey));
        var keys = new long?[plan.Rows.Count];
        var recorded = RecordedKeys(work, plan, keys, steps);
        if (work.Completed(RowsDoneStep) is { } done && done.GetValueOrDefault("fingerprint") == plan.Fingerprint && recorded == plan.Rows.Count)
        {
            steps.Resumed(RowsDoneStep, done);
            if (work.Completed(SyncStep) is { } synced)
            {
                returned[ParentKey] = synced.GetValueOrDefault(ReservoirManagementTables.ParentObjectColumn, string.Empty);
            }

            returned[RowsKey] = ReservoirManagementRows.Encode(plan.Rows.Select((r, i) => (r.Table.Segment, keys[i]!.Value)));
            return (0, string.Create(CultureInfo.InvariantCulture, $"{plan.Rows.Count} row(s) posted by an earlier try"));
        }

        var sent = 0;
        var found = 0;
        if (plan.Rows.Count > 0)
        {
            var header = await SyncAsync(work, route, plan, steps, ct).ConfigureAwait(false);
            returned[ParentKey] = header.ParentObjectId;
            var begun = work.Completed(RowsBeginStep) is { } begin && begin.GetValueOrDefault("fingerprint") == plan.Fingerprint;
            var next = recorded;
            if (begun)
            {
                steps.Resumed(RowsBeginStep, work.Completed(RowsBeginStep)!);
                var excluded = earlier.Select(e => (e.Table, e.Key)).ToHashSet();
                next = await FindPostedAsync(work, route, plan, header, keys, recorded, excluded, ct).ConfigureAwait(false);
                found = next - recorded;
            }
            else
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["fingerprint"] = plan.Fingerprint,
                    ["rows"] = plan.Rows.Count.ToString(CultureInfo.InvariantCulture),
                };
                steps.Add(RowsBeginStep, steps.Now, null, values);
                await work.ReportStepAsync(RowsBeginStep, values, ct).ConfigureAwait(false);
            }

            var started = steps.Now;
            if (next > recorded && (next % RowsPerStep == 0 || next == plan.Rows.Count))
            {
                await RecordBlockAsync(work, plan, keys, (next - 1) / RowsPerStep, steps, started, ct).ConfigureAwait(false);
                started = steps.Now;
            }

            for (var i = next; i < plan.Rows.Count; i++)
            {
                try
                {
                    keys[i] = await PostAsync(work, route, plan, header, keys, i, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Every row posted since the last block, and the row whose post may have landed without an answer, are the
                    // unit's before the try fails: a delivery that does not complete has each of them deleted.
                    await RecordPartialAsync(work, plan, header, keys, i, Unanswered(ex)).ConfigureAwait(false);
                    throw;
                }

                sent++;
                if ((i + 1) % RowsPerStep == 0 || i == plan.Rows.Count - 1)
                {
                    await RecordBlockAsync(work, plan, keys, i / RowsPerStep, steps, started, ct).ConfigureAwait(false);
                    started = steps.Now;
                }
            }
        }

        // The rows the record's earlier delivery posted give way to the new ones, the rows below first. A delivery that fails
        // from here may have deleted some of them, which nothing puts back: the step says the replacement began.
        var posted = plan.Rows.Select((r, i) => (r.Table.Segment, keys[i]!.Value)).ToHashSet();
        var replacing = earlier.Where(e => !posted.Contains((e.Table, e.Key))).ToList();
        if (replacing.Count > 0)
        {
            await work.ReportStepAsync(
                RowsReplaceStep,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["fingerprint"] = plan.Fingerprint, ["earlier"] = replacing.Count.ToString(CultureInfo.InvariantCulture) },
                [TargetArtifact.Created(ReplaceSlot, ArtifactRoles.Rows, work.TargetId, locator: string.Create(CultureInfo.InvariantCulture, $"{replacing.Count} earlier row(s) to delete"))],
                ct).ConfigureAwait(false);
        }

        var removed = await RemoveRowsAsync(route, replacing, ct).ConfigureAwait(false);
        var finished = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fingerprint"] = plan.Fingerprint,
            ["rows"] = plan.Rows.Count.ToString(CultureInfo.InvariantCulture),
            ["removed"] = removed.ToString(CultureInfo.InvariantCulture),
        };
        steps.Add(RowsDoneStep, steps.Now, null, finished);
        await work.ReportStepAsync(RowsDoneStep, finished, ct).ConfigureAwait(false);
        returned[RowsKey] = ReservoirManagementRows.Encode(plan.Rows.Select((r, i) => (r.Table.Segment, keys[i]!.Value)));
        _logger.LogInformation("Posted {Sent} of the {Rows} row(s) of {TargetId} to {Ddms}; {Removed} earlier row(s) deleted.", sent, plan.Rows.Count, work.TargetId, Describe(route), removed);

        var detail = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"{plan.Rows.Count} row(s), {sent} posted by this try"));
        if (recorded > 0 || found > 0)
        {
            detail.Append(string.Create(CultureInfo.InvariantCulture, $", {recorded + found} by an earlier one"));
            if (found > 0)
            {
                detail.Append(string.Create(CultureInfo.InvariantCulture, $" ({found} found among the service's rows)"));
            }
        }

        if (removed > 0)
        {
            detail.Append(string.Create(CultureInfo.InvariantCulture, $"; {removed} row(s) of an earlier delivery deleted"));
        }

        return (sent, detail.ToString());
    }

    /// <summary>Takes the keys the steps of earlier tries recorded, block by block from the first; returns how many rows they cover.</summary>
    private static int RecordedKeys(DeliveryWork work, ReservoirManagementPlan plan, long?[] keys, DeliverySteps steps)
    {
        var covered = 0;
        for (var block = 0; covered < plan.Rows.Count; block++)
        {
            var name = RowsStepPrefix + block.ToString(CultureInfo.InvariantCulture);
            if (work.Completed(name) is not { } values || values.GetValueOrDefault("fingerprint") != plan.Fingerprint)
            {
                break;
            }

            var listed = (values.GetValueOrDefault("keys") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
            var expected = Math.Min(RowsPerStep, plan.Rows.Count - covered);
            if (listed.Length != expected || listed.Any(k => !long.TryParse(k, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            {
                break;
            }

            for (var i = 0; i < listed.Length; i++)
            {
                keys[covered + i] = long.Parse(listed[i], NumberStyles.None, CultureInfo.InvariantCulture);
            }

            steps.Resumed(name, values);
            covered += listed.Length;
        }

        return covered;
    }

    private static async Task RecordBlockAsync(DeliveryWork work, ReservoirManagementPlan plan, long?[] keys, int block, DeliverySteps steps, DateTime started, CancellationToken ct)
    {
        var first = block * RowsPerStep;
        var last = Math.Min(plan.Rows.Count, first + RowsPerStep);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fingerprint"] = plan.Fingerprint,
            ["keys"] = string.Join(',', Enumerable.Range(first, last - first).Select(i => keys[i]!.Value.ToString(CultureInfo.InvariantCulture))),
        };
        var name = RowsStepPrefix + block.ToString(CultureInfo.InvariantCulture);
        steps.Add(name, started, null, values);
        await work.ReportStepAsync(name, values, BlockArtifacts(work, plan, keys, block, last), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records, before a try that failed posting row <paramref name="failed"/> ends, the rows it posted since the last block,
    /// and, when the post's answer was lost rather than a refusal, the row it may have made: which table, under which parent.
    /// </summary>
    private static async Task RecordPartialAsync(DeliveryWork work, ReservoirManagementPlan plan, HeaderRow header, long?[] keys, int failed, bool unanswered)
    {
        var block = failed / RowsPerStep;
        var artifacts = BlockArtifacts(work, plan, keys, block, failed).ToList();
        if (unanswered)
        {
            var row = plan.Rows[failed];
            var parent = row.Parent is { } above ? keys[above]!.Value.ToString(CultureInfo.InvariantCulture) : work.TargetId;
            artifacts.Add(TargetArtifact.Intent(PendingRowSlotPrefix + failed.ToString(CultureInfo.InvariantCulture), ArtifactRoles.Rows, $"{row.Table.Segment}|{parent}"));
        }

        if (artifacts.Count == 0)
        {
            return;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fingerprint"] = plan.Fingerprint,
            ["failedAt"] = failed.ToString(CultureInfo.InvariantCulture),
            ["parent"] = header.ParentObjectId,
        };

        // Written whatever stopped the try: what it records already happened in the service.
        await work.ReportStepAsync(RowsPartialStep, values, artifacts, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// The rows of block <paramref name="block"/> posted up to <paramref name="end"/> (exclusive), as artifacts whose locators
    /// name them by table and key in runs, a block split where its runs would not fit one locator.
    /// </summary>
    private static IReadOnlyList<TargetArtifact> BlockArtifacts(DeliveryWork work, ReservoirManagementPlan plan, long?[] keys, int block, int end)
    {
        var first = block * RowsPerStep;
        var rows = Enumerable.Range(first, Math.Max(0, end - first)).Where(i => keys[i] is not null).Select(i => (plan.Rows[i].Table.Segment, keys[i]!.Value)).ToList();
        var artifacts = new List<TargetArtifact>();
        var part = new List<(string, long)>();
        foreach (var row in rows)
        {
            part.Add(row);
            if (ReservoirManagementRows.Encode(part).Length > MaxRowsLocator)
            {
                part.RemoveAt(part.Count - 1);
                artifacts.Add(RowsArtifact(work, block, artifacts.Count, part));
                part = [row];
            }
        }

        if (part.Count > 0)
        {
            artifacts.Add(RowsArtifact(work, block, artifacts.Count, part));
        }

        return artifacts;
    }

    private static TargetArtifact RowsArtifact(DeliveryWork work, int block, int part, IReadOnlyList<(string Table, long Key)> rows)
        => TargetArtifact.Created(
            RowsSlotPrefix + block.ToString(CultureInfo.InvariantCulture) + (part == 0 ? string.Empty : ":" + part.ToString(CultureInfo.InvariantCulture)),
            ArtifactRoles.Rows,
            work.TargetId,
            locator: ReservoirManagementRows.Encode(rows));

    /// <summary>Whether a failed post may have landed without an answer: anything but the service's refusal of the row.</summary>
    private static bool Unanswered(Exception ex) => ex is not (RecordHeldException or OsduStatusException { StatusCode: >= 400 and < 500 });

    /// <summary>
    /// Undoes the rows unfinished deliveries posted under the record: every row a unit recorded is deleted, the rows below
    /// before the rows above; a row whose post was never answered is looked for among the rows the service holds under its
    /// parent, and deleted when exactly one row there is named by no delivery. The rows of the record's earlier delivery that
    /// a replacement deleted before the delivery failed cannot be put back; the record's next delivery posts its rows again.
    /// </summary>
    public async Task<IReadOnlyList<UndoResult>> UndoAsync(UndoWork work, DdmsRecordPaths paths, IReadOnlyList<UndoItem> items, CancellationToken ct)
    {
        var route = RouteOf(paths);
        var results = new List<UndoResult>(items.Count);
        var blocks = items
            .Where(i => i.Artifact.Slot.StartsWith(RowsSlotPrefix, StringComparison.Ordinal) && i.Artifact.Locator is not null)
            .OrderBy(i => i.ArtifactId)
            .ToList();
        var mine = blocks.SelectMany(b => ReservoirManagementRows.Parse(b.Artifact.Locator)).ToHashSet();
        var committed = ReservoirManagementRows.Parse(work.TargetState.GetValueOrDefault(RowsKey)).ToHashSet();

        // A post whose answer never came was sent after every row a block names, and its row can stand under one of them: it goes
        // first, so the rows it hangs from are free to go.
        foreach (var item in items.Except(blocks))
        {
            if (item.Artifact.Slot == ReplaceSlot)
            {
                results.Add(UndoResult.Kept(item, $"the delivery had posted every row and began deleting the earlier delivery's ({item.Artifact.Locator}); the ones it deleted cannot be put back, and the record's next delivery posts its rows again"));
                continue;
            }

            if (!item.Artifact.Slot.StartsWith(PendingRowSlotPrefix, StringComparison.Ordinal) || item.Artifact.Locator?.Split('|') is not [var segment, var parent])
            {
                results.Add(UndoResult.Kept(item, "the Reservoir Management shape makes nothing of this kind beside a record"));
                continue;
            }

            try
            {
                results.Add(await UndoUnansweredAsync(route, item, segment, parent, mine, committed, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ArtifactUndo.Answerable(ex, ct))
            {
                results.Add(UndoResult.Failed(item, ArtifactUndo.Redact(ex)));
            }
        }

        // The rows a unit posted go in the reverse of the order they were posted, across every block of it.
        if (blocks.Count > 0)
        {
            try
            {
                var rows = blocks.SelectMany(b => ReservoirManagementRows.Parse(b.Artifact.Locator)).ToList();
                var deleted = await RemoveRowsAsync(route, rows, ct).ConfigureAwait(false);
                results.AddRange(blocks.Select(b => deleted == 0
                    ? UndoResult.Gone(b, $"the service no longer holds the rows {b.Artifact.Locator}")
                    : UndoResult.Removed(b, $"rows {b.Artifact.Locator} deleted (the service has no reversible delete)")));
            }
            catch (Exception ex) when (ArtifactUndo.Answerable(ex, ct))
            {
                results.AddRange(blocks.Select(b => UndoResult.Failed(b, ArtifactUndo.Redact(ex))));
            }
        }

        return results;
    }

    /// <summary>A row whose post was never answered: the one row under its parent that no delivery names, if there is one.</summary>
    private async Task<UndoResult> UndoUnansweredAsync(
        DdmsRoute route, UndoItem item, string segment, string parent, IReadOnlySet<(string Table, long Key)> mine, IReadOnlySet<(string Table, long Key)> committed, CancellationToken ct)
    {
        if (ReservoirManagementTables.Table(segment) is not { } table)
        {
            return UndoResult.Kept(item, $"{segment} is not a table of the Reservoir Management DDMS this route knows, so its rows cannot be read");
        }

        var held = await HeldRowsAsync(route, table, parent, ct).ConfigureAwait(false);
        var unnamed = held
            .Select(r => KeyOf(r, table))
            .OfType<long>()
            .Where(k => !mine.Contains((segment, k)) && !committed.Contains((segment, k)))
            .Distinct()
            .ToList();
        if (unnamed.Count == 0)
        {
            return UndoResult.Gone(item, $"no row of {segment} under {parent} is outside what the deliveries name: the post never landed");
        }

        if (unnamed.Count > 1)
        {
            return UndoResult.Kept(item, string.Create(CultureInfo.InvariantCulture, $"{unnamed.Count} rows of {segment} under {parent} are named by no delivery ({string.Join(", ", unnamed.Take(10))}); which one the unanswered post made cannot be told, so none is deleted"));
        }

        await RemoveRowsAsync(route, [(segment, unnamed[0])], ct).ConfigureAwait(false);
        return UndoResult.Removed(item, string.Create(CultureInfo.InvariantCulture, $"row {unnamed[0]} of {segment} under {parent}, which the unanswered post made, deleted"));
    }

    /// <summary>The record is a Storage record: its reversible removal and its read are storage's.</summary>
    public RecordSide RecordSide(DdmsRecordPaths paths) => new()
    {
        Remove = _routing.StorageDeletePath is { } delete
            ? (id, token) => RecordWriter.DeleteAsync(_client, new RemovalPaths(delete, delete, delete), id, RemovalScope.Record, token)
            : null,
        RemoveRefusal = "the record is a Storage record, and this flow does not say where the storage service is; give the DDMS its root under target.ddms, the flow's endpoint being the OSDU platform root",
        Read = (id, token) => RecordWriter.ReadAsync(_client, RouteOf(paths).RecordPath, id, token),
    };

    /// <summary>
    /// Takes the service's copy of the record in: its copy is read, and while it is missing the list call, which copies
    /// the records Search serves, runs, for at most <c>settleSeconds</c>. A copy still missing then is left for the next
    /// try while Search does not serve the record, and holds the record when Search serves it, since the list call takes
    /// only the first 100 records of the kind Search returns and fails for a record it cannot copy.
    /// </summary>
    private async Task<HeaderRow> SyncAsync(DeliveryWork work, DdmsRoute route, ReservoirManagementPlan plan, DeliverySteps steps, CancellationToken ct)
    {
        if (work.Completed(SyncStep) is { } done && done.TryGetValue(ReservoirManagementTables.ParentObjectColumn, out var knownParent))
        {
            steps.Resumed(SyncStep, done);
            return new HeaderRow(
                knownParent,
                long.TryParse(done.GetValueOrDefault(ReservoirManagementTables.ForecastBaseColumn), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var forecastBase) ? forecastBase : null);
        }

        if (!work.DeliverMetadata)
        {
            var stored = await RecordWriter.VerifyAsync(_client, route.RecordPath, work.TargetId, null, ct).ConfigureAwait(false);
            if (stored.Outcome == VerifyOutcome.Missing)
            {
                throw new RecordHeldException($"Storage does not hold {work.TargetId}, whose rows this delivery posts under the service's copy of it; redeliver the record to write it again");
            }
        }

        var settings = route.Service.ReservoirManagement ?? new ReservoirManagementSettings();
        var started = steps.Now;
        var deadline = _time.GetUtcNow() + TimeSpan.FromSeconds(settings.SettleSeconds);
        var parentType = ParentTypeOf(Text(work.Document["data"]?["ParentObjectID"]));
        var lists = 0;
        string? listed = null;
        while (true)
        {
            // Another record's list call may have taken this one in already.
            var row = await ReadCopyAsync(route, plan.Header, work.TargetId, ct).ConfigureAwait(false);
            if (row is null)
            {
                listed = await ListAsync(route, plan.Header, parentType, ct).ConfigureAwait(false);
                lists++;
                row = await ReadCopyAsync(route, plan.Header, work.TargetId, ct).ConfigureAwait(false);
            }

            if (row is not null)
            {
                if (plan.Header.Tables.Any(t => ReservoirManagementTables.Table(t)!.ForecastBase) && row.ForecastBase is null)
                {
                    throw new RecordHeldException($"{Describe(route)} holds a copy of {work.TargetId} without the forecast base its rows name");
                }

                var values = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ReservoirManagementTables.ParentObjectColumn] = row.ParentObjectId,
                    ["lists"] = lists.ToString(CultureInfo.InvariantCulture),
                };
                if (row.ForecastBase is { } forecastBase)
                {
                    values[ReservoirManagementTables.ForecastBaseColumn] = forecastBase.ToString(CultureInfo.InvariantCulture);
                }

                steps.Add(SyncStep, started, null, values);
                await work.ReportStepAsync(SyncStep, values, ct).ConfigureAwait(false);
                return row;
            }

            if (_time.GetUtcNow() >= deadline)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(settings.PollSeconds), _time, ct).ConfigureAwait(false);
        }

        var answered = listed is null ? string.Empty : $" (its list call answered: {listed})";
        if (!await SearchServesAsync(plan.Header, work.TargetId, ct).ConfigureAwait(false))
        {
            throw new DeliveryException(
                $"Search does not serve {work.TargetId} yet, and {Describe(route)} takes into its database only the records Search serves{answered}; the next try asks again.");
        }

        throw new RecordHeldException(
            $"Search serves {work.TargetId}, and {Describe(route)} did not take it into its database{answered}. Its list call takes only the first 100 {plan.Header.Kind} "
            + "records Search returns, and fails for a record without a pool row for its parent or the reference rows its README lists, which an operator inserts; "
            + (plan.Header.Segment == "kr-synthesis" ? "and it cannot take in any Kr synthesis, whose row an operator inserts too; " : string.Empty)
            + "release the record once the service holds its copy");
    }

    /// <summary>The service's copy of the record (<c>GET /ddms/{collection}/{id}?catalog_entity_id=</c>), or null.</summary>
    private async Task<HeaderRow?> ReadCopyAsync(DdmsRoute route, ReservoirManagementHeader header, string targetId, CancellationToken ct)
    {
        var url = Query(
            _client.Url(Root(route) + header.Segment + "/" + UrlPath.EscapeSegment(targetId)),
            ("data_partition_id", Partition(route)),
            ("catalog_entity_id", targetId));
        var result = await _client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);
        if ((int)result.Status == 404)
        {
            return null;
        }

        var row = OsduHttpClient.ParseJson(result, url);
        if (row.ValueKind != JsonValueKind.Object)
        {
            throw new DeliveryException($"{Describe(route)} answered the read of its copy of {targetId} with something other than a row: {Preview(result)}");
        }

        var parent = row.TryGetProperty(ReservoirManagementTables.ParentObjectColumn, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        if (string.IsNullOrEmpty(parent))
        {
            throw new RecordHeldException($"{Describe(route)} holds a copy of {targetId} without the parent its rows name");
        }

        long? forecastBase = row.TryGetProperty(ReservoirManagementTables.ForecastBaseColumn, out var b) && b.ValueKind == JsonValueKind.Number && b.TryGetInt64(out var value) ? value : null;
        return new HeaderRow(parent, forecastBase);
    }

    /// <summary>
    /// Runs the list call (<c>GET /ddms/{collection}/?parent_type=</c>), which copies the records Search serves into the
    /// service's database. Returns what it answered when the copy failed (500), and null otherwise.
    /// </summary>
    private async Task<string?> ListAsync(DdmsRoute route, ReservoirManagementHeader header, string parentType, CancellationToken ct)
    {
        var url = Query(_client.Url(Root(route) + header.Segment + "/"), ("data_partition_id", Partition(route)), ("parent_type", parentType));
        var result = await _client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 404, 500 }, ct).ConfigureAwait(false);
        return (int)result.Status == 500 ? $"500 {HeaderRedaction.RedactMessage(OsduError.Describe(result.BodyText))}" : null;
    }

    /// <summary>Whether Search serves the record under the kind the service searches.</summary>
    private async Task<bool> SearchServesAsync(ReservoirManagementHeader header, string targetId, CancellationToken ct)
    {
        var url = _client.Url(_options.SearchQueryPath ?? OsduManifestProtocol.DefaultSearchQueryPath);
        var body = new JsonObject
        {
            ["kind"] = header.Kind,
            ["query"] = "id:\"" + targetId + "\"",
            ["limit"] = 1,
            ["returnedFields"] = new JsonArray(JsonValue.Create("id")),
        };
        var result = await _client.SendJsonAsync(HttpMethod.Post, url, body, null, ct, idempotent: true).ConfigureAwait(false);
        return JsonPathReader.SelectElements(OsduHttpClient.ParseJson(result, url), "results[*]")
            .Any(hit => hit.ValueKind == JsonValueKind.Object && hit.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == targetId);
    }

    /// <summary>
    /// Finds rows an earlier try posted after the last block it recorded, among the rows the service holds under the same
    /// parent (<c>GET /ddms/{table}/header-entity/{key}?header_entity_id=</c>): a held row with the values the row would be
    /// posted with, whose key no row has taken. Rows are posted in order, so the first row not found ends the search.
    /// Returns the place of the first row still to post.
    /// </summary>
    private async Task<int> FindPostedAsync(
        DeliveryWork work, DdmsRoute route, ReservoirManagementPlan plan, HeaderRow header, long?[] keys, int from, IReadOnlySet<(string Table, long Key)> excluded, CancellationToken ct)
    {
        var end = Math.Min(plan.Rows.Count, from + RowsPerStep);
        var taken = new HashSet<(string, long)>(Enumerable.Range(0, from).Select(i => (plan.Rows[i].Table.Segment, keys[i]!.Value)));
        var held = new Dictionary<(string Table, string Parent), List<JsonObject>>();
        for (var i = from; i < end; i++)
        {
            var row = plan.Rows[i];
            var body = Body(work.TargetId, row, header, keys);
            var parent = row.Parent is { } above ? keys[above]!.Value.ToString(CultureInfo.InvariantCulture) : work.TargetId;
            if (!held.TryGetValue((row.Table.Segment, parent), out var candidates))
            {
                candidates = await HeldRowsAsync(route, row.Table, parent, ct).ConfigureAwait(false);
                held[(row.Table.Segment, parent)] = candidates;
            }

            long? match = null;
            foreach (var candidate in candidates)
            {
                if (KeyOf(candidate, row.Table) is { } key && !taken.Contains((row.Table.Segment, key)) && !excluded.Contains((row.Table.Segment, key)) && Holds(candidate, body))
                {
                    match = key;
                    break;
                }
            }

            if (match is not { } found)
            {
                return i;
            }

            keys[i] = found;
            taken.Add((row.Table.Segment, found));
        }

        return end;
    }

    /// <summary>The rows the service holds under a parent (a header id, or a row key), as it answers them.</summary>
    private async Task<List<JsonObject>> HeldRowsAsync(DdmsRoute route, ReservoirManagementTable table, string parent, CancellationToken ct)
    {
        var url = Query(_client.Url(Root(route) + table.Segment + "/header-entity/" + UrlPath.EscapeSegment(parent)), ("header_entity_id", parent));
        var result = await _client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);
        if ((int)result.Status == 404 || result.Body.Length == 0)
        {
            return [];
        }

        JsonNode? answered;
        try
        {
            answered = JsonNode.Parse(result.Body);
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"{Describe(route)} answered the rows of {table.Segment} under {parent} with a body that is not JSON: {Preview(result)}", ex);
        }

        return answered is JsonArray rows
            ? rows.OfType<JsonObject>().ToList()
            : throw new DeliveryException($"{Describe(route)} answered the rows of {table.Segment} under {parent} with something other than an array: {Preview(result)}");
    }

    /// <summary>Posts one row (<c>POST /ddms/{table}</c>) and returns the key the service gave it.</summary>
    private async Task<long> PostAsync(DeliveryWork work, DdmsRoute route, ReservoirManagementPlan plan, HeaderRow header, long?[] keys, int index, CancellationToken ct)
    {
        var row = plan.Rows[index];
        var url = _client.Url(Root(route) + row.Table.Segment);
        var body = Body(work.TargetId, row, header, keys);
        var result = await _client.SendJsonAsync(HttpMethod.Post, url, body, new HashSet<int> { 404, 422 }, ct, idempotent: false).ConfigureAwait(false);
        switch ((int)result.Status)
        {
            case 404:
                throw new RecordHeldException(
                    $"{Describe(route)} no longer holds the row {row.Where} belongs to ({HeaderRedaction.RedactMessage(OsduError.Describe(result.BodyText))}); "
                    + "its rows were changed outside this flow, so release the record to deliver them again");
            case 422:
                throw new RecordHeldException(
                    $"{Describe(route)} refused {row.Where} ({HeaderRedaction.RedactMessage(OsduError.Describe(result.BodyText))}): a column it requires is missing, "
                    + "a value is not one its database takes, or a reference row it names does not exist");
        }

        JsonNode? answered;
        try
        {
            answered = JsonNode.Parse(result.Body);
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"{Describe(route)} answered the post of {row.Where} with a body that is not JSON; the next try looks for the row among the service's rows.", ex);
        }

        return answered is JsonObject stored && KeyOf(stored, row.Table) is { } key
            ? key
            : throw new DeliveryException(
                $"{Describe(route)} answered the post of {row.Where} without the row's {row.Table.KeyColumn} ({Preview(result)}); the next try looks for the row among the service's rows.");
    }

    /// <summary>A row as it is posted: its columns, with the header record, its parent, the row above it and the forecast base filled in.</summary>
    private static JsonObject Body(string targetId, ReservoirManagementRow row, HeaderRow header, long?[] keys)
    {
        var body = (JsonObject)row.Values.DeepClone();
        body[row.Table.HeaderColumn] = targetId;
        body[ReservoirManagementTables.ParentObjectColumn] = header.ParentObjectId;
        if (row.Parent is { } parent)
        {
            body[row.Table.ParentColumn] = keys[parent] ?? throw new InvalidOperationException($"The row above {row.Where} has no key yet.");
        }

        if (row.Table.ForecastBase)
        {
            body[ReservoirManagementTables.ForecastBaseColumn] = header.ForecastBase ?? throw new InvalidOperationException($"The forecast base of {row.Where} is not known.");
        }

        return body;
    }

    /// <summary>
    /// Deletes rows (<c>DELETE /ddms/{table}/{key}?catalog_entity_id=</c>) in the reverse of the order they were posted, so
    /// the rows below go before the rows above; returns how many the service had. The service answers 200 either way,
    /// with a message that starts with <c>Error</c> for a row it no longer had.
    /// </summary>
    private async Task<int> RemoveRowsAsync(DdmsRoute route, IReadOnlyList<(string Table, long Key)> rows, CancellationToken ct)
    {
        var deleted = 0;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            var (table, key) = rows[i];
            var text = key.ToString(CultureInfo.InvariantCulture);
            var url = Query(_client.Url(Root(route) + table + "/" + text), ("catalog_entity_id", text));
            var result = await _client.SendJsonAsync(HttpMethod.Delete, url, null, null, ct, idempotent: true).ConfigureAwait(false);
            if (!AnswersError(result))
            {
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>Whether the service answered with its failure form: an array whose first string starts with <c>Error</c>.</summary>
    private static bool AnswersError(HttpFetchResult result)
    {
        try
        {
            return JsonNode.Parse(result.Body) is JsonArray { Count: > 0 } answer
                   && answer[0] is JsonValue first
                   && first.GetValueKind() == JsonValueKind.String
                   && first.GetValue<string>().StartsWith("Error", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Whether a row the service holds has every value <paramref name="body"/> would post.</summary>
    private static bool Holds(JsonObject held, JsonObject body)
    {
        foreach (var (name, value) in body)
        {
            if (!held.TryGetPropertyValue(name, out var stored) || !SameValue(stored, value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameValue(JsonNode? stored, JsonNode? sent)
    {
        if (stored is null || sent is null)
        {
            return stored is null && sent is null;
        }

        if (stored is not JsonValue left || sent is not JsonValue right)
        {
            return false;
        }

        var (leftKind, rightKind) = (left.GetValueKind(), right.GetValueKind());
        if (leftKind == JsonValueKind.Number && rightKind == JsonValueKind.Number)
        {
            var a = left.ToJsonString();
            var b = right.ToJsonString();
            return decimal.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && decimal.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                ? x == y
                : double.Parse(a, CultureInfo.InvariantCulture).Equals(double.Parse(b, CultureInfo.InvariantCulture));
        }

        return leftKind == rightKind && left.ToJsonString() == right.ToJsonString();
    }

    private static long? KeyOf(JsonObject row, ReservoirManagementTable table)
        => row[table.KeyColumn] is JsonValue value
           && value.GetValueKind() == JsonValueKind.Number
           && long.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var key)
            ? key
            : null;

    private static ReservoirManagementHeader HeaderOf(DdmsRoute route)
        => ReservoirManagementTables.Header(route.Collection.Segment)
            ?? throw new RecordHeldException($"{Describe(route)} has no header collection {route.Collection.Segment}");

    private string Partition(DdmsRoute route)
        => _client.Header(FlowMapper.PartitionHeader) is { Length: > 0 } partition
            ? partition
            : throw new RecordHeldException($"{Describe(route)} takes the partition as data_partition_id, and the flow sends no {FlowMapper.PartitionHeader} to take it from");

    private static string Root(DdmsRoute route) => (route.Service.Root ?? string.Empty) + DdmsRoute.ReservoirManagementPrefix;

    private static string Describe(DdmsRoute route) => DdmsRouting.Describe(route.Service);

    private static DdmsRoute RouteOf(DdmsRecordPaths paths)
        => paths.Route ?? throw new InvalidOperationException($"{paths.EntityType} records have no Reservoir Management DDMS to go to.");

    private static Uri Query(Uri url, params (string Name, string Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            url = OsduHttpClient.WithQuery(url, name, value);
        }

        return url;
    }

    /// <summary>The entity type part of a kind (<c>work-product-component--PersistedCollection</c>).</summary>
    private static string KindType(string kind) => kind.Split(':') is { Length: 4 } parts ? parts[2] : kind;

    private static string Preview(HttpFetchResult result)
    {
        var text = HeaderRedaction.RedactMessage(result.BodyText);
        return text.Length <= 200 ? text : text[..200] + "...";
    }

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;
}

/// <summary>
/// The rows a record's delivery posted, as its target state keeps them: runs of one table's keys in posting order,
/// <c>table=key,first-last;table=key</c>, a range standing for consecutive keys.
/// </summary>
internal static class ReservoirManagementRows
{
    public static string Encode(IEnumerable<(string Table, long Key)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var text = new StringBuilder();
        string? table = null;
        long? first = null;
        long last = 0;
        void Close()
        {
            if (first is { } start)
            {
                text.Append(start == last ? start.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{start}-{last}"));
            }
        }

        foreach (var (name, key) in rows)
        {
            if (!string.Equals(name, table, StringComparison.Ordinal))
            {
                Close();
                if (table is not null)
                {
                    text.Append(';');
                }

                text.Append(name).Append('=');
                (table, first, last) = (name, key, key);
                continue;
            }

            if (key == last + 1)
            {
                last = key;
                continue;
            }

            Close();
            text.Append(',');
            (first, last) = (key, key);
        }

        Close();
        return text.ToString();
    }

    /// <summary>The rows <paramref name="text"/> names, in posting order; nothing for an empty or unreadable value.</summary>
    public static IReadOnlyList<(string Table, long Key)> Parse(string? text)
    {
        var rows = new List<(string, long)>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return rows;
        }

        foreach (var run in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = run.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0 || ReservoirManagementTables.Table(run[..equals]) is not { } table)
            {
                return [];
            }

            foreach (var part in run[(equals + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var dash = part.IndexOf('-', StringComparison.Ordinal);
                if (dash < 0 && long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var single))
                {
                    rows.Add((table.Segment, single));
                }
                else if (dash > 0
                         && long.TryParse(part[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
                         && long.TryParse(part[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var end)
                         && end >= start
                         && end - start < ReservoirManagementShape.MaxRows)
                {
                    for (var key = start; key <= end; key++)
                    {
                        rows.Add((table.Segment, key));
                    }
                }
                else
                {
                    return [];
                }
            }
        }

        return rows;
    }
}
