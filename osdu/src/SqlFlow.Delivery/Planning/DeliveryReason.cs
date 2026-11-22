using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlFlow.Delivery.Planning;

/// <summary>
/// The inputs whose move made a record render and be sent (osdu/docs/reference/concepts/delivery-reasons.md), as the flags
/// <c>osdu.Attempt.Triggers</c> keeps of a delivered attempt so a submission's deliveries are counted by them. Each flag is
/// one <see cref="ReasonTrigger.Kind"/>; the values are stored, so they never change.
/// </summary>
[Flags]
public enum TriggerKinds
{
    None = 0,
    FirstDelivery = 1 << 0,
    SourceChanged = 1 << 1,
    ChildOrLookupChanged = 1 << 2,
    MappingChanged = 1 << 3,
    TemplateChanged = 1 << 4,
    ParametersChanged = 1 << 5,
    EnrichmentChanged = 1 << 6,
    CacheChanged = 1 << 7,
    Requested = 1 << 8,
    Drift = 1 << 9,
    Forced = 1 << 10,
    PayloadChanged = 1 << 11,
    OtherInputChanged = 1 << 12,
}

/// <summary>The kinds of <see cref="ReasonTrigger"/>, as the ledger, the API and the GUI name them, in the order a reason lists them.</summary>
public static class ReasonKinds
{
    public const string FirstDelivery = "first-delivery";
    public const string SourceChanged = "source-changed";
    public const string ChildOrLookupChanged = "child-or-lookup-changed";
    public const string MappingChanged = "mapping-changed";
    public const string TemplateChanged = "template-changed";
    public const string ParametersChanged = "parameters-changed";
    public const string EnrichmentChanged = "enrichment-changed";
    public const string CacheChanged = "cache-changed";
    public const string Requested = "requested";
    public const string Drift = "drift";
    public const string Forced = "forced";
    public const string PayloadChanged = "payload-changed";

    /// <summary>
    /// None of the inputs the ledger compares moved, and the document still renders differently: the render read something
    /// they do not cover (a record it searches for in OSDU, its parent, a child it links), or it replaces work already queued.
    /// </summary>
    public const string OtherInputChanged = "other-input-changed";

    /// <summary>Every kind with its flag, in the order a reason lists its triggers.</summary>
    public static IReadOnlyList<(string Kind, TriggerKinds Flag)> All { get; } =
    [
        (FirstDelivery, TriggerKinds.FirstDelivery),
        (Requested, TriggerKinds.Requested),
        (Drift, TriggerKinds.Drift),
        (Forced, TriggerKinds.Forced),
        (SourceChanged, TriggerKinds.SourceChanged),
        (ChildOrLookupChanged, TriggerKinds.ChildOrLookupChanged),
        (MappingChanged, TriggerKinds.MappingChanged),
        (TemplateChanged, TriggerKinds.TemplateChanged),
        (ParametersChanged, TriggerKinds.ParametersChanged),
        (EnrichmentChanged, TriggerKinds.EnrichmentChanged),
        (CacheChanged, TriggerKinds.CacheChanged),
        (PayloadChanged, TriggerKinds.PayloadChanged),
        (OtherInputChanged, TriggerKinds.OtherInputChanged),
    ];

    public static TriggerKinds Flag(string kind)
    {
        foreach (var (name, flag) in All)
        {
            if (string.Equals(name, kind, StringComparison.Ordinal))
            {
                return flag;
            }
        }

        return TriggerKinds.None;
    }

    /// <summary>Where <paramref name="kind"/> stands among <see cref="All"/>; an unknown kind last.</summary>
    public static int Order(string kind)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (string.Equals(All[i].Kind, kind, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return All.Count;
    }

    /// <summary>The kinds <paramref name="flags"/> holds, in <see cref="All"/>'s order.</summary>
    public static IReadOnlyList<string> Of(TriggerKinds flags) => All.Where(k => (flags & k.Flag) != 0).Select(k => k.Kind).ToList();
}

/// <summary>
/// One input that moved between the version the ledger holds for a record and the render a run sends: what kind of input, what
/// it was and what it is now, and what else names it. Values from the source never appear; what is named is the ledger's own
/// bookkeeping (a mapping reference and fingerprint, a template or cache version, a parameter's name, a row's update moment).
/// </summary>
public sealed record ReasonTrigger
{
    /// <summary>One of <see cref="ReasonKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>What the input was: a mapping reference, a template or cache version, a row's update moment, a payload hash.</summary>
    public string? From { get; init; }

    /// <summary>What the input is in this render.</summary>
    public string? To { get; init; }

    /// <summary>For a mapping: the fingerprint of the document the version OSDU holds was rendered with.</summary>
    public string? FromFingerprint { get; init; }

    /// <summary>For a mapping: the fingerprint of the document this render read.</summary>
    public string? ToFingerprint { get; init; }

    /// <summary>For a mapping: the estate commit the version OSDU holds was planned at, when its run knew it.</summary>
    public string? FromCommit { get; init; }

    /// <summary>For a mapping: the estate commit this run executes, when it knows it.</summary>
    public string? ToCommit { get; init; }

    /// <summary>What the trigger names: the parameters or enrichments that moved, the cache changes that reached the record, the change keys that force it.</summary>
    public IReadOnlyList<string> Names { get; init; } = [];

    /// <summary>How many more it names than <see cref="Names"/> lists.</summary>
    public int More { get; init; }

    /// <summary>What happened, in a sentence, where the fields alone do not say it.</summary>
    public string? Detail { get; init; }

    /// <summary>When: the moment a plan was asked for, or the one verification found the difference.</summary>
    public DateTime? At { get; init; }

    /// <summary>For a request: the intervention that asked, as the audit trail numbers it.</summary>
    public long? ActivityId { get; init; }

    /// <summary>For a request: what the intervention was (redeliver, release, rerender, sync, ...).</summary>
    public string? Activity { get; init; }

    /// <summary>For a request: who asked.</summary>
    public string? Actor { get; init; }

    internal JsonObject ToJson()
    {
        var node = new JsonObject { ["kind"] = Kind };
        Put(node, "from", From);
        Put(node, "to", To);
        Put(node, "fromFingerprint", FromFingerprint);
        Put(node, "toFingerprint", ToFingerprint);
        Put(node, "fromCommit", FromCommit);
        Put(node, "toCommit", ToCommit);
        if (Names.Count > 0)
        {
            node["names"] = new JsonArray([.. Names.Select(n => (JsonNode?)JsonValue.Create(n))]);
        }

        if (More > 0)
        {
            node["more"] = More;
        }

        Put(node, "detail", Detail);
        if (At is { } at)
        {
            node["at"] = DateTime.SpecifyKind(at, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
        }

        if (ActivityId is { } activity)
        {
            node["activityId"] = activity;
        }

        Put(node, "activity", Activity);
        Put(node, "actor", Actor);
        return node;
    }

    internal static ReasonTrigger? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o || Text(o, "kind") is not { } kind)
        {
            return null;
        }

        return new ReasonTrigger
        {
            Kind = kind,
            From = Text(o, "from"),
            To = Text(o, "to"),
            FromFingerprint = Text(o, "fromFingerprint"),
            ToFingerprint = Text(o, "toFingerprint"),
            FromCommit = Text(o, "fromCommit"),
            ToCommit = Text(o, "toCommit"),
            Names = o["names"] is JsonArray names ? [.. names.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>()] : [],
            More = o["more"] is JsonValue more && more.TryGetValue<int>(out var m) ? m : 0,
            Detail = Text(o, "detail"),
            At = Text(o, "at") is { } at && DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null,
            ActivityId = o["activityId"] is JsonValue id && id.TryGetValue<long>(out var a) ? a : null,
            Activity = Text(o, "activity"),
            Actor = Text(o, "actor"),
        };
    }

    /// <summary>The trigger in a few words, for a reason's one line.</summary>
    internal string Describe()
    {
        var names = Names.Count == 0 ? string.Empty : string.Join(", ", Names) + (More > 0 ? string.Create(CultureInfo.InvariantCulture, $" and {More} more") : string.Empty);
        return Kind switch
        {
            ReasonKinds.FirstDelivery => "first delivery",
            ReasonKinds.SourceChanged => "source row changed",
            ReasonKinds.ChildOrLookupChanged => "a child or lookup row changed",
            ReasonKinds.MappingChanged => (From, To) switch
            {
                ({ } from, { } to) when !string.Equals(from, to, StringComparison.Ordinal) => $"mapping {from} changed to {to}{Commit(ToCommit)}",
                (_, { } to) => $"mapping {to} changed{Commit(ToCommit)}",
                _ => $"mapping changed{Commit(ToCommit)}",
            },
            ReasonKinds.TemplateChanged => To is { } template ? $"template moved to {template}" : "template changed",
            ReasonKinds.ParametersChanged => names.Length > 0 ? $"parameters changed: {names}" : "parameters changed",
            ReasonKinds.EnrichmentChanged => names.Length > 0 ? $"enrichment changed: {names}" : "enrichment changed",
            ReasonKinds.CacheChanged => To is { } version ? $"cache moved to {version}" : "cache changed",
            ReasonKinds.Requested => (Activity, Actor) switch
            {
                ({ } activity, { } actor) => $"{activity} asked by {actor}",
                ({ } activity, null) => $"{activity} asked",
                _ => "asked to be planned again",
            },
            ReasonKinds.Drift => "OSDU differed from the ledger",
            ReasonKinds.Forced => names.Length > 0 ? $"forced by {names}" : "forced",
            ReasonKinds.PayloadChanged => "payload changed",
            ReasonKinds.OtherInputChanged => "rendered differently with no compared input moved",
            _ => Kind,
        };
    }

    /// <summary>An estate commit as an operator reads one, its first seven characters, in brackets after what it dates.</summary>
    internal static string Commit(string? commit)
        => string.IsNullOrWhiteSpace(commit) ? string.Empty : $" ({(commit.Length > 7 ? commit[..7] : commit)})";

    private static void Put(JsonObject node, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            node[name] = value;
        }
    }

    private static string? Text(JsonObject o, string name)
        => o[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}

/// <summary>
/// Why a run sent a record (osdu/docs/reference/concepts/delivery-reasons.md): what it sends, every
/// input that moved since the version the ledger holds (<see cref="Triggers"/>), and which paths of its document changed
/// (<see cref="Paths"/>). The plan builds it from what it already holds, it travels with the document in the work batch, and
/// the delivered attempt keeps it under <c>reason</c> in its result.
/// </summary>
public sealed record DeliveryReason
{
    /// <summary>What the try sends: <c>create</c>, <c>update-metadata</c>, <c>update-payload</c> or <c>update-both</c>.</summary>
    public required string Action { get; init; }

    /// <summary>Every input that moved, in <see cref="ReasonKinds.All"/>'s order; never empty.</summary>
    public required IReadOnlyList<ReasonTrigger> Triggers { get; init; }

    /// <summary>The paths of the document that changed, or why they were not compared; null when the document is not sent.</summary>
    public PathChanges? Paths { get; init; }

    /// <summary>The kinds of <see cref="Triggers"/>, as <c>osdu.Attempt.Triggers</c> keeps them.</summary>
    public TriggerKinds Kinds => Triggers.Aggregate(TriggerKinds.None, (all, t) => all | ReasonKinds.Flag(t.Kind));

    /// <summary>The paths listed in a reason's one line; the rest are counted.</summary>
    public const int PathsInSummary = 3;

    /// <summary>
    /// The reason in one line, as the record's timeline titles a delivery with it: the triggers, then the paths that changed
    /// (<c>mapping WellLog@1.4.0 changed (852db64); acl.viewers changed</c>).
    /// </summary>
    public string Summary()
    {
        var line = string.Join(", ", Triggers.Select(t => t.Describe()));
        if (Paths is not { Compared: true } paths)
        {
            return line;
        }

        if (paths.Total == 0)
        {
            return $"{line}; no path changed";
        }

        var shown = paths.Listed.Take(PathsInSummary).Select(c => $"{c.Path} {Word(c.Change)}");
        var rest = paths.Total - Math.Min(PathsInSummary, paths.Listed.Count);
        return $"{line}; {string.Join(", ", shown)}" + (rest > 0 ? string.Create(CultureInfo.InvariantCulture, $" and {rest} more path(s)") : string.Empty);
    }

    /// <summary>The reason as the attempt's result keeps it, with its one line, so every reader shows the same words.</summary>
    public JsonObject ToJson()
    {
        var node = new JsonObject
        {
            ["action"] = Action,
            ["summary"] = Summary(),
            ["triggers"] = new JsonArray([.. Triggers.Select(t => (JsonNode?)t.ToJson())]),
        };
        if (Paths is { } paths)
        {
            var p = new JsonObject { ["compared"] = paths.Compared };
            if (paths.NotCompared is { } why)
            {
                p["why"] = why;
            }
            else
            {
                p["added"] = paths.Added;
                p["removed"] = paths.Removed;
                p["changed"] = paths.Changed;
                p["listed"] = new JsonArray([.. paths.Listed.Select(c => (JsonNode?)new JsonObject { ["path"] = c.Path, ["change"] = Word(c.Change) })]);
                if (paths.More > 0)
                {
                    p["more"] = paths.More;
                }
            }

            node["paths"] = p;
        }

        return node;
    }

    /// <summary>The reason <see cref="ToJson"/> wrote, or null for a node that is not one.</summary>
    public static DeliveryReason? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o || o["action"] is not JsonValue action || !action.TryGetValue<string>(out var named)
            || o["triggers"] is not JsonArray triggers)
        {
            return null;
        }

        var listed = triggers.Select(ReasonTrigger.FromJson).OfType<ReasonTrigger>().ToList();
        if (listed.Count == 0)
        {
            return null;
        }

        PathChanges? paths = null;
        if (o["paths"] is JsonObject p)
        {
            paths = p["why"] is JsonValue why && why.TryGetValue<string>(out var text)
                ? PathChanges.Without(text)
                : new PathChanges
                {
                    Added = Count(p, "added"),
                    Removed = Count(p, "removed"),
                    Changed = Count(p, "changed"),
                    Listed = p["listed"] is JsonArray changes
                        ? [.. changes.OfType<JsonObject>().Select(c => (Path: c["path"]?.GetValue<string>(), Change: Kind(c["change"]?.GetValue<string>())))
                            .Where(c => c.Path is not null && c.Change is not null)
                            .Select(c => new PathChange(c.Path!, c.Change!.Value))]
                        : [],
                };
        }

        return new DeliveryReason { Action = named, Triggers = listed, Paths = paths };
    }

    /// <summary>The reason a delivered attempt's result keeps, or null for one written before the ledger kept reasons.</summary>
    public static DeliveryReason? OfResult(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(resultJson) is JsonObject result ? FromJson(result["reason"]) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The action words of <see cref="Action"/> for a planned action that sends something.</summary>
    public static string ActionOf(PlannedAction action) => action switch
    {
        PlannedAction.Create => "create",
        PlannedAction.UpdateMetadata => "update-metadata",
        PlannedAction.UpdatePayload => "update-payload",
        PlannedAction.UpdateBoth => "update-both",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Only an action that sends something has a delivery reason."),
    };

    /// <summary>A path change as a reason words it.</summary>
    public static string Word(PathChangeKind change) => change switch
    {
        PathChangeKind.Added => "added",
        PathChangeKind.Removed => "removed",
        _ => "changed",
    };

    private static PathChangeKind? Kind(string? word) => word switch
    {
        "added" => PathChangeKind.Added,
        "removed" => PathChangeKind.Removed,
        "changed" => PathChangeKind.Changed,
        _ => null,
    };

    private static int Count(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
}
