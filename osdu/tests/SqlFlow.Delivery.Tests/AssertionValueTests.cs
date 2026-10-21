using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>How an assertion compares what a record holds with what it expects, and how a test is held to its kind's template.</summary>
[Trait("Category", "Integration")]
public sealed class AssertionValueTests
{
    private static readonly DeliveryDocumentLoader Loader = new();

    private static ValueCondition Condition(string assertion)
    {
        var flow = Loader.ParseAssertion($"""
            flowType: assertion
            name: t
            source: {"{"} endpoint: http://x {"}"}
            tests:
              - name: one
                kind: "{Samples.WellboreKind}"
                assert:
                  - {assertion}
            """, "flow.yaml");
        return ((ValueAssertion)flow.Tests[0].Assertions[0]).Condition;
    }

    private static bool Holds(string assertion, string json) => ValueComparer.Holds(Condition(assertion), JsonNode.Parse(json)!, out _);

    [Theory]
    [InlineData("{ field: data.A, equals: 5 }", "5", true)]
    [InlineData("{ field: data.A, equals: 5 }", "5.0", true)]
    [InlineData("{ field: data.A, equals: 5 }", "\"5\"", true)]
    [InlineData("{ field: data.A, equals: 5 }", "5.01", false)]
    [InlineData("{ field: data.A, equals: 5, tolerance: 0.02 }", "5.01", true)]
    [InlineData("{ field: data.A, equals: \"5\" }", "5", true)]
    [InlineData("{ field: data.A, equals: 0.1 }", "0.1", true)]
    [InlineData("{ field: data.A, equals: 12345678901234567 }", "12345678901234567", true)]
    [InlineData("{ field: data.A, equals: abc }", "\"ABC\"", false)]
    [InlineData("{ field: data.A, equals: abc, ignoreCase: true }", "\"ABC\"", true)]
    [InlineData("{ field: data.A, equals: \"2026-01-01T00:00:00Z\" }", "\"2026-01-01T01:00:00+01:00\"", true)]
    [InlineData("{ field: data.A, equals: true }", "true", true)]
    [InlineData("{ field: data.A, equals: true }", "\"TRUE\"", true)]
    [InlineData("{ field: data.A, equals: true }", "false", false)]
    [InlineData("{ field: data.A, equals: x }", "[\"x\"]", false)]
    [InlineData("{ field: data.A, notEquals: x }", "\"y\"", true)]
    [InlineData("{ field: data.A, in: [a, b, 3] }", "3", true)]
    [InlineData("{ field: data.A, in: [a, b, 3] }", "\"c\"", false)]
    [InlineData("{ field: data.A, notIn: [a, b] }", "\"b\"", false)]
    [InlineData("{ field: data.A, atLeast: 10 }", "10", true)]
    [InlineData("{ field: data.A, atLeast: 10 }", "9.99", false)]
    [InlineData("{ field: data.A, atLeast: 10, tolerance: 0.1 }", "9.95", true)]
    [InlineData("{ field: data.A, atLeast: 10 }", "\"12\"", true)]
    [InlineData("{ field: data.A, atLeast: 10 }", "\"twelve\"", false)]
    [InlineData("{ field: data.A, greaterThan: 10 }", "10", false)]
    [InlineData("{ field: data.A, lessThan: \"2026-01-01\" }", "\"2025-12-31T23:59:59Z\"", true)]
    [InlineData("{ field: data.A, atMost: \"b\" }", "\"a\"", true)]
    [InlineData("{ field: data.A, between: [1, 3] }", "3", true)]
    [InlineData("{ field: data.A, between: [1, 3] }", "3.5", false)]
    [InlineData("{ field: data.A, between: [\"2026-01-01\", \"2026-12-31\"] }", "\"2026-06-01T12:00:00Z\"", true)]
    [InlineData("{ field: data.A, matches: '^A/1-F-\\d+$' }", "\"A/1-F-11\"", true)]
    [InlineData("{ field: data.A, matches: '^A/1' }", "\"B/2\"", false)]
    [InlineData("{ field: data.A, notMatches: '^A/1' }", "\"B/2\"", true)]
    [InlineData("{ field: data.A, startsWith: ab }", "\"abc\"", true)]
    [InlineData("{ field: data.A, endsWith: BC, ignoreCase: true }", "\"abc\"", true)]
    [InlineData("{ field: data.A, contains: b }", "\"abc\"", true)]
    [InlineData("{ field: data.A, contains: b }", "[\"a\", \"b\"]", true)]
    [InlineData("{ field: data.A, contains: 2 }", "[1, 2]", true)]
    [InlineData("{ field: data.A, notContains: c }", "[\"a\", \"b\"]", true)]
    [InlineData("{ field: data.A, contains: 1 }", "1", false)]
    [InlineData("{ field: data.A, empty: true }", "\"\"", true)]
    [InlineData("{ field: data.A, empty: true }", "[]", true)]
    [InlineData("{ field: data.A, empty: false }", "{}", false)]
    [InlineData("{ field: data.A, type: integer }", "3", true)]
    [InlineData("{ field: data.A, type: integer }", "3.5", false)]
    [InlineData("{ field: data.A, type: object }", "{}", true)]
    [InlineData("{ field: data.A, length: { atLeast: 2, atMost: 3 } }", "\"abc\"", true)]
    [InlineData("{ field: data.A, length: 2 }", "[1, 2, 3]", false)]
    [InlineData("{ field: data.A, length: 2 }", "12", false)]
    public void A_value_meets_a_condition_as_its_type_reads_it(string assertion, string json, bool expected)
        => Assert.Equal(expected, Holds(assertion, json));

    [Fact]
    public void A_value_that_fails_says_why_in_terms_a_reader_acts_on()
    {
        ValueComparer.Holds(Condition("{ field: data.A, atLeast: 10 }"), JsonValue.Create("twelve"), out var incomparable);
        Assert.Contains("does not order against the number 10", incomparable);
        ValueComparer.Holds(Condition("{ field: data.A, equals: x }"), JsonNode.Parse("[\"x\"]")!, out var list);
        Assert.Contains("compare its items", list);
        ValueComparer.Holds(Condition("{ field: data.A, between: [1, 3] }"), JsonValue.Create(9), out var range);
        Assert.Contains("outside 1 to 3", range);
    }

    [Fact]
    public void A_comparison_of_a_measure_holds_when_every_term_does()
    {
        var comparison = new Comparison([
            new ComparisonTerm(ComparisonOperator.AtLeast, ExpectedValue.OfNumber(5)),
            new ComparisonTerm(ComparisonOperator.AtMost, ExpectedValue.OfNumber(10)),
        ]);
        Assert.True(ValueComparer.Evaluate(comparison, Measured.Of(7), null));
        Assert.False(ValueComparer.Evaluate(comparison, Measured.Of(11), null));
        Assert.True(ValueComparer.Evaluate(comparison, Measured.Of(10.05), 0.1));
        var dated = new Comparison([new ComparisonTerm(ComparisonOperator.LessThan, ExpectedValue.OfText("2026-01-01"))]);
        Assert.True(ValueComparer.Evaluate(dated, Measured.Of(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)), null));
        Assert.False(ValueComparer.Evaluate(dated, Measured.Of(3), null));
    }

    [Fact]
    public void A_quantifier_decides_over_every_record_considered()
    {
        var all = new Tally(Quantifier.All, 5);
        Assert.False(all.Passed);
        all.Add(true, "a", "1", string.Empty);
        all.Add(false, "b", "2", "why");
        Assert.False(all.Passed);
        Assert.Equal(1, all.Failing);
        Assert.Equal("b", Assert.Single(all.Examples).Id);

        var share = new Tally(Quantifier.AtLeast(50), 5);
        share.Add(true, "a", null, string.Empty);
        share.Add(false, "b", null, "why");
        Assert.True(share.Passed);
        Assert.Contains("(50%)", share.Actual("record"));

        var none = new Tally(Quantifier.None, 5);
        Assert.True(none.Passed);
        none.Add(true, "a", "x", string.Empty);
        Assert.False(none.Passed);
        Assert.Equal("a", Assert.Single(none.Examples).Id);
        Assert.Contains("meet it, and none may", none.Message("record"));
    }

    [Fact]
    public void An_aggregate_adds_up_numbers_and_dates_and_says_what_it_left_out()
    {
        var aggregator = new Aggregator(distinct: true);
        foreach (var value in new JsonNode[] { JsonValue.Create(3), JsonValue.Create("4"), JsonValue.Create(5.5), JsonValue.Create("n/a"), JsonValue.Create(3) })
        {
            aggregator.Add(value);
        }

        var numbers = new Comparison([new ComparisonTerm(ComparisonOperator.AtLeast, ExpectedValue.OfNumber(0))]);
        Assert.Equal(15.5, aggregator.Measure(AggregateFunction.Sum, 0, numbers).Value!.Value.Number);
        Assert.Equal(5.5, aggregator.Measure(AggregateFunction.Max, 0, numbers).Value!.Value.Number);
        Assert.Equal(4, aggregator.Measure(AggregateFunction.Distinct, 0, numbers).Value!.Value.Number);
        Assert.Equal(5, aggregator.Measure(AggregateFunction.Count, 0, numbers).Value!.Value.Number);
        Assert.Equal(2, aggregator.Measure(AggregateFunction.Missing, 2, numbers).Value!.Value.Number);
        Assert.Contains("1 of 5 value(s) are neither numbers nor dates", aggregator.Note());

        var dates = new Aggregator(distinct: false);
        dates.Add(JsonValue.Create("2026-01-01T00:00:00Z"));
        dates.Add(JsonValue.Create("2025-06-01"));
        var dated = new Comparison([new ComparisonTerm(ComparisonOperator.AtLeast, ExpectedValue.OfText("2025-01-01"))]);
        Assert.Equal(new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero), dates.Measure(AggregateFunction.Min, 0, dated).Value!.Value.Instant);
        Assert.Equal("no value is a number", dates.Measure(AggregateFunction.Sum, 0, numbers).Why);
    }

    [Fact]
    public void Record_values_are_read_through_arrays_the_way_cache_paths_read_them()
    {
        var record = JsonNode.Parse("""{"data":{"VerticalMeasurements":[{"VerticalMeasurement":1},{"VerticalMeasurement":2},{"Other":3}],"Name":null},"acl":{"viewers":["a","b"]}}""")!;
        Assert.Equal(["1", "2"], RecordValues.Select(record, "data.VerticalMeasurements.VerticalMeasurement").Select(RecordValues.Text));
        Assert.Equal(["1", "2"], RecordValues.Select(record, "data.VerticalMeasurements[*].VerticalMeasurement").Select(RecordValues.Text));
        Assert.Single(RecordValues.Select(record, "acl.viewers"));
        Assert.Equal(2, RecordValues.Select(record, "acl.viewers[*]").Count);
        Assert.Empty(RecordValues.Select(record, "data.Name"));
        Assert.Equal("[a, b]", RecordValues.Describe(RecordValues.Select(record, "acl.viewers[*]")));
    }

    [Fact]
    public void Tokens_are_substituted_everywhere_but_in_a_regular_expression()
    {
        var flow = Loader.ParseAssertion($$"""
            flowType: assertion
            name: t
            parameters: { region: { default: North } }
            source: { endpoint: http://x }
            tests:
              - name: one
                kind: "{{Samples.WellboreKind}}"
                query: 'data.Region:"{region}" AND tags.p:"{partition}"'
                assert:
                  - { field: data.Region, equals: "{region}" }
                  - { field: data.Code, matches: '^\d{2}$' }
                  - { groupBy: data.Region, groups: { "{region}": 1 } }
            """, "flow.yaml");
        var bound = AssertionBinding.Bind(flow.Tests[0], new Dictionary<string, string> { ["region"] = "South", ["partition"] = "dev" });
        Assert.Equal("data.Region:\"South\" AND tags.p:\"dev\"", bound.Query);
        Assert.Equal("South", ((ValueAssertion)bound.Assertions[0]).Condition.Operands[0].Text);
        Assert.Equal(@"^\d{2}$", ((ValueAssertion)bound.Assertions[1]).Condition.Operands[0].Text);
        Assert.Equal("South", ((GroupAssertion)bound.Assertions[2]).Groups[0].Key);
    }

    private static async Task<TemplateFit> FitAsync(string assertions, string testKeys = "", string kind = Samples.WellboreKind)
    {
        var flow = Loader.ParseAssertion($"""
            flowType: assertion
            name: t
            source: {"{"} endpoint: http://x {"}"}
            tests:
              - name: one
                kind: "{kind}"
            {string.Join("\n", testKeys.Split('\n').Where(l => l.Length > 0).Select(l => "    " + l))}
                assert:
            {string.Join("\n", assertions.Split('\n').Where(l => l.Length > 0).Select(l => "      " + l))}
            """, "flow.yaml");
        var fits = await AssertionTemplates.FitAsync(flow.Tests, Samples.SampleTemplates, CancellationToken.None);
        return fits["one"];
    }

    [Fact]
    public async Task A_test_that_fits_its_template_has_no_problem()
    {
        var fit = await FitAsync("""
            - { field: data.FacilityName, startsWith: "A/" }
            - { field: data.SequenceNumber, between: [0, 10] }
            - { field: data.WellID, resolves: master-data--Well }
            - { field: data.WellID, equals: "osdu:master-data--Well:1234:" }
            - { field: acl.viewers, contains: data.default.viewers@dev.dataservices.energy }
            - { field: "acl.viewers[*]", endsWith: "@dev.dataservices.energy" }
            - { field: data.VerticalMeasurements.VerticalMeasurement, atLeast: 0 }
            - { field: tags.RunMarker, equals: ODLIVE }
            - { aggregate: max, field: data.SequenceNumber, atMost: 99 }
            - { aggregate: min, field: createTime, atLeast: "2020-01-01" }
            - { unique: [data.FacilityName, id] }
            - { groupBy: data.FacilityName, groupCount: { atLeast: 1 } }
            - { recordSet: { columns: [data.FacilityName], rows: [] } }
            - { conforms: true }
            """);
        Assert.Empty(fit.Problems);
        Assert.NotNull(fit.Template);
        Assert.NotNull(fit.Version);
    }

    [Theory]
    [InlineData("- { field: data.FacilityNam, exists: true }", "'data.FacilityNam' is not a property of osdu:wks:master-data--Wellbore:1.3.0", "did you mean 'data.FacilityName'")]
    [InlineData("- { field: data.SequenceNumber, startsWith: a }", "'data.SequenceNumber' is an integer", "startsWith compares text")]
    [InlineData("- { field: data.SequenceNumber, equals: abc }", "is an integer, and \"abc\" is not a number", "")]
    [InlineData("- { field: acl.viewers, equals: x }", "'acl.viewers' is a list", "acl.viewers[*]")]
    [InlineData("- { field: data.VerticalMeasurements, equals: x }", "'data.VerticalMeasurements' is a list", "")]
    [InlineData("- { field: data.WellID, resolves: master-data--Wellbore }", "refers to master-data--Well", "never to master-data--Wellbore")]
    [InlineData("- { field: data.WellID, equals: not-a-reference }", "cannot be a value of 'data.WellID', whose schema pattern is", "")]
    [InlineData("- { aggregate: sum, field: data.FacilityName, atLeast: 1 }", "is a string; sum adds up numbers", "")]
    [InlineData("- { field: createTime, atLeast: 5 }", "is a date-time, and 5 is a number", "")]
    [InlineData("- { unique: [data.Nope] }", "'data.Nope' is not a property", "")]
    public async Task A_test_that_does_not_fit_its_template_says_where(string assertion, string expected, string also)
    {
        var fit = await FitAsync(assertion);
        var problem = Assert.Single(fit.Problems);
        Assert.Contains(expected, problem);
        Assert.Contains(also, problem);
    }

    [Fact]
    public async Task The_sample_estates_assertion_flow_fits_the_templates_it_reads()
    {
        var flow = Loader.LoadAssertion(Path.Combine(Samples.Source, "flows", "welldb-welllog-04-header-assertion.yaml"));
        Assert.Equal(["logs-delivered", "log-headers", "log-curves", "log-sources"], flow.Tests.Select(t => t.Name));
        var fits = await AssertionTemplates.FitAsync(flow.Tests, Samples.SampleTemplates, CancellationToken.None);
        Assert.All(fits.Values, fit => Assert.Empty(fit.Problems));
        Assert.NotNull(fits["log-headers"].Template);
    }

    [Fact]
    public async Task A_test_of_a_kind_no_template_is_saved_for_is_told_how_to_capture_one()
    {
        var fit = await FitAsync("- { field: data.X, exists: true }", kind: "osdu:wks:master-data--Rig:1.0.0");
        Assert.Contains(
            "The test's fields are checked against the template of osdu:wks:master-data--Rig:1.0.0, and no template of osdu:wks:master-data--Rig:1.0.0 is saved",
            Assert.Single(fit.Problems));
        Assert.Contains("sqlflow template capture --kind osdu:wks:master-data--Rig:1.0.0", fit.Problems[0]);

        var pinned = await FitAsync("- { field: data.FacilityName, exists: true }", "template: 0000000000000000");
        Assert.Contains("template osdu:wks:master-data--Wellbore:1.3.0 version 0000000000000000 is not saved", Assert.Single(pinned.Problems));

        var countOnly = await FitAsync("- count: 1", kind: "osdu:wks:master-data--Rig:1.0.0");
        Assert.Empty(countOnly.Problems);
        Assert.Null(countOnly.Template);
    }

    [Fact]
    public async Task A_test_names_its_template_by_the_mapping_that_pins_it()
    {
        var mapping = Loader.LoadMapping(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml"));
        Task<(MappingDefinition? Mapping, string? Problem)> Synced(string reference, CancellationToken ct) => Task.FromResult(reference == mapping.Reference
            ? ((MappingDefinition?)mapping, (string?)null)
            : (null, $"no mapping {reference} is synced; sync the repository that declares it, then run again"));
        var flow = Loader.ParseAssertion($$"""
            flowType: assertion
            name: t
            source: { endpoint: http://x }
            tests:
              - name: logs
                kind: {{Samples.WellLogKind}}
                template: WellLog@1.4.0
                assert:
                  - { field: data.Name, exists: true }
              - name: unsynced
                kind: {{Samples.WellLogKind}}
                template: WellLog@9.9.9
                assert:
                  - { field: data.Name, exists: true }
              - name: other-kind
                kind: {{Samples.WellboreKind}}
                template: WellLog@1.4.0
                assert:
                  - { field: data.FacilityName, exists: true }
            """, "flow.yaml");
        Assert.Equal(("WellLog@1.4.0", (string?)null), (flow.Tests[0].TemplateMapping, flow.Tests[0].Template));

        var fits = await AssertionTemplates.FitAsync(flow.Tests, Samples.SampleTemplates, Synced, CancellationToken.None);

        // The test is checked against the version the mapping pins, and follows the mapping when it pins another.
        Assert.Empty(fits["logs"].Problems);
        Assert.Equal(mapping.Template.Version, fits["logs"].Version);
        Assert.Equal(
            "The test is checked against the template mapping WellLog@9.9.9 fills, and no mapping WellLog@9.9.9 is synced; sync the repository that declares it, then run again.",
            Assert.Single(fits["unsynced"].Problems));
        Assert.Equal(
            $"Mapping WellLog@1.4.0 fills a template of {Samples.WellLogKind}, and the test reads {Samples.WellboreKind}; name the mapping of {Samples.WellboreKind}, or a template version of it.",
            Assert.Single(fits["other-kind"].Problems));

        // Where no mapping can be read, the test says so rather than taking another template.
        var unread = await AssertionTemplates.FitAsync(flow.Tests, Samples.SampleTemplates, CancellationToken.None);
        Assert.Contains("no mapping can be read where the test is checked", Assert.Single(unread["logs"].Problems), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("template: Well@Log@1", "template 'Well@Log@1' names the mapping whose template the test is checked against as Name@version, such as template: WellLog@1.5.0.")]
    [InlineData("template: '@1.5.0'", "template '@1.5.0' names the mapping")]
    [InlineData("template: 'two words'", "template 'two words' is not a template version (at most 64 characters, no whitespace), nor a mapping as Name@version")]
    public void A_template_that_is_neither_a_version_nor_a_mapping_is_refused(string template, string expected)
    {
        var refused = Assert.Throws<SqlFlow.Core.FlowValidationException>(() => Loader.ParseAssertion($$"""
            flowType: assertion
            name: t
            source: { endpoint: http://x }
            tests:
              - name: logs
                kind: {{Samples.WellLogKind}}
                {{template}}
                assert:
                  - count: 1
            """, "flow.yaml"));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A test written before except, the warning band, dates and known values hashes as it did, so its last result is still
    /// its own after an upgrade: the hashes below are what the release before them gave this flow.
    /// </summary>
    [Fact]
    public void A_test_written_before_the_newer_keys_hashes_as_it_did()
    {
        var flow = Loader.ParseAssertion("""
            flowType: assertion
            name: hash-pin
            partitions: [dev]
            source:
              endpoint: http://localhost
            tests:
              - name: logs
                kind: osdu:wks:work-product-component--WellLog:1.4.0
                template: 26a3c3441882db4f
                query: 'data.Name:"COMPOSITE"'
                severity: warning
                assert:
                  - count: { atLeast: 1, atMost: 5000 }
                  - field: data.Name
                    equals: COMPOSITE
                    ignoreCase: true
                  - field: data.SamplingStart
                    between: [0, 100]
                    tolerance: 1
                  - aggregate: count
                    field: data.Curves.CurveID
                    equals: 7842
                  - aggregate: max
                    field: data.SamplingStop
                    atMost: 5000
                    tolerance: 2
                  - unique: [data.WellboreID, data.Name]
                  - groupBy: data.Name
                    groups: { COMPOSITE: 1 }
                  - recordSet: { columns: [data.Name], rows: [[COMPOSITE]] }
                  - conforms: true
                    severity: info
              - name: wellbores
                kind: osdu:wks:master-data--Wellbore:1.3.0
                ids: ["{partition}:master-data--Wellbore:1001"]
                assert:
                  - count: 1
                  - field: data.WellID
                    resolves: master-data--Well
            """, "flow.yaml");
        Assert.Equal(["e1b4a0414a523dac", "ba37aacab7027028"], flow.Tests.Select(t => t.DefinitionHash));
    }

    [Fact]
    public async Task A_result_keeps_within_its_budget_by_naming_fewer_examples()
    {
        var examples = Enumerable.Range(0, 500).Select(i => new AssertionExample($"id-{i}", new string('v', 290), new string('r', 1500))).ToList();
        var result = new TestResult
        {
            Test = "t",
            Kind = "a:b:c:1.0.0",
            Outcome = "failed",
            DefinitionHash = "0123456789abcdef",
            Assertions = Enumerable.Range(0, 3).Select(i => new AssertionOutcome
            {
                Index = i, Label = "l", Type = "field", Severity = "error", Outcome = "failed", Expected = "e", Examples = examples,
            }).ToList(),
        };
        var json = TestResults.Serialize(result);
        Assert.True(json.Length <= TestResults.DetailBudget, $"{json.Length} characters");
        var back = TestResults.Deserialize(json)!;
        Assert.All(back.Assertions, a => Assert.True(a.ExamplesTrimmed));
        Assert.All(back.Assertions, a => Assert.True(a.Examples.Count is > 0 and < 500));
        Assert.Null(TestResults.Deserialize(string.Empty));
        Assert.Null(TestResults.Deserialize("not json"));
        await Task.CompletedTask;
    }
}
