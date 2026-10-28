using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Validation;
using ContentHash = SqlFlow.Delivery.Hashing.ContentHash;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// One dataset a step of <c>enrich:</c> registers for a record (osdu/docs/reference/flow/enrich-files.md):
/// its id and kind, where its files are and their content hash, what the step's dataset fragment rendered into it, and the
/// media type its files go as. <see cref="Hash"/> is what the record's ledger keeps of it: a change to the files or to the
/// dataset record moves it, and the delivery registers the dataset again.
/// </summary>
/// <param name="Step">The step's name.</param>
/// <param name="DatasetId">The dataset's OSDU id, made from the record's id and the step.</param>
/// <param name="Kind">The dataset kind it is registered as.</param>
/// <param name="Location">Where its files are, as a payload location (folder and pattern).</param>
/// <param name="FilesHash">The content hash of its files, from the step's hash column.</param>
/// <param name="Record">What the step's dataset fragment rendered into the dataset record, as canonical JSON (<c>{}</c> for none).</param>
/// <param name="ContentType">The media type its files are uploaded as; null takes the flow's.</param>
public sealed record EnrichPart(string Step, string DatasetId, string Kind, string Location, string FilesHash, string Record, string? ContentType)
{
    /// <summary>The role every step part is forced by in a redelivery: <c>files</c> sends a record's step files again too.</summary>
    public const string Role = PayloadParts.Files;

    /// <summary>The hash of the dataset record as the part renders it: its kind and what the fragment wrote, without the files.</summary>
    public string RecordHash => ContentHash.Of(Kind + "\n" + Record);

    /// <summary>The hash of the whole part: the dataset record and its files.</summary>
    public string Hash => ContentHash.Of(RecordHash + "\n" + FilesHash);
}

/// <summary>
/// What a delivery of a record does with its step parts, as the plan decided it: the parts, whether the record's document is
/// the one OSDU holds (its datasets alone moved, so the record is not written again), and whether every part goes whatever
/// its hash says (the record was asked to be sent again, or never was).
/// </summary>
/// <param name="Parts">The datasets the record's steps register.</param>
/// <param name="DocumentUnchanged">True when the document is the one the last delivery wrote.</param>
/// <param name="Forced">True when every part goes, whatever the hash its last delivery kept.</param>
public sealed record EnrichWork(IReadOnlyList<EnrichPart> Parts, bool DocumentUnchanged, bool Forced)
{
    /// <summary>A record whose steps register no datasets.</summary>
    public static EnrichWork None { get; } = new([], false, false);

    /// <summary>
    /// What each step set aside on its holds (<c>onHold: step</c>) composed into this document, as its kept contribution
    /// (<see cref="KeptStep"/>), by the step's name: what the record goes ahead with on a later render the step holds in.
    /// </summary>
    public IReadOnlyDictionary<string, string> Keep { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The datasets the steps set aside on this render registered on an earlier one, which the record keeps linking.</summary>
    public IReadOnlyList<string> KeptDatasets { get; init; } = [];

    /// <summary>Whether a delivery has anything of the steps to do: datasets to register or keep linking, or contributions to keep.</summary>
    public bool IsEmpty => Parts.Count == 0 && Keep.Count == 0 && KeptDatasets.Count == 0;
}

/// <summary>A step set aside on its holds whose files could not be found where its row says, with why: the plan renders the record again with it held.</summary>
public sealed record StepAside(string Step, string Reason);

/// <summary>
/// The step parts of a record (osdu/docs/reference/flow/enrich-files.md): resolved by the plan from the rows
/// of each step that registers files, written beside the document into its work batch line, and read back by the delivery that
/// registers them. A record whose steps register no files has none, and its document's hash is the document's alone.
/// </summary>
public static class EnrichParts
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// <paramref name="render"/> with the parts of its steps' datasets, each dataset's files found where its row says and their
    /// content hash read from the step's hash column, and its document's hash taken over the document and every part, so a
    /// change to a dataset's files or its record plans the record again though the document is the same. With them, what the
    /// delivery does (<see cref="EnrichWork"/>): the document is unchanged when, taken with the parts its last delivery kept
    /// (<paramref name="state"/>'s target state), it hashes as the record was delivered; and every part goes when the record
    /// was never delivered or was asked to be sent again (its document's hash, or its payload's on a flow with
    /// <paramref name="hasPayload"/>, cleared; or a redelivery naming files). Or why the record is held: a row naming no
    /// folder, or no content hash; or, for a step set aside on its holds (<c>onHold: step</c>), the step and that reason, which
    /// the plan renders the record again with the step held for. What each step set aside on its holds composed is kept with
    /// the work, and the datasets of one set aside go on being linked at the hash their last delivery kept.
    /// </summary>
    public static (RenderResult Render, string? Hold, StepAside? Aside) Resolve(
        FlowDefinition flow, IReadOnlyDictionary<string, string> values, SourceRecord record, RenderResult render, IReadOnlyList<ResolvedStep> steps,
        RecordState? state, bool hasPayload)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(steps);
        var parts = new List<EnrichPart>();
        var keep = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var contribution in render.Enrichment)
        {
            var step = steps.First(s => string.Equals(s.Step.Name, contribution.Step, StringComparison.Ordinal)).Step;
            if (contribution.SetAside || contribution.NotRendered)
            {
                continue;
            }

            foreach (var dataset in contribution.Datasets)
            {
                var row = record.ScopeRows(step.Name)[dataset.Row];
                var files = step.Files!;
                var location = PayloadLocations.ResolveStepFiles(flow, values, row, step);
                var hash = row.GetString(files.Payload.HashColumn!)?.Trim();
                var refusal = location.Refusal ?? (string.IsNullOrEmpty(hash)
                    ? $"files take their content hash from column '{files.Payload.HashColumn}', which row {dataset.Row + 1} of the step's dataset leaves empty"
                    : null);
                if (refusal is not null)
                {
                    return step.OnHold == EnrichHold.Step
                        ? (render, null, new StepAside(step.Name, refusal))
                        : (render, $"enrich.{step.Name}: {refusal}", null);
                }

                parts.Add(new EnrichPart(step.Name, dataset.Id, dataset.Kind, location.Location!.Value.ToString(), hash!, CanonicalJson.ToString(dataset.Record), files.ContentType));
            }

            if (step.OnHold == EnrichHold.Step)
            {
                keep[step.Name] = new KeptStep(contribution.Composed, contribution.Datasets.Select(d => d.Id).ToList()).ToText();
            }
        }

        var kept = JsonMerge.ToValues(state?.TargetStateJson);
        var keptDatasets = render.Enrichment.Where(c => c.SetAside).SelectMany(c => c.KeptDatasets).Distinct(StringComparer.Ordinal).ToList();
        var work = new EnrichWork(parts, false, false) { Keep = keep, KeptDatasets = keptDatasets };
        if (work.IsEmpty)
        {
            return (render, null, null);
        }

        // The document's hash takes in every dataset the record links: those registered now at their new hash, those of a step
        // set aside at the hash their last delivery kept.
        var linked = parts.Select(p => (p.DatasetId, p.Hash)).Concat(keptDatasets.Select(id => (id, kept.GetValueOrDefault(StateKey(id)) ?? string.Empty))).ToList();
        var delivered = state?.MetadataHash;
        var previous = Datasets(kept).Select(id => (id, kept.GetValueOrDefault(StateKey(id)))).Where(p => p.Item2 is not null).Select(p => (p.id, p.Item2!));
        var unchanged = delivered is not null && string.Equals(Fold(render.Canonical, previous), delivered, StringComparison.Ordinal);
        var forced = delivered is null
            || (hasPayload && state!.PayloadHash is null)
            || PayloadParts.RedeliverRoles(state!.PayloadHash)?.Contains(EnrichPart.Role) == true;
        return (render with
        {
            MetadataHash = linked.Count == 0 ? render.MetadataHash : Fold(render.Canonical, linked),
            StepWork = work with { DocumentUnchanged = unchanged, Forced = forced },
        }, null, null);
    }

    /// <summary>
    /// The hash of a record's document taken with its step parts: the document's own when it has none, else the hash of the
    /// canonical document followed by a line per part, <c>id=hash</c>, in id order, so the same parts hash the same in any order.
    /// </summary>
    public static string Fold(string canonical, IEnumerable<(string Id, string Hash)> parts)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        ArgumentNullException.ThrowIfNull(parts);
        var text = new StringBuilder(canonical);
        foreach (var (id, hash) in parts.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            text.Append('\n').Append(id).Append('=').Append(hash);
        }

        return ContentHash.Of(text.ToString());
    }

    /// <summary>What a delivery does with the parts, as the work batch line carries it: compact JSON, or null for a record without parts.</summary>
    public static string? Encode(EnrichWork? work)
    {
        return work is null || work.IsEmpty ? null : JsonSerializer.Serialize(work, Json);
    }

    /// <summary>What a work batch line says a delivery does with the parts; none for a line without them.</summary>
    /// <exception cref="DeliveryException">The line carries parts that do not read.</exception>
    public static EnrichWork Decode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return EnrichWork.None;
        }

        try
        {
            var work = JsonSerializer.Deserialize<EnrichWork>(text, Json);
            return work is { Parts: not null } ? work : throw new DeliveryException("The step parts of a work batch line name no parts.");
        }
        catch (JsonException ex)
        {
            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"The step parts of a work batch line cannot be read: {ex.Message}"), ex);
        }
    }

    /// <summary>The target state value holding the hash a step's dataset was last delivered with.</summary>
    public static string StateKey(string datasetId) => "enrich." + datasetId;

    /// <summary>The target state value holding the content hash of the files a step's dataset was last registered with.</summary>
    public static string FilesStateKey(string datasetId) => "enrich." + datasetId + ".files";

    /// <summary>The target state value listing, as a JSON array, every dataset the record's steps registered and the record links.</summary>
    public const string DatasetsValue = "enrich.datasets";

    /// <summary>The target state value holding the hash of the document the record was last written with, its parts left out.</summary>
    public const string DocumentValue = "enrich.document";

    /// <summary>The datasets the record's steps registered, as its target state lists them.</summary>
    public static IReadOnlyList<string> Datasets(IReadOnlyDictionary<string, string>? state)
    {
        if (state is null || !state.TryGetValue(DatasetsValue, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(text) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>A list of dataset ids as the target state keeps it.</summary>
    public static string DatasetsText(IEnumerable<string> ids) => JsonSerializer.Serialize(ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());

    /// <summary>The record a step part registers, as its fragment rendered it.</summary>
    public static JsonObject RecordOf(EnrichPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return JsonNode.Parse(part.Record) as JsonObject ?? new JsonObject();
    }
}
