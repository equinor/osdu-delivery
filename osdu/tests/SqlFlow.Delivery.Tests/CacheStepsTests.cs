using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using Xunit;
using static SqlFlow.Delivery.Tests.EnrichmentSuite;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A pipeline's cache steps (osdu/docs/reference/flow/delivery.md, cache): the <c>cache:</c> block a delivery flow declares,
/// each step <c>auto</c> or a list of cache types and entity types; how the entries resolve against what the partition's
/// cache flows declare, and every entry a step cannot work on, refused before a run reads anything; the merge that adds
/// what a run changed and takes out what OSDU no longer holds; and the sentence a run's result carries for each step.
/// </summary>
public sealed class CacheStepsTests
{
    private const string UnitKind = "osdu:wks:reference-data--UnitOfMeasure:1.0.0";
    private const string WellboreKind = "osdu:wks:master-data--Wellbore:1.3.0";
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------------------------------------------------------- yaml

    [Fact]
    public void A_pipeline_names_its_cache_steps_auto_or_by_cache_type_and_entity_type()
    {
        var flow = Flow("""
            cache:
              before: auto
              after: [WellLog, master-data--Wellbore]
            """);

        Assert.True(flow.Cache.Before.Auto);
        Assert.Empty(flow.Cache.Before.Named);
        Assert.Equal(["WellLog", "master-data--Wellbore"], flow.Cache.After.Named);
        Assert.True(CacheStepEntries.IsEntityType("master-data--Wellbore"));
        Assert.False(CacheStepEntries.IsEntityType("WellLog"));
        Assert.True(Flow(string.Empty).Cache.IsEmpty);
    }

    [Theory]
    [InlineData("before: everything", "is 'everything': write auto, or list the types")]
    [InlineData("after: []", "lists nothing")]
    [InlineData("after: [auto]", "auto stands alone")]
    [InlineData("after: [UnitOfMeasure, unitofmeasure]", "names 'unitofmeasure' twice")]
    [InlineData("after: { type: UnitOfMeasure }", "is a map")]
    [InlineData("after: [\"Unit Of Measure\"]", "names neither a type of the partition's cache nor an OSDU entity type")]
    [InlineData("after: [\"osdu:wks:reference-data--UnitOfMeasure:1.0.0\"]", "names neither a type of the partition's cache nor an OSDU entity type")]
    public void A_cache_step_that_names_nothing_a_cache_holds_is_refused_as_the_flow_is_read(string step, string refusal)
    {
        var refused = Assert.Throws<FlowValidationException>(() => Flow($"cache:\n  {step}"));
        Assert.Contains(refusal, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_names_at_most_its_limit()
    {
        var many = string.Join(", ", Enumerable.Range(1, CacheStepEntries.MaxNamed + 1).Select(i => $"Type{i}"));
        var refused = Assert.Throws<FlowValidationException>(() => Flow($"cache:\n  after: [{many}]"));
        Assert.Contains($"more than the {CacheStepEntries.MaxNamed} a step names", refused.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------- resolution

    [Fact]
    public void An_entry_names_a_type_by_its_name_or_every_type_holding_its_entity_type()
    {
        var byName = Plan(new FlowCacheSteps { After = Named("UnitOfMeasure") }, UnitKind);
        var type = Assert.Single(byName.After);
        Assert.Equal("UnitOfMeasure", type.TypeName);
        // Every path any declaring flow keeps, in flow name order.
        Assert.Equal(["Name", "Code"], type.Fields.Select(f => f.Name));
        Assert.Equal(["other-cache", "reference-cache"], type.Declarations.Select(d => d.FlowName));

        var byEntity = Plan(new FlowCacheSteps { After = Named("reference-data--UnitOfMeasure") }, UnitKind);
        Assert.Equal(["UnitOfMeasure"], byEntity.After.Select(t => t.TypeName));
        Assert.Same(CacheStepPlan.None, Plan(new FlowCacheSteps(), UnitKind));
    }

    [Fact]
    public void Auto_after_takes_every_type_holding_what_the_pipeline_delivers_that_no_query_narrows()
    {
        Assert.Equal(["UnitOfMeasure"], Plan(new FlowCacheSteps { After = Auto() }, UnitKind).After.Select(t => t.TypeName));

        // Every flow declaring the wellbores narrows them with a query, so auto leaves them out.
        Assert.Empty(Plan(new FlowCacheSteps { After = Auto() }, WellboreKind).After);
    }

    [Fact]
    public void Auto_before_takes_the_OSDU_types_the_mapping_reads_by_name_or_by_the_entity_type_it_checks_ids_against()
    {
        var reads = new MappingCacheReads(["UnitAlias"], ["reference-data--UnitOfMeasure"], []);
        var plan = Plan(new FlowCacheSteps { Before = Auto() }, WellboreKind, reads);
        Assert.Equal(["UnitOfMeasure"], plan.Before.Select(t => t.TypeName));
    }

    [Fact]
    public void Auto_before_takes_what_the_chain_reads_the_mapping_and_every_fragment_of_its_steps()
    {
        var unit = Fragment("data:\n  Unit:\n    $from: unit\n    $modifiers:\n      - ref: reference-data--UnitOfMeasure", name: "Unit", parameters: "dataPartition: { required: true }\n");

        // The flow's own mapping checks no reference; the step's fragment checks the unit against the cached units.
        Assert.Empty(MappingCacheReads.Of(Owner(), TestSchema.Build()).EntityTypes);
        var reads = MappingCacheReads.OfChain(Owner(), TestSchema.Build(), [(unit, TestSchema.Build())]);
        Assert.Equal(["reference-data--UnitOfMeasure"], reads.EntityTypes);

        Assert.Equal(["UnitOfMeasure"], Plan(new FlowCacheSteps { Before = Auto() }, WellboreKind, reads).Before.Select(t => t.TypeName));
    }

    [Fact]
    public void The_after_step_takes_a_type_holding_the_kind_the_pipeline_s_steps_register_and_reads_their_datasets_back()
    {
        const string datasetKind = "osdu:wks:dataset--File.Generic:1.0.0";
        var declaration = new CacheDeclaration("dev",
        [
            Declared("reference-cache", "UnitOfMeasure", "osdu:wks:reference-data--UnitOfMeasure:*", ["data.Code"]),
            Declared("dataset-cache", "LasFile", "osdu:wks:dataset--File.Generic:*", ["data.Name"]),
        ]);
        CacheStepPlan After(CacheStepEntries after, string kind, IReadOnlyList<string>? stepKinds)
            => CacheStepPlan.Resolve("welldb-logs", new FlowCacheSteps { After = after }, declaration, kind, new MappingCacheReads([], [], []), stepKinds);

        var auto = After(Auto(), UnitKind, [datasetKind]);
        Assert.Equal(["LasFile", "UnitOfMeasure"], auto.After.Select(t => t.TypeName).Order(StringComparer.Ordinal));
        Assert.True(auto.TakesStepDatasets);
        var named = After(Named("dataset--File.Generic"), WellboreKind, [datasetKind]);
        Assert.Equal(["LasFile"], named.After.Select(t => t.TypeName));
        Assert.True(named.TakesStepDatasets);

        // Without a step registering the kind, auto leaves the type out and a name is refused; with steps, the refusal names what they register.
        var without = After(Auto(), UnitKind, null);
        Assert.Equal(["UnitOfMeasure"], without.After.Select(t => t.TypeName));
        Assert.False(without.TakesStepDatasets);
        Assert.Contains(
            "LasFile holds osdu:wks:dataset--File.Generic:*, and the pipeline delivers osdu:wks:master-data--Wellbore:1.3.0",
            Assert.Throws<DeliveryException>(() => After(Named("LasFile"), WellboreKind, null)).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "and the pipeline delivers osdu:wks:master-data--Wellbore:1.3.0, and its mapping's enrichments add records of osdu:wks:dataset--File.Generic:1.0.0",
            Assert.Throws<DeliveryException>(() => After(Named("UnitOfMeasure"), WellboreKind, [datasetKind])).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_lookup_table_from_an_ingestion_table_may_be_brought_up_to_date_before_the_run_and_auto_takes_it_when_the_mapping_reads_it()
    {
        var declaration = new CacheDeclaration("dev",
        [
            new CacheTypeDeclaration(
                "lookups", "UnitAlias", "lookup--UnitAlias", null, "*", [new ReferenceFieldSpec("unit", "unit")], CacheChangeMode.Auto, CacheOrigin.Table, "alias",
                Connection: "${env:OSDU_DATA_DB}", SourceObject: "OsduData.silver.UnitAlias"),
            new CacheTypeDeclaration("lookups", "SamplingDomain", "lookup--SamplingDomain", null, "*", [], CacheChangeMode.Auto, CacheOrigin.Dictionary, "code"),
        ]);
        var reads = new MappingCacheReads(["UnitAlias", "SamplingDomain"], [], []);

        var named = CacheStepPlan.Resolve("welldb-logs", new FlowCacheSteps { Before = Named("UnitAlias") }, declaration, WellboreKind, reads);
        Assert.True(Assert.Single(named.Before).IsTable);
        Assert.Equal(["UnitAlias"], CacheStepPlan.Resolve("welldb-logs", new FlowCacheSteps { Before = Auto() }, declaration, WellboreKind, reads).Before.Select(t => t.TypeName));

        var refused = Assert.Throws<DeliveryException>(() => CacheStepPlan.Resolve("welldb-logs", new FlowCacheSteps { Before = Named("SamplingDomain") }, declaration, WellboreKind, reads));
        Assert.Contains("from a dictionary or a dimension", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("after", "Unknown", UnitKind, "names 'Unknown', which is neither a type of the partition's cache nor an entity type one holds")]
    [InlineData("after", "UnitOfMeasure", WellboreKind, "and the pipeline delivers osdu:wks:master-data--Wellbore:1.3.0")]
    [InlineData("after", "Wellbore", WellboreKind, "narrows it with a query")]
    [InlineData("after", "UnitAlias", UnitKind, "UnitAlias is a lookup table")]
    [InlineData("before", "UnitAlias", UnitKind, "run that cache flow before the pipeline")]
    public void A_step_that_cannot_work_on_what_it_names_is_refused_before_the_run_reads_anything(string step, string entry, string kind, string refusal)
    {
        var steps = step == "after" ? new FlowCacheSteps { After = Named(entry) } : new FlowCacheSteps { Before = Named(entry) };
        var refused = Assert.Throws<DeliveryException>(() => Plan(steps, kind));
        Assert.Contains(refusal, refused.Message, StringComparison.Ordinal);
        Assert.Contains("so nothing was read or sent", refused.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------- merge

    [Fact]
    public void A_capture_that_adds_takes_out_only_what_OSDU_no_longer_holds_whoever_held_it()
    {
        var current = new ReferenceSnapshot("v1", T0, [Type("UnitOfMeasure", ("m", "metre"), ("ft", "foot"), ("s", "second"))], []);
        var members = new Dictionary<CacheMemberKey, IReadOnlySet<string>>(CacheMemberKey.Comparer)
        {
            [new CacheMemberKey("UnitOfMeasure", "ft")] = new HashSet<string>(StringComparer.Ordinal) { "reference-cache" },
        };
        IReadOnlySet<string> gone = new HashSet<string>(StringComparer.Ordinal) { "ft", "kg" };
        var removed = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase) { ["UnitOfMeasure"] = gone };

        var plan = CacheMerge.Apply(current, members, "delivery:welldb-unit-delivery", [Type("UnitOfMeasure", ("s", "SI second"))], [], "v2", T0.AddHours(1), adds: true, removedRecords: removed);

        Assert.Equal(
            ["m=metre", "s=SI second"],
            plan.Snapshot.Type("UnitOfMeasure")!.Items.Select(i => $"{i.Id}={i.Fields["Code"].Text}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_delivery_holds_its_records_under_its_ledger_s_name_and_a_long_name_stays_distinct()
    {
        Assert.Equal("delivery:welldb-unit-delivery", CacheHolders.Delivery("welldb-unit-delivery"));
        Assert.True(CacheHolders.IsDelivery(CacheHolders.Delivery("welldb-unit-delivery")));
        Assert.False(CacheHolders.IsDelivery("welldb-reference-cache"));

        var a = CacheHolders.Delivery(new string('a', 250) + "1");
        var b = CacheHolders.Delivery(new string('a', 250) + "2");
        Assert.Equal(CacheHolders.MaxLength, a.Length);
        Assert.NotEqual(a, b);
        Assert.True(CacheHolders.IsDelivery(a));
    }

    // ---------------------------------------------------------------------------------------------------------------- runs

    [Fact]
    public void A_cache_run_may_name_the_types_it_refreshes_and_a_name_the_flow_does_not_declare_is_refused()
    {
        var flow = new DeliveryDocumentLoader().ParseCache("""
            flowType: cache
            name: welldb-reference-cache
            source:
              endpoint: ${env:OSDU_URL}
              headers:
                data-partition-id: dev
            types:
              - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
                fields: [data.Code]
              - kind: "osdu:wks:reference-data--UnitQuantity:*"
                fields: [data.Code]
            """);

        Assert.Same(flow, CacheRefresher.Narrow(flow, []));
        Assert.Equal(["UnitQuantity"], CacheRefresher.Narrow(flow, ["unitquantity"]).Types.Select(t => t.Name));
        var refused = Assert.Throws<SqlFlowException>(() => CacheRefresher.Narrow(flow, ["UnitQuantity", "Well"]));
        Assert.Contains("'Well'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("it declares UnitOfMeasure, UnitQuantity", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_types_a_run_names_are_a_cache_run_s_and_no_other_kind_s()
    {
        var payload = DeliveryRunPayload.Parse("""{ "types": ["UnitOfMeasure"] }""");
        Assert.Equal(["UnitOfMeasure"], payload.Types);
        payload.RefuseOtherThan("cache", [DeliveryRunPayload.TypesProperty]);

        var refused = Assert.Throws<SqlFlowException>(() => payload.RefuseOtherThan("dimension", [DeliveryRunPayload.DimensionsProperty]));
        Assert.Contains("only a cache flow's runs select types", refused.Message, StringComparison.Ordinal);
        Assert.Equal(["UnitOfMeasure"], DeliveryRunPayload.Parse(payload.ToJson()).Types);
    }

    [Fact]
    public void A_run_s_result_carries_what_each_cache_step_did_in_one_sentence()
    {
        static string Summary(CacheStepOutcome step)
            => JsonDocument.Parse(JsonSerializer.Serialize(step, JsonSerializerOptions.Web)).RootElement.GetProperty("summary").GetString()!;

        Assert.Equal(
            "The cache of partition 'dev' (WellLog) took the 2 record(s) the run landed, taking out 1 OSDU no longer holds, as version v2; 3 record(s) built without what changed are planned again.",
            Summary(new CacheStepOutcome(CacheStepOutcome.AfterStep, "dev", ["WellLog"], 3, 2, 1, "v2", true, 3, null)));
        Assert.Equal(
            "The run changed no record in OSDU that the cache of partition 'dev' (WellLog) holds.",
            Summary(new CacheStepOutcome(CacheStepOutcome.AfterStep, "dev", ["WellLog"], 0, 0, 0, null, false, 0, null)));
        Assert.Equal(
            "The cache of partition 'dev' (UnitOfMeasure) was brought up to date before the run from the 12 record(s) OSDU holds, unchanged at version v1.",
            Summary(new CacheStepOutcome(CacheStepOutcome.BeforeStep, "dev", ["UnitOfMeasure"], 12, 12, 0, "v1", false, 0, null)));
        Assert.Equal(
            "The cache of partition 'dev' was not brought up to date after the run: storage did not answer.",
            Summary(new CacheStepOutcome(CacheStepOutcome.AfterStep, "dev", [], 0, 0, 0, null, false, 0, "storage did not answer.")));
    }

    // ---------------------------------------------------------------------------------------------------------------- helpers

    /// <summary>The sample flow with the cache block the test names in place of its own.</summary>
    private static FlowDefinition Flow(string cache)
    {
        var lines = File.ReadAllLines(Samples.Flow).ToList();
        var start = lines.IndexOf("cache:");
        if (start >= 0)
        {
            var end = lines.FindIndex(start + 1, line => line.Length > 0 && !char.IsWhiteSpace(line[0]));
            lines.RemoveRange(start, (end < 0 ? lines.Count : end) - start);
        }

        var yaml = string.Join("\n", lines).TrimEnd() + "\n" + cache + "\n";
        return new DeliveryDocumentLoader().ParseFlow(yaml, Samples.Flow);
    }

    private static CacheStepEntries Auto() => new() { Auto = true };

    private static CacheStepEntries Named(params string[] entries) => new() { Named = entries };

    private static CacheStepPlan Plan(FlowCacheSteps steps, string kind, MappingCacheReads? reads = null)
        => CacheStepPlan.Resolve("welldb-units", steps, Declaration(), kind, reads ?? new MappingCacheReads([], [], []));

    private static CacheDeclaration Declaration() => new("dev",
    [
        Declared("reference-cache", "UnitOfMeasure", "osdu:wks:reference-data--UnitOfMeasure:*", ["data.Code"]),
        Declared("other-cache", "UnitOfMeasure", "osdu:wks:reference-data--UnitOfMeasure:1.*", ["data.Name"]),
        Declared("wellbore-cache", "Wellbore", "osdu:wks:master-data--Wellbore:*", ["data.FacilityName"], query: "data.Source:WellDB"),
        new CacheTypeDeclaration("lookups", "UnitAlias", "lookup--UnitAlias", null, "*", [new ReferenceFieldSpec("unit", "unit")], CacheChangeMode.Auto, CacheOrigin.Table, "alias"),
    ]);

    private static CacheTypeDeclaration Declared(string flow, string name, string kind, string[] paths, string query = "*")
        => new(flow, name, kind.Split(':')[2], kind, query, [.. paths.Select(p => new ReferenceFieldSpec(p))], CacheChangeMode.Auto);

    private static ReferenceType Type(string name, params (string Id, string Code)[] items)
        => new(name, "reference-data--" + name, items.Select(i => ReferenceItem.FromText(i.Id, new Dictionary<string, string> { ["Code"] = i.Code })));
}
