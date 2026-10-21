using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Http;

namespace SqlFlow.Delivery.Validation;

/// <summary>What a check of a record against its schema came to.</summary>
public enum ValidationOutcome
{
    /// <summary>Every rule that applies was checked and met.</summary>
    Valid,

    /// <summary>At least one rule is broken, or the record refers to a record that does not exist.</summary>
    Invalid,

    /// <summary>No rule is broken, and some part of the record could not be checked.</summary>
    Unverified,

    /// <summary>The try sent nothing of the record's document, so nothing was checked.</summary>
    NotValidated,
}

/// <summary>The names outcomes are written with, in the ledger, an attempt's result and the API.</summary>
public static class ValidationOutcomes
{
    public const string Valid = "valid";
    public const string Invalid = "invalid";
    public const string Unverified = "unverified";
    public const string NotValidated = "notValidated";

    public static string Name(ValidationOutcome outcome) => outcome switch
    {
        ValidationOutcome.Valid => Valid,
        ValidationOutcome.Invalid => Invalid,
        ValidationOutcome.Unverified => Unverified,
        _ => NotValidated,
    };

    /// <summary>The outcome a name writes, or null for one that names none.</summary>
    public static ValidationOutcome? Parse(string? name) => name switch
    {
        Valid => ValidationOutcome.Valid,
        Invalid => ValidationOutcome.Invalid,
        Unverified => ValidationOutcome.Unverified,
        NotValidated => ValidationOutcome.NotValidated,
        _ => null,
    };
}

/// <summary>Which schema a record was checked against, and where it came from.</summary>
/// <param name="Kind">The kind whose schema it is.</param>
/// <param name="Version">The schema's content version (<see cref="Snapshots.SchemaSnapshot.Version"/>).</param>
/// <param name="Source"><c>template</c> for a template OSDU Delivery holds (a template folder's, or a saved one), <c>schema-service</c> for the partition's Schema service.</param>
public sealed record VerdictSchema(string Kind, string Version, string Source)
{
    public const string Template = "template";
    public const string SchemaService = "schema-service";
}

/// <summary>An id a record refers to whose record was not found, and what said so.</summary>
public sealed record MissingReference(string Id, string At, string Path, string Detail);

/// <summary>What was found of the records a record refers to, counted by where.</summary>
public sealed record VerdictReferences
{
    public static VerdictReferences None { get; } = new();

    /// <summary>The distinct ids the record names at its relationships.</summary>
    public int Total { get; init; }

    public int InLedger { get; init; }

    public int InCache { get; init; }

    public int InOsdu { get; init; }

    public int Missing { get; init; }

    public int NotChecked { get; init; }

    /// <summary>Whether the record names more ids than a check collects, so the counts cover the first ones only.</summary>
    public bool Cut { get; init; }

    /// <summary>The ids not found, at most <see cref="ValidationVerdict.MaxMissingListed"/>.</summary>
    public IReadOnlyList<MissingReference> MissingListed { get; init; } = [];
}

/// <summary>
/// What a check of one record against its schema came to (osdu/docs/reference/concepts/preflight.md, What a verdict
/// holds): the outcome, the schema, the problems and the parts not checked (listed up to a bound, counted
/// exactly), what was found of the records it refers to, and whether an operator accepted it. An attempt carries the
/// verdict of the document it sent or held, and the record keeps the last outcome, so whether a record was validated,
/// when, against what and with what result is in the ledger.
/// </summary>
public sealed record ValidationVerdict
{
    /// <summary>
    /// The version of the checks a verdict was reached by. It moves when what a check finds changes meaning, so verdicts
    /// reached by different releases can be told apart.
    /// </summary>
    public const string CurrentRulesVersion = "1";

    /// <summary>The most missing ids a verdict lists.</summary>
    public const int MaxMissingListed = 20;

    /// <summary>The most characters a verdict takes in an attempt's result; past it the listings are shortened.</summary>
    public const int MaxJsonChars = 16_000;

    /// <summary>The most problems a hold's message names.</summary>
    private const int ProblemsInMessage = 3;

    public required ValidationOutcome Outcome { get; init; }

    public VerdictSchema? Schema { get; init; }

    /// <summary>How many rules were applied.</summary>
    public long Rules { get; init; }

    public IReadOnlyList<SchemaFinding> Problems { get; init; } = [];

    public long ProblemCount { get; init; }

    public IReadOnlyList<SchemaFinding> Unverified { get; init; } = [];

    public long UnverifiedCount { get; init; }

    public VerdictReferences References { get; init; } = VerdictReferences.None;

    /// <summary>What the schema states that no check asserts (<see cref="SchemaRules.Notes"/>), and why nothing was checked when it was not.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Whether an operator's release accepted this verdict for the document, so it was sent whatever it says.</summary>
    public bool Accepted { get; init; }

    /// <summary>
    /// What the mapping's assertions found of the document (osdu/docs/reference/flow/mapping-assertions.md), or null when
    /// its mapping states none. They are judged beside the schema, not by it: <see cref="Outcome"/> says what the schema
    /// check came to, and a failure here holds the record when its assertion says so, whatever the outcome.
    /// </summary>
    public AssertionFindings? Assertions { get; init; }

    public string RulesVersion { get; init; } = CurrentRulesVersion;

    public DateTime CheckedUtc { get; init; }

    /// <summary>
    /// The verdict of a record checked against <paramref name="rules"/>: the schema's findings, with each id whose record was
    /// not found added as a problem of the rule <c>reference</c>. Every message and value is redacted as a stored error is.
    /// <paramref name="rules"/> is null for a record no schema could be found for, whose findings say so.
    /// </summary>
    public static ValidationVerdict Of(
        RecordFindings findings,
        SchemaRules? rules,
        string source,
        IReadOnlyDictionary<string, ReferenceAnswer> answers,
        DateTime checkedUtc)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(answers);
        int ledger = 0, cache = 0, osdu = 0, missing = 0, notChecked = 0;
        var missingListed = new List<MissingReference>();
        var referenceProblems = new List<SchemaFinding>();
        foreach (var reference in findings.References)
        {
            var answer = answers.GetValueOrDefault(ReferenceResolver.Key(reference.Id)) ?? new ReferenceAnswer(ReferenceState.NotChecked);
            switch (answer.State)
            {
                case ReferenceState.InLedger:
                    ledger++;
                    break;
                case ReferenceState.InCache:
                    cache++;
                    break;
                case ReferenceState.InOsdu:
                    osdu++;
                    break;
                case ReferenceState.Missing:
                    missing++;
                    var detail = answer.Detail ?? "no record holds it";
                    if (missingListed.Count < MaxMissingListed)
                    {
                        missingListed.Add(new MissingReference(reference.Id, reference.At, reference.Path, detail));
                    }

                    referenceProblems.Add(new SchemaFinding(
                        reference.At, reference.Path, "reference", $"'{reference.Id}' names a {reference.EntityType} record, and {detail}", reference.Id));
                    break;
                default:
                    notChecked++;
                    break;
            }
        }

        var problems = findings.Problems.Concat(referenceProblems.Take(MaxMissingListed)).Select(Redacted).ToList();
        var problemCount = findings.ProblemCount + referenceProblems.Count;
        var unverified = findings.Unverified.Select(Redacted).ToList();
        var outcome = problemCount > 0 ? ValidationOutcome.Invalid : findings.UnverifiedCount > 0 ? ValidationOutcome.Unverified : ValidationOutcome.Valid;
        return new ValidationVerdict
        {
            Outcome = outcome,
            Schema = rules is null ? null : new VerdictSchema(rules.Kind, rules.Version, source),
            Rules = findings.Rules,
            Problems = problems,
            ProblemCount = problemCount,
            Unverified = unverified,
            UnverifiedCount = findings.UnverifiedCount,
            References = new VerdictReferences
            {
                Total = findings.References.Count,
                InLedger = ledger,
                InCache = cache,
                InOsdu = osdu,
                Missing = missing,
                NotChecked = notChecked,
                Cut = findings.ReferencesCut,
                MissingListed = missingListed,
            },
            Notes = [.. findings.Notes, .. rules?.Notes ?? []],
            CheckedUtc = checkedUtc,
        };
    }

    /// <summary>The verdict of a try that sent nothing of the document, saying why nothing was checked.</summary>
    public static ValidationVerdict NotChecked(string why, DateTime checkedUtc)
        => new() { Outcome = ValidationOutcome.NotValidated, Notes = [why], CheckedUtc = checkedUtc };

    /// <summary>One line saying what the verdict is: the outcome, the schema, and what broke or went unchecked first.</summary>
    public string Summary()
    {
        var against = Schema is { } schema ? $" against {schema.Kind}" : string.Empty;
        var asserted = Assertions is { Failed: > 0 } findings ? "; " + findings.Summary() : string.Empty;
        return Outcome switch
        {
            ValidationOutcome.Valid => string.Create(CultureInfo.InvariantCulture, $"valid{against}: {Rules} rule(s) met{asserted}{(Accepted && asserted.Length > 0 ? "; accepted by a release" : string.Empty)}"),
            ValidationOutcome.Invalid => string.Create(CultureInfo.InvariantCulture, $"invalid{against}: {ProblemCount} problem(s){First(Problems)}{asserted}{(Accepted ? "; accepted by a release" : string.Empty)}"),
            ValidationOutcome.Unverified => string.Create(CultureInfo.InvariantCulture, $"unverified{against}: {UnverifiedCount} part(s) not checked{First(Unverified)}{asserted}{(Accepted ? "; accepted by a release" : string.Empty)}"),
            _ => $"not validated: {(Notes.Count > 0 ? Notes[0] : "nothing of the document was sent")}",
        };

        static string First(IReadOnlyList<SchemaFinding> findings)
            => findings.Count == 0 ? string.Empty : $", first {Where(findings[0])} {findings[0].Rule}";
    }

    /// <summary>
    /// The error a record held for this verdict carries: the rules it breaks (or the parts not checked), by the property
    /// path every record shares, with the values quoted, so records broken the same way are one issue
    /// (<see cref="Ledger.ProblemSignature"/>), and how to send it anyway.
    /// </summary>
    public string HoldMessage(string setting)
    {
        var kind = Schema?.Kind ?? "its kind";
        var listed = Outcome == ValidationOutcome.Invalid ? Problems : Unverified;
        var count = Outcome == ValidationOutcome.Invalid ? ProblemCount : UnverifiedCount;
        var what = Outcome == ValidationOutcome.Invalid ? "breaks the schema of" : "could not be fully checked against the schema of";
        var named = string.Join("; ", listed.Take(ProblemsInMessage).Select(f => $"{Where(f)} {f.Rule}: {f.Message}"));
        var more = count > ProblemsInMessage ? string.Create(CultureInfo.InvariantCulture, $"; and {count - ProblemsInMessage} more") : string.Empty;
        return $"validation: the record {what} {kind}: {named}{more}. {setting}, so it is held; release it to send this document as it is, or correct the source or the mapping.";
    }

    /// <summary>The verdict as an attempt's result and the API carry it, within <see cref="MaxJsonChars"/>.</summary>
    public JsonObject ToJson()
    {
        var json = Build(Problems, Unverified, References.MissingListed, AssertionFindings.MaxListed);
        if (json.ToJsonString().Length <= MaxJsonChars)
        {
            return json;
        }

        // Shortened step by step: the parts not checked, then the problems, the missing ids and the assertions' failures,
        // each to a few. Every count stays exact.
        json = Build(Problems, Unverified.Take(3).ToList(), References.MissingListed, AssertionFindings.MaxListed);
        if (json.ToJsonString().Length > MaxJsonChars)
        {
            json = Build(Problems.Take(10).ToList(), Unverified.Take(3).ToList(), References.MissingListed.Take(5).ToList(), 10);
        }

        if (json.ToJsonString().Length > MaxJsonChars)
        {
            json = Build(Problems.Take(3).Select(Short).ToList(), Unverified.Take(1).Select(Short).ToList(), References.MissingListed.Take(1).ToList(), 3);
        }

        json["shortened"] = true;
        return json;
    }

    /// <summary>The verdict an attempt's result or an answer carries, or null when <paramref name="node"/> holds none.</summary>
    public static ValidationVerdict? FromJson(JsonNode? node)
    {
        if (node is not JsonObject json || ValidationOutcomes.Parse(Text(json, "outcome")) is not { } outcome)
        {
            return null;
        }

        var schema = json["schema"] as JsonObject;
        var references = json["references"] as JsonObject;
        return new ValidationVerdict
        {
            Outcome = outcome,
            Schema = schema is null ? null : new VerdictSchema(Text(schema, "kind") ?? string.Empty, Text(schema, "version") ?? string.Empty, Text(schema, "source") ?? VerdictSchema.Template),
            Rules = Number(json, "rules"),
            Problems = Findings(json["problems"]),
            ProblemCount = Number(json, "problemCount"),
            Unverified = Findings(json["unverified"]),
            UnverifiedCount = Number(json, "unverifiedCount"),
            References = references is null ? VerdictReferences.None : new VerdictReferences
            {
                Total = (int)Number(references, "total"),
                InLedger = (int)Number(references, "ledger"),
                InCache = (int)Number(references, "cache"),
                InOsdu = (int)Number(references, "osdu"),
                Missing = (int)Number(references, "missing"),
                NotChecked = (int)Number(references, "notChecked"),
                Cut = references["cut"] is JsonValue cut && cut.TryGetValue<bool>(out var isCut) && isCut,
                MissingListed = (references["missingIds"] as JsonArray ?? [])
                    .OfType<JsonObject>()
                    .Select(m => new MissingReference(Text(m, "id") ?? string.Empty, Text(m, "at") ?? string.Empty, Text(m, "path") ?? string.Empty, Text(m, "detail") ?? string.Empty))
                    .ToList(),
            },
            Notes = (json["notes"] as JsonArray ?? []).OfType<JsonValue>().Select(n => n.TryGetValue<string>(out var t) ? t : null).OfType<string>().ToList(),
            Accepted = json["accepted"] is JsonValue accepted && accepted.TryGetValue<bool>(out var isAccepted) && isAccepted,
            Assertions = AssertionFindings.FromJson(json["assertions"]),
            RulesVersion = Text(json, "rulesVersion") ?? CurrentRulesVersion,
            CheckedUtc = DateTime.TryParse(Text(json, "checkedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at) ? at : default,
        };
    }

    /// <summary>How a finding names where it is: its path, or the record itself for a finding at the root.</summary>
    public static string Where(SchemaFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return finding.At.Length > 0 ? finding.At : "the record";
    }

    private JsonObject Build(IReadOnlyList<SchemaFinding> problems, IReadOnlyList<SchemaFinding> unverified, IReadOnlyList<MissingReference> missing, int assertionsListed)
    {
        var json = new JsonObject { ["outcome"] = ValidationOutcomes.Name(Outcome) };
        if (Schema is { } schema)
        {
            json["schema"] = new JsonObject { ["kind"] = schema.Kind, ["version"] = schema.Version, ["source"] = schema.Source };
        }

        json["rules"] = Rules;
        json["problemCount"] = ProblemCount;
        json["problems"] = new JsonArray(problems.Select(Finding).ToArray<JsonNode?>());
        json["unverifiedCount"] = UnverifiedCount;
        json["unverified"] = new JsonArray(unverified.Select(Finding).ToArray<JsonNode?>());
        json["references"] = new JsonObject
        {
            ["total"] = References.Total,
            ["ledger"] = References.InLedger,
            ["cache"] = References.InCache,
            ["osdu"] = References.InOsdu,
            ["missing"] = References.Missing,
            ["notChecked"] = References.NotChecked,
            ["cut"] = References.Cut,
            ["missingIds"] = new JsonArray(missing
                .Select(m => (JsonNode?)new JsonObject { ["id"] = m.Id, ["at"] = m.At, ["path"] = m.Path, ["detail"] = m.Detail })
                .ToArray()),
        };
        json["notes"] = new JsonArray(Notes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        if (Assertions is { } assertions)
        {
            json["assertions"] = assertions.ToJson(assertionsListed);
        }

        if (Accepted)
        {
            json["accepted"] = true;
        }

        json["rulesVersion"] = RulesVersion;
        json["checkedUtc"] = CheckedUtc.ToString("O", CultureInfo.InvariantCulture);
        return json;
    }

    private static JsonObject Finding(SchemaFinding finding) => new()
    {
        ["at"] = finding.At,
        ["path"] = finding.Path,
        ["rule"] = finding.Rule,
        ["message"] = finding.Message,
        ["value"] = finding.Value,
    };

    private static List<SchemaFinding> Findings(JsonNode? node)
        => (node as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(f => new SchemaFinding(Text(f, "at") ?? string.Empty, Text(f, "path") ?? string.Empty, Text(f, "rule") ?? string.Empty, Text(f, "message") ?? string.Empty, Text(f, "value") ?? string.Empty))
            .ToList();

    private static SchemaFinding Redacted(SchemaFinding finding)
        => finding with { Message = HeaderRedaction.RedactMessage(finding.Message), Value = HeaderRedaction.RedactMessage(finding.Value) };

    /// <summary>A finding cut to fit a shortened verdict: its message and value clipped.</summary>
    private static SchemaFinding Short(SchemaFinding finding)
        => finding with { Message = Clip(finding.Message, 300), Value = Clip(finding.Value, 60) };

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    private static string? Text(JsonObject json, string name)
        => json[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>A whole number a verdict holds, whether it was parsed from text or built in memory from any integer type.</summary>
    private static long Number(JsonObject json, string name)
        => json[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number
            && long.TryParse(value.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;
}
