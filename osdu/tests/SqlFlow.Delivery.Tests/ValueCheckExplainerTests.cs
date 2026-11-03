using SqlFlow.Delivery.Engine.Checks;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a finding of a value check says in plain words (<see cref="ValueCheckExplainer"/>): what was looked for and not found,
/// or the rule broken, what that does to the records, and what to do about it, the likeliest first.
/// </summary>
public sealed class ValueCheckExplainerTests
{
    private static readonly ValueCheckInputs Inputs = new("WellLog@1.4.0", "osdu:wks:work-product-component--WellLog:1.4.0", "abc", "dev", "20261101T120000Z");

    private static ValueCheckVariable Variable(string target = "osdu.data.Curves[].CurveUnit", bool required = true)
        => new() { Target = target, Rows = new ValueCheckCounts(), Required = required };

    private static ValueCheckFinding Finding(string outcome, ValueCheckCause cause, params (string Value, long Count)[] values) => new()
    {
        Outcome = outcome,
        At = "osdu.data.Curves[].CurveUnit",
        Message = "the reason as the render states it",
        Count = values.Sum(v => v.Count),
        Rows = values.Sum(v => v.Count),
        Values = values.Select(v => new ValueCheckValue(v.Value, v.Count)).ToList(),
        Causes = [cause],
    };

    [Fact]
    public void A_lookup_that_found_nothing_names_the_type_the_fields_and_the_values_and_says_where_to_fix_it()
    {
        var cause = new ValueCheckCause { Kind = ValueCheckCause.NoMatch, Types = ["UnitOfMeasure"], EntityType = "reference-data--UnitOfMeasure", Fields = ["Code", "Name"], Columns = ["dataset.curves.curve_unit"] };

        var said = ValueCheckExplainer.Explain(Finding("held", cause, ("FURLONG", 40), ("API", 3), ("x", 1), ("y", 1)), Variable(), Inputs);

        Assert.Equal("No `UnitOfMeasure` in the cache has `Code` or `Name` equal to `FURLONG`, `API`, `x` and 1 more values", said.Headline);
        Assert.Equal("Held: these records are not delivered until it is fixed.", said.Effect);
        Assert.Equal("Looked up in version `20261101T120000Z` of the cache of partition `dev`, which holds what a cache flow last captured.", said.Detail);
        Assert.Equal(
            [
                "If the value is wrong, correct `dataset.curves.curve_unit` in the source for the records listed.",
                "If OSDU knows it under another code, translate it in the mapping: a `replace` modifier, or a row of the lookup table the mapping replaces through.",
                "If the record is missing from OSDU, load it into the partition, then refresh the cache flow that captures `UnitOfMeasure`, so the next check finds it.",
            ],
            said.Fixes);
    }

    [Fact]
    public void A_row_missing_from_a_lookup_table_is_added_to_the_table_rather_than_loaded_into_OSDU()
    {
        var cause = new ValueCheckCause { Kind = ValueCheckCause.NoMatch, Types = ["UnitAlias"], Fields = ["key"], Columns = ["dataset.unit"] };

        var said = ValueCheckExplainer.Explain(Finding("empty", cause, ("FURLONG", 2)), Variable(required: false), Inputs);

        Assert.Equal("Left out: these records are delivered without `CurveUnit`.", said.Effect);
        Assert.Equal("If the row is missing from the lookup table `UnitAlias`, add it to the table the cache flow reads, then refresh that cache flow.", said.Fixes[2]);
    }

    [Fact]
    public void An_id_the_cache_does_not_hold_names_the_code_it_ends_with_and_what_the_source_held()
    {
        var cause = new ValueCheckCause { Kind = ValueCheckCause.IdNotHeld, Types = ["UnitOfMeasure"], EntityType = "reference-data--UnitOfMeasure", Fields = ["id"], Columns = ["dataset.unit"] };
        var finding = Finding("held", cause, ("dev:reference-data--UnitOfMeasure:Gamma%20Ray", 2)) with
        {
            SourceValues = [new ValueCheckSourceValue("dataset.unit", "GAMMA RAY", 2)],
        };

        var said = ValueCheckExplainer.Explain(finding, Variable(), Inputs);

        Assert.Equal("The cache holds no `reference-data--UnitOfMeasure` record `Gamma Ray`", said.Headline);
        Assert.EndsWith(
            "The source holds `GAMMA RAY` in `dataset.unit`, which the mapping turns into `dev:reference-data--UnitOfMeasure:Gamma%20Ray`.",
            said.Detail,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ValueCheckCause.Ambiguous, "`ft` matches 2 `UnitOfMeasure` records once case is ignored (`dev:u:ft`, `dev:u:fT`), so none is taken")]
    [InlineData(ValueCheckCause.NoType, "The cache holds no type `UnitOfMeasure`")]
    [InlineData(ValueCheckCause.FieldEmpty, "The `UnitOfMeasure` records `dev:u:ft`, `dev:u:fT` hold nothing at `Code`")]
    [InlineData(ValueCheckCause.RuleBroken, "The value breaks the template's `pattern` rule: the reason as the render states it")]
    [InlineData(ValueCheckCause.Assertion, "The value fails the mapping's assertion `pattern`")]
    [InlineData(ValueCheckCause.Other, "The reason as the render states it")]
    public void Each_cause_has_a_headline_of_its_own_and_something_to_do(string kind, string headline)
    {
        var cause = new ValueCheckCause
        {
            Kind = kind, Types = ["UnitOfMeasure"], Fields = ["Code"], Candidates = ["dev:u:ft", "dev:u:fT"], Columns = ["dataset.unit"], Rule = "pattern",
        };

        var said = ValueCheckExplainer.Explain(Finding("held", cause, ("ft", 1)) with { Rule = "pattern" }, Variable(), Inputs);

        Assert.Equal(headline, said.Headline);
        Assert.NotEmpty(said.Fixes);
    }

    [Fact]
    public void A_coalesce_none_of_whose_alternatives_found_a_value_says_so_before_the_first_one_s_reason()
    {
        var finding = Finding("held", new ValueCheckCause { Kind = ValueCheckCause.Empty, Columns = ["dataset.curves.curve_unit"] }) with
        {
            Causes =
            [
                new ValueCheckCause { Kind = ValueCheckCause.Empty, Columns = ["dataset.curves.curve_unit"] },
                new ValueCheckCause { Kind = ValueCheckCause.Empty, Columns = ["dataset.curves.unit_alt"] },
            ],
        };

        var said = ValueCheckExplainer.Explain(finding, Variable(), Inputs);

        Assert.Equal("None of the 2 ways the mapping gives `CurveUnit` found a value; the first: `dataset.curves.curve_unit` is empty", said.Headline);
    }
}
