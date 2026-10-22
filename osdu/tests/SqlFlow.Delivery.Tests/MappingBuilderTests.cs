using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>The mapping builder: drafts from a template and the cache, what a draft still lacks, and the YAML it is written as.</summary>
[Trait("Category", "Integration")]
public class MappingBuilderTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static MappingDefinition Sample(string reference) => new MappingCatalog(Samples.FixtureMappings, new DeliveryDocumentLoader()).Load(reference);

    private static async Task<ReferenceSnapshot> SampleCacheAsync()
        => (await Samples.SampleCache.LoadAsync(Samples.SampleCacheScope, (await Samples.SampleCache.CurrentVersionAsync(Samples.SampleCacheScope))!))!;

    [Theory]
    [InlineData("WellLog@1.4.0")]
    [InlineData("Wellbore@1.0.0")]
    public async Task A_sample_mapping_opened_in_the_builder_is_written_back_as_the_same_mapping(string reference)
    {
        var original = Sample(reference);
        var draft = MappingBuilder.FromDefinition(original);
        Assert.Empty(MappingBuilder.Incomplete(draft));

        var yaml = MappingBuilder.ToYaml(draft);
        var reread = new DeliveryDocumentLoader().ParseMapping(yaml, reference + ".yaml");
        var again = MappingBuilder.FromDefinition(reread);

        Assert.Equal(JsonSerializer.Serialize(draft, Json), JsonSerializer.Serialize(again, Json));
        Assert.Equal(yaml, MappingBuilder.ToYaml(again));

        // What the builder wrote passes the preflight against the sample template and cache.
        var schema = await Samples.SampleTemplates.LoadAsync(reread.Template);
        Assert.NotNull(schema);
        var references = await SampleCacheAsync();
        var context = new RenderContext
        {
            MappingReference = reread.Reference,
            CacheScope = Samples.SampleCacheScope,
            CacheVersion = references.Version,
            SchemaSnapshotVersion = schema.Version,
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RenderContext.DataPartitionParameter] = "dev",
                ["aclOwner"] = "data.welldb.owners@dev.dataservices.energy",
                ["aclViewer"] = "data.welldb.viewers@dev.dataservices.energy",
                ["legalTag"] = "dev-osdu-default-legal",
            },
        };
        var searches = await RenderResolver.SearchesAsync(Samples.SampleTemplates, reread, schema);
        var issues = Preflight.Check(reread, schema, references, context, sourceColumns: null, searches);
        Assert.True(issues.All(i => i.Severity != IssueSeverity.Error), string.Join(Environment.NewLine, issues));
    }

    [Fact]
    public void A_new_draft_prefills_the_variables_the_cache_can_answer_outside_a_repeater()
    {
        var template = OsduTemplate.From(Samples.SampleTemplate(Samples.WellLogKind));
        var cache = new List<CachedTypeInfo>
        {
            new("VerticalMeasurementType", "reference-data--VerticalMeasurementType", ["Code", "Name"]),
            new("UnitOfMeasure", "reference-data--UnitOfMeasure", ["Code", "Name", "ID"]),
        };

        var draft = MappingBuilder.Draft(template, cache, "WellLog", "2.0.0", "wells");

        Assert.Equal(new TemplateReference(template.Kind, template.Version), new TemplateReference(draft.TemplateKind, draft.TemplateVersion));
        Assert.Equal(MappingBuilder.EnvelopeTargets, draft.Entries.Take(4).Select(e => e.Target));
        Assert.All(draft.Entries.Take(4), e => Assert.Equal(MappingDraftInput.Static, e.Input));

        var type = Assert.Single(draft.Entries, e => e.Target == "osdu.data.VerticalMeasurement.VerticalMeasurementTypeID");
        Assert.True(type.Prefilled);
        Assert.Equal(MappingDraftInput.Cache, type.Input);
        Assert.Equal("VerticalMeasurementType", type.CacheType);
        Assert.Equal("id", type.CacheField);
        Assert.Equal("Code", Assert.Single(type.FindBy).Field);

        Assert.Equal("UnitOfMeasure", Assert.Single(draft.Entries, e => e.Target == "osdu.data.VerticalMeasurement.VerticalMeasurementUnitOfMeasureID").CacheType);
        Assert.DoesNotContain(draft.Entries, e => e.Target.Contains("[]", StringComparison.Ordinal));

        // Wellbores are searched for rather than cached, so nothing in the cache answers the wellbore reference, and the
        // draft leaves it to be written as a search.
        Assert.DoesNotContain(draft.Entries, e => e.Target == "osdu.data.WellboreID");

        // A draft says what is still missing: the envelope values, the key, and the dataset side of every cache entry.
        var missing = MappingBuilder.Incomplete(draft);
        Assert.Contains(missing, i => i.Target == "osdu.acl.owners" && i.Message.Contains("at least one value", StringComparison.Ordinal));
        Assert.Contains(missing, i => i.Target is null && i.Message.Contains("identify a record", StringComparison.Ordinal));
        Assert.Contains(missing, i => i.Target == "osdu.data.VerticalMeasurement.VerticalMeasurementTypeID" && i.Message.Contains("findBy Code needs the dataset column", StringComparison.Ordinal));
    }

    [Fact]
    public void A_number_modifier_is_written_back_with_its_separators()
    {
        var loader = new DeliveryDocumentLoader();
        var original = loader.ParseMapping(TestSchema.MappingDocument("""
            Weight: { $from: a, $modifiers: [number] }
            Symbol: { $from: b, $modifiers: [{ number: { decimal: ",", group: " " } }] }
            Count: { $from: c, $modifiers: [{ number: { decimal: ",", group: "." } }] }
            Big: { $from: d, $modifiers: [{ number: { group: "'" } }] }
            """), "m.yaml");
        var draft = MappingBuilder.FromDefinition(original);

        var yaml = MappingBuilder.ToYaml(draft);
        var again = MappingBuilder.FromDefinition(loader.ParseMapping(yaml, "m.yaml"));

        Assert.Equal(JsonSerializer.Serialize(draft, Json), JsonSerializer.Serialize(again, Json));
        Assert.Contains("- number\n", yaml.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("number: { decimal: \",\", group: \" \" }", yaml, StringComparison.Ordinal);
        Assert.Contains("number: { decimal: \".\", group: \"'\" }", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Incomplete_entries_are_named_by_their_target()
    {
        var draft = new MappingDraft
        {
            Name = "Thing",
            Version = "1.0.0",
            TemplateKind = TestSchema.Kind,
            TemplateVersion = TestSchema.Build().Version,
            System = "test",
            Key = ["name"],
            Entries =
            [
                new MappingDraftEntry { Target = "osdu.data.Name", Input = MappingDraftInput.Dataset },
                new MappingDraftEntry { Target = "osdu.data.Curves", Input = MappingDraftInput.Repeat },
                new MappingDraftEntry { Target = "osdu.data.Unit", Input = MappingDraftInput.Cache, CacheType = "UnitOfMeasure", CacheField = "id" },
                new MappingDraftEntry { Target = "osdu.data.Symbol", Input = MappingDraftInput.Static, Static = "{not json" },
                new MappingDraftEntry { Target = "osdu.data.Description", Input = MappingDraftInput.Dataset, Column = "a", Modifiers = [new MappingDraftModifier("split", ",")] },
                new MappingDraftEntry { Target = "osdu.data.Count", Input = MappingDraftInput.Dataset, Column = "a", When = "a =" },
                new MappingDraftEntry { Target = "data.Nope", Input = MappingDraftInput.Dataset, Column = "a" },
                new MappingDraftEntry { Target = "osdu.data.When", Input = MappingDraftInput.Dataset, Column = "a", When = "a is FINAL" },
                new MappingDraftEntry { Target = "osdu.data.Symbol", Input = MappingDraftInput.Expression, Expression = "coalesce(a)", Where = "not empty(a)" },
                new MappingDraftEntry { Target = "osdu.data.Aliases", Input = MappingDraftInput.Expression, Expression = "upper(\"x\")" },
                new MappingDraftEntry { Target = "osdu.data.Weight2", Input = MappingDraftInput.Dataset, Column = "a", When = "a" },
                new MappingDraftEntry { Target = "osdu.data.Name", Input = MappingDraftInput.Dataset, Column = "name" },
                new MappingDraftEntry { Target = "osdu.data.Day", Input = MappingDraftInput.Dataset, Column = "a", Modifiers = [new MappingDraftModifier("date", Text: "dd.MM.yy")] },
                new MappingDraftEntry { Target = "osdu.data.Weight", Input = MappingDraftInput.Dataset, Column = "a", Modifiers = [new MappingDraftModifier("number", DecimalSeparator: ",", GroupSeparator: ",")] },
            ],
        };

        var issues = MappingBuilder.Incomplete(draft);
        Assert.Contains(issues, i => i.Target == "osdu.data.Weight" && i.Message.Contains("number cannot use ',' both between digit groups and before the decimals", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Day" && i.Message.Contains("the date format 'dd.MM.yy' reads a two-digit year", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Name" && i.Message.Contains("choose the dataset column", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Name" && i.Message.Contains("more than one entry", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Curves" && i.Message.Contains("child dataset", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Unit" && i.Message.Contains("at least one findBy line", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Symbol" && i.Message.Contains("not valid JSON", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Description" && i.Message.Contains("split needs", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Count" && i.Message.Contains("$when 'a =': the expression ends", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Symbol" && i.Message.Contains("gives coalesce 1 value, and it takes at least 2 values", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Symbol" && i.Message.Contains("only a repeat input takes it", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Aliases" && i.Message.Contains("reads no column, so every record gets the same value", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Weight2" && i.Message.Contains("gives a value, and $when is a condition", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "data.Nope" && i.Message.Contains("must start with 'osdu.'", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.When" && i.Message.Contains("a condition is an expression now: $when: a = \"FINAL\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1.0")]
    [InlineData("")]
    [InlineData("a: b")]
    [InlineData("{dataset.x} run")]
    [InlineData("V/V")]
    [InlineData("say \"hi\" #1")]
    [InlineData(" padded ")]
    [InlineData("osdu.data.Curves[].X")]
    [InlineData("line one\nline two")]
    public void Awkward_text_survives_being_written_and_read_back(string text)
    {
        var draft = BaseDraft() with
        {
            Description = text.Length == 0 ? null : text,
            Entries =
            [
                .. BaseDraft().Entries,
                new MappingDraftEntry { Target = "osdu.data.Symbol", Input = MappingDraftInput.Static, Static = JsonSerializer.Serialize(text) },
                new MappingDraftEntry { Target = "osdu.data.Aliases", Input = MappingDraftInput.Static, Static = JsonSerializer.Serialize(new[] { text, "x" }) },
                new MappingDraftEntry
                {
                    Target = "osdu.data.Description",
                    Input = MappingDraftInput.Dataset,
                    Column = "name",
                    Modifiers = [new MappingDraftModifier("replace", Replacements: [new MappingDraftReplacement(text.Length == 0 ? "empty" : text, text)])],
                    When = ConditionText(text),
                },
                new MappingDraftEntry { Target = "osdu.data.Count", Input = MappingDraftInput.Expression, Expression = $"length({ExpressionText(text)} & name)" },
            ],
        };

        var mapping = new DeliveryDocumentLoader().ParseMapping(MappingBuilder.ToYaml(draft), "awkward.yaml");
        Assert.Equal(text, mapping.Entries.Single(e => e.Target.Text == "osdu.data.Symbol").Static!.GetValue<string>());
        Assert.Equal([text, "x"], mapping.Entries.Single(e => e.Target.Text == "osdu.data.Aliases").Static!.AsArray().Select(v => v!.GetValue<string>()));
        var description = mapping.Entries.Single(e => e.Target.Text == "osdu.data.Description");
        Assert.Equal(text, description.Modifiers.Single().Replacements.Values.Single());
        Assert.Equal(ConditionText(text), description.AppliesWhen!.Text);
        Assert.Equal($"length({ExpressionText(text)} & name)", mapping.Entries.Single(e => e.Target.Text == "osdu.data.Count").Source!.Expression!.Text);
        if (text.Length > 0)
        {
            Assert.Equal(text.Trim(), mapping.Description?.Trim());
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("keep", null)]
    [InlineData("empty", null)]
    [InlineData("text", "Unevaluated")]
    [InlineData("text", "~")]
    [InlineData("text", "a: b")]
    public void A_replace_writes_no_value_and_otherwise_and_reads_them_back(string? otherwiseKind, string? otherwiseText)
    {
        var draft = BaseDraft() with
        {
            Entries =
            [
                .. BaseDraft().Entries,
                new MappingDraftEntry
                {
                    Target = "osdu.data.Symbol",
                    Input = MappingDraftInput.Dataset,
                    Column = "name",
                    Required = false,
                    Modifiers =
                    [
                        new MappingDraftModifier(
                            "replace",
                            Replacements: [new MappingDraftReplacement("M", "m"), new MappingDraftReplacement("NONE", null), new MappingDraftReplacement("~", "tilde")],
                            OtherwiseKind: otherwiseKind,
                            OtherwiseText: otherwiseText),
                        new MappingDraftModifier("upper"),
                    ],
                },
            ],
        };

        var yaml = MappingBuilder.ToYaml(draft);
        var mapping = new DeliveryDocumentLoader().ParseMapping(yaml, "replace.yaml");
        var modifiers = mapping.Entries.Single(e => e.Target.Text == "osdu.data.Symbol").Modifiers;
        Assert.Equal([ModifierKind.Replace, ModifierKind.Upper], modifiers.Select(m => m.Kind));
        var replace = modifiers[0];
        Assert.Equal("m", replace.Replacements["M"]);
        Assert.Null(replace.Replacements["NONE"]);
        Assert.Equal("tilde", replace.Replacements["~"]);
        var expected = otherwiseKind switch
        {
            "empty" => ReplaceFallback.Empty,
            "text" => ReplaceFallback.Of(otherwiseText),
            _ => ReplaceFallback.Keep,
        };
        Assert.Equal(expected, replace.Otherwise);

        // The draft read back from the document says the same, so the builder reopens what it wrote.
        var reopened = MappingBuilder.FromDefinition(mapping).Entries.Single(e => e.Target == "osdu.data.Symbol").Modifiers[0];
        Assert.Equal(otherwiseKind is null or "keep" ? "keep" : otherwiseKind, reopened.OtherwiseKind);
        Assert.Equal(otherwiseKind == "text" ? otherwiseText : null, reopened.OtherwiseText);
        Assert.Null(reopened.Replacements!.Single(r => r.From == "NONE").To);
    }

    [Fact]
    public void A_replace_that_lists_a_value_twice_or_an_unknown_otherwise_is_an_issue()
    {
        string Issue(MappingDraftModifier modifier)
        {
            var draft = BaseDraft() with
            {
                Entries = [.. BaseDraft().Entries, new MappingDraftEntry { Target = "osdu.data.Symbol", Input = MappingDraftInput.Dataset, Column = "name", Modifiers = [modifier] }],
            };
            return Assert.Single(MappingBuilder.Incomplete(draft), issue => issue.Target == "osdu.data.Symbol").Message;
        }

        Assert.Contains("lists 'M' more than once", Issue(new MappingDraftModifier("replace", Replacements: [new("M", "m"), new(" M ", "metre")])), StringComparison.Ordinal);
        Assert.Contains("not 'sometimes'", Issue(new MappingDraftModifier("replace", Replacements: [new("M", "m")], OtherwiseKind: "sometimes")), StringComparison.Ordinal);
        Assert.Contains("needs the text an unlisted value becomes", Issue(new MappingDraftModifier("replace", Replacements: [new("M", "m")], OtherwiseKind: "text", OtherwiseText: " ")), StringComparison.Ordinal);

        // A table read from the cache is named and not listed, and its fields go with it.
        Assert.Contains("reads its table from the cache or lists its values, not both", Issue(new MappingDraftModifier("replace", Replacements: [new("M", "m")], Table: "UnitAlias")), StringComparison.Ordinal);
        Assert.Contains("replace reads cache type 'Unit Alias'", Issue(new MappingDraftModifier("replace", Table: "Unit Alias")), StringComparison.Ordinal);
        Assert.Contains("replace's match names the cached field", Issue(new MappingDraftModifier("replace", Table: "CurveClasses", Match: "a b")), StringComparison.Ordinal);
        Assert.Contains("replace's field names the cached field", Issue(new MappingDraftModifier("replace", Table: "CurveClasses", Field: "family?")), StringComparison.Ordinal);
        Assert.Contains("match and field choose the fields of a table read from the cache; choose the cached type too", Issue(new MappingDraftModifier("replace", Replacements: [new("M", "m")], Field: "value")), StringComparison.Ordinal);
        Assert.Contains("or a cached table to read them from", Issue(new MappingDraftModifier("replace")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, "curve_family", "empty")]
    [InlineData("mnemonic", "curve_family", "text")]
    public void A_replace_reading_a_cached_table_writes_its_fields_and_reads_them_back(string? match, string? field, string? otherwiseKind)
    {
        var draft = BaseDraft() with
        {
            Entries =
            [
                .. BaseDraft().Entries,
                new MappingDraftEntry
                {
                    Target = "osdu.data.Symbol",
                    Input = MappingDraftInput.Dataset,
                    Column = "name",
                    Required = false,
                    Modifiers =
                    [
                        new MappingDraftModifier("replace", Table: "CurveClasses", Match: match, Field: field, OtherwiseKind: otherwiseKind, OtherwiseText: otherwiseKind == "text" ? "Unknown" : null),
                    ],
                },
            ],
        };
        Assert.DoesNotContain(MappingBuilder.Incomplete(draft), issue => issue.Target == "osdu.data.Symbol");

        var yaml = MappingBuilder.ToYaml(draft).ReplaceLineEndings("\n");
        Assert.Contains("        - replace: $cache.CurveClasses\n", yaml, StringComparison.Ordinal);
        Assert.Equal(match is not null, yaml.Contains("          match: mnemonic\n", StringComparison.Ordinal));
        Assert.Equal(field is not null, yaml.Contains("          field: curve_family\n", StringComparison.Ordinal));

        var modifier = new DeliveryDocumentLoader().ParseMapping(yaml, "cached-replace.yaml").Entries.Single(e => e.Target.Text == "osdu.data.Symbol").Modifiers.Single();
        Assert.Equal(new CachedReplaceTable("CurveClasses", match, field), modifier.Table);
        Assert.Empty(modifier.Replacements);
        Assert.Equal(otherwiseKind switch { "empty" => ReplaceFallback.Empty, "text" => ReplaceFallback.Of("Unknown"), _ => ReplaceFallback.Keep }, modifier.Otherwise);

        // The builder reopens exactly what it wrote: the table and the fields it names, and no pairs.
        var reopened = Assert.Single(MappingBuilder.FromDefinition(new DeliveryDocumentLoader().ParseMapping(yaml, "cached-replace.yaml")).Entries.Single(e => e.Target == "osdu.data.Symbol").Modifiers);
        Assert.Equal(("CurveClasses", match, field), (reopened.Table, reopened.Match, reopened.Field));
        Assert.Null(reopened.Replacements);
    }

    [Fact]
    public void Static_objects_and_lists_of_objects_are_written_as_yaml_and_read_back_as_the_same_json()
    {
        const string Meta = """[{"kind":"Unit","name":"ft","persistableReference":"{\"abcd\":{\"a\":0.0}}","propertyNames":["SamplingStart","SamplingStop"]}]""";
        var draft = BaseDraft() with
        {
            Entries =
            [
                .. BaseDraft().Entries,
                new MappingDraftEntry { Target = "osdu.data.Nested", Input = MappingDraftInput.Static, Static = """{"Inner":"x: y","Deeper":{"Count":3,"Flag":false}}""" },
                new MappingDraftEntry { Target = "osdu.tags.Meta", Input = MappingDraftInput.Static, Static = Meta },
            ],
        };

        var mapping = new DeliveryDocumentLoader().ParseMapping(MappingBuilder.ToYaml(draft), "statics.yaml");
        Assert.Equal("""{"Inner":"x: y","Deeper":{"Count":3,"Flag":false}}""", mapping.Entries.Single(e => e.Target.Text == "osdu.data.Nested").Static!.ToJsonString());
        // Compared as JSON, not as text: writing a string back out may escape its quotes differently.
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(Meta), mapping.Entries.Single(e => e.Target.Text == "osdu.tags.Meta").Static));
    }

    [Fact]
    public void A_draft_is_written_as_the_record_tree_its_targets_name()
    {
        var draft = BaseDraft() with
        {
            Label = "{name} / {$dataset.depth}",
            Entries =
            [
                .. BaseDraft().Entries,
                new MappingDraftEntry { Target = "osdu.tags.Source", Input = MappingDraftInput.Static, Static = "\"{$param.dataPartition}-test\"" },
                new MappingDraftEntry { Target = "osdu.data.Depth", Input = MappingDraftInput.Dataset, Column = "depth" },
                new MappingDraftEntry { Target = "osdu.data.Curves[].TopDepth", Input = MappingDraftInput.Dataset, Column = "curves.top", Required = false },
                new MappingDraftEntry
                {
                    Target = "osdu.data.Curves",
                    Input = MappingDraftInput.Repeat,
                    Child = "curves",
                    Where = "curve_id != \"DEPT\"",
                    When = "not empty(flag)",
                },
                new MappingDraftEntry
                {
                    Target = "osdu.data.Curves[].CurveID",
                    Input = MappingDraftInput.Dataset,
                    Column = "name",
                    Modifiers = [new MappingDraftModifier("id", Text: "{$param.dataPartition}:reference-data--LogCurveType:{curve_id}-{$value}:")],
                    When = "curve_id != \"MD\"",
                },
                new MappingDraftEntry { Target = "osdu.data.Curves[].Mnemonic", Input = MappingDraftInput.Expression, Expression = "coalesce(mnemonic, curve_id)", Modifiers = [new MappingDraftModifier("upper")] },
                new MappingDraftEntry { Target = "osdu.data.Symbol", Input = MappingDraftInput.Expression, Expression = "iif(depth > 1000, 'deep', 'shallow')" },
                new MappingDraftEntry { Target = "osdu.data.Nested.Inner", Input = MappingDraftInput.Static, Static = "\"fixed\"", Description = "Always the same." },
                new MappingDraftEntry
                {
                    Target = "osdu.data.Unit",
                    Input = MappingDraftInput.Cache,
                    CacheType = "UnitOfMeasure",
                    CacheField = "id",
                    FindBy = [new MappingDraftFind("Code", "unit", null), new MappingDraftFind("Name", null, "metre")],
                    IgnoreSeparators = true,
                },
                // A word YAML would read as a boolean is quoted inside a flow list, so it reads back as the same text.
                new MappingDraftEntry { Target = "osdu.data.Aliases", Input = MappingDraftInput.Static, Static = """["no", "A-1"]""" },
            ],
        };
        Assert.Empty(MappingBuilder.Incomplete(draft));

        var yaml = MappingBuilder.ToYaml(draft).ReplaceLineEndings("\n");
        Assert.Contains(
            """
            record:
              acl:
                owners: [owners@x]
                viewers: [viewers@x]
              legal:
                legaltags: [tag]
                otherRelevantDataCountries: [US]
              data:
                Name: { $from: name }
                Depth: { $from: depth }
                Curves:
                  $forEach: curves
                  $where: curve_id != "DEPT"
                  $when: not empty(flag)
                  $item:
                    TopDepth:
                      $from: top
                      $required: false
                    CurveID:
                      $from: $dataset.name
                      $modifiers:
                        - id: "{$param.dataPartition}:reference-data--LogCurveType:{curve_id}-{$value}:"
                      $when: curve_id != "MD"
                    Mnemonic:
                      $expr: coalesce(mnemonic, curve_id)
                      $modifiers:
                        - upper
                Symbol:
                  $expr: iif(depth > 1000, 'deep', 'shallow')
                Nested:
                  Inner:
                    $value: fixed
                    $description: Always the same.
                Unit:
                  $cache: UnitOfMeasure.id
                  $findBy:
                    - Code = unit
                    - Name = 'metre'
                  $ignoreSeparators: true
                Aliases: ["no", A-1]
              tags:
                Source: "{$param.dataPartition}-test"
            """,
            yaml,
            StringComparison.Ordinal);
        Assert.Contains("  label: \"{name} / {$dataset.depth}\"\n", yaml, StringComparison.Ordinal);

        // What the builder wrote loads, reads the item's row and the dataset's row where the draft said, and opens again as
        // the same entries in the tree's order.
        var mapping = new DeliveryDocumentLoader().ParseMapping(yaml, "tree.yaml");
        var curveId = mapping.Entries.Single(e => e.Target.Text == "osdu.data.Curves[].CurveID");
        Assert.Equal(new DatasetColumn(null, "name"), curveId.Source!.Column);
        Assert.Equal([new DatasetColumn("curves", "curve_id")], curveId.AppliesWhen!.Columns);
        var curves = mapping.Entries.Single(e => e.Target.Text == "osdu.data.Curves");
        Assert.Equal([new DatasetColumn("curves", "curve_id")], curves.RowFilter!.Columns);
        Assert.Equal([new DatasetColumn(null, "flag")], curves.AppliesWhen!.Columns);
        Assert.Equal(
            [new DatasetColumn("curves", "mnemonic"), new DatasetColumn("curves", "curve_id")],
            mapping.Entries.Single(e => e.Target.Text == "osdu.data.Curves[].Mnemonic").Source!.Expression!.Columns);
        Assert.Equal([new DatasetColumn("curves", "curve_id")], curveId.Modifiers.Single().Id!.Columns);
        Assert.Equal("record.data.Curves.$item.CurveID", curveId.Location);
        Assert.Equal("{name} / {depth}", mapping.Dataset.Label);

        var reopened = MappingBuilder.FromDefinition(mapping);
        Assert.Equal(yaml.Replace("{$dataset.depth}", "{depth}", StringComparison.Ordinal), MappingBuilder.ToYaml(reopened).ReplaceLineEndings("\n"));
        Assert.Equal(MappingBuilder.ToYaml(reopened), MappingBuilder.ToYaml(MappingBuilder.FromDefinition(new DeliveryDocumentLoader().ParseMapping(MappingBuilder.ToYaml(reopened), "again.yaml"))));
    }

    [Fact]
    public void An_entry_the_tree_has_no_place_for_is_left_out_with_why_and_reported()
    {
        var draft = BaseDraft() with
        {
            Entries =
            [
                .. BaseDraft().Entries,
                new MappingDraftEntry { Target = "osdu.data.Curves[].CurveID", Input = MappingDraftInput.Dataset, Column = "curves.curve_id" },
                new MappingDraftEntry { Target = "osdu.data.Nested", Input = MappingDraftInput.Static, Static = """{"Inner":"x"}""" },
                new MappingDraftEntry { Target = "osdu.data.Nested.Inner", Input = MappingDraftInput.Dataset, Column = "name" },
                new MappingDraftEntry { Target = "osdu.data.Depth", Input = MappingDraftInput.Dataset, Column = "depth" },
            ],
        };

        var issues = MappingBuilder.Incomplete(draft);
        Assert.Contains(issues, i => i.Target == "osdu.data.Curves[].CurveID" && i.Message.Contains("no entry repeats a child dataset's rows at osdu.data.Curves", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Curves[].CurveID" && i.Message.Contains("reads a column of child dataset 'curves'", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Nested.Inner" && i.Message.Contains("lies inside osdu.data.Nested, which an entry fills whole", StringComparison.Ordinal));

        // What has a place is written, and loads; the rest is named in a comment where the record starts.
        var yaml = MappingBuilder.ToYaml(draft).ReplaceLineEndings("\n");
        Assert.Contains("record:\n  # Left out: osdu.data.Nested.Inner lies inside osdu.data.Nested, which an entry fills whole.\n", yaml, StringComparison.Ordinal);
        Assert.Contains("  # Left out: osdu.data.Curves[].CurveID fills a property of the items of osdu.data.Curves", yaml, StringComparison.Ordinal);
        var mapping = new DeliveryDocumentLoader().ParseMapping(yaml, "partial.yaml");
        Assert.Equal("""{"Inner":"x"}""", mapping.Entries.Single(e => e.Target.Text == "osdu.data.Nested").Static!.ToJsonString());
        Assert.DoesNotContain(mapping.Entries, e => e.Target.Text is "osdu.data.Curves[].CurveID" or "osdu.data.Nested.Inner");

        // A repeat whose items nothing fills yet is written with an empty item for the author to fill, and reported.
        var empty = BaseDraft() with { Entries = [.. BaseDraft().Entries, new MappingDraftEntry { Target = "osdu.data.Curves", Input = MappingDraftInput.Repeat, Child = "curves" }] };
        Assert.Contains(MappingBuilder.Incomplete(empty), i => i.Target == "osdu.data.Curves" && i.Message.Contains("add an entry for each property an item takes", StringComparison.Ordinal));
        Assert.Contains("    Curves:\n      $forEach: curves\n      $item: {}\n", MappingBuilder.ToYaml(empty).ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_literal_keeps_properties_whose_names_start_with_a_dollar()
    {
        const string Listed = """[{"$ref":"a","Plain":{"$deep":1}}]""";
        const string Whole = """{"$ref":"b","List":[{"$id":2}]}""";
        var draft = BaseDraft() with
        {
            Entries =
            [
                .. BaseDraft().Entries,
                new MappingDraftEntry { Target = "osdu.data.Curves", Input = MappingDraftInput.Static, Static = Listed },
                new MappingDraftEntry { Target = "osdu.data.Nested", Input = MappingDraftInput.Static, Static = Whole },
            ],
        };

        // In the tree a list's objects write such a property with one more '$'; what $value holds is written as it is.
        var yaml = MappingBuilder.ToYaml(draft).ReplaceLineEndings("\n");
        Assert.Contains("      - $$ref: a\n        Plain:\n          $$deep: 1\n", yaml, StringComparison.Ordinal);
        Assert.Contains("      $value:\n        $ref: b\n", yaml, StringComparison.Ordinal);

        var mapping = new DeliveryDocumentLoader().ParseMapping(yaml, "dollars.yaml");
        Assert.Equal(Listed, mapping.Entries.Single(e => e.Target.Text == "osdu.data.Curves").Static!.ToJsonString());
        Assert.Equal(Whole, mapping.Entries.Single(e => e.Target.Text == "osdu.data.Nested").Static!.ToJsonString());
    }

    [Theory]
    [InlineData("\"{$dataset.name}\"", "a literal reads only a parameter")]
    [InlineData("\"{param.dataPartition}\"", "a parameter is read as {$param.dataPartition}")]
    [InlineData("[\"{$value}\"]", "a literal reads only a parameter")]
    public void A_fixed_value_holding_a_token_it_cannot_read_is_an_issue_and_the_loader_refuses_it(string value, string problem)
    {
        var draft = BaseDraft() with { Entries = [.. BaseDraft().Entries, new MappingDraftEntry { Target = "osdu.data.Symbol", Input = MappingDraftInput.Static, Static = value }] };
        Assert.Contains(MappingBuilder.Incomplete(draft), i => i.Target == "osdu.data.Symbol" && i.Message.Contains(problem, StringComparison.Ordinal));
        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(MappingBuilder.ToYaml(draft), "tokens.yaml"));
        Assert.Contains(problem, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Lookups_find_alls_and_lists_that_would_not_load_are_named_where_they_are_written()
    {
        static MappingDraftEntry Fixed(string target, string json) => new() { Target = target, Input = MappingDraftInput.Static, Static = json };
        var draft = BaseDraft() with
        {
            Lookups =
            [
                new MappingDraftLookup { Name = "wellbore", CacheType = "Wellbore", FindBy = [new MappingDraftFind("FacilityName", "curves.uwi", null)] },
                new MappingDraftLookup { Name = "field", CacheType = "Field", FindBy = [new MappingDraftFind("Name", "field_name", null)], Modifiers = [new MappingDraftModifier("ref")] },
            ],
            Entries =
            [
                Fixed("osdu.acl.owners", """["owners@x"]"""),
                new MappingDraftEntry
                {
                    Target = "osdu.acl.viewers",
                    Input = MappingDraftInput.List,
                    Items =
                    [
                        new MappingDraftEntry
                        {
                            Target = "osdu.acl.viewers", Input = MappingDraftInput.Cache, CacheType = "Wellbore", CacheField = "id",
                            FindAll = new MappingDraftFindAll { Field = "FacilityName", Lookup = "wellbore.FacilityName" },
                        },
                        Fixed("osdu.acl.viewers", """{"a":1}""") with { When = "not empty(name)" },
                    ],
                },
                new MappingDraftEntry { Target = "osdu.legal.legaltags", Input = MappingDraftInput.List, Items = [Fixed("osdu.legal.legaltags", "\"tag\"")] },
                Fixed("osdu.legal.otherRelevantDataCountries", """["US"]"""),
                new MappingDraftEntry { Target = "osdu.data.Name", Input = MappingDraftInput.Dataset, Column = "name" },
                new MappingDraftEntry { Target = "osdu.data.WellboreID", Input = MappingDraftInput.Lookup, Lookup = "well", CacheField = "id" },
                new MappingDraftEntry
                {
                    Target = "osdu.data.Aliases", Input = MappingDraftInput.Cache, CacheType = "AccessGroup", CacheField = "EntitlementGroupEmail",
                    FindAll = new MappingDraftFindAll { Field = "FieldList", Column = "uwi", Literal = "x", Empty = ["FieldList"] },
                },
            ],
        };

        var issues = MappingBuilder.Incomplete(draft);
        void Named(string target, string text)
            => Assert.Contains(issues, i => i.Target == target && i.Message.Contains(text, StringComparison.Ordinal));

        Named("lookups.wellbore", "compares curves.uwi, a column of a child dataset; a lookup reads the dataset's own row");
        Named("lookups.field", "the ref modifier builds an id to write");
        Named("lookups.field", "Lookup 'field' is read by no entry");
        Assert.DoesNotContain(issues, i => i.Message.Contains("Lookup 'wellbore' is read by no entry", StringComparison.Ordinal));
        Named("osdu.acl.viewers", "list at least one fixed value without a condition");
        Named("osdu.acl.viewers", "item 1: find all finds Wellbore rows by a field of the Wellbore record lookup 'wellbore' finds");
        Named("osdu.acl.viewers", "item 2: an item of a list of values is one value");
        Named("osdu.legal.legaltags", "the legal service checks a record's tags and countries before a run");
        Named("osdu.data.WellboreID", "choose one of the mapping's lookups to read");
        Named("osdu.data.Aliases", "find all compares FieldList with one value");
        Named("osdu.data.Aliases", "find all finds rows by FieldList and asks for FieldList to be empty");
    }

    /// <summary>A condition comparing the name with the awkward text, written as an expression text literal.</summary>
    private static string ConditionText(string text) => $"name != {ExpressionText(text)}";

    /// <summary>A text as an expression writes it: in double quotes, with its backslashes, quotes and line breaks escaped.</summary>
    private static string ExpressionText(string text)
        => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    internal static MappingDraft BaseDraft() => new()
    {
        Name = "Thing",
        Version = "1.0.0",
        TemplateKind = TestSchema.Kind,
        TemplateVersion = TestSchema.Build().Version,
        System = "test",
        Key = ["name"],
        Parameters = [new MappingDraftParameter("dataPartition", true, null, null)],
        Entries =
        [
            new MappingDraftEntry { Target = "osdu.acl.owners", Input = MappingDraftInput.Static, Static = """["owners@x"]""" },
            new MappingDraftEntry { Target = "osdu.acl.viewers", Input = MappingDraftInput.Static, Static = """["viewers@x"]""" },
            new MappingDraftEntry { Target = "osdu.legal.legaltags", Input = MappingDraftInput.Static, Static = """["tag"]""" },
            new MappingDraftEntry { Target = "osdu.legal.otherRelevantDataCountries", Input = MappingDraftInput.Static, Static = """["US"]""" },
            new MappingDraftEntry { Target = "osdu.data.Name", Input = MappingDraftInput.Dataset, Column = "name" },
        ],
    };
}
