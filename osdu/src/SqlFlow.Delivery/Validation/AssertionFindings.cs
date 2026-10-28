using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Validation;

/// <summary>
/// One value that broke an assertion of its mapping (osdu/docs/reference/flow/mapping-assertions.md).
/// </summary>
/// <param name="At">The property every record shares (<c>data.Curves[].Mnemonic</c>).</param>
/// <param name="Path">
/// Where the value was found: in the record (<c>data.Curves[3].Mnemonic</c>), or for the incoming stage in the row
/// (<c>dataset.curves[3].curve_id</c>).
/// </param>
/// <param name="Assertion">What the assertion is called: its name, or the label read off its condition.</param>
/// <param name="Stage">The stage it judges, <c>record</c> or <c>incoming</c>.</param>
/// <param name="OnFail">What its failure does: <c>hold</c>, <c>report</c> or <c>omit</c>.</param>
/// <param name="Message">Why the value fails it.</param>
/// <param name="Value">The value, as text, clipped and redacted.</param>
public sealed record AssertionFailure(string At, string Path, string Assertion, string Stage, string OnFail, string Message, string Value);

/// <summary>
/// What a mapping's assertions found of one record: how many judgements were made, how many failed and what each failure
/// did, and the failures themselves, listed up to a bound and counted exactly. It travels with the document it judges
/// (the work batch line), joins the record's verdict at the check before sending, and is what the explorer, a preview and
/// an assertion flow say of a record.
/// </summary>
public sealed record AssertionFindings
{
    /// <summary>The most failures one record lists; every failure is counted.</summary>
    public const int MaxListed = 50;

    /// <summary>The most failures a hold's message names.</summary>
    private const int FailuresInMessage = 3;

    /// <summary>The longest value a failure keeps.</summary>
    internal const int MaxValueChars = 200;

    /// <summary>The longest message a failure keeps.</summary>
    internal const int MaxMessageChars = 500;

    /// <summary>The mapping whose assertions were judged, as <c>Name@version</c>.</summary>
    public required string Mapping { get; init; }

    /// <summary>How many judgements were made: one per assertion and value judged.</summary>
    public long Checked { get; init; }

    /// <summary>How many judgements failed.</summary>
    public long Failed { get; init; }

    /// <summary>How many failures hold the record (<c>onFail: hold</c>).</summary>
    public long Held { get; init; }

    /// <summary>How many failures were sent as they are (<c>onFail: report</c>).</summary>
    public long Reported { get; init; }

    /// <summary>How many failures left their value out (<c>onFail: omit</c>).</summary>
    public long Omitted { get; init; }

    /// <summary>The failures, at most <see cref="MaxListed"/>, in the order they were found.</summary>
    public IReadOnlyList<AssertionFailure> Failures { get; init; } = [];

    /// <summary>True when a failure holds the record.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Holds => Held > 0;

    /// <summary>
    /// What the assertions of several mappings found of one record, as one: a record of a flow with <c>enrich:</c> steps is
    /// judged by its flow's mapping and by each step's fragment (osdu/docs/reference/flow/enrich.md). The mappings are named
    /// in order, the judgements and failures counted together, and the failures listed in order up to <see cref="MaxListed"/>.
    /// Null when none of them states an assertion; the one findings as they are when only one does.
    /// </summary>
    public static AssertionFindings? Combine(IReadOnlyList<AssertionFindings> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return findings.Count switch
        {
            0 => null,
            1 => findings[0],
            _ => new AssertionFindings
            {
                Mapping = string.Join(", ", findings.Select(f => f.Mapping).Distinct(StringComparer.Ordinal)),
                Checked = findings.Sum(f => f.Checked),
                Failed = findings.Sum(f => f.Failed),
                Held = findings.Sum(f => f.Held),
                Reported = findings.Sum(f => f.Reported),
                Omitted = findings.Sum(f => f.Omitted),
                Failures = findings.SelectMany(f => f.Failures).Take(MaxListed).ToList(),
            },
        };
    }

    /// <summary>One line saying what the assertions found.</summary>
    public string Summary()
    {
        if (Failed == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Checked} assertion judgement(s) of {Mapping} met");
        }

        var actions = new List<string>(3);
        if (Held > 0)
        {
            actions.Add(string.Create(CultureInfo.InvariantCulture, $"{Held} holding"));
        }

        if (Reported > 0)
        {
            actions.Add(string.Create(CultureInfo.InvariantCulture, $"{Reported} reported"));
        }

        if (Omitted > 0)
        {
            actions.Add(string.Create(CultureInfo.InvariantCulture, $"{Omitted} left out"));
        }

        var first = Failures.Count > 0 ? $", first {Failures[0].At} {Quoted(Failures[0].Assertion)}" : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{Failed} of {Checked} assertion judgement(s) of {Mapping} failed ({string.Join(", ", actions)}){first}");
    }

    /// <summary>
    /// The error a record held for its assertions carries: the property and the assertion of the first failures that hold,
    /// the values single-quoted, so the records one assertion holds are one issue (<see cref="Ledger.ProblemSignature"/>), and
    /// how to send it anyway.
    /// </summary>
    public string HoldMessage()
    {
        var holding = Failures.Where(f => f.OnFail == AssertionWords.Hold).ToList();
        var named = string.Join("; ", holding.Take(FailuresInMessage).Select(f => $"{f.At} fails {Quoted(f.Assertion)} with '{Clip(f.Value.Replace('\'', '`'), 60)}'"));
        var more = Held > Math.Min(holding.Count, FailuresInMessage)
            ? string.Create(CultureInfo.InvariantCulture, $"; and {Held - Math.Min(holding.Count, FailuresInMessage)} more")
            : string.Empty;
        return $"assertion: the record fails what its mapping {Mapping} asserts: {named}{more}. Its mapping holds a record that fails them (onFail: hold), so it is held; "
            + "release it to send this document as it is, or correct the source or the mapping.";
    }

    /// <summary>The findings as a verdict, a work item and an answer carry them, the failures listed up to <paramref name="listed"/>.</summary>
    public JsonObject ToJson(int listed = MaxListed)
    {
        var json = new JsonObject
        {
            ["mapping"] = Mapping,
            ["checked"] = Checked,
            ["failed"] = Failed,
            ["held"] = Held,
            ["reported"] = Reported,
            ["omitted"] = Omitted,
            ["failures"] = new JsonArray(Failures.Take(Math.Max(0, listed)).Select(f => (JsonNode?)new JsonObject
            {
                ["at"] = f.At,
                ["path"] = f.Path,
                ["assertion"] = f.Assertion,
                ["stage"] = f.Stage,
                ["onFail"] = f.OnFail,
                ["message"] = f.Message,
                ["value"] = f.Value,
            }).ToArray()),
        };
        if (listed < Failures.Count)
        {
            json["shortened"] = true;
        }

        return json;
    }

    /// <summary>The findings <paramref name="node"/> holds, or null when it holds none.</summary>
    public static AssertionFindings? FromJson(JsonNode? node)
    {
        if (node is not JsonObject json || Text(json, "mapping") is not { } mapping)
        {
            return null;
        }

        return new AssertionFindings
        {
            Mapping = mapping,
            Checked = Number(json, "checked"),
            Failed = Number(json, "failed"),
            Held = Number(json, "held"),
            Reported = Number(json, "reported"),
            Omitted = Number(json, "omitted"),
            Failures = (json["failures"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(f => new AssertionFailure(
                    Text(f, "at") ?? string.Empty,
                    Text(f, "path") ?? string.Empty,
                    Text(f, "assertion") ?? string.Empty,
                    Text(f, "stage") ?? AssertionWords.Record,
                    Text(f, "onFail") ?? AssertionWords.Hold,
                    Text(f, "message") ?? string.Empty,
                    Text(f, "value") ?? string.Empty))
                .ToList(),
        };
    }

    /// <summary>The findings as compact JSON text, as a work batch line carries them.</summary>
    public string ToText() => ToJson().ToJsonString();

    /// <summary>The findings a work batch line's text holds, or null for none or text that is not findings.</summary>
    public static AssertionFindings? FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return FromJson(JsonNode.Parse(text));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Quoted(string assertion) => "\"" + assertion.Replace('"', '\'') + "\"";

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    private static string? Text(JsonObject json, string name)
        => json[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static long Number(JsonObject json, string name)
        => json[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number
            && long.TryParse(value.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;

    /// <summary>A failure's text as it is kept: clipped, then redacted as every stored error is.</summary>
    internal static string Kept(string text, int max) => HeaderRedaction.RedactMessage(Clip(text, max));
}

/// <summary>
/// Collects what a mapping's assertions find of one record while it is rendered or read: every judgement counted, every
/// failure counted by what it does, the first <see cref="AssertionFindings.MaxListed"/> listed. It belongs to one record,
/// never to a renderer the plan's parallel workers share.
/// </summary>
public sealed class AssertionLog
{
    private readonly List<AssertionFailure> _failures = [];

    public AssertionLog(string mapping)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapping);
        Mapping = mapping;
    }

    public string Mapping { get; }

    public long Checked { get; private set; }

    public long Failed { get; private set; }

    public long Held { get; private set; }

    public long Reported { get; private set; }

    public long Omitted { get; private set; }

    /// <summary>Counts one judgement, whatever it came to.</summary>
    public void Judged() => Checked++;

    /// <summary>Records one failure of <paramref name="assertion"/>, counted under what it does.</summary>
    /// <param name="at">The property every record shares.</param>
    /// <param name="path">Where the value was found.</param>
    /// <param name="assertion">The assertion it broke.</param>
    /// <param name="action">What the failure does: the assertion's action, or what it came to when the value could not be left out.</param>
    /// <param name="value">The value, as text.</param>
    /// <param name="reason">Why it fails.</param>
    public void Fail(string at, string path, NodeAssertion assertion, AssertionAction action, string value, string reason)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        Failed++;
        switch (action)
        {
            case AssertionAction.Hold:
                Held++;
                break;
            case AssertionAction.Report:
                Reported++;
                break;
            default:
                Omitted++;
                break;
        }

        if (_failures.Count < AssertionFindings.MaxListed)
        {
            _failures.Add(new AssertionFailure(
                at,
                AssertionFindings.Kept(path, AssertionFindings.MaxValueChars),
                assertion.Label,
                AssertionWords.Of(assertion.Stage),
                AssertionWords.Of(action),
                AssertionFindings.Kept(reason, AssertionFindings.MaxMessageChars),
                AssertionFindings.Kept(value, AssertionFindings.MaxValueChars)));
        }
    }

    /// <summary>What the assertions found, so far.</summary>
    public AssertionFindings Findings() => new()
    {
        Mapping = Mapping,
        Checked = Checked,
        Failed = Failed,
        Held = Held,
        Reported = Reported,
        Omitted = Omitted,
        Failures = [.. _failures],
    };
}
