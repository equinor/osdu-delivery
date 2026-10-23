using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The shape of an attempt's result (<c>ResultJson</c>): the correlation id its OSDU requests carried, the steps it took,
/// what the target returned, and, for an attempt that did not fail, the note that says what it did. The attempt's error
/// is kept for errors: a note such as "1 chunk(s)" or "removed from OSDU" in that column reads as a failure.
/// </summary>
public static class AttemptResult
{
    /// <summary>
    /// <paramref name="resultJson"/> with <paramref name="detail"/> added. A result that held nothing yet is started with
    /// an empty steps array, so every reader finds the same shape.
    /// </summary>
    public static string? WithDetail(string? resultJson, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return resultJson;
        }

        var result = ObjectOrEmpty(resultJson);
        result["detail"] = detail;
        return result.ToJsonString();
    }

    /// <summary>
    /// The result of a plan that delivered a held, failed or reverted record again with nothing sent, OSDU already holding the
    /// version its row gives: the note saying so, and <c>settled</c>, so a record's history shows the record delivered again
    /// rather than a skip.
    /// </summary>
    public static string Settled(string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        return new JsonObject { ["detail"] = detail, ["settled"] = true }.ToJsonString();
    }

    /// <summary>
    /// The result of giving a record a new OSDU id: the id OSDU lost and the one the record is written under from now on,
    /// under <c>reassigned</c>, what OSDU held for the record under the lost id (its target state, under <c>returned</c>),
    /// and the note saying why.
    /// </summary>
    public static string Reassigned(string lost, string assigned, string? targetStateJson, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lost);
        ArgumentException.ThrowIfNullOrWhiteSpace(assigned);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        var result = new JsonObject
        {
            ["detail"] = detail,
            ["reassigned"] = new JsonObject { ["lost"] = lost, ["assigned"] = assigned },
        };
        if (!string.IsNullOrWhiteSpace(targetStateJson) && JsonNode.Parse(targetStateJson) is JsonObject state)
        {
            result["returned"] = state;
        }

        return result.ToJsonString();
    }

    /// <summary>
    /// The result of an adoption: the OSDU id the record claimed and the version OSDU held there when the record was held for
    /// it, under <c>adopted</c>, and the note saying who adopted it and what follows.
    /// </summary>
    public static string Adopted(string targetId, long? version, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        var adopted = new JsonObject { ["targetId"] = targetId };
        if (version is { } held)
        {
            adopted["version"] = held;
        }

        return new JsonObject { ["detail"] = detail, ["adopted"] = adopted }.ToJsonString();
    }

    /// <summary>
    /// The result of a removal: the correlation id its call carried, what OSDU held for the record when it was removed (the
    /// record's target state, under <c>returned</c>), and the note saying what went.
    /// </summary>
    public static string Removal(string? correlationId, string? targetStateJson, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        var result = new JsonObject();
        if (correlationId is not null)
        {
            result["correlationId"] = correlationId;
        }

        result["steps"] = new JsonArray();
        if (Parse(targetStateJson) is { } held)
        {
            result["returned"] = held;
        }

        result["detail"] = detail;
        return result.ToJsonString();
    }

    /// <summary>
    /// The result of taking out a record its source no longer holds (osdu/docs/reference/flow/removal.md): a removal's result,
    /// and under <c>removal</c> the removal flow, its removal and run, why the source no longer holds the key, as of when, and
    /// the file and row that listed it as removed.
    /// </summary>
    public static string SourceRemoval(string? correlationId, string? targetStateJson, string detail, SourceRemovalNote source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = JsonNode.Parse(Removal(correlationId, targetStateJson, detail))!.AsObject();
        var removal = new JsonObject
        {
            ["flow"] = source.RemovalFlow,
            ["entry"] = source.Entry,
            ["removalRunId"] = source.RemovalRunId,
            ["reason"] = source.Reason,
            ["removedUtc"] = source.RemovedUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        };
        if (source.RunId is { } run)
        {
            removal["runId"] = run.ToString("D");
        }

        if (source.FileName is { } file)
        {
            removal["fileName"] = file;
        }

        if (source.RowNumber is { } row)
        {
            removal["rowNumber"] = row;
        }

        result["removal"] = removal;
        return result.ToJsonString();
    }

    /// <summary>
    /// The result of writing a record's version before the latest back as its current version: the correlation id its calls
    /// carried, the target state the record holds now (under <c>returned</c>), the two versions under <c>previous</c>
    /// (<c>replacedVersion</c>, the latest taken out of being current; <c>restoredVersion</c>, the one put back), and the
    /// note saying what was done.
    /// </summary>
    public static string PreviousRestored(string? correlationId, string? targetStateJson, long replacedVersion, long restoredVersion, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        var result = new JsonObject();
        if (correlationId is not null)
        {
            result["correlationId"] = correlationId;
        }

        result["steps"] = new JsonArray();
        if (Parse(targetStateJson) is { } held)
        {
            result["returned"] = held;
        }

        result["previous"] = new JsonObject { ["replacedVersion"] = replacedVersion, ["restoredVersion"] = restoredVersion };
        result["detail"] = detail;
        return result.ToJsonString();
    }

    /// <summary>
    /// <paramref name="resultJson"/> with the version of the record the delivery replaced added under <c>replaced</c>
    /// (<c>{"version": n}</c>, or <c>{"version": null}</c> when OSDU held no version of the record before it, as the ledger
    /// knew it when the try was claimed): what a reversal of the delivery puts back
    /// (osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a run or a submission). An attempt written before
    /// the ledger kept it has no <c>replaced</c>, and a reversal reads the record's earlier attempts instead.
    /// </summary>
    public static string WithReplaced(string? resultJson, long? version)
    {
        var result = ObjectOrEmpty(resultJson);
        result["replaced"] = new JsonObject { ["version"] = version is { } v ? JsonValue.Create(v) : null };
        return result.ToJsonString();
    }

    /// <summary>The version an attempt's result says its delivery replaced: recorded or not, and the version (null for none).</summary>
    public static (bool Recorded, long? Version) Replaced(string? resultJson)
    {
        if (Parse(resultJson)?["replaced"] is not JsonObject replaced)
        {
            return (false, null);
        }

        return (true, replaced["version"] is JsonValue value && value.TryGetValue<long>(out var version) ? version : null);
    }

    /// <summary>
    /// <paramref name="resultJson"/> with the verdict the gate reached on the try's document added under <c>validation</c>
    /// (osdu/docs/reference/concepts/preflight.md, Validation before a record is sent), so every attempt says what its
    /// document was checked against and what the check found.
    /// </summary>
    public static string? WithValidation(string? resultJson, ValidationVerdict? verdict)
    {
        if (verdict is null)
        {
            return resultJson;
        }

        var result = ObjectOrEmpty(resultJson);
        result["validation"] = verdict.ToJson();
        return result.ToJsonString();
    }

    /// <summary>The verdict an attempt's result carries, or null for an attempt that carries none.</summary>
    public static ValidationVerdict? Validation(string? resultJson) => ValidationVerdict.FromJson(Parse(resultJson)?["validation"]);

    private static JsonObject ObjectOrEmpty(string? json)
    {
        var result = Parse(json) ?? new JsonObject();
        if (result["steps"] is not JsonArray)
        {
            result["steps"] = new JsonArray();
        }

        return result;
    }

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            // A value that is not JSON is not the shape anything wrote; it is left out rather than failing the attempt.
            return null;
        }
    }
}
