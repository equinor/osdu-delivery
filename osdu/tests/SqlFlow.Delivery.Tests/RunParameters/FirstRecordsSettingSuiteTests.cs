using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The first records of a delivery's scope as a run setting (osdu/docs/reference/flow/delivery.md, The first records of the
/// scope): over every operation of the kind, every value a run's payload may give (none, 0, the smallest and largest it
/// reads, and the values past them), the flow's own <c>source.record.maxRecords</c> or none, and what else a payload names
/// (records, a submission, slices, sending again, bringing up to date). A run's value stands in for the document's, more
/// or fewer, and 0 reads the scope whole; the document's is the default every run without one takes. Each refusal names
/// maxRecords and says what the run reads instead.
/// </summary>
[Trait(RunParameterSuite.Trait, RunParameterSuite.Name)]
public sealed class FirstRecordsSettingSuiteTests
{
    private const string Key = "0d1e6f3c-9a4b-4c1e-8e7a-2b3c4d5e6f72";
    private const string Submission = "0d1e6f3c-9a4b-4c1e-8e7a-2b3c4d5e6f70";

    /// <summary>The values a payload's maxRecords may be given; null leaves it out.</summary>
    private static readonly int?[] Asked = [null, 0, 1, 2, 5, 1000, 1001, -1];

    /// <summary>What the flow's document says, or nothing.</summary>
    private static readonly int?[] Documented = [null, 2];

    /// <summary>What else the payload names beside maxRecords, as its JSON properties.</summary>
    private static readonly (string Name, string Json)[] Beside =
    [
        ("nothing", ""),
        ("records", $"\"recordKeys\":[\"{Key}\"]"),
        ("a submission", $"\"submissionId\":\"{Submission}\""),
        ("a submission and slices", $"\"submissionId\":\"{Submission}\",\"slices\":[0,1]"),
        ("sending again", "\"recordKeys\":[\"" + Key + "\"],\"redeliver\":\"all\""),
        ("bringing up to date", "\"rerender\":true"),
        ("force", "\"force\":true"),
    ];

    public static TheoryData<string, string> OperationsAndBeside()
    {
        var data = new TheoryData<string, string>();
        foreach (var operation in DeliveryFlowKind.DeliveryOperationList.Select(o => o.Name))
        {
            foreach (var (name, _) in Beside)
            {
                data.Add(operation, name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(OperationsAndBeside))]
    public void A_runs_maxRecords_is_taken_by_the_operations_that_read_the_scope_and_refused_by_name_elsewhere(string operation, string beside)
    {
        var besideJson = Beside.Single(b => b.Name == beside).Json;
        var alone = Validated(Payload(besideJson, null), operation);
        if (alone is not null)
        {
            // What the payload names is refused for this operation whatever maxRecords says; nothing here is about it.
            return;
        }

        foreach (var asked in Asked.Where(a => a is not null))
        {
            var refusal = Validated(Payload(besideJson, asked), operation);
            var expected = Expected(operation, beside, asked!.Value);
            if (expected is null)
            {
                Assert.True(refusal is null, $"{operation} with maxRecords {asked} beside {beside} was refused: {refusal}");
            }
            else
            {
                Assert.True(refusal is not null && refusal.Contains(expected, StringComparison.Ordinal), $"{operation} with maxRecords {asked} beside {beside}: expected '{expected}', got '{refusal}'.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(OperationsAndBeside))]
    public void A_run_reads_its_own_first_records_or_the_documents_or_the_scope_whole(string operation, string beside)
    {
        var besideJson = Beside.Single(b => b.Name == beside).Json;
        if (!KindRunInputs.ReadingOperations.Contains(operation))
        {
            return;
        }

        foreach (var documented in Documented)
        {
            var flow = Flow(documented);
            foreach (var asked in Asked)
            {
                var payload = Payload(besideJson, asked);
                if (Validated(payload, operation) is not null)
                {
                    continue;
                }

                var parsed = DeliveryRunPayload.Parse(new SqlFlow.Core.Runs.RunParameters { Operation = operation, Payload = payload });
                var first = ScopeReads.FirstRecords(flow, parsed, submission: null);
                var namesRecords = parsed.RecordKeys.Count > 0 || parsed.SubmissionId is not null || parsed.Slices.Count > 0;
                (int, string)? expected = namesRecords || asked == 0
                    ? null
                    : asked is { } count
                        ? (count, DeliveryRunPayload.MaxRecordsProperty)
                        : documented is { } limit ? (limit, "source.record.maxRecords") : null;
                Assert.True(expected == first, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{operation}, maxRecords {(asked is null ? "none" : asked)}, document {(documented is null ? "none" : documented)}, beside {beside}: expected {expected}, got {first}."));
            }
        }
    }

    [Fact]
    public void A_runs_maxRecords_is_a_whole_number_and_anything_else_is_refused_as_it_is_read()
    {
        foreach (var written in new[] { "\"100\"", "2.5", "true", "[1]", "{}", "1e2", "99999999999" })
        {
            var refused = Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse($"{{\"maxRecords\":{written}}}"));
            Assert.Equal("payload maxRecords must be a whole number: how many of the first records of its scope the run reads.", refused.Message);
        }

        Assert.Null(DeliveryRunPayload.Parse("""{"maxRecords":null}""").MaxRecords);
    }

    [Fact]
    public void A_documents_maxRecords_is_one_to_a_thousand_and_leaving_it_out_reads_the_scope_whole()
    {
        var loader = new DeliveryDocumentLoader();
        var kind = FlowKindDocument.Of(FlowDefinition.FlowTypeName);
        foreach (var (written, message) in new (string, string?)[]
        {
            ("1", null), ("1000", null), ("0", "source.record.maxRecords is 0;"), ("1001", "source.record.maxRecords is 1001;"), ("-5", "source.record.maxRecords is -5;"),
        })
        {
            var yaml = kind.Document().Replace("    scope: { log_source: logSource }\n", $"    scope: {{ log_source: logSource }}\n    maxRecords: {written}\n", StringComparison.Ordinal);
            if (message is null)
            {
                Assert.Equal(int.Parse(written, System.Globalization.CultureInfo.InvariantCulture), loader.ParseFlow(yaml, "flows/suite.yaml").Source.Record.MaxRecords);
            }
            else
            {
                var refused = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(yaml, "flows/suite.yaml")).Message;
                Assert.Contains(message, refused, StringComparison.Ordinal);
                Assert.EndsWith("a run reads the first 1 to 1,000 records of its scope, or leave it out to read the scope whole.", refused, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// What refuses a payload's maxRecords of <paramref name="asked"/> for <paramref name="operation"/> beside what else it
    /// names, as the reference states it, written out here rather than read from the code under test; null when nothing does.
    /// </summary>
    private static string? Expected(string operation, string beside, int asked)
    {
        if (!KindRunInputs.ReadingOperations.Contains(operation))
        {
            return $"payload maxRecords does not apply to the {operation} operation";
        }

        if (asked is < 0 or > 1000)
        {
            return $"payload maxRecords is {asked}; a run reads the first 1 to 1,000 records of its scope, or 0 reads the scope whole.";
        }

        if (asked == 0)
        {
            return null;
        }

        return beside switch
        {
            "records" or "sending again" => "payload names both recordKeys and maxRecords",
            "a submission" => "payload names both maxRecords and submissionId",
            "a submission and slices" => "payload names both maxRecords and submissionId",
            "bringing up to date" when operation == DeliveryOperations.Deliver => "payload rerender does not apply to the deliver operation: a run of the first records of its scope",
            _ => null,
        };
    }

    private static string Payload(string beside, int? asked)
    {
        var parts = new List<string>();
        if (beside.Length > 0)
        {
            parts.Add(beside);
        }

        if (asked is { } count)
        {
            parts.Add($"\"maxRecords\":{count.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        var json = "{" + string.Join(",", parts) + "}";
        _ = JsonNode.Parse(json);
        return json;
    }

    /// <summary>The refusal of <paramref name="payload"/> for <paramref name="operation"/>, as a trigger checks it; null when it is taken.</summary>
    private static string? Validated(string payload, string operation)
    {
        try
        {
            DeliveryRunPayload.Parse(new SqlFlow.Core.Runs.RunParameters { Operation = operation, Payload = payload }).Validate(operation);
            return null;
        }
        catch (SqlFlowException ex)
        {
            return ex.Message;
        }
    }

    private static FlowDefinition Flow(int? documented)
    {
        var flow = new DeliveryDocumentLoader().ParseFlow(FlowKindDocument.Of(FlowDefinition.FlowTypeName).Document(), "flows/suite.yaml");
        return flow with { Source = flow.Source with { Record = flow.Source.Record with { MaxRecords = documented } } };
    }
}
