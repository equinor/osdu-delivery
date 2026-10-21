using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A dimension's blueprint (osdu/docs/reference/concepts/dimension-blueprint.md): its YAML found line by line, each
/// path read through the template of the records it is read from (the forms of a <c>oneOf</c> looked into, a filter's
/// property looked up), the records each key names reached step by step and grouped as a build's searches group them,
/// and the table's columns with the reads each is written from. The templates are held in memory, so each case says
/// exactly which a repository holds.
/// </summary>
public class DimensionBlueprintTests
{
    private const string WellLogKind = "osdu:wks:work-product-component--WellLog:1.4.0";
    private const string WellboreKind = "osdu:wks:master-data--Wellbore:1.3.0";
    private const string CountryKind = "osdu:wks:master-data--GeoPoliticalEntity:1.0.0";

    /// <summary>The well database estate's Wellbore dimension, as its flow writes it, comments and all.</summary>
    private const string Flow = """
        flowType: dimension
        name: welldb-welllog-05-dimensions
        source:
          endpoint: http://localhost
        dimensions:
          # The wellbore of each log.
          - name: Wellbore
            description: >
              The wellbore of each log, by its name.
            kind: osdu:wks:work-product-component--WellLog:1.4.0
            path: data.WellboreID
            label: data.FacilityName
            unlabelled: Not specified
            attributes:
              UUID: data.FacilityID
              Field: [data.GeoContexts.FieldID, data.FieldName]
              # GeoContexts can name a region and a block beside the country.
              Country:
                - data.GeoContexts.GeoPoliticalEntityID
                - 'data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityName'
              Source: { collect: data.Source }
            clean: [trim, collapseSpaces]
          - name: LogSource
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: data.Source
        """;

    private static DimensionFlowDefinition Parsed => new DeliveryDocumentLoader().ParseDimension(Flow, "flows/welldb-welllog-05-dimensions.yaml");

    private static DimensionSpec Wellbore => Parsed.Dimension("Wellbore")!;

    [Fact]
    public void The_yaml_of_a_dimension_is_found_with_the_line_and_columns_of_everything_it_declares()
    {
        var block = DimensionYamlSource.Locate(Flow, "wellbore");

        Assert.NotNull(block);

        // The comment just above the item is about it; the next item is not part of it.
        Assert.Equal(6, block!.FirstLine);
        Assert.Equal("  # The wellbore of each log.", block.Lines[0]);
        Assert.Equal("    clean: [trim, collapseSpaces]", block.Lines[^1]);
        Assert.False(block.Cut);
        var spans = block.Spans.ToDictionary(s => s.Target);

        Assert.Equal((11, 5, 11, 26), At(spans["path"]));
        Assert.Equal((12, 12, 12, 29), At(spans["label.0"]));

        // A folded description ends on its own last line, not where the next key starts.
        Assert.Equal((8, 5, 9, 45), At(spans["description"]));

        // A list written on one line names each of its items by its columns.
        Assert.Equal((16, 15, 16, 39), At(spans["attributes.Field.0"]));
        Assert.Equal((16, 41, 16, 55), At(spans["attributes.Field.1"]));
        Assert.Equal((19, 11, 19, 48), At(spans["attributes.Country.0"]));
        Assert.Equal(20, spans["attributes.Country.1"].Line);
        Assert.Equal((18, 7, 20, 99), At(spans["attributes.Country"]));
        Assert.Equal((21, 17, 21, 37), At(spans["attributes.Source.collect"]));
        Assert.Equal((22, 19, 22, 33), At(spans["clean.1"]));

        // A list or a map written in brackets ends after its closing bracket, though the parser marks it at the opening one.
        Assert.Equal((16, 7, 16, 56), At(spans["attributes.Field"]));
        Assert.Equal((21, 7, 21, 39), At(spans["attributes.Source"]));
        Assert.Equal((14, 5, 21, 39), At(spans["attributes"]));
        Assert.Equal((22, 5, 22, 34), At(spans["clean"]));
        Assert.Equal(block.FirstLine + block.Lines.Count - 1, spans.Values.Max(s => s.EndLine));
    }

    [Fact]
    public void A_dimension_the_document_does_not_declare_or_a_document_that_does_not_parse_has_no_yaml()
    {
        Assert.Null(DimensionYamlSource.Locate(Flow, "Country"));
        Assert.Null(DimensionYamlSource.Locate("dimensions: [ { name: Wellbore", "Wellbore"));
        Assert.Null(DimensionYamlSource.Locate("flowType: dimension\n", "Wellbore"));

        var second = DimensionYamlSource.Locate(Flow, "LogSource")!;
        Assert.Equal(["  - name: LogSource", "    kind: \"osdu:wks:work-product-component--WellLog:*\"", "    path: data.Source"], second.Lines);
    }

    [Fact]
    public void A_path_is_read_through_the_forms_a_oneOf_allows_and_a_filter_s_property_is_looked_up()
    {
        var wellbore = Schema(WellboreKind);

        var field = SchemaPathReader.Read(wellbore, Path("data.GeoContexts.FieldID"));
        Assert.Null(field.Problem);
        Assert.Equal("nested", field.Segments[1].Indexing);
        Assert.Equal("array", field.Segments[1].Type);
        Assert.Equal("object", field.Segments[1].ItemType);

        // Only the field's context of the five a GeoContext can be declares FieldID.
        Assert.Equal("AbstractGeoFieldContext", field.Leaf!.Branch);
        Assert.Equal(["master-data--Field"], field.References);

        var name = SchemaPathReader.Read(wellbore, Path("data.FacilityName"));
        Assert.Equal("Name of the Facility.", name.Leaf!.Description);
        Assert.Empty(name.References);

        var filtered = SchemaPathReader.Read(wellbore, Path("data.GeoContexts[GeoTypeID=Field].FieldID"));
        Assert.True(filtered.Segments[1].Filter!.Known);
        Assert.Equal("equals", filtered.Segments[1].Filter!.Compare);
        Assert.False(SchemaPathReader.Read(wellbore, Path("data.GeoContexts[Nothing$=x].FieldID")).Segments[1].Filter!.Known);

        var missing = SchemaPathReader.Read(wellbore, Path("data.GeoContexts.FieldName"));
        Assert.False(missing.Complete);
        Assert.Contains("one of 5 forms", missing.Problem, StringComparison.Ordinal);
        Assert.Contains("FieldName", missing.Problem, StringComparison.Ordinal);
        Assert.Contains(WellboreKind, SchemaPathReader.Read(wellbore, Path("data.Nothing.Here")).Problem, StringComparison.Ordinal);
        Assert.False(SchemaPathReader.Read(wellbore, Path("data.Nothing.Here")).Segments[2].Found);
    }

    [Fact]
    public async Task A_blueprint_reads_the_logs_then_each_wellbore_then_its_field_and_country_in_the_templates_of_each()
    {
        var templates = new MemoryTemplates(Schema(WellLogKind), Schema(WellboreKind), Country());

        var blueprint = await DimensionBlueprints.DescribeAsync(Wellbore, templates, []);

        // Before a build, the kinds are the templates the repositories hold that the pattern matches.
        var source = blueprint.Source;
        Assert.False(source.Built);
        Assert.Equal(WellLogKind, Assert.Single(source.Kinds).Kind);
        Assert.Equal(WellLogKind, source.Template!.Kind);
        Assert.Null(source.Missing);

        // The key: the logs' WellboreID, read as the build's search reads it, naming the wellbore.
        var key = source.Reads[0];
        Assert.Equal(BlueprintRoles.KeyRead, key.Id);
        Assert.Equal("data.WellboreID.keyword", key.Index!.AggregateBy);
        Assert.Equal("text", key.Index.Index);
        Assert.Equal(["master-data--Wellbore"], key.References);
        Assert.Equal("1:master-data--Wellbore", key.LeadsTo);
        Assert.Null(key.Problem);

        // The collected source is read from the logs themselves.
        var collected = source.Reads[1];
        Assert.Equal(BlueprintRoles.CollectRead("Source"), collected.Id);
        Assert.Equal("data.Source.keyword", collected.Index!.AggregateBy);
        Assert.Equal(new BlueprintUse(BlueprintRoles.Collect, "Source", 0, true), Assert.Single(collected.Uses));

        // Each wellbore is read once for its label and all three attributes that start there.
        var wellbore = blueprint.Records.Single(r => r.Id == "1:master-data--Wellbore");
        Assert.Equal(WellboreKind, wellbore.Template!.Kind);
        Assert.Equal(["data.FacilityName", "data.FacilityID", "data.GeoContexts.FieldID", "data.GeoContexts.GeoPoliticalEntityID"], wellbore.Reads.Select(r => r.Path));
        Assert.Equal(new BlueprintUse(BlueprintRoles.Label, null, 0, true), Assert.Single(wellbore.Reads[0].Uses));
        Assert.Equal("2:master-data--Field", wellbore.Reads[2].LeadsTo);
        Assert.Equal("AbstractGeoFieldContext", wellbore.Reads[2].Schema!.Leaf!.Branch);
        Assert.All(wellbore.Reads, r => Assert.Null(r.Problem));

        // No repository holds a template of a field: its records are read as written, and the blueprint says so.
        var field = blueprint.Records.Single(r => r.Id == "2:master-data--Field");
        Assert.Null(field.Template);
        Assert.Contains("master-data--Field", field.Missing, StringComparison.Ordinal);
        Assert.Null(Assert.Single(field.Reads).Schema);
        Assert.Equal(new BlueprintUse(BlueprintRoles.Attribute, "Field", 1, true), Assert.Single(field.Reads[0].Uses));

        // The country's own type is looked up in its template, where the filter compares it.
        var country = blueprint.Records.Single(r => r.Id == "2:master-data--GeoPoliticalEntity");
        Assert.Equal(CountryKind, country.Template!.Kind);
        var name = Assert.Single(country.Reads);
        Assert.True(name.Schema!.Segments[0].Filter!.Known);
        Assert.Equal("contains", name.Schema.Segments[0].Filter!.Compare);
        Assert.Equal("The name of the entity.", name.Schema.Leaf!.Description);
        Assert.Equal("1:master-data--Wellbore/data.GeoContexts.GeoPoliticalEntityID", country.From);

        // The table's columns in its order, each with the read it is written from.
        Assert.Equal(
            ["id", "partition", "key_id", "WellboreID", "FacilityName", "records", "filter", "UUID", "Field", "Country", "Source"],
            blueprint.Columns.Select(c => c.Name));
        var columns = blueprint.Columns.ToDictionary(c => c.Name);
        Assert.Equal(["1:master-data--Wellbore/data.FacilityName"], columns["FacilityName"].From);
        Assert.Equal(["2:master-data--Field/data.FieldName"], columns["Field"].From);
        Assert.Equal([BlueprintRoles.CollectRead("Source")], columns["Source"].From);
        Assert.Equal([BlueprintRoles.KeyRead, BlueprintRoles.CollectRead("Source")], columns["records"].From);
    }

    [Fact]
    public async Task After_a_build_the_kinds_it_read_describe_the_records_with_their_counts()
    {
        var templates = new MemoryTemplates(Schema(WellLogKind), Schema(WellboreKind));
        var logs = Schema(WellLogKind);
        var dimension = Parsed.Dimension("LogSource")!;

        var blueprint = await DimensionBlueprints.DescribeAsync(
            dimension, templates, [new DimensionKind("osdu:wks:work-product-component--WellLog:1.0.0", 3, null), new DimensionKind(WellLogKind, 120, logs.Version)]);

        Assert.True(blueprint.Source.Built);
        Assert.Equal([(WellLogKind, 120L), ("osdu:wks:work-product-component--WellLog:1.0.0", 3L)], blueprint.Source.Kinds.Select(k => (k.Kind, k.Records!.Value)));
        Assert.Equal(new BlueprintTemplate(WellLogKind, logs.Version), blueprint.Source.Template);
        Assert.Empty(blueprint.Records);

        // With no label the key is its own value, and the value's column is the dimension's.
        Assert.Equal([BlueprintRoles.KeyRead], blueprint.Columns.Single(c => c.Role == BlueprintRoles.Value).From);
        Assert.Equal(["LogSource", "Source"], blueprint.Columns.Where(c => c.Role is BlueprintRoles.Value or BlueprintRoles.Key).Select(c => c.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task With_no_template_held_the_blueprint_lays_out_the_reads_and_says_what_it_cannot_describe()
    {
        // A dimension the explorer drafts, which no flow holds yet, is described by the templates the repositories hold.
        var blueprint = await DimensionBlueprints.DescribeAsync(Wellbore, new MemoryTemplates(), []);

        Assert.Null(blueprint.Source.Template);
        Assert.Contains("No template a synced repository holds matches", blueprint.Source.Missing, StringComparison.Ordinal);
        Assert.Null(blueprint.Source.Reads[0].Index);
        Assert.Null(blueprint.Source.Reads[0].Schema);

        // Which records a key names is not known without the logs' template, so the step is named by the read before it.
        var first = Assert.Single(blueprint.Records, r => r.Depth == 1);
        Assert.Equal("1:from:key", first.Id);
        Assert.Empty(first.EntityTypes);
        Assert.Contains("which records its ids name is not known", first.Missing, StringComparison.Ordinal);
        Assert.Equal(4, first.Reads.Count);
        Assert.Equal(2, blueprint.Records.Count(r => r.Depth == 2));
    }

    [Fact]
    public async Task A_flows_dimension_no_template_folder_on_its_path_describes_says_where_to_put_the_template()
    {
        var root = Samples.NewTempDirectory();
        try
        {
            // A repository whose only template folder sits beside another project's documents, off the flow's path.
            Directory.CreateDirectory(System.IO.Path.Combine(root, ".git"));
            Directory.CreateDirectory(System.IO.Path.Combine(root, "other", "templates"));
            File.WriteAllText(
                System.IO.Path.Combine(root, "other", "templates", TemplateSources.FileName(Samples.WellLogKind)),
                TemplateSources.FileText(Samples.SampleTemplate(Samples.WellLogKind)));
            Directory.CreateDirectory(System.IO.Path.Combine(root, "welldb", "flows"));
            var document = System.IO.Path.Combine(root, "welldb", "flows", "dimensions.yaml");
            File.WriteAllText(document, "flowType: dimension\n");

            var blueprint = await DimensionBlueprints.DescribeAsync(Wellbore, new FolderTemplates(root), document, []);

            Assert.Null(blueprint.Source.Template);
            Assert.Contains("No template the flow reads matches", blueprint.Source.Missing, StringComparison.Ordinal);
            Assert.Contains("template folder on the flow's path", blueprint.Source.Missing, StringComparison.Ordinal);

            // The same template one folder up the flow's path describes it.
            Directory.Move(System.IO.Path.Combine(root, "other", "templates"), System.IO.Path.Combine(root, "welldb", "templates"));
            var described = await DimensionBlueprints.DescribeAsync(Wellbore, new FolderTemplates(root), document, []);
            Assert.Equal(Samples.WellLogKind, described.Source.Template?.Kind);
            Assert.Null(described.Source.Missing);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_read_the_template_says_cannot_be_followed_or_searched_says_why()
    {
        var yaml = """
            flowType: dimension
            name: odd
            source: { endpoint: http://localhost }
            dimensions:
              - name: Odd
                kind: osdu:wks:work-product-component--WellLog:1.4.0
                path: data.Source
                label: data.FacilityName
                attributes:
                  Spud: [data.Name, data.FacilityName]
                  Missing: data.Nothing
            """;
        var dimension = new DeliveryDocumentLoader().ParseDimension(yaml, "flows/odd.yaml").Dimensions[0];

        var blueprint = await DimensionBlueprints.DescribeAsync(dimension, new MemoryTemplates(Schema(WellLogKind), Schema(WellboreKind)), []);

        // data.Source is no reference, so a label is read only where a key is a record id.
        Assert.Contains("does not declare data.Source a reference", blueprint.Source.Reads[0].Problem, StringComparison.Ordinal);
        var unknown = Assert.Single(blueprint.Records, r => r.Depth == 1);
        Assert.Contains("does not say which records data.Source names", unknown.Missing, StringComparison.Ordinal);

        // A path a key's records are not described for carries no problem of its own; the records say why.
        Assert.All(unknown.Reads, r => Assert.Null(r.Problem));

        // The index cannot read a list of objects as a value.
        var curves = await DimensionBlueprints.DescribeAsync(
            dimension with { Path = "data.Curves", Label = [], Attributes = [] }, new MemoryTemplates(Schema(WellLogKind)), []);
        Assert.StartsWith("A build cannot read data.Curves:", curves.Source.Reads[0].Problem, StringComparison.Ordinal);
        Assert.Null(curves.Source.Reads[0].Index);
    }

    [Theory]
    [InlineData("osdu:wks:work-product-component--WellLog:1.*", "osdu:wks:work-product-component--WellLog:1.4.0", true)]
    [InlineData("osdu:wks:work-product-component--WellLog:1.*", "osdu:wks:work-product-component--WellLog:2.0.0", false)]
    [InlineData("*:*:master-data--Wellbore:*", "osdu:wks:master-data--Wellbore:1.3.0", true)]
    [InlineData("OSDU:wks:*--Wellbore:*", "osdu:wks:master-data--Wellbore:1.3.0", true)]
    [InlineData("osdu:wks:*", "osdu:wks:master-data--Wellbore:1.3.0", false)]
    [InlineData("osdu:wks:master-data--Well:*", "osdu:wks:master-data--Wellbore:1.3.0", false)]
    public void A_kind_pattern_matches_segment_by_segment(string pattern, string kind, bool matches)
        => Assert.Equal(matches, KindPatterns.Matches(pattern, kind));

    // ---- The keys a kind's template suggests (osdu/docs/reference/concepts/explorer.md, Building a dimension) ----

    [Fact]
    public async Task A_logs_template_suggests_the_wellbore_it_belongs_to_first()
    {
        var logs = Schema(WellLogKind);
        var suggested = await DimensionKeyCandidates.SuggestAsync(WellLogKind, new MemoryTemplates(logs, Schema(WellboreKind)), CancellationToken.None);

        Assert.Null(suggested.Missing);
        Assert.Equal(new BlueprintTemplate(WellLogKind, logs.Version), suggested.Template);
        var first = suggested.Keys[0];
        Assert.Equal(("data.WellboreID", false), (first.Path, first.Repeated));
        Assert.Contains("master-data--Wellbore", first.Names);

        // Every suggestion names records and is a path of data, once; a single value comes before every list.
        Assert.All(suggested.Keys, k => Assert.StartsWith("data.", k.Path, StringComparison.Ordinal));
        Assert.All(suggested.Keys, k => Assert.NotEmpty(k.Names));
        Assert.Equal(suggested.Keys.Count, suggested.Keys.Select(k => k.Path).Distinct(StringComparer.Ordinal).Count());
        var firstRepeated = suggested.Keys.ToList().FindIndex(k => k.Repeated);
        Assert.True(firstRepeated > 0);
        Assert.All(suggested.Keys.Skip(firstRepeated), k => Assert.True(k.Repeated));
        Assert.Contains(suggested.Keys, k => k.Path == "data.ResourceHostRegionIDs" && k.Repeated);

        // A property of data itself before one nested in an object of it, among the single values.
        var singles = suggested.Keys.Take(firstRepeated).Select(k => k.Path.Split('.').Length).ToList();
        Assert.Equal(singles.Order(), singles);
    }

    [Fact]
    public async Task A_wellbores_template_suggests_its_well_before_its_operators()
    {
        var suggested = await DimensionKeyCandidates.SuggestAsync(WellboreKind, new MemoryTemplates(Schema(WellboreKind)), CancellationToken.None);

        Assert.Equal("data.WellID", suggested.Keys[0].Path);
        Assert.Contains("master-data--Well", suggested.Keys[0].Names);
        var operators = suggested.Keys.ToList().FindIndex(k => k.Names.Contains("master-data--Organisation"));
        Assert.True(operators > 0);
    }

    [Fact]
    public async Task A_type_is_suggested_for_by_its_newest_template_as_a_blueprint_describes_it()
    {
        var logs = Schema(WellLogKind);
        var suggested = await DimensionKeyCandidates.SuggestAsync("*:*:work-product-component--WellLog:*", new MemoryTemplates(Schema(WellboreKind), logs), CancellationToken.None);

        Assert.Equal(new BlueprintTemplate(WellLogKind, logs.Version), suggested.Template);
        Assert.Equal("data.WellboreID", suggested.Keys[0].Path);
    }

    [Fact]
    public async Task A_kind_no_template_matches_suggests_nothing_and_says_where_to_put_one()
    {
        var suggested = await DimensionKeyCandidates.SuggestAsync("osdu:wks:master-data--Field:1.0.0", new MemoryTemplates(Schema(WellLogKind)), CancellationToken.None);

        Assert.Empty(suggested.Keys);
        Assert.Null(suggested.Template);
        Assert.Contains("No template a synced repository holds matches osdu:wks:master-data--Field:1.0.0", suggested.Missing, StringComparison.Ordinal);
        Assert.Contains("template folder", suggested.Missing, StringComparison.Ordinal);
    }

    [Fact]
    public void Suggestions_put_a_single_value_first_then_a_property_of_data_then_master_data_then_one_named_after_its_type()
    {
        static string Names(string group, string entity) => $$"""[{ "GroupType": "{{group}}", "EntityType": "{{entity}}" }]""";
        var schema = new SchemaSnapshot("osdu:wks:work-product-component--Thing:1.0.0", JsonNode.Parse($$"""
            {
              "type": "object",
              "properties": {
                "id": { "type": "string" },
                "data": {
                  "type": "object",
                  "properties": {
                    "ColourID": { "type": "string", "x-osdu-relationship": {{Names("reference-data", "Colour")}} },
                    "OwnerID": { "type": "string", "x-osdu-relationship": {{Names("master-data", "Organisation")}} },
                    "ReportID": { "type": "string", "x-osdu-relationship": {{Names("work-product-component", "Report")}} },
                    "WellboreID": { "type": "string", "x-osdu-relationship": {{Names("master-data", "Wellbore")}} },
                    "Plain": { "type": "string" },
                    "Place": { "type": "object", "properties": { "CountryID": { "type": "string", "x-osdu-relationship": {{Names("master-data", "Country")}} } } },
                    "WellboreIDs": { "type": "array", "items": { "type": "string", "x-osdu-relationship": {{Names("master-data", "Wellbore")}} } },
                    "Items": {
                      "type": "array",
                      "items": { "type": "object", "properties": { "ColourID": { "type": "string", "x-osdu-relationship": {{Names("reference-data", "Colour")}} } } }
                    },
                    "Datasets": { "type": "array", "items": { "type": "string", "x-osdu-relationship": [{ "GroupType": "dataset" }] } }
                  }
                }
              }
            }
            """)!.AsObject(), DateTimeOffset.UnixEpoch);

        var suggested = DimensionKeyCandidates.Of(OsduTemplate.From(schema));

        Assert.Equal(
            [
                ("data.WellboreID", false), ("data.OwnerID", false), ("data.ReportID", false), ("data.ColourID", false), ("data.Place.CountryID", false),
                ("data.WellboreIDs", true), ("data.Datasets", true), ("data.Items.ColourID", true),
            ],
            suggested.Select(k => (k.Path, k.Repeated)));
        Assert.Equal(["dataset"], suggested.Single(k => k.Path == "data.Datasets").Names);
    }

    private static (int, int, int, int) At(DimensionYamlSpan span) => (span.Line, span.Column, span.EndLine, span.EndColumn);

    private static DimensionPath Path(string text) => DimensionPath.Parse(text).Path!;

    private static SchemaSnapshot Schema(string kind)
    {
        // The sample estate saves each template as authority_source_entityType_version.json.
        var file = System.IO.Path.Combine(Samples.TemplateFiles, kind.Replace(':', '_') + ".json");
        return SchemaSnapshot.Parse(kind, File.ReadAllText(file), DateTimeOffset.UnixEpoch);
    }

    /// <summary>A country's template, as small as the reads need: its name, and its type, which names a reference.</summary>
    private static SchemaSnapshot Country() => new(CountryKind, JsonNode.Parse("""
        {
          "title": "GeoPoliticalEntity",
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "data": {
              "allOf": [
                {
                  "type": "object",
                  "properties": {
                    "GeoPoliticalEntityName": { "type": "string", "description": "The name of the entity." },
                    "GeoPoliticalEntityTypeID": {
                      "type": "string",
                      "description": "The type of the entity.",
                      "x-osdu-relationship": [{ "GroupType": "reference-data", "EntityType": "GeoPoliticalEntityType" }]
                    }
                  }
                }
              ]
            }
          }
        }
        """)!.AsObject(), DateTimeOffset.UnixEpoch);
}
