using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>The assertion kind's document: what the loader reads, and everything it refuses, each with a message naming it.</summary>
public sealed class AssertionDocumentTests
{
    private const string Wellbore = "osdu:wks:master-data--Wellbore:1.3.0";

    private const string WellLog = "osdu:wks:work-product-component--WellLog:1.4.0";

    /// <summary>A flow using every subject an assertion can have, in the forms a document writes them.</summary>
    internal const string Full = """
        flowType: assertion
        name: welldb-welllog-04-header-assertion
        description: What the well database's logs look like in OSDU.
        batch: welldb
        partitions: [dev, test]
        parameters:
          logSource: { default: COMPOSITE, description: The well database's log source. }
        source:
          endpoint: http://localhost
          ddmsRoot: /api/os-wellbore-ddms
        defaults:
          maxRecords: 500
          examples: 5
        failRunOn: warning
        reliability: { retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        parallelism: { concurrency: 2 }
        tests:
          - name: wellbores
            description: Every wellbore the well database delivered is there, whole.
            tags: [smoke, wellbore]
            kind: osdu:wks:master-data--Wellbore:1.3.0
            query: 'data.Source:"{logSource}"'
            sort: [{ field: data.FacilityName, order: asc }]
            assert:
              - count: { atLeast: 1, atMost: 1000 }
              - field: data.FacilityName
                exists: true
              - field: data.FacilityName
                matches: '^\d{2}/\d{1,2}-'
                for: 95%
                severity: warning
              - field: data.SequenceNumber
                between: [0, 99]
                optional: true
                where: [{ field: data.FacilityName, startsWith: "15/" }]
              - field: data.WellID
                resolves: master-data--Well
              - field: acl.viewers
                contains: data.default.viewers@dev.dataservices.energy
              - field: data.VerticalMeasurements[*].VerticalMeasurement
                atLeast: 0
                values: any
              - aggregate: max
                field: data.SequenceNumber
                atMost: 50
                tolerance: 0.5
              - unique: [data.FacilityName]
              - groupBy: kind
                groups: { "osdu:wks:master-data--Wellbore:1.3.0": { atLeast: 1 } }
                absent: ["osdu:wks:master-data--Wellbore:1.0.0"]
                groupCount: 1
              - recordSet:
                  columns: [data.FacilityName, data.SequenceNumber]
                  mode: ordered
                  rows: [["A/1-F-11", 1], ["A/1-F-12", null]]
              - conforms: true
                severity: info
              - indexed: true
              - legal: valid
              - delivered: welldb-welllog-03-header-delivery
                exact: true
          - name: log-curves
            tags: [bulk]
            partitions: [dev]
            kind: osdu:wks:work-product-component--WellLog:1.4.0
            ids: ["{partition}:work-product-component--WellLog:abc"]
            read: index
            maxRecords: 10
            sample: true
            bulk: { columns: [MD, GR], maxRows: 5000 }
            assert:
              - rowCount: { atLeast: 10 }
              - columns: { includes: [MD, GR], excludes: [CALI] }
              - column: GR
                between: [0, 300]
                for: 99%
                where: [{ column: MD, greaterThan: 100 }]
              - column: MD
                monotonic: strictlyIncreasing
              - aggregate: avg
                column: GR
                between: [10, 200]
          - name: wells
            kind: osdu:wks:master-data--Well:1.0.0
            assert:
              - count: 0
        """;

    private static readonly DeliveryDocumentLoader Loader = new();

    private static AssertionFlowDefinition Parse(string yaml) => Loader.ParseAssertion(yaml, "flow.yaml");

    /// <summary>A one-test flow around an assertion (and test keys) written in the document's own form.</summary>
    private static string OneTest(string assertion, string testKeys = "", string kind = Wellbore) => $"""
        flowType: assertion
        name: t
        partitions: [dev]
        source: {"{"} endpoint: http://localhost {"}"}
        tests:
          - name: one
            kind: "{kind}"
        {Indent(testKeys, 4)}
            assert:
        {Indent(assertion, 6)}
        """;

    private static string Indent(string text, int spaces)
        => string.Join("\n", text.Split('\n').Where(l => l.Trim().Length > 0).Select(l => new string(' ', spaces) + l.TrimEnd('\r')));

    private static string Refusal(string yaml) => Assert.Throws<FlowValidationException>(() => Parse(yaml)).Message;

    [Fact]
    public void A_full_document_maps_every_subject_with_its_operands()
    {
        var flow = Parse(Full);
        Assert.Equal("welldb-welllog-04-header-assertion", flow.Name);
        Assert.Equal(["dev", "test"], flow.Partitions);
        Assert.False(flow.FollowsRegistry);
        Assert.Equal(FailRunOn.Warning, flow.FailRunOn);
        Assert.Equal(500, flow.Defaults.MaxRecords);
        Assert.Equal(5, flow.Defaults.Examples);
        Assert.Equal(3, flow.Tests.Count);

        var wellbores = flow.Tests[0];
        Assert.Equal(["smoke", "wellbore"], wellbores.Tags);
        Assert.Equal(AssertionRead.Storage, wellbores.Read);
        Assert.Equal(500, wellbores.MaxRecords);
        Assert.Single(wellbores.Sort);
        var assertions = wellbores.Assertions;
        Assert.Equal(15, assertions.Count);

        var count = Assert.IsType<CountAssertion>(assertions[0]);
        Assert.Equal([ComparisonOperator.AtLeast, ComparisonOperator.AtMost], count.Comparison.Terms.Select(t => t.Operator));
        Assert.Equal("count atLeast 1 and atMost 1000", count.Label);

        var exists = Assert.IsType<ValueAssertion>(assertions[1]);
        Assert.Equal(ValueOperator.Exists, exists.Condition.Operator);
        Assert.Equal(AssertionSeverity.Error, exists.Severity);

        var matches = Assert.IsType<ValueAssertion>(assertions[2]);
        Assert.NotNull(matches.Condition.Pattern);
        Assert.Equal(QuantifierMode.Share, matches.For.Mode);
        Assert.Equal(95, matches.For.Share);
        Assert.Equal(AssertionSeverity.Warning, matches.Severity);

        var between = Assert.IsType<ValueAssertion>(assertions[3]);
        Assert.True(between.Optional);
        Assert.Equal(ValueOperator.StartsWith, Assert.Single(between.Where).Condition.Operator);
        Assert.Equal([0.0, 99.0], between.Condition.Operands.Select(o => o.Number!.Value));

        var resolves = Assert.IsType<ValueAssertion>(assertions[4]);
        Assert.Equal("master-data--Well", resolves.Condition.EntityType);
        Assert.Equal("field", resolves.Type);

        var any = Assert.IsType<ValueAssertion>(assertions[6]);
        Assert.True(any.AnyValue);

        var aggregate = Assert.IsType<AggregateAssertion>(assertions[7]);
        Assert.Equal(AggregateFunction.Max, aggregate.Function);
        Assert.Equal(0.5, aggregate.Tolerance);
        Assert.Equal("max(data.SequenceNumber) atMost 50 (tolerance 0.5)", aggregate.Label);

        Assert.Equal(["data.FacilityName"], Assert.IsType<UniqueAssertion>(assertions[8]).Fields);

        var group = Assert.IsType<GroupAssertion>(assertions[9]);
        Assert.Equal("kind", group.Field);
        Assert.False(group.Exact);
        Assert.Single(group.Groups);
        Assert.Single(group.Absent);
        Assert.NotNull(group.GroupCount);

        var set = Assert.IsType<RecordSetAssertion>(assertions[10]);
        Assert.Equal(RecordSetMode.Ordered, set.Mode);
        Assert.Null(set.Rows[1][1]);
        Assert.Equal(ExpectedValueKind.Number, set.Rows[0][1]!.Kind);

        Assert.Equal(AssertionSeverity.Info, Assert.IsType<ConformsAssertion>(assertions[11]).Severity);
        Assert.IsType<IndexedAssertion>(assertions[12]);
        Assert.IsType<LegalAssertion>(assertions[13]);
        var delivered = Assert.IsType<DeliveredAssertion>(assertions[14]);
        Assert.True(delivered.Exact);
        Assert.Equal("welldb-welllog-03-header-delivery", delivered.Flow);

        var logs = flow.Tests[1];
        Assert.True(logs.ByIds);
        Assert.Equal(AssertionRead.Index, logs.Read);
        Assert.True(logs.Sample);
        Assert.Equal("welllogs", logs.Bulk!.Collection);
        Assert.Equal(["MD", "GR"], logs.Bulk.Columns);
        Assert.Equal(5000, logs.Bulk.MaxRows);
        Assert.Equal(["dev"], logs.Partitions);
        Assert.IsType<RowCountAssertion>(logs.Assertions[0]);
        var columns = Assert.IsType<ColumnsAssertion>(logs.Assertions[1]);
        Assert.Equal(["CALI"], columns.Excludes);
        var column = Assert.IsType<ValueAssertion>(logs.Assertions[2]);
        Assert.True(column.Target.IsColumn);
        Assert.Equal("column", column.Type);
        Assert.True(Assert.Single(column.Where).Target.IsColumn);
        Assert.Equal(MonotonicDirection.StrictlyIncreasing, Assert.IsType<MonotonicAssertion>(logs.Assertions[3]).Direction);
        Assert.True(Assert.IsType<AggregateAssertion>(logs.Assertions[4]).Target.IsColumn);
    }

    [Theory]
    [InlineData("osdu:wks:*:*")]
    [InlineData("osdu:wks:master-data--Wellbore:*")]
    [InlineData("osdu:wks:work-product-component--WellLog:1.*.*")]
    public void A_test_reads_one_type_in_one_version(string kind)
    {
        // The type a mapping delivers (its template.kind) is one version of one type: a test of it reads that type, is
        // checked against that type's template, and links to that type in lineage, so a kind with wildcards is refused.
        Assert.Contains("has wildcards; a test reads one type in one version", Refusal(OneTest("- count: { atLeast: 0 }", kind: kind)));
    }

    [Fact]
    public void A_test_that_only_counts_or_asks_the_index_needs_no_template()
    {
        var flow = Parse(OneTest("- count: { atLeast: 0 }\n- indexed: true"));
        Assert.False(Engine.Assertions.AssertionTemplates.ReadsFields(flow.Tests[0]));
    }

    [Fact]
    public void A_bulk_test_that_lists_no_columns_reads_those_its_assertions_name()
    {
        var flow = Parse(OneTest("""
            - column: GR
              between: [0, 300]
              where: [{ column: MD, atLeast: 0 }]
            - column: DEPT
              monotonic: increasing
            """, "bulk: { maxRows: 10 }", WellLog));
        Assert.Equal(["GR", "MD", "DEPT"], flow.Tests[0].Bulk!.Columns);
    }

    [Fact]
    public void The_definition_hash_follows_what_a_test_says_not_how_it_is_laid_out()
    {
        var one = Parse(OneTest("- field: data.FacilityName\n  equals: A"));
        var laidOut = Parse(OneTest("- { field: data.FacilityName, equals: A }"));
        var changed = Parse(OneTest("- field: data.FacilityName\n  equals: B"));
        Assert.Equal(16, one.Tests[0].DefinitionHash.Length);
        Assert.Equal(one.Tests[0].DefinitionHash, laidOut.Tests[0].DefinitionHash);
        Assert.NotEqual(one.Tests[0].DefinitionHash, changed.Tests[0].DefinitionHash);
    }

    [Fact]
    public void A_scalar_keeps_the_type_the_document_writes_it_in()
    {
        var flow = Parse(OneTest("""
            - field: data.SequenceNumber
              equals: 5
            - field: data.FacilityName
              equals: "5"
            - field: data.Flag
              equals: true
            """));
        var kinds = flow.Tests[0].Assertions.Cast<ValueAssertion>().Select(a => a.Condition.Operands[0].Kind).ToList();
        Assert.Equal([ExpectedValueKind.Number, ExpectedValueKind.Text, ExpectedValueKind.Boolean], kinds);
    }

    [Theory]
    [InlineData("- field: data.A\n  equals: 1\n  count: 1", "is a count assertion, which does not take field, equals")]
    [InlineData("- severity: error", "names no subject")]
    [InlineData("- field: data.A", "names no condition")]
    [InlineData("- field: data.A\n  equals: 1\n  atLeast: 0", "names equals and atLeast; a condition has one operator")]
    [InlineData("- field: data.A\n  column: B\n  equals: 1", "names both a field and a column")]
    [InlineData("- count: { atLeast: 1, near: 2 }", "has 'near', which is not one of")]
    [InlineData("- count: -1", "is a whole number, zero or more")]
    [InlineData("- count: 1.5", "is a whole number, zero or more")]
    [InlineData("- count: \"five\"", "compares a number, and 'five' is not one")]
    [InlineData("- field: data.A\n  between: [5, 1]", "write the lower bound first")]
    [InlineData("- field: data.A\n  between: [1]", "lists two values")]
    [InlineData("- field: data.A\n  between: [1, \"2026-01-01\"]", "both bounds are numbers, or both dates")]
    [InlineData("- field: data.A\n  atLeast: 1\n  ignoreCase: true", "ignoreCase applies to text comparisons")]
    [InlineData("- field: data.A\n  startsWith: x\n  tolerance: 1", "tolerance applies to comparisons of numbers")]
    [InlineData("- field: data.A\n  equals: x\n  tolerance: 1", "the value compared with is not a number")]
    [InlineData("- field: data.A\n  exists: true\n  for: 110%", "is all, any, none, or a share")]
    [InlineData("- field: data.A\n  exists: true\n  values: some", "is not all or any")]
    [InlineData("- field: data.A\n  resolves: true\n  for: any", "for and values do not apply")]
    [InlineData("- field: data.A\n  resolves: Well", "the entity type the references point at")]
    [InlineData("- field: data.A\n  matches: '('", "is not a regular expression")]
    [InlineData("- field: data.A\n  startsWith: ''", "every value starts and ends with nothing")]
    [InlineData("- field: data.A\n  in: []", "lists between 1 and 1000 values")]
    [InlineData("- field: data.A\n  equals: { a: 1 }", "takes a single value")]
    [InlineData("- field: data.A\n  type: text", "is not one of string, number")]
    [InlineData("- field: Data.A\n  exists: true", "is not a property of an OSDU record")]
    [InlineData("- field: data..A\n  exists: true", "is not a path into a record")]
    [InlineData("- field: data.A\n  exists: true\n  where: [{ field: data.B, resolves: true }]", "which only an assertion can ask")]
    [InlineData("- field: data.A\n  exists: true\n  where: [{ field: data.B, equals: 1, for: all }]", "invalid YAML")]
    [InlineData("- count: 1\n  tolerance: 1", "a count assertion, which does not take tolerance")]
    [InlineData("- unique: []", "lists between 1 and 20 fields")]
    [InlineData("- unique: [data.A, data.A]", "names 'data.A' more than once")]
    [InlineData("- aggregate: median\n  field: data.A\n  equals: 1", "'median' is not one of")]
    [InlineData("- aggregate: sum\n  field: data.A", "compares it with nothing")]
    [InlineData("- aggregate: sum\n  field: data.A\n  atLeast: \"2026-01-01\"", "compares a number")]
    [InlineData("- groupBy: kind", "expects nothing of the groups")]
    [InlineData("- groupBy: kind\n  mode: exact\n  absent: [x]", "groups lists none")]
    [InlineData("- groupBy: kind\n  groups: { a: 1 }\n  absent: [a]", "both expects and excludes the group a")]
    [InlineData("- recordSet: { columns: [data.A], mode: ordered, rows: [[1]] }", "Give the test a sort")]
    [InlineData("- recordSet: { columns: [data.A], rows: [[1, 2]] }", "holds 2 value(s) for 1 column(s)")]
    [InlineData("- recordSet: { columns: [data.A], mode: includes, rows: [] }", "has nothing to compare")]
    [InlineData("- conforms: false", "is written 'true'")]
    [InlineData("- legal: expired", "is written 'valid'")]
    [InlineData("- delivered: ''", "names the delivery flow")]
    [InlineData("- delivered: flow\n  interface: '1x'", "is not an interface name")]
    [InlineData("- rowCount: 1", "which reads each record's bulk data; give the test a bulk block")]
    [InlineData("- column: GR\n  exists: true", "which reads each record's bulk data")]
    [InlineData("- monotonic: sideways\n  column: MD", "which reads each record's bulk data")]
    public void An_assertion_that_does_not_say_one_thing_clearly_is_refused(string assertion, string expected)
        => Assert.Contains(expected, Refusal(OneTest(assertion)));

    [Theory]
    [InlineData("ids: [a:b:c]\nquery: x", "- count: 1", "reads records by ids, and names a query as well")]
    [InlineData("ids: [a:b:c]", "- indexed: true", "the test reads its records by id; give it a query instead")]
    [InlineData("ids: [a:b:c]", "- groupBy: kind\n  groupCount: 1", "the test reads its records by id")]
    [InlineData("read: index", "- conforms: true", "Take read: index out")]
    [InlineData("query: 'data.A:\"{missing}\"'", "- count: 1", "uses '{missing}', which is neither {partition} nor declared")]
    [InlineData("maxRecords: 0", "- count: 1", "maxRecords must be between 1 and 1,000,000")]
    [InlineData("template: 26a3c3441882db4f", "- count: 1", "")]
    [InlineData("bulk: { maxRows: 10 }", "- count: 1", "is not one the Wellbore DDMS keeps bulk data for")]
    [InlineData("bulk: { columns: [MD, MD] }", "- count: 1", "is not one the Wellbore DDMS keeps bulk data for")]
    [InlineData("spatial: { byDistance: { point: { latitude: 1, longitude: 2 }, distance: 5 } }", "- count: 1", "spatial.field is required")]
    [InlineData("spatial: { field: data.X, byDistance: { distance: 5 } }", "- count: 1", "must be a point")]
    [InlineData("spatial: { field: data.X, byBoundingBox: { topLeft: { latitude: 99, longitude: 0 }, bottomRight: { latitude: 0, longitude: 0 } } }", "- count: 1", "latitude between -90 and 90")]
    [InlineData("spatial: { field: data.X }", "- count: 1", "names no filter")]
    [InlineData("tags: [a, A]", "- count: 1", "names 'A' more than once")]
    [InlineData("partitions: [prod]", "- count: 1", "is not a partition of the flow")]
    public void A_test_is_refused_where_its_keys_do_not_fit_together(string testKeys, string assertion, string expected)
    {
        if (expected.Length == 0)
        {
            Assert.Single(Parse(OneTest(assertion, testKeys)).Tests);
            return;
        }

        Assert.Contains(expected, Refusal(OneTest(assertion, testKeys)));
    }

    [Theory]
    [InlineData("spatial: { field: data.SpatialLocation.Wgs84Coordinates, byBoundingBox: { topLeft: { latitude: 72, longitude: 0 }, bottomRight: { latitude: 56, longitude: 32 } } }")]
    [InlineData("spatial: { field: data.SpatialLocation.Wgs84Coordinates, byDistance: { point: { latitude: 60, longitude: 5 }, distance: 1000 } }")]
    [InlineData("spatial: { field: data.SpatialLocation.Wgs84Coordinates, byGeoPolygon: { points: [{ latitude: 56, longitude: 0 }, { latitude: 72, longitude: 0 }, { latitude: 72, longitude: 32 }] } }")]
    public void A_spatial_filter_takes_whole_numbers_as_coordinates_and_distances(string testKeys)
    {
        // YAML reads 72 as a whole number and 72.5 as a fraction; a latitude, a longitude or a distance in whole units is a
        // number all the same, and goes to the search as the document wrote it.
        var test = Assert.Single(Parse(OneTest("- count: { atLeast: 1 }", testKeys)).Tests);
        Assert.NotNull(test.Spatial);
        Assert.Equal("data.SpatialLocation.Wgs84Coordinates", test.Spatial["field"]!.GetValue<string>());
    }

    [Fact]
    public void The_flow_is_refused_where_its_own_keys_do_not_fit()
    {
        Assert.Contains("tests must list at least one test", Refusal("flowType: assertion\nname: t\nsource: { endpoint: http://x }\ntests: []"));
        Assert.Contains("'source' is required", Refusal("flowType: assertion\nname: t\ntests: [{ name: a, kind: 'a:b:c:1.0.0', assert: [{ count: 1 }] }]"));
        Assert.Contains("keeps for the partition a run tests", Refusal(
            "flowType: assertion\nname: t\nparameters: { partition: { default: x } }\nsource: { endpoint: http://x }\ntests: [{ name: a, kind: 'a:b:c:1.0.0', assert: [{ count: 1 }] }]"));
        Assert.Contains("names its partitions: every run sets the header", Refusal(
            "flowType: assertion\nname: t\npartitions: [dev]\nsource: { endpoint: http://x, headers: { data-partition-id: dev } }\ntests: [{ name: a, kind: 'a:b:c:1.0.0', assert: [{ count: 1 }] }]"));
        Assert.Contains("as an earlier test is", Refusal(
            "flowType: assertion\nname: t\nsource: { endpoint: http://x }\ntests: [{ name: a, kind: 'a:b:c:1.0.0', assert: [{ count: 1 }] }, { name: A, kind: 'a:b:c:1.0.0', assert: [{ count: 1 }] }]"));
        Assert.Contains("is not a test name", Refusal(
            "flowType: assertion\nname: t\nsource: { endpoint: http://x }\ntests: [{ name: 'a b', kind: 'a:b:c:1.0.0', assert: [{ count: 1 }] }]"));
        Assert.Contains("'failRunOn' value 'sometimes'", Refusal(
            "flowType: assertion\nname: t\nfailRunOn: sometimes\nsource: { endpoint: http://x }\ntests: [{ name: a, kind: 'a:b:c:1.0.0', assert: [{ count: 1 }] }]"));
        Assert.Contains("source.queryPath 'api/q' must be a path", Refusal(
            "flowType: assertion\nname: t\nsource: { endpoint: http://x, queryPath: api/q }\ntests: [{ name: a, kind: 'a:b:c:1.0.0', assert: [{ count: 1 }] }]"));
        Assert.Contains("defaults.examples must be between 1 and 500", Refusal(
            "flowType: assertion\nname: t\ndefaults: { examples: 0 }\nsource: { endpoint: http://x }\ntests: [{ name: a, kind: 'a:b:c:1.0.0', assert: [{ count: 1 }] }]"));
    }

    [Fact]
    public void A_key_the_loader_does_not_know_is_refused_wherever_it_is()
    {
        Assert.Contains("invalid YAML", Refusal(OneTest("- count: 1\n  cuont: 2")));
        Assert.Contains("invalid YAML", Refusal(OneTest("- count: 1", "tagz: [a]")));
    }

    [Fact]
    public void The_kind_offers_test_by_default_and_plan_and_takes_a_selection_of_tests()
    {
        var kind = new AssertionFlowKind(Loader);
        Assert.Equal("assertion", kind.FlowType);
        Assert.Equal([DeliveryOperations.Test, DeliveryOperations.Plan], kind.Operations.Select(o => o.Name));
        Assert.All(kind.Operations, o => Assert.False(o.WritesTarget));
        Assert.Equal(DeliveryOperations.Test, AssertionFlowKind.Operation(RunParameters.None));
        Assert.Throws<SqlFlowException>(() => AssertionFlowKind.Operation(new RunParameters { Operation = DeliveryOperations.Deliver }));

        kind.ValidateParameters(new RunParameters { Operation = DeliveryOperations.Test, Payload = """{"tests":["wellbores"],"tags":["smoke"]}""" });
        Assert.Contains("payload recordKeys does not apply to an assertion flow", Assert.Throws<SqlFlowException>(
            () => kind.ValidateParameters(new RunParameters { Payload = """{"recordKeys":["2f3d9c3c-0000-0000-0000-000000000001"]}""" })).Message);
        Assert.Contains("is not a name", Assert.Throws<SqlFlowException>(
            () => kind.ValidateParameters(new RunParameters { Payload = """{"tests":["a b"]}""" })).Message);
        Assert.Contains("more than once", Assert.Throws<SqlFlowException>(
            () => kind.ValidateParameters(new RunParameters { Payload = """{"tags":["a","A"]}""" })).Message);

        var document = Assert.IsType<AssertionFlowDocument>(kind.Parse(Full, "flow.yaml"));
        Assert.Equal("report", document.TargetReference);
        Assert.Equal("http://localhost", document.SourceReference);
        Assert.False(document.RequiresRepoTree);
    }

    [Fact]
    public void Other_kinds_refuse_a_selection_of_tests()
    {
        var retrieval = new RetrievalFlowKind(Loader);
        Assert.Throws<SqlFlowException>(() => retrieval.ValidateParameters(new RunParameters { Payload = """{"tests":["a"]}""" }));
        var cache = new CacheFlowKind(Loader);
        Assert.Throws<SqlFlowException>(() => cache.ValidateParameters(new RunParameters { Payload = """{"tags":["a"]}""" }));
        var payload = DeliveryRunPayload.Parse("""{"tests":["a"]}""");
        Assert.Contains("only an assertion flow's runs select tests", Assert.Throws<SqlFlowException>(() => payload.Validate(DeliveryOperations.Deliver)).Message);
        Assert.Equal("""{"tests":["a"]}""", payload.ToJson());
        Assert.False(payload.CarriesOnlyConfiguration);
    }

    [Fact]
    public void A_run_selects_tests_by_name_and_by_tag_and_names_what_it_cannot_find()
    {
        var flow = Parse(Full);
        Assert.Equal(3, flow.Select([], []).Count);
        Assert.Equal(["wellbores", "log-curves"], flow.Select(["WELLBORES"], ["bulk"]).Select(t => t.Name));
        Assert.Contains("has no test named 'nope'", Assert.Throws<DeliveryException>(() => flow.Select(["nope"], [])).Message);
        Assert.Contains("carries the tag 'nope'", Assert.Throws<DeliveryException>(() => flow.Select([], ["nope"])).Message);
    }

    [Fact]
    public void A_run_tests_one_partition_settled_as_every_run_settles_it()
    {
        var flow = Parse(Full);
        var registry = new RegisteredPartitions([new RegisteredPartition("test", null, true)]);
        Assert.Equal("test", flow.ForRun(null, registry).Partition);
        Assert.Equal("dev", flow.ForRun("DEV", registry).Partition);
        Assert.Equal("dev", flow.ForRun("dev", RegisteredPartitions.None).Source.Headers["data-partition-id"]);
        Assert.Contains("does not test partition 'prod'", Assert.Throws<DeliveryException>(() => flow.ForRun("prod", registry)).Message);
        Assert.Contains("tests one partition per run", Assert.Throws<DeliveryException>(() => flow.ForRun("*", registry)).Message);
        Assert.NotEqual(flow.ForRun("dev", registry).LedgerId, flow.ForRun("test", registry).LedgerId);
        Assert.Equal("welldb-welllog-04-header-assertion@dev", flow.ForRun("dev", registry).LedgerName);
        Assert.Throws<InvalidOperationException>(() => flow.LedgerId);

        var header = Parse("flowType: assertion\nname: h\nsource: { endpoint: http://x, headers: { data-partition-id: dev } }\ntests: [{ name: a, kind: 'a:b:c:1.0.0', assert: [{ count: 1 }] }]");
        Assert.False(header.Partitioned);
        Assert.Equal(Identity.FlowId.Of("h"), header.LedgerId);
        Assert.Contains("names no partitions", Assert.Throws<DeliveryException>(() => header.ForRun("dev", registry)).Message);

        var registryDriven = Parse("flowType: assertion\nname: r\nsource: { endpoint: http://x }\ntests: [{ name: a, kind: 'a:b:c:1.0.0', assert: [{ count: 1 }] }]");
        Assert.True(registryDriven.FollowsRegistry);
        Assert.Equal("test", registryDriven.ForRun(null, registry).Partition);
        Assert.Contains("is not registered", Assert.Throws<DeliveryException>(() => registryDriven.ForRun("dev", registry)).Message);
    }

    [Fact]
    public void Lineage_reads_the_kind_of_every_test_in_every_partition_it_runs_in()
    {
        var lineage = AssertionLineage.Describe(Parse(Full));
        Assert.Empty(lineage.Warnings);
        Assert.All(lineage.Datasets, d => Assert.Equal(LineageRelation.Reads, d.Relation));
        var reads = lineage.Datasets.Select(d => (d.Namespace, d.Name)).ToList();
        Assert.Contains(("dev", Wellbore), reads);
        Assert.Contains(("test", Wellbore), reads);
        Assert.Contains(("dev", WellLog), reads);
        Assert.DoesNotContain(("test", WellLog), reads);
        Assert.Contains(("dev", "osdu:wks:master-data--Well:1.0.0"), reads);

        var registryDriven = AssertionLineage.Describe(Parse("flowType: assertion\nname: r\nsource: { endpoint: http://x }\ntests: [{ name: a, kind: 'osdu:wks:master-data--Well:1.0.0', assert: [{ count: 1 }] }]"));
        Assert.Equal(PartitionNames.Every, Assert.Single(registryDriven.Datasets).Namespace);
    }

    [Fact]
    public void A_flow_names_its_credential_references_and_nothing_else()
    {
        var flow = Parse("""
            flowType: assertion
            name: t
            source:
              endpoint: ${env:OSDU_URL}
              auth: { type: oauth2ClientCredentials, secretRef: "${env:SECRET}", secondarySecretRef: "${env:CLIENT}", token: { url: "https://login/x", body: { scope: "${env:SCOPE}" } } }
              headers: { data-partition-id: dev, x-extra: plain }
            tests: [{ name: a, kind: 'a:b:c:1.0.0', assert: [{ count: 1 }] }]
            """);
        Assert.Equal(
            ["source.auth.secretRef", "source.auth.secondarySecretRef", "source.endpoint", "source.auth.token.body.scope"],
            flow.CredentialReferences().Select(r => r.Key));
    }
}
