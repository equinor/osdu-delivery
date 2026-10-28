using System.Text.Json.Nodes;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using Xunit;
using static SqlFlow.Delivery.Tests.EnrichmentSuite;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A step set aside on its hold (<c>onHold: step</c>, osdu/docs/reference/flow/enrich-holds.md), as the chain renders it: the
/// record goes ahead with what the step added to its last delivery, nothing for a record never delivered, and the steps after
/// it render over that; a record OSDU holds a version of whose contribution was not kept, or whose kept contribution no longer
/// fits, is held; a step that holds the record holds it as before; and a step that holds leaves nothing half composed.
/// </summary>
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class StepHoldsChainTests
{
    private static readonly MappingDefinition Grade = Fragment("""
        data:
          Symbol: { $from: grade }
          Aliases: [{ $from: alias, $required: false }]
        """, name: "Grade");

    private static readonly MappingDefinition Copy = Fragment("""
        data:
          Nested:
            Inner: { $base: data.Symbol, $required: false }
        """, name: "Copy");

    /// <summary>A step of <paramref name="fragment"/> set aside on its hold.</summary>
    private static ResolvedStep Aside(MappingDefinition fragment, string name = "grade")
    {
        var step = Step(name, fragment);
        return step with { Step = step.Step with { OnHold = EnrichHold.Step } };
    }

    /// <summary>A step reading no rows, rendered after <paramref name="after"/>.</summary>
    private static ResolvedStep After(MappingDefinition fragment, string name, params string[] after) => Step(name, fragment, readsRows: false, after: after);

    private static RecordChain Chain(params ResolvedStep[] steps) => new(Renderer(Owner()), steps);

    private static KeptSteps Kept(bool delivered, params (string Step, JsonObject[] Fragments, string[] Datasets)[] steps) => new()
    {
        Delivered = delivered,
        Steps = steps.ToDictionary(s => s.Step, s => new KeptStep(s.Fragments, s.Datasets), StringComparer.Ordinal),
    };

    private static JsonObject SymbolOf(string symbol) => new() { ["data"] = new JsonObject { ["Symbol"] = symbol } };

    private static string? Symbol(RenderResult result) => result.Document["data"]?["Symbol"]?.GetValue<string>();

    private static IReadOnlyDictionary<string, string?>[] Several => [Row(("grade", "A")), Row(("grade", "B"))];

    [Fact]
    public void A_step_set_aside_on_a_record_never_delivered_adds_nothing_and_the_record_goes_ahead()
    {
        var plain = Renderer(Owner()).Render(Record("a", 1));
        var result = Chain(Aside(Grade)).Render(Record("a", 1, ("grade", Several)));

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal(plain.MetadataHash, result.MetadataHash);
        var step = Assert.Single(result.Enrichment);
        Assert.True(step.SetAside);
        Assert.Contains("rows: one", Assert.Single(step.Holds), StringComparison.Ordinal);
        Assert.Equal(["grade"], result.StepsSetAside);
    }

    [Fact]
    public void A_step_set_aside_takes_what_it_added_to_the_last_delivery_and_the_steps_after_it_render_over_that()
    {
        var kept = Kept(true, ("grade", [SymbolOf("K")], []));
        var result = Chain(Aside(Grade), After(Copy, "copy", "grade")).Render(Record("a", 1, ("grade", Several)), new ChainInputs { Kept = kept });

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal("K", Symbol(result));
        Assert.Equal("K", result.Document["data"]!["Nested"]!["Inner"]!.GetValue<string>());
        Assert.Equal(["data.Symbol"], result.Enrichment[0].Effects.Select(e => e.Path));
        Assert.False(result.Enrichment[1].SetAside);
    }

    [Fact]
    public void The_record_with_what_a_step_kept_hashes_as_the_record_it_last_delivered()
    {
        var delivered = Chain(Aside(Grade)).Render(Record("a", 1, ("grade", [Row(("grade", "A"))])));
        var composed = delivered.Enrichment[0].Composed;
        var held = Chain(Aside(Grade)).Render(Record("a", 1, ("grade", Several)), new ChainInputs { Kept = Kept(true, ("grade", composed.ToArray(), [])) });

        Assert.Equal(delivered.MetadataHash, held.MetadataHash);
    }

    [Fact]
    public void A_record_OSDU_holds_a_version_of_whose_contribution_was_not_kept_is_held()
    {
        var result = Chain(Aside(Grade)).Render(Record("a", 1, ("grade", Several)), new ChainInputs { Kept = Kept(true) });

        Assert.True(result.IsHeld);
        Assert.Contains(result.Holds, h => h.Contains("what it added to the version OSDU holds was not kept", StringComparison.Ordinal));
        Assert.Contains(result.Holds, h => h.Contains("rows: one", StringComparison.Ordinal));
        Assert.Empty(result.StepsSetAside);
    }

    [Fact]
    public void What_a_step_kept_that_no_longer_fits_the_record_holds_it()
    {
        var owner = new RecordChain(Renderer(Owner(data: "Symbol: { $value: owner }")), [Aside(Grade)]);
        var result = owner.Render(Record("a", 1, ("grade", Several)), new ChainInputs { Kept = Kept(true, ("grade", [SymbolOf("K")], [])) });

        Assert.True(result.IsHeld);
        Assert.Contains(result.Holds, h => h.Contains("no longer fits the record", StringComparison.Ordinal));
    }

    [Fact]
    public void A_step_whose_hold_holds_the_record_holds_it_whatever_was_kept()
    {
        var result = Chain(Step("grade", Grade)).Render(Record("a", 1, ("grade", Several)), new ChainInputs { Kept = Kept(true, ("grade", [SymbolOf("K")], [])) });

        Assert.True(result.IsHeld);
        Assert.False(result.Enrichment[0].SetAside);
    }

    [Fact]
    public void A_step_held_whatever_it_renders_is_set_aside_with_the_reason_given()
    {
        var inputs = new ChainInputs { Kept = Kept(true, ("grade", [SymbolOf("K")], ["dev:dataset--File.Generic:a-enrich.grade"])) }.Hold("grade", "its files are not there");
        var result = Chain(Aside(Grade)).Render(Record("a", 1, ("grade", [Row(("grade", "A"))])), inputs);

        Assert.Equal("K", Symbol(result));
        var step = Assert.Single(result.Enrichment);
        Assert.Equal(["enrich.grade: its files are not there"], step.Holds);
        Assert.Equal(["dev:dataset--File.Generic:a-enrich.grade"], step.KeptDatasets);
    }

    [Fact]
    public void A_step_that_conflicts_halfway_leaves_nothing_of_itself_in_the_record()
    {
        // The fragment adds an alias (which composes) and sets Symbol, which the mapping already set otherwise (a conflict).
        var owner = new RecordChain(Renderer(Owner(data: "Symbol: { $value: owner }")), [Aside(Grade)]);
        var result = owner.Render(Record("a", 1, ("grade", [Row(("grade", "A"), ("alias", "x"))])));

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.True(result.Enrichment[0].SetAside);
        Assert.Null(result.Document["data"]!["Aliases"]);
        Assert.Equal("owner", Symbol(result));
    }

    [Fact]
    public void A_step_that_rendered_carries_what_it_composed_for_a_later_render_it_holds_in()
    {
        var result = Chain(Aside(Grade)).Render(Record("a", 1, ("grade", [Row(("grade", "A"))])));

        var step = Assert.Single(result.Enrichment);
        Assert.False(step.SetAside);
        Assert.Equal("A", Assert.Single(step.Composed)["data"]!["Symbol"]!.GetValue<string>());
    }

    [Fact]
    public void A_kept_contribution_reads_back_as_written_and_a_value_that_does_not_read_as_one_is_none()
    {
        var kept = new KeptStep([SymbolOf("K"), new JsonObject { ["data"] = new JsonObject { ["Datasets"] = new JsonArray("b:", "a:") } }], ["b", "a", "b"]);
        var text = kept.ToText();
        var read = KeptStep.Parse(text)!;

        Assert.Equal(text, read.ToText());
        Assert.Equal(["a", "b"], read.Datasets);
        Assert.Equal(2, read.Fragments.Count);
        Assert.Null(KeptStep.Parse("not json"));
        Assert.Null(KeptStep.Parse("{\"fragments\":[1],\"datasets\":[]}"));
        Assert.Null(KeptStep.Parse(null));
    }

    [Fact]
    public void What_a_record_kept_is_read_from_its_target_state_and_says_whether_OSDU_holds_it()
    {
        var kept = new KeptStep([SymbolOf("K")], []).ToText();
        var state = new JsonObject { [KeptStep.StateKey("grade")] = kept, ["enrich.other"] = "x", [KeptStep.StateKey("broken")] = "{" };
        var record = new RecordState
        {
            DeliveryKey = DeliveryKey.Derive("test", ["a"]),
            FlowId = Guid.NewGuid(),
            SourceKey = "a",
            MappingName = "Thing",
            TargetVersion = 4,
            TargetStateJson = state.ToJsonString(),
        };

        var read = KeptSteps.From(record);

        Assert.True(read.Delivered);
        Assert.Equal(["grade"], read.Steps.Keys);
        Assert.False(KeptSteps.From(record with { TargetVersion = null }).Delivered);
        Assert.Same(KeptSteps.None, KeptSteps.From(null));
    }
}
