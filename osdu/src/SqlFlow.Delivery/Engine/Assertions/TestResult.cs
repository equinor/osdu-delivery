using System.Text.Json;
using System.Text.Json.Serialization;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>One record (or row) that failed an assertion, with what it held and why it failed.</summary>
/// <param name="Id">The record's OSDU id, or for a value a test found without one (a group, a legal tag), what it is.</param>
/// <param name="Value">What the record held there, clipped to <see cref="TestResults.MaxQuoted"/> characters.</param>
/// <param name="Reason">Why it fails the assertion.</param>
/// <param name="Key">
/// For a row of a delivery flow's staged data, its key parts in the flow's key order: what a query of the rows the assertion
/// read narrows to (<see cref="AssertionOutcome.Staged"/>). Null for an example that is not a staged row, and for a row with a
/// blank key column, which no key names.
/// </param>
public sealed record AssertionExample(string? Id, string? Value, string Reason, IReadOnlyList<string>? Key = null);

/// <summary>
/// The staged rows an assertion read (osdu/docs/reference/flow/staged-accounting.md, The rows in the source): the delivery
/// flow, its interface, and the values of its parameters the rows were read under, so the T-SQL that shows them in the flow's
/// own tables is written from what the run read.
/// </summary>
/// <param name="Flow">The delivery flow, by name.</param>
/// <param name="Interface">The interface of that flow, for a source with interfaces.</param>
/// <param name="Values">The flow's parameter values the rows were read under: the assertion's, then the flow's defaults.</param>
public sealed record StagedScope(string Flow, string? Interface, IReadOnlyDictionary<string, string> Values);

/// <summary>How one assertion of a test came out: what it expected, what it found, and the records that failed it.</summary>
public sealed record AssertionOutcome
{
    /// <summary>The assertion's position in its test, from 0.</summary>
    public required int Index { get; init; }

    public required string Label { get; init; }

    public string? Description { get; init; }

    /// <summary>count, field, column, aggregate, unique, groupBy, recordSet, conforms, indexed, legal, delivered, rowCount, columns, monotonic.</summary>
    public required string Type { get; init; }

    /// <summary>error, warning or info.</summary>
    public required string Severity { get; init; }

    /// <summary>passed, failed, errored or skipped.</summary>
    public required string Outcome { get; init; }

    public required string Expected { get; init; }

    /// <summary>What the test found, in words; null when it did not get as far.</summary>
    public string? Actual { get; init; }

    /// <summary>Why the assertion failed, errored or was skipped.</summary>
    public string? Message { get; init; }

    /// <summary>How many records (rows, references, groups, legal tags) the assertion looked at.</summary>
    public long? Checked { get; init; }

    /// <summary>How many of them failed it.</summary>
    public long? Failing { get; init; }

    /// <summary>The number the assertion measured (a count, an aggregate, the share that held), for a trend across runs.</summary>
    public double? Value { get; init; }

    /// <summary>Records that failed it, the first ones met, up to the flow's examples.</summary>
    public IReadOnlyList<AssertionExample> Examples { get; init; } = [];

    /// <summary>True when the examples were cut back so the result stays within what a result keeps.</summary>
    public bool ExamplesTrimmed { get; init; }

    /// <summary>The staged rows the assertion read, for a staged delivered assertion; null for an assertion that reads OSDU alone.</summary>
    public StagedScope? Staged { get; init; }

    public static AssertionOutcome Skipped(int index, Model.TestAssertion assertion, string why) => new()
    {
        Index = index,
        Label = assertion.Label,
        Description = assertion.Description,
        Type = assertion.Type,
        Severity = Model.AssertionText.Of(assertion.Severity),
        Outcome = TestOutcomes.Skipped,
        Expected = assertion.Expected,
        Message = why,
    };

    /// <summary>
    /// The outcome of an assertion whose proof values are past the day they hold until: not judged against them, and flagged
    /// as a warning saying they need restating, so a board and a report show which tests ask for new proof values.
    /// </summary>
    public static AssertionOutcome NeedsNewProofValues(int index, Model.TestAssertion assertion, string why, string? asOf) => new()
    {
        Index = index,
        Label = assertion.Label,
        Description = assertion.Description,
        Type = assertion.Type,
        Severity = Model.AssertionText.Of(Model.AssertionSeverity.Warning),
        Outcome = TestOutcomes.Failed,
        Expected = assertion.Expected + Model.AssertionActivity.Stated(asOf),
        Actual = "not judged",
        Message = why,
    };

    public static AssertionOutcome Errored(int index, Model.TestAssertion assertion, string why) => new()
    {
        Index = index,
        Label = assertion.Label,
        Description = assertion.Description,
        Type = assertion.Type,
        Severity = Model.AssertionText.Of(assertion.Severity),
        Outcome = TestOutcomes.Errored,
        Expected = assertion.Expected,
        Message = why,
    };
}

/// <summary>
/// How one test came out in one run (osdu/docs/reference/flow/assertion.md, Outcomes): what it read, what it matched,
/// every assertion's outcome, and the problems that kept it from being evaluated. This is what the ledger keeps as a
/// result's detail and what every report is rendered from.
/// </summary>
public sealed record TestResult
{
    public required string Test { get; init; }

    public string? Description { get; init; }

    public required string Kind { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>passed, failed, warned, errored or skipped.</summary>
    public required string Outcome { get; init; }

    /// <summary>The heaviest severity among the assertions that failed; null when none did.</summary>
    public string? Severity { get; init; }

    /// <summary>The records the test matched: the index's exact count, or the ids storage holds.</summary>
    public long? Matched { get; init; }

    /// <summary>The records the test read and held its assertions to.</summary>
    public long? Evaluated { get; init; }

    /// <summary>True when more records matched than the test reads and it evaluated the first ones as a sample.</summary>
    public bool Sampled { get; init; }

    /// <summary>The query as it ran, its tokens substituted.</summary>
    public string? Query { get; init; }

    /// <summary>How many records the test named by id.</summary>
    public int Ids { get; init; }

    /// <summary>The template version the test's fields were checked against, or null when it reads none.</summary>
    public string? Template { get; init; }

    public required string DefinitionHash { get; init; }

    public long DurationMs { get; init; }

    /// <summary>Why the test could not be evaluated, or null.</summary>
    public string? Error { get; init; }

    /// <summary>Where the test does not fit the template of its kind: each is a reason it was not evaluated.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>What a reader of the result needs to know that is not an assertion's: ids storage did not return, records of another kind.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public IReadOnlyList<AssertionOutcome> Assertions { get; init; } = [];

    public DateTime StartedUtc { get; init; }

    public DateTime CompletedUtc { get; init; }
}

/// <summary>How a result is kept: its JSON form, bounded, and the outcome its assertions add up to.</summary>
public static class TestResults
{
    /// <summary>The longest value an example quotes.</summary>
    public const int MaxQuoted = 300;

    /// <summary>The most characters one result's detail keeps; examples are cut back until it fits.</summary>
    public const int DetailBudget = 1_000_000;

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The outcome a test's assertions add up to: errored when one could not be evaluated, else by the heaviest severity that failed.</summary>
    public static (string Outcome, string? Severity) Outcome(IReadOnlyList<AssertionOutcome> assertions)
    {
        ArgumentNullException.ThrowIfNull(assertions);
        if (assertions.Any(a => a.Outcome == TestOutcomes.Errored))
        {
            return (TestOutcomes.Errored, Heaviest(assertions));
        }

        var heaviest = Heaviest(assertions);
        return heaviest switch
        {
            "error" => (TestOutcomes.Failed, heaviest),
            "warning" => (TestOutcomes.Warned, heaviest),
            _ => (TestOutcomes.Passed, heaviest),
        };
    }

    private static string? Heaviest(IReadOnlyList<AssertionOutcome> assertions)
    {
        var failed = assertions.Where(a => a.Outcome == TestOutcomes.Failed).Select(a => a.Severity).ToList();
        return failed.Contains("error") ? "error" : failed.Contains("warning") ? "warning" : failed.Contains("info") ? "info" : null;
    }

    /// <summary>
    /// The result as the ledger keeps it: its JSON, within <see cref="DetailBudget"/>. A result whose examples take it past
    /// the budget keeps fewer of them, the same share of each assertion's, each marked as cut back.
    /// </summary>
    public static string Serialize(TestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var json = JsonSerializer.Serialize(result, Json);
        var keep = result.Assertions.Count == 0 ? 0 : result.Assertions.Max(a => a.Examples.Count);
        while (json.Length > DetailBudget && keep > 0)
        {
            keep /= 2;
            var cut = keep;
            result = result with
            {
                Assertions = result.Assertions
                    .Select(a => a.Examples.Count > cut ? a with { Examples = a.Examples.Take(cut).ToList(), ExamplesTrimmed = true } : a)
                    .ToList(),
            };
            json = JsonSerializer.Serialize(result, Json);
        }

        if (json.Length > DetailBudget)
        {
            // Without a single example the result is its assertions and problems alone, whose number and length the loader
            // bounds; what is left over the budget is the notes, which are cut to their count.
            result = result with { Notes = [$"{result.Notes.Count} note(s) left out: the result was larger than a result keeps."] };
            json = JsonSerializer.Serialize(result, Json);
        }

        return json;
    }

    /// <summary>A result the ledger keeps, read back; null for detail that is not a result (a summary row read without it).</summary>
    public static TestResult? Deserialize(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TestResult>(detail, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A value as an example quotes it: clipped, with the length it had when it was clipped.</summary>
    public static string Quote(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length <= MaxQuoted ? text : text[..MaxQuoted] + $"... ({text.Length} characters)";
    }
}
