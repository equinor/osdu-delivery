using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Runs;
using SqlFlow.Execution;
using SqlFlow.Lineage.Collection;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// A lineage description (flowType: lin) is read strictly and is never run. These pin what the document may say, that
/// every problem names its line and all are reported together, and that each boundary that could start a run refuses
/// one: the loader's one rule, the executor, and the header every catalog consumer projects.
/// </summary>
public sealed class LineageDescriptionLoaderTests
{
    private const string Full = """
        flowType: lin
        name: orders-u2-publish-lin
        description: Publishes the curated orders table as the daily drop-off file.
        batch: lin
        system: Databricks
        unit: job orders-publish, task publish_orders
        owner: data-platform@example.com
        url: https://lakehouse.example.com/jobs/42
        evidence:
          - { repo: lakehouse, path: jobs/orders/publish.py, commit: 4f1c2ab9, note: The transformation. }
          - jobs/orders/job.yml
        reads:
          - { name: curated.orders, as: orders, note: One row per order. }
          - { system: erp, name: "ref.*_units", as: units, origin: true }
        writes:
          - feeds: land-orders
            note: One row per order.
            columns:
              order_id: orders.id
              amount_eur:
                from: [orders.amount, units.factor]
                note: amount * factor
              loaded_at:
              "*": { from: "orders.*" }
          - { name: audit.publish_log }
        context: |
          ## Rows
          Test orders are left out.
        """;

    [Fact]
    public void AFullDescription_ReadsAsWritten()
    {
        var flow = YamlLineageDescriptionFlowLoader.Parse(Full, "lineage/orders.yaml");

        Assert.Equal("orders-u2-publish-lin", flow.Name);
        Assert.Equal("databricks", flow.System);
        Assert.Equal("job orders-publish, task publish_orders", flow.Unit);
        Assert.Equal("data-platform@example.com", flow.Owner);
        Assert.Equal("https://lakehouse.example.com/jobs/42", flow.Url);
        Assert.Equal("lin", flow.Batch);
        Assert.StartsWith("## Rows", flow.Context, StringComparison.Ordinal);
        Assert.Contains("Test orders are left out.", flow.Context, StringComparison.Ordinal);

        Assert.Equal(2, flow.Evidence.Count);
        Assert.Equal(("lakehouse", "jobs/orders/publish.py", "4f1c2ab9"), (flow.Evidence[0].Repo, flow.Evidence[0].Path, flow.Evidence[0].Commit));
        Assert.Equal((null, "jobs/orders/job.yml"), (flow.Evidence[1].Repo, flow.Evidence[1].Path));

        // A read without a system of its own is in the description's system; one that names another keeps it.
        Assert.Equal(("databricks", "curated.orders", "orders", false), (flow.Reads[0].System, flow.Reads[0].Name, flow.Reads[0].Alias, flow.Reads[0].Origin));
        Assert.Equal(("erp", "ref.*_units", "units", true), (flow.Reads[1].System, flow.Reads[1].Name, flow.Reads[1].Alias, flow.Reads[1].Origin));

        var fed = flow.Writes[0];
        Assert.True(fed.NamesFlow);
        Assert.Equal("land-orders", fed.Flow);
        Assert.Null(fed.Name);
        Assert.Null(fed.System);
        Assert.Equal(["order_id", "amount_eur", "loaded_at", "*"], fed.Columns.Select(c => c.Name));
        Assert.Equal([new LineageDescriptionColumnSource("orders", "id")], fed.Columns[0].From);
        Assert.Equal(
            [new LineageDescriptionColumnSource("orders", "amount"), new LineageDescriptionColumnSource("units", "factor")],
            fed.Columns[1].From);
        Assert.Equal("amount * factor", fed.Columns[1].Note);
        Assert.Empty(fed.Columns[2].From);
        Assert.Equal([new LineageDescriptionColumnSource("orders", "*")], fed.Columns[3].From);

        Assert.Equal(("databricks", "audit.publish_log"), (flow.Writes[1].System, flow.Writes[1].Name));
        Assert.Empty(flow.Writes[1].Columns);
    }

    [Fact]
    public void TheSmallestDescription_NamesOneObject()
    {
        var flow = YamlLineageDescriptionFlowLoader.Parse("""
            flowType: lin
            name: manual-upload
            system: manual
            writes:
              - feeds: land-orders
            """);

        Assert.Empty(flow.Reads);
        Assert.Single(flow.Writes);
        Assert.Null(flow.Context);
    }

    [Fact]
    public void AnAbsentValue_IsNothing_HoweverYamlWritesIt()
    {
        var flow = YamlLineageDescriptionFlowLoader.Parse("""
            flowType: lin
            name: tidy
            system: fabric
            description: ~
            unit: null
            evidence:
            reads:
              - { name: lake.orders, as: o }
            writes:
              - name: lake.orders_clean
                columns:
                  made_here: ~
                  id: o.id
            """);

        Assert.Null(flow.Description);
        Assert.Null(flow.Unit);
        Assert.Empty(flow.Evidence);
        Assert.Empty(flow.Writes[0].Columns[0].From);
    }

    public static TheoryData<string, string, string> Problems => new()
    {
        { "an unknown key", "colour: blue", "unknown key 'colour'" },
        { "a key in the wrong case", "Context: text", "keys are case-sensitive, so write 'context'" },
        { "a schedule", "schedule: nightly", "a lineage description is never executed, so remove it" },
        { "a mode", "mode: manual", "a lineage description is never executed, so remove it" },
        { "connections", "connections: { dwh: \"${env:X}\" }", "connects to nothing and names objects under 'reads' and 'writes'" },
        { "a system with a space", "system: data bricks", "a system is a letter followed by letters, digits or hyphens" },
        { "a credential in the url", "url: https://user:hunter2@example.com/job", "'url' of the lineage description appears to carry a credential" },
        { "a description over its limit", "description: " + new string('x', 1001), "is 1001 characters; it may be at most 1000" },
        { "a name over several lines", "name: |\n  two\n  lines", "spans several lines" },
    };

    [Theory]
    [MemberData(nameof(Problems))]
    public void ARootProblem_IsRefused_NamingItsLine(string what, string line, string expected)
    {
        // The line under test comes last, so a key it repeats (system, name) is the one YAML keeps.
        var keys = new[] { "name", "system" }.Where(k => !line.StartsWith(k + ":", StringComparison.Ordinal));
        var yaml = "flowType: lin\n" + string.Join(string.Empty, keys.Select(k => k == "name" ? "name: step\n" : "system: databricks\n"))
            + "writes:\n  - { name: out }\n" + line + "\n";

        var ex = Assert.Throws<FlowValidationException>(() => YamlLineageDescriptionFlowLoader.Parse(yaml, "lineage/step.yaml"));

        Assert.True(ex.Message.Contains(expected, StringComparison.Ordinal), $"{what}: {ex.Message}");
        Assert.Matches(@"^lineage/step\.yaml:\d+: ", ex.Message);
        Assert.DoesNotContain("hunter2", ex.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, string, string> EndpointProblems => new()
    {
        { "a name and a flow", "writes:\n  - { name: out, feeds: land }", "has both 'name' and 'feeds'" },
        { "neither a name nor a flow", "writes:\n  - { note: something }", "names nothing; give it a 'name', or a 'feeds'" },
        { "feeds on a read", "reads:\n  - { feeds: land }", "has 'feeds', which belongs to a writes entry" },
        { "fedBy on a write", "writes:\n  - { fedBy: load }", "has 'fedBy', which belongs to a reads entry" },
        { "columns on a read", "reads:\n  - { name: a, columns: { x: a.x } }", "has 'columns', which belongs to a writes entry" },
        { "at with a name", "writes:\n  - { name: out, at: part }", "'at' of writes entry 1 narrows a flow's objects" },
        { "a system with a flow", "writes:\n  - { feeds: land, system: erp }", "'system' of writes entry 1 goes with 'name'" },
        { "an origin fed by a flow", "reads:\n  - { fedBy: load, origin: true }", "'origin' of reads entry 1 marks where described data starts" },
        { "a bar in a name", "writes:\n  - { name: \"a|b\" }", "holds a '|' or a control character" },
        { "an alias with a dot", "reads:\n  - { name: a, as: a.b }", "an alias is a letter or underscore" },
        { "an alias taken twice", "reads:\n  - { name: a, as: x }\n  - { name: b, as: X }", "takes the alias 'X', which reads entry 1 already has" },
        { "a reference in an object's name", "reads:\n  - { name: \"${env:CATALOG}.curated.orders\" }", "holds a reference ('${...}'); a lineage description resolves nothing" },
        { "a reference in the flow named", "writes:\n  - { feeds: \"${env:FLOW}\" }", "holds a reference ('${...}')" },
        { "a credential in a note", "reads:\n  - { name: a, note: \"decrypt with Password=hunter2;\" }", "'note' of reads entry 1 appears to carry a credential" },
        { "a credential in a column note", "reads:\n  - { name: a, as: a }\nwrites:\n  - { name: out, columns: { x: { from: a.x, note: \"secret=hunter2\" } } }", "appears to carry a credential" },
        { "a reference in a source column", "reads:\n  - { name: a, as: a }\nwrites:\n  - { name: out, columns: { x: \"a.${env:COL}\" } }", "'from' of column 'x' of writes entry 1 holds a reference" },
        { "a comma in an unquoted note", "reads:\n  - { name: a, note: Which rows, and why. }", "reads 'and why.' as a key with no value; inside { } a comma starts the next key" },
        { "an object read twice", "reads:\n  - { name: a }\n  - { name: A }", "which an earlier reads entry already names" },
        { "an origin that is not a flag", "reads:\n  - { name: a, origin: maybe }", "'origin' of reads entry 1 must be true or false" },
        { "reads that is not a list", "reads: a", "reads must be a list" },
        { "a source without an alias", "reads:\n  - { name: a, as: a }\nwrites:\n  - { name: out, columns: { x: id } }", "a source is written 'alias.column'" },
        { "a source from an unknown alias", "reads:\n  - { name: a, as: a }\nwrites:\n  - { name: out, columns: { x: b.id } }", "names the alias 'b', which no reads entry declares; the aliases are: a" },
        { "a source when nothing has an alias", "reads:\n  - { name: a }\nwrites:\n  - { name: out, columns: { x: a.id } }", "but no reads entry declares an alias" },
        { "a column mapped twice", "reads:\n  - { name: a, as: a }\nwrites:\n  - { name: out, columns: { x: a.id, X: a.id } }", "is mapped twice" },
        { "a source listed twice", "reads:\n  - { name: a, as: a }\nwrites:\n  - { name: out, columns: { x: [a.id, a.ID] } }", "lists 'a.ID' twice" },
        { "two stars in a column", "reads:\n  - { name: a, as: a }\nwrites:\n  - { name: out, columns: { \"*_*\": \"a.*\" } }", "has 2 '*' in its name" },
        { "a star source for a named column", "reads:\n  - { name: a, as: a }\nwrites:\n  - { name: out, columns: { x: \"a.*\" } }", "a '*' in a source stands for the column of the same name" },
    };

    [Theory]
    [MemberData(nameof(EndpointProblems))]
    public void AnEntryProblem_IsRefused_SayingWhatToWrite(string what, string body, string expected)
    {
        var yaml = "flowType: lin\nname: step\nsystem: databricks\n" + body + "\n";

        var ex = Assert.Throws<FlowValidationException>(() => YamlLineageDescriptionFlowLoader.Parse(yaml, "lineage/step.yaml"));

        Assert.True(ex.Message.Contains(expected, StringComparison.Ordinal), $"{what}: {ex.Message}");
    }

    [Fact]
    public void AReference_OrAnything_MayBeQuotedInProse_ButNotACredential()
    {
        var flow = YamlLineageDescriptionFlowLoader.Parse("""
            flowType: lin
            name: step
            system: databricks
            description: Reads the table the job is told about in ${catalog}.
            unit: job orders, task load (catalog from ${env:CATALOG})
            reads:
              - { name: curated.orders, note: "Named ${catalog}.curated.orders where it runs." }
            writes:
              - { name: out }
            context: |
              The job resolves ${env:CATALOG} at run time.
            """);

        Assert.Contains("${catalog}", flow.Description, StringComparison.Ordinal);
        Assert.Contains("${env:CATALOG}", flow.Context, StringComparison.Ordinal);

        var ex = Assert.Throws<FlowValidationException>(() => YamlLineageDescriptionFlowLoader.Parse("""
            flowType: lin
            name: step
            system: databricks
            writes:
              - { name: out }
            context: |
              Connect with Server=x;Password=hunter2; to check.
            """, "lineage/step.yaml"));
        Assert.Contains("'context' of the lineage description appears to carry a credential", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDocumentsOwnLimit_HoldsHoweverItsMappingsAreWritten()
    {
        // One mapping of 1200 columns, written once and repeated under five writes with an anchor: 6000 columns in a
        // file of a few lines. Each object is within its own limit; the document is not.
        var columns = string.Join(", ", Enumerable.Range(1, 1200).Select(i => $"c{i}: a.c{i}"));
        var yaml = "flowType: lin\nname: step\nsystem: databricks\nreads:\n  - { name: in, as: a }\nwrites:\n"
            + "  - { name: out1, columns: &all { " + columns + " } }\n"
            + string.Join(string.Empty, Enumerable.Range(2, 4).Select(i => $"  - {{ name: out{i}, columns: *all }}\n"));

        var ex = Assert.Throws<FlowValidationException>(() => YamlLineageDescriptionFlowLoader.Parse(yaml, "lineage/step.yaml"));

        Assert.Contains($"maps more than {LineageDescriptionFlow.MaxDocumentColumns} columns over all it writes", ex.Message, StringComparison.Ordinal);
        Assert.Single(ex.Message.Split('\n'), line => line.Contains("maps more than", StringComparison.Ordinal));

        // Four of them fit.
        var within = "flowType: lin\nname: step\nsystem: databricks\nreads:\n  - { name: in, as: a }\nwrites:\n"
            + "  - { name: out1, columns: &all { " + columns + " } }\n"
            + string.Join(string.Empty, Enumerable.Range(2, 3).Select(i => $"  - {{ name: out{i}, columns: *all }}\n"));
        Assert.Equal(4800, YamlLineageDescriptionFlowLoader.Parse(within).Writes.Sum(w => w.Columns.Count));
    }

    [Fact]
    public void EveryProblem_IsReportedTogether()
    {
        var ex = Assert.Throws<FlowValidationException>(() => YamlLineageDescriptionFlowLoader.Parse("""
            flowType: lin
            system: Data Bricks
            schedule: nightly
            reads:
              - { name: a, as: 1a }
            writes:
              - { name: out, columns: { x: z.id } }
            """, "lineage/step.yaml"));

        Assert.StartsWith("lineage/step.yaml: 5 problems in the lineage description:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("has no 'name'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("a system is a letter", ex.Message, StringComparison.Ordinal);
        Assert.Contains("never executed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("an alias is a letter", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no reads entry declares an alias", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("flowType: lin\nname: step\nsystem: databricks\n", "has neither 'reads' nor 'writes'")]
    [InlineData("flowType: lin\nname: step\nsystem: databricks\nreads: []\nwrites: []\n", "'reads' and 'writes' are both empty")]
    [InlineData("", "the document is empty")]
    [InlineData("- a\n- b\n", "must be a mapping that starts with 'flowType: lin'")]
    [InlineData("flowType: lin\nname: [unclosed\n", "invalid YAML")]
    public void ADocumentThatConnectsNothing_OrIsNotOne_IsRefused(string yaml, string expected)
    {
        var ex = Assert.Throws<FlowValidationException>(() => YamlLineageDescriptionFlowLoader.Parse(yaml, "lineage/step.yaml"));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLoader_ReadsItInEveryHost_AsAPipelineThatIsNeverRun()
    {
        var loader = YamlDocumentLoader.CreateDefault();

        var document = Assert.IsType<LineageDescriptionFlowDocument>(loader.Parse(Full, "lineage/orders.yaml"));

        Assert.True(loader.IsKnownFlowType("lin"));
        Assert.Null(loader.FindKind("lin"));
        Assert.DoesNotContain(loader.Kinds, k => k.FlowType == "lin");
        Assert.Equal("lin", document.Kind);
        Assert.Equal(ExecutionMode.Disabled, document.Mode);
        Assert.Null(document.Schedule);
        Assert.False(document.ParticipatesInLineage);
        Assert.Null(document.SourceConnectionReference);
        Assert.Null(document.TargetConnectionReference);
        Assert.Empty(document.CredentialReferences);

        // Every catalog consumer projects the same header: a pipeline outside the graph, with no server on either side.
        var header = Assert.Single(FlowDocumentHeaders.Project(document));
        Assert.Equal(("orders-u2-publish-lin", "lin", "lin"), (header.Name, header.Kind, header.Batch));
        Assert.False(header.ParticipatesInLineage);
        Assert.Null(header.SourceServerRef);
        Assert.Null(header.Schedule);
        Assert.Equal(ExecutionMode.Disabled, header.Mode);

        var profile = FlowLoadProfiles.Describe(document);
        Assert.Equal(FlowReadModes.NotApplicable, profile.ReadMode);
        Assert.Contains("never executed", profile.Write, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostKind_MayNotTakeItsFlowType()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => YamlDocumentLoader.CreateDefault([new ImpostorKind()]));

        Assert.Contains("claims the built-in flowType 'LIN'", ex.Message, StringComparison.Ordinal);
    }

    public static TheoryData<RunParameters> Runs => new()
    {
        new RunParameters(),
        new RunParameters { FullLoad = true },
        new RunParameters { Operation = "refresh" },
    };

    [Theory]
    [MemberData(nameof(Runs))]
    public void TheLoadersRule_RefusesEveryRun(RunParameters parameters)
    {
        var ex = Assert.Throws<SqlFlowException>(() => YamlDocumentLoader.CreateDefault().ValidateRunParameters("lin", parameters));

        Assert.Equal(LineageDescriptionFlowKind.NeverRuns, ex.Message);
    }

    [Fact]
    public async Task TheExecutor_RefusesIt_BeforeAnythingRuns()
    {
        var loader = YamlDocumentLoader.CreateDefault();
        using var provider = new ServiceCollection().AddSingleton(loader).BuildServiceProvider();
        var document = loader.Parse(Full, "lineage/orders.yaml");

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => new DocumentExecutor(provider).ExecuteAsync(
            document, "lineage/orders.yaml", new DocumentExecutionOptions()));

        Assert.Equal(LineageDescriptionFlowKind.NeverRuns, ex.Message);
    }

    private sealed class ImpostorKind : IFlowDocumentKind
    {
        public string FlowType => "LIN";

        public string Description => "an impostor";

        public IReadOnlyList<FlowKindOperation> Operations => [];

        public RegisteredFlowDocument Parse(string yaml, string source) => throw new InvalidOperationException("never parsed");

        public void ValidateParameters(RunParameters parameters)
        {
        }
    }
}
