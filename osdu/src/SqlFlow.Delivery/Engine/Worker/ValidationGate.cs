using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Engine.Worker;

/// <summary>What the gate decided for one record: its verdict, and the reason it is held, or null when it is sent.</summary>
public sealed record GateDecision(ValidationVerdict Verdict, string? Hold);

/// <summary>
/// The check every document goes through immediately before it is sent (osdu/docs/reference/concepts/preflight.md,
/// Validation before a record is sent). Each document whose metadata a try writes is checked against the template of
/// its own kind and render context (the one it was rendered for, whatever the flow pins now), as the route will send
/// it, and the records it refers to are looked up once for the whole group (<see cref="ReferenceResolver"/>). The
/// verdict goes on the try's attempt and the record whatever it says; the flow's <c>target.validation</c> decides which
/// verdicts hold the record. What the mapping's assertions found of the document when it was rendered
/// (osdu/docs/reference/flow/mapping-assertions.md) joins the verdict, and a failure whose action is hold holds the
/// record whatever the policy. A record whose references storage does not hold, under
/// <c>target.verifyReferences: storage</c>, is held whatever the policy, as that setting has always meant.
/// </summary>
/// <remarks>
/// A part of the document the route fills when it sends it (the dataset list the File service's ids replace, the data keys
/// carried forward from the version OSDU holds, the bulk link a DDMS manages) is not judged as it was rendered. A document
/// an operator's release accepted is sent whatever its verdict, and the verdict says it was accepted.
/// </remarks>
public sealed class ValidationGate
{
    private readonly FlowDefinition _flow;
    private readonly Func<string, string?, CancellationToken, Task<SchemaSnapshot?>> _schemas;
    private readonly ReferenceResolver _references;
    private readonly TimeProvider _time;
    private readonly ValidationLimits _limits;

    /// <param name="flow">The flow whose records are checked, with its policy (<c>target.validation</c>).</param>
    /// <param name="schemas">The schema of a kind at a content version (null for the newest the caller has), or null when no template folder holds it.</param>
    /// <param name="references">Where the records a document refers to are looked up.</param>
    /// <param name="time">The clock verdicts are stamped with.</param>
    /// <param name="limits">The bounds of each check; the defaults when null.</param>
    public ValidationGate(
        FlowDefinition flow,
        Func<string, string?, CancellationToken, Task<SchemaSnapshot?>> schemas,
        ReferenceResolver references,
        TimeProvider time,
        ValidationLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(time);
        _flow = flow;
        _schemas = schemas;
        _references = references;
        _time = time;
        _limits = limits ?? ValidationLimits.Default;
    }

    /// <summary>What the gate does with a verdict.</summary>
    public ValidationPolicy Policy => _flow.Target.Validation;

    /// <summary>
    /// The decision for each record of a group about to be sent, in order. Throws when the records referred to cannot be
    /// looked up (the ledger or storage cannot be read), so the caller tries the group again later rather than sending it
    /// unchecked or holding it for a question nobody answered.
    /// </summary>
    public async Task<IReadOnlyList<GateDecision>> DecideAsync(
        IReadOnlyList<(RecordState Record, JsonObject Document, bool WritesMetadata, AssertionFindings? Assertions)> works, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(works);
        var now = _time.GetUtcNow().UtcDateTime;
        var checks = new Check[works.Count];
        var asked = new List<FoundReference>();
        for (var i = 0; i < works.Count; i++)
        {
            var (record, document, writesMetadata, _) = works[i];
            if (!writesMetadata)
            {
                checks[i] = new Check(null, null, "the try sends the payload alone, so the document OSDU holds is not sent again and nothing of it is checked");
                continue;
            }

            var kind = document["kind"] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
            if (kind is null)
            {
                checks[i] = new Check(Unchecked("the document names no kind, so there is no schema to check it against"), null, null);
                continue;
            }

            var version = SchemaVersionOf(record.PendingRenderContext);
            var schema = await _schemas(kind, version, ct).ConfigureAwait(false);
            if (schema is null)
            {
                checks[i] = new Check(
                    Unchecked(version is null
                        ? $"the document was rendered for no version of {kind} its flow reads, so it is not checked against its schema"
                        : $"template {kind} at version {version}, which the document was rendered for, is neither in a template folder on the mapping's path nor saved, so the document is not checked against its schema"),
                    null,
                    null);
                continue;
            }

            var rules = SchemaRules.Of(schema);
            var findings = RecordValidator.Check(document, rules, RouteFilled(_flow, document), _limits);
            checks[i] = new Check(findings, rules, null);
            asked.AddRange(findings.References);
            asked.AddRange(record.PendingReferences.Select(r => new FoundReference(r.Id, EntityTypeOf(r.Id), r.Property, r.Property)));
        }

        var answers = asked.Count == 0
            ? new Dictionary<string, ReferenceAnswer>(StringComparer.Ordinal)
            : await _references.ResolveAsync(asked, ct).ConfigureAwait(false);

        var decisions = new GateDecision[works.Count];
        for (var i = 0; i < works.Count; i++)
        {
            var record = works[i].Record;
            var check = checks[i];
            if (check.NotChecked is { } why)
            {
                decisions[i] = new GateDecision(ValidationVerdict.NotChecked(why, now), null);
                continue;
            }

            var verdict = ValidationVerdict.Of(check.Findings!, check.Rules, VerdictSchema.Template, answers, now) with { Assertions = works[i].Assertions };

            // What target.verifyReferences: storage has always held: a reference neither the ledger nor storage holds.
            if (_references.AsksOsdu)
            {
                var missing = record.PendingReferences
                    .Where(r => answers.GetValueOrDefault(ReferenceResolver.Key(r.Id))?.State == ReferenceState.Missing)
                    .ToList();
                if (missing.Count > 0)
                {
                    decisions[i] = new GateDecision(verdict, ReferenceCheck.Describe(missing));
                    continue;
                }
            }

            // What the mapping asserts holds the record when an assertion that failed says so; the schema, when the policy does.
            var schemaHolds = Policy.Holds(verdict.Outcome);
            var assertionsHold = verdict.Assertions?.Holds == true;
            if (!schemaHolds && !assertionsHold)
            {
                decisions[i] = new GateDecision(verdict, null);
            }
            else if (record.PendingAccepted)
            {
                decisions[i] = new GateDecision(verdict with { Accepted = true }, null);
            }
            else
            {
                decisions[i] = new GateDecision(verdict, HoldMessage(verdict, schemaHolds, assertionsHold));
            }
        }

        return decisions;
    }

    /// <summary>
    /// Why the gate holds a record: the assertions that hold it first, since they name what the mapping says the record must
    /// be, then the schema when the policy holds it as well; or the schema alone.
    /// </summary>
    private static string HoldMessage(ValidationVerdict verdict, bool schemaHolds, bool assertionsHold)
    {
        if (!assertionsHold)
        {
            return verdict.HoldMessage(ValidationPolicy.Setting(verdict.Outcome));
        }

        var asserted = verdict.Assertions!.HoldMessage();
        return schemaHolds
            ? asserted + string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $" It does not meet the schema of {verdict.Schema?.Kind ?? "its kind"} either ({(verdict.Outcome == ValidationOutcome.Invalid ? $"{verdict.ProblemCount} problem(s)" : $"{verdict.UnverifiedCount} part(s) not checked")}), which {ValidationPolicy.Setting(verdict.Outcome)} holds as well.")
            : asserted;
    }

    /// <summary>
    /// The property paths of <paramref name="document"/> the flow's route fills or replaces when it sends it, which the gate
    /// does not judge as rendered: the dataset list of a route that registers files or datasets (the File service mints those
    /// ids), the dataset properties the Dataset service fills, the data keys an update carries forward from the version OSDU
    /// holds, and the bulk link a DDMS manages.
    /// </summary>
    public static IReadOnlySet<string> RouteFilled(FlowDefinition flow, JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(document);
        var options = flow.Target.ProtocolOptions;
        var filled = new HashSet<string>(StringComparer.Ordinal);
        switch (flow.Target.Protocol)
        {
            case DeliveryProtocol.File or DeliveryProtocol.Manifest or DeliveryProtocol.FileAndDdms or DeliveryProtocol.ManifestAndDdms or DeliveryProtocol.Workflow:
                filled.Add($"data.{options.DatasetsProperty}");
                break;
            case DeliveryProtocol.Dataset:
                filled.Add($"data.{options.DatasetsProperty}");
                filled.Add("data.DatasetProperties");
                break;
        }

        if (DeliveryProtocols.ReachesDdms(flow.Target.Protocol))
        {
            filled.Add("data.ExtensionProperties.wdms");
        }

        foreach (var key in OwnedContent.PreservedKeys(options, document))
        {
            filled.Add($"data.{key}");
        }

        return filled;
    }

    /// <summary>The template content version a render context names, or null when it names none or cannot be read.</summary>
    internal static string? SchemaVersionOf(string? renderContext)
    {
        if (string.IsNullOrWhiteSpace(renderContext))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(renderContext) is JsonObject context && context["schema"] is JsonValue schema && schema.TryGetValue<string>(out var version) && !string.IsNullOrWhiteSpace(version)
                ? version
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string EntityTypeOf(string id)
    {
        var parts = id.Split(':');
        return parts.Length > 1 ? parts[1] : string.Empty;
    }

    /// <summary>Findings that say the document could not be checked at all, and why.</summary>
    private static RecordFindings Unchecked(string why) => new()
    {
        Problems = [],
        ProblemCount = 0,
        Unverified = [new SchemaFinding(string.Empty, string.Empty, "schema", why, string.Empty)],
        UnverifiedCount = 1,
        References = [],
    };

    private sealed record Check(RecordFindings? Findings, SchemaRules? Rules, string? NotChecked);
}
