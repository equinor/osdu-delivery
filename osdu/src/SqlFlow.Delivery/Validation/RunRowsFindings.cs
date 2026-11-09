using System.Globalization;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Validation;

/// <summary>
/// What a mapping's rules judged once a run (<c>dataset.assert</c> with <c>per: run</c>,
/// osdu/docs/reference/flow/mapping-dataset-assertions.md) found of the record table's rows in the run's scope, and what that
/// makes of each record the run renders. A <c>count</c> rule that fails reaches every record of the run; a <c>unique</c>
/// rule reaches the records whose rows share values with another row, or every record when more rows share values than a
/// run names. Every record is judged against every such rule, and a record a failure reaches carries it among its own
/// assertion findings, held or reported as the rule says.
/// </summary>
public sealed class RunRowsFindings
{
    /// <summary>The most rows sharing values a run names, so the records among them are held one by one; past it, every record is.</summary>
    public const int MaxListed = 100_000;

    private readonly string _mapping;
    private readonly IReadOnlyList<RuleOutcome> _rules;

    private RunRowsFindings(string mapping, IReadOnlyList<RuleOutcome> rules)
    {
        _mapping = mapping;
        _rules = rules;
    }

    /// <summary>A run whose mapping judges no rule once a run.</summary>
    public static RunRowsFindings None { get; } = new("-", []);

    /// <summary>Whether a rule was judged.</summary>
    public bool IsEmpty => _rules.Count == 0;

    /// <summary>For each rule that failed, what it found, as the run's trace says it: its name, then why.</summary>
    public IReadOnlyList<string> Failures => _rules.Where(r => r.Summary is not null).Select(r => $"{r.Rule.Label}: {r.Summary}").ToList();

    /// <summary>Judges every answer of the source, one per rule, for the mapping called <paramref name="mapping"/>.</summary>
    public static RunRowsFindings Judge(string mapping, IReadOnlyList<RunRowsAnswer> answers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapping);
        ArgumentNullException.ThrowIfNull(answers);
        return answers.Count == 0 ? None : new RunRowsFindings(mapping, answers.Select(Outcome).ToList());
    }

    /// <summary>
    /// The assertion findings of <paramref name="record"/>: what its render found (<paramref name="rendered"/>), with what the
    /// rules judged once a run find of it. Every record counts one judgement per rule; null only when there is nothing to say.
    /// </summary>
    public AssertionFindings? Apply(SourceRecord record, AssertionFindings? rendered)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_rules.Count == 0)
        {
            return rendered;
        }

        var log = new AssertionLog(_mapping);
        foreach (var outcome in _rules)
        {
            log.Judged();
            if (outcome.Summary is null)
            {
                continue;
            }

            if (outcome.All is { } all)
            {
                log.Fail(outcome.Rule.At, outcome.Rule.At, outcome.Rule.Label, AssertionStage.Incoming, outcome.Rule.OnFail, all.Value, all.Reason);
            }
            else if (record.SourceKeyJson is { } key && outcome.Shared.TryGetValue(key, out var shared))
            {
                log.Fail(outcome.Rule.At, outcome.Rule.At, outcome.Rule.Label, AssertionStage.Incoming, outcome.Rule.OnFail, shared.Values,
                    string.Create(CultureInfo.InvariantCulture, $"{shared.Sharing} rows of the record table in the run's scope share it, and the rule asks that no two rows do"));
            }
        }

        var found = log.Findings();
        return rendered is null ? found : AssertionFindings.Combine([rendered, found]);
    }

    private static RuleOutcome Outcome(RunRowsAnswer answer)
    {
        var rule = answer.Rule;
        if (rule.Per != DatasetAssertionPer.Run || rule.Kind == DatasetAssertionKind.Value)
        {
            throw new ArgumentException($"{rule.Location} is judged as each record renders, not once a run.", nameof(answer));
        }

        if (rule.Kind == DatasetAssertionKind.Count)
        {
            if (Engine.Assertions.ValueComparer.Evaluate(rule.Count!, Engine.Assertions.Measured.Of(answer.Rows), null))
            {
                return new RuleOutcome(rule, null, null, Empty);
            }

            var reason = DatasetAssertionJudge.CountReason(rule, answer.Rows);
            return new RuleOutcome(rule, $"{reason}; every record of the run {Does(rule)}", (answer.Rows.ToString(CultureInfo.InvariantCulture), reason), Empty);
        }

        if (answer.SharedRows == 0)
        {
            return new RuleOutcome(rule, null, null, Empty);
        }

        var columns = string.Join(", ", rule.Columns);
        var first = answer.Shared.Count > 0
            ? $" (first {DatasetAssertionJudge.Described(rule.Columns, answer.Shared[0].Values.Select(v => (System.Text.Json.Nodes.JsonNode)v).ToList())}, in {DatasetAssertion.RowsText(answer.Shared[0].Sharing)})"
            : string.Empty;
        if (answer.SharedRows > answer.Shared.Count)
        {
            // More rows share values than a run names: which records they key is not known one by one, so every record is.
            var reason = string.Create(CultureInfo.InvariantCulture,
                $"{answer.SharedRows} rows of the record table in the run's scope share values of {columns} with another row{first}, more than a run names ({answer.Shared.Count})");
            return new RuleOutcome(rule, $"{reason}; every record of the run {Does(rule)}", (string.Empty, reason + ", so every record of the run is judged to fail it"), Empty);
        }

        var shared = new Dictionary<string, (string Values, long Sharing)>(StringComparer.Ordinal);
        foreach (var row in answer.Shared)
        {
            shared[row.Key.Json] = (DatasetAssertionJudge.Described(rule.Columns, row.Values.Select(v => (System.Text.Json.Nodes.JsonNode)v).ToList()), row.Sharing);
        }

        var summary = string.Create(CultureInfo.InvariantCulture,
            $"{answer.SharedRows} rows of the record table in the run's scope share values of {columns} with another row{first}; the record of each such row the run renders {Does(rule)}");
        return new RuleOutcome(rule, summary, null, shared);
    }

    private static string Does(DatasetAssertion rule) => rule.OnFail == AssertionAction.Report ? "is sent, the failure recorded" : "is held";

    private static readonly IReadOnlyDictionary<string, (string Values, long Sharing)> Empty = new Dictionary<string, (string Values, long Sharing)>(StringComparer.Ordinal);

    /// <summary>What one rule came to: null summary for one met; else every record's failure, or the records sharing values by their key.</summary>
    private sealed record RuleOutcome(
        DatasetAssertion Rule, string? Summary, (string Value, string Reason)? All, IReadOnlyDictionary<string, (string Values, long Sharing)> Shared);
}
