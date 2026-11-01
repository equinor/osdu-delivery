using SqlFlow.Delivery.Documents;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a mapping does, laid out for a person to read (osdu/docs/reference/concepts/mapping-overview.md): read from the
/// definition a render reads, so every node of the language has its place, and a value is drawn in the order a render takes it.
/// </summary>
public class MappingOutlineTests
{
    private static MappingOutlineNode At(MappingOutline outline, string path)
        => Walk(outline.Record).Single(node => node.Path == path);

    private static IEnumerable<MappingOutlineNode> Walk(IEnumerable<MappingOutlineNode> nodes)
        => nodes.SelectMany(node => Walk(node.Children).Prepend(node));

    private static MappingOutlineSource Source(MappingOutline outline, string id)
        => outline.Sources.Single(source => source.Id == id);

    [Fact]
    public void A_column_written_as_it_is_is_copied_and_the_key_makes_the_id()
    {
        var outline = MappingOutline.Of(TestSchema.Mapping());

        var name = At(outline, "data.Name");
        Assert.Equal(MappingOutlineShape.Value, name.Shape);
        Assert.Equal(MappingOutlineHow.Copied, name.How);
        Assert.Equal(new MappingOutlineRead(MappingOutline.DatasetSource, "name", "value"), Assert.Single(name.Flow!.Reads));
        Assert.Empty(name.Flow.Steps);

        // No node fills the id: the engine makes it from the key, so the key is what the outline draws entering it.
        var id = outline.Record[0];
        Assert.Equal(("id", MappingOutlineHow.Key), (id.Path, id.How));
        Assert.Equal(outline.Dataset.Key, id.Flow!.Reads.Select(read => read.Field));
        Assert.All(outline.Dataset.Key, key => Assert.True(Source(outline, MappingOutline.DatasetSource).Fields.Single(f => f.Name == key).Key));
    }

    [Fact]
    public void An_object_is_a_group_of_what_it_holds_in_the_order_the_document_writes_it()
    {
        var outline = MappingOutline.Of(TestSchema.Mapping("""
            Nested:
              Inner: { $from: inner }
            """));

        Assert.Equal(["id", "acl", "legal", "data"], outline.Record.Select(node => node.Path));
        var nested = At(outline, "data.Nested");
        Assert.Equal(MappingOutlineShape.Group, nested.Shape);
        Assert.Equal("data.Nested.Inner", Assert.Single(nested.Children).Path);
    }

    [Fact]
    public void A_literal_is_fixed_and_a_parameter_alone_enters_as_the_parameter()
    {
        var outline = MappingOutline.Of(TestSchema.Mapping(record: """
            tags:
              Owner: "{$param.dataPartition}"
              Label: "from {$param.dataPartition}"
              Plain: fixed
            """));

        var owner = At(outline, "tags.Owner");
        Assert.Equal(("parameter", MappingOutlineHow.Fixed, null), (owner.Flow!.Origin, owner.How, owner.Flow.Literal));
        Assert.Equal(new MappingOutlineRead(MappingOutline.ParametersSource, "dataPartition", "value"), Assert.Single(owner.Flow.Reads));

        // A text around a parameter is written as it stands, and still says what it reads.
        var label = At(outline, "tags.Label");
        Assert.Equal("literal", label.Flow!.Origin);
        Assert.Contains("from {$param.dataPartition}", label.Flow.Literal, StringComparison.Ordinal);
        Assert.Equal("dataPartition", Assert.Single(label.Flow.Reads).Field);

        Assert.Empty(At(outline, "tags.Plain").Flow!.Reads);
    }

    [Fact]
    public void Modifiers_change_a_value_in_the_order_they_are_written()
    {
        var outline = MappingOutline.Of(TestSchema.Mapping("""
            Symbol:
              $from: symbol
              $modifiers:
                - split: { separator: ",", part: 2 }
                - trim
                - replace: { A: B }
            """));

        var symbol = At(outline, "data.Symbol");
        Assert.Equal(MappingOutlineHow.Changed, symbol.How);
        Assert.Equal(["split", "trim", "replace"], symbol.Flow!.Steps.Select(step => step.Kind));
        Assert.Equal("split 2", symbol.Flow.Steps[0].Label);
        Assert.Contains("A to B", symbol.Flow.Steps[2].Detail, StringComparison.Ordinal);
        Assert.Empty(symbol.Flow.Steps[2].Lookups);
    }

    [Fact]
    public void A_cache_node_changes_the_value_it_compares_before_it_finds_the_record()
    {
        var outline = MappingOutline.Of(TestSchema.Mapping("""
            Unit:
              $cache: UnitOfMeasure.id
              $findBy:
                - Code = unit
                - Name = unit
              $modifiers: [upper]
            """));

        var unit = At(outline, "data.Unit");
        Assert.Equal(MappingOutlineHow.LookedUp, unit.How);
        Assert.Equal(["upper", "find"], unit.Flow!.Steps.Select(step => step.Kind));
        var lookup = Assert.Single(unit.Flow.Steps[1].Lookups);
        Assert.Equal(("cache.UnitOfMeasure", "id"), (lookup.Source, lookup.Field));
        Assert.Equal(["Code", "Name"], lookup.Matches.Select(match => match.Field));

        // What it compares enters the flow, read in the role of a match, and the cache's fields say which they are.
        Assert.Equal(new MappingOutlineRead(MappingOutline.DatasetSource, "unit", "match"), Assert.Single(unit.Flow.Reads));
        var cached = Source(outline, "cache.UnitOfMeasure");
        Assert.True(cached.Fields.Single(field => field.Name == "Code").Match);
        Assert.Equal(1, cached.Fields.Single(field => field.Name == "id").Reads);
    }

    [Fact]
    public void A_replace_from_the_cache_and_an_id_token_read_their_tables()
    {
        var outline = MappingOutline.Of(TestSchema.Mapping("""
            Symbol:
              $from: symbol
              $modifiers:
                - replace: $cache.Alias
            Unit:
              $from: unit
              $modifiers:
                - id: "{$param.dataPartition}:reference-data--UnitOfMeasure:{$cache.Dictionary.code}:"
            """));

        var replace = Assert.Single(At(outline, "data.Symbol").Flow!.Steps);
        Assert.Equal("cache.Alias", Assert.Single(replace.Lookups).Source);

        var id = Assert.Single(At(outline, "data.Unit").Flow!.Steps);
        Assert.Equal("id", id.Kind);
        var token = Assert.Single(id.Lookups);
        Assert.Equal(("cache.Dictionary", "code"), (token.Source, token.Field));
        Assert.Equal(MappingOutlineHow.LookedUp, At(outline, "data.Unit").How);
    }

    [Fact]
    public void A_coalesce_is_a_choice_of_its_alternatives_in_the_order_they_are_tried()
    {
        var outline = MappingOutline.Of(TestSchema.Mapping("""
            Symbol:
              $coalesce:
                - $from: symbol
                - $cache: Alias.value
                  $findBy: Code = symbol
                - $from: fallback
            """));

        var symbol = At(outline, "data.Symbol");
        Assert.Equal(MappingOutlineShape.Choice, symbol.Shape);
        Assert.Equal(MappingOutlineHow.LookedUp, symbol.How);
        Assert.Equal(["1", "2", "3"], symbol.Children.Select(child => child.Name));
        Assert.Equal(["column", "find", "column"], symbol.Children.Select(child => child.Flow!.Origin));
    }

    [Fact]
    public void A_foreach_repeats_its_child_dataset_and_the_item_reads_that_rows_columns()
    {
        var outline = MappingOutline.Of(TestSchema.Mapping("""
            Curves:
              $forEach: curves
              $where: curve_id != "DEPT"
              $item:
                CurveID: { $from: curve_id }
                TopDepth: { $from: $dataset.depth }
            """));

        var curves = At(outline, "data.Curves");
        Assert.Equal((MappingOutlineShape.Repeat, "curves"), (curves.Shape, curves.Dataset));
        Assert.Equal("curve_id != \"DEPT\"", curves.Where);
        Assert.Equal(["data.Curves[].CurveID", "data.Curves[].TopDepth"], curves.Children.Select(child => child.Path));
        Assert.Equal(MappingOutlineSourceKind.Child, Source(outline, "dataset.curves").Kind);
        Assert.Equal("dataset.curves", At(outline, "data.Curves[].CurveID").Flow!.Reads[0].Source);

        // $dataset reads the record's own row from inside an item.
        Assert.Equal(MappingOutline.DatasetSource, At(outline, "data.Curves[].TopDepth").Flow!.Reads[0].Source);
    }

    [Fact]
    public void A_list_and_a_findAll_a_draft_cannot_hold_are_laid_out_all_the_same()
    {
        var outline = MappingOutline.Of(TestSchema.Mapping("""
            Aliases:
              - viewers@x
              - $cache: AccessGroup.GroupEmail
                $findAll:
                  - Field = field_id
                  - Country is empty
            """));

        var viewers = At(outline, "data.Aliases");
        Assert.Equal(MappingOutlineShape.List, viewers.Shape);
        Assert.Equal(["1", "2"], viewers.Children.Select(child => child.Name));
        Assert.Equal(MappingOutlineHow.Fixed, viewers.Children[0].How);

        var found = viewers.Children[1];
        Assert.Equal(MappingOutlineHow.LookedUp, found.How);
        var lookup = Assert.Single(Assert.Single(found.Flow!.Steps).Lookups);
        Assert.Equal(("cache.AccessGroup", "GroupEmail"), (lookup.Source, lookup.Field));
        Assert.Equal("Field", Assert.Single(lookup.Matches).Field);
        Assert.Equal(new MappingOutlineRead(MappingOutline.DatasetSource, "field_id", "match"), Assert.Single(found.Flow.Reads));
    }

    [Fact]
    public void A_condition_enters_as_what_it_tests_and_a_rule_is_stated_on_its_node()
    {
        var outline = MappingOutline.Of(TestSchema.Mapping("""
            Count:
              $from: count
              $when: flag = "Y"
              $required: false
              $assert:
                - name: small
                  atMost: 10
                  onFail: report
            """));

        var count = At(outline, "data.Count");
        Assert.False(count.Required);
        Assert.Equal("flag = \"Y\"", count.When);
        Assert.Contains(new MappingOutlineRead(MappingOutline.DatasetSource, "flag", "condition"), count.Flow!.Reads);
        var rule = Assert.Single(count.Assertions);
        Assert.Equal(("small", "report"), (rule.Label, rule.OnFail));
    }

    [Fact]
    public void An_expression_enters_every_column_it_reads()
    {
        var outline = MappingOutline.Of(TestSchema.Mapping("""
            Symbol:
              $expr: coalesce(symbol, fallback)
            """));

        var symbol = At(outline, "data.Symbol");
        Assert.Equal((MappingOutlineHow.Changed, "expression"), (symbol.How, symbol.Flow!.Origin));
        Assert.Equal(["symbol", "fallback"], symbol.Flow.Reads.Select(read => read.Field));
        Assert.Equal("coalesce(symbol, fallback)", Assert.Single(symbol.Flow.Steps).Text);
    }

    [Fact]
    public void The_template_gives_each_property_its_type_and_tells_a_ref_what_it_names()
    {
        var outline = MappingOutline.Of(
            TestSchema.Mapping("""
                Unit:
                  $from: unit
                  $modifiers: [ref]
                """),
            TestSchema.Build());

        Assert.Equal("number", At(outline, "data.Depth").Type);
        var unit = At(outline, "data.Unit");
        Assert.Equal("reference", unit.Type);
        Assert.Contains("reference-data--UnitOfMeasure", Assert.Single(unit.Flow!.Steps).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mapping_read_with_no_template_is_laid_out_without_types()
    {
        var outline = MappingOutline.Of(TestSchema.Mapping());
        Assert.All(Walk(outline.Record).Where(node => node.Path != "id"), node => Assert.Null(node.Type));
    }

    [Fact]
    public void The_sample_well_log_is_laid_out_whole_with_each_node_at_its_lines()
    {
        var path = RepositoryRoot.Combine("osdu", "samples", "welldb", "mappings", "WellLog@1.4.0.yaml");
        var yaml = File.ReadAllText(path);
        var outline = MappingOutline.Of(new DeliveryDocumentLoader().ParseMapping(yaml, "WellLog@1.4.0.yaml"), yaml: yaml);

        // Reads what the document reads, the dataset first and what it looks in last.
        Assert.Equal(
            ["record", "child", "parameters"],
            outline.Sources.Select(source => source.Kind).Take(3));
        Assert.Equal(["key", "key"], Source(outline, MappingOutline.DatasetSource).Fields.Where(f => f.Key).Select(_ => "key"));
        Assert.Contains(outline.Sources, source => source.Id == "search.Wellbore");
        Assert.Contains(outline.Sources, source => source.Id == "cache.UnitOfMeasure");
        Assert.Contains(outline.Sources, source => source.Id == "cache.DepthUnitAlias");
        Assert.Contains(outline.Sources, source => source.Id == "cache.CurveDictionary");

        // A $coalesce of the document, with the three alternatives its comment describes, in the lines it is written on.
        var lines = yaml.ReplaceLineEndings("\n").Split('\n');
        var unit = At(outline, "data.Curves[].CurveUnit");
        Assert.Equal(MappingOutlineShape.Choice, unit.Shape);
        Assert.Equal(3, unit.Children.Count);
        Assert.Equal("        CurveUnit:", lines[unit.Line!.Value - 1]);
        Assert.Contains("ref", lines[unit.EndLine!.Value - 1], StringComparison.Ordinal);
        Assert.All(unit.Children, child => Assert.NotNull(child.Line));
        Assert.Contains("$cache: UnitOfMeasure.id", lines[unit.Children[1].Line!.Value - 1], StringComparison.Ordinal);

        // The value a split, a table and a ref take in turn, in that order.
        var elevation = At(outline, "data.VerticalMeasurement.VerticalMeasurementUnitOfMeasureID").Children[0];
        Assert.Equal(["split", "replace", "ref"], elevation.Flow!.Steps.Select(step => step.Kind));
        Assert.Equal("cache.DepthUnitAlias", elevation.Flow.Steps[1].Lookups[0].Source);

        // Everything on the document's path: nothing the document writes is left out of the tree.
        var wellbore = At(outline, "data.WellboreID");
        Assert.Equal(MappingOutlineHow.LookedUp, wellbore.How);
        Assert.Equal("search.Wellbore", wellbore.Flow!.Steps[0].Lookups[0].Source);
        Assert.Equal(["data.FacilityName", "data.NameAliases.AliasName"], wellbore.Flow.Steps[0].Lookups[0].Matches.Select(match => match.Field));
    }
}
