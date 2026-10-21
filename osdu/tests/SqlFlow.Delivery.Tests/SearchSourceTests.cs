using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A mapping that resolves a reference by searching the platform rather than out of the partition's cache, for the
/// records a delivery refers to: business data that grows without bound, where capturing the set to answer one lookup
/// costs more every day.
/// </summary>
/// <remarks>
/// The render stays synchronous and does no I/O. It asks what the run already knows; a question nobody has asked leaves
/// the render unfinished, naming what it needs, and the caller asks the platform once and renders again. These tests
/// are that protocol, and what each answer the platform can give does to the record.
/// </remarks>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public class SearchSourceTests
{
    /// <summary>A wellbore schema with one property of every shape the indexer gives a property.</summary>
    internal static SchemaSnapshot WellboreSchema() => SchemaSnapshot.Parse("osdu:wks:master-data--Wellbore:1.3.0", """
        {
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "kind": { "type": "string" },
            "data": {
              "allOf": [
                { "$ref": "#/definitions/AbstractMaster.1.0.0" },
                {
                  "type": "object",
                  "properties": {
                    "FacilityName": { "type": "string" },
                    "SpudDate": { "type": "string", "format": "date" },
                    "Uri": { "type": "string", "format": "uri" },
                    "TotalDepth": { "type": "number" },
                    "Count": { "type": "string", "format": "int64" },
                    "LegacyRef": { "type": "string", "pattern": "^srn:master-data/Well:[^:]+:[0-9]*$" },
                    "WellID": { "type": "string", "pattern": "^[a-z]+:master-data--Well:.+$" },
                    "Codes": { "type": "array", "items": { "type": "string" } },
                    "LegacyRefs": { "type": "array", "items": { "type": "string", "pattern": "^srn:.+$" } },
                    "Depths": { "type": "array", "items": { "type": "number" } },
                    "Untyped": { },
                    "Location": { "type": "object", "properties": { "Label": { "type": "string" } } },
                    "Plain": { "type": "array", "items": { "type": "object", "properties": { "Code": { "type": "string" } } } },
                    "FacilitySpecifications": {
                      "type": "array",
                      "x-osdu-indexing": { "type": "flattened" },
                      "items": { "type": "object", "properties": { "FacilitySpecificationText": { "type": "string" }, "Size": { "type": "number" }, "Inner": { "type": "object", "properties": { "Deep": { "type": "string" } } } } }
                    },
                    "VerticalMeasurements": {
                      "type": "array",
                      "x-osdu-indexing": { "type": "nested" },
                      "items": {
                        "type": "object",
                        "properties": {
                          "VerticalMeasurementID": { "type": "string" },
                          "Reference": { "type": "object", "properties": { "Label": { "type": "string" } } },
                          "Readings": { "type": "array", "x-osdu-indexing": { "type": "nested" }, "items": { "type": "object", "properties": { "Value": { "type": "string" } } } }
                        }
                      }
                    }
                  }
                }
              ]
            }
          },
          "definitions": {
            "AbstractMaster.1.0.0": {
              "type": "object",
              "properties": {
                "NameAliases": { "type": "array", "x-osdu-indexing": { "type": "nested" }, "items": { "$ref": "#/definitions/AbstractAliasNames.1.0.0" } }
              }
            },
            "AbstractAliasNames.1.0.0": { "type": "object", "properties": { "AliasName": { "type": "string" }, "AliasNameTypeID": { "type": "string" } } }
          }
        }
        """, DateTimeOffset.UnixEpoch);

    internal const string SearchedKind = "osdu:wks:master-data--Wellbore:*";

    internal static string SearchesBlock(SchemaSnapshot schema) => $"""
        searches:
          Wellbore:
            kind: "{SearchedKind}"
            schema:
              kind: {schema.Kind}
              version: {schema.Version}
        """;

    /// <summary>A mapping over the test template whose entries include <paramref name="entries"/>, declaring the Wellbore search.</summary>
    internal static MappingDefinition Mapping(string entries)
        => new DeliveryDocumentLoader().ParseMapping(Document(entries), "thing.yaml");

    internal static string Document(string entries)
        => TestSchema.MappingDocument(entries).Replace("parameters:", SearchesBlock(WellboreSchema()) + "\nparameters:", StringComparison.Ordinal);

    internal static ResolvedSearches Resolve(MappingDefinition mapping)
    {
        var schema = WellboreSchema();
        return ResolvedSearches.Resolve(mapping, new Dictionary<TemplateReference, SchemaSnapshot> { [new TemplateReference(schema.Kind, schema.Version)] = schema });
    }

    private const string ByNameThenAlias = """
          WellboreID:
            $search: Wellbore
            $findBy:
              - data.FacilityName = wb
              - data.NameAliases.AliasName = wb
        """;

    private const string OptionalByNameThenAlias = ByNameThenAlias + """

            $required: false
        """;

    /// <param name="search">Where the render's questions are answered.</param>
    /// <param name="entries">The entries the mapping adds to the base ones.</param>
    /// <param name="keywordLower">The state the render is pinned to of the indexer's keywordLower property; unknown when null.</param>
    private static MappingRenderer Renderer(IRecordSearch search, string entries = ByNameThenAlias, SystemPropertyState? keywordLower = null)
    {
        var mapping = Mapping(entries);
        var context = TestSchema.Context() with
        {
            SystemProperties = keywordLower is { } state
                ? SystemProperties.Pinned([new SystemProperty(SystemProperties.Indexer, SystemProperties.KeywordLower, state, "dataPartition", null)])
                : [],
        };
        return new MappingRenderer(mapping, TestSchema.Build(), ReferenceSnapshot.Empty, context, Resolve(mapping), search);
    }

    private const SystemPropertyState KeywordLowerOn = SystemPropertyState.Enabled;

    /// <summary>A row carrying the wellbore the search looks for and the schema-required property, so a hold names a search.</summary>
    private static SourceRecord Row(string? wellbore)
        => new() { Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["name"] = "x", ["depth"] = "1", ["wb"] = wellbore }) };

    private static FixedRecordSearch Platform(params (string Field, string Value, string Id)[] known) => new(known);

    /// <summary>Renders the row, answering what it asks between renders, until it finishes; returns the result and how many renders it took.</summary>
    private static async Task<(RenderResult Result, int Renders)> Settle(MappingRenderer renderer, IRecordSearch search, SourceRecord row)
    {
        for (var renders = 1; renders <= 10; renders++)
        {
            var result = renderer.Render(row);
            if (!result.IsIncomplete)
            {
                return (result, renders);
            }

            await search.AnswerAsync(result.Unanswered);
        }

        throw new InvalidOperationException("the render never finished");
    }

    private static string? WellboreId(RenderResult result) => result.Document["data"]?["WellboreID"]?.GetValue<string>();

    [Fact]
    public void A_question_the_run_has_not_asked_leaves_the_render_unfinished_rather_than_guessing()
    {
        var search = Platform(("data.FacilityName", "WB-1", "dev:master-data--Wellbore:abc"));
        var result = Renderer(search).Render(Row("WB-1"));

        Assert.True(result.IsIncomplete);
        var question = Assert.Single(result.Unanswered);
        Assert.Equal(new SearchQuestion(SearchedKind, "data.FacilityName", "WB-1", "data.FacilityName.keyword:\"WB-1\""), question);

        // Nothing was asked of the platform by the render itself: that is the caller's to do.
        Assert.Empty(search.Asked);
    }

    [Fact]
    public async Task The_second_render_finishes_once_the_question_has_been_answered()
    {
        var search = Platform(("data.FacilityName", "WB-1", "dev:master-data--Wellbore:abc"));
        var (result, renders) = await Settle(Renderer(search), search, Row("WB-1"));

        Assert.Equal(2, renders);
        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal("dev:master-data--Wellbore:abc:", WellboreId(result));
    }

    [Fact]
    public async Task A_question_is_asked_of_the_platform_once_however_many_rows_refer_to_it()
    {
        var search = Platform(("data.FacilityName", "WB-1", "dev:master-data--Wellbore:abc"));
        var renderer = Renderer(search);

        await search.AnswerAsync(renderer.Render(Row("WB-1")).Unanswered);
        foreach (var _ in Enumerable.Range(0, 50))
        {
            Assert.False(renderer.Render(Row("WB-1")).IsIncomplete);
        }

        // Fifty-one rows, one round trip: this is what makes a search cheaper than capturing the set it searches.
        Assert.Single(search.Asked);
    }

    [Fact]
    public async Task The_lines_are_asked_in_turn_and_the_alias_only_when_the_name_finds_nothing()
    {
        var search = Platform(("data.NameAliases.AliasName", "OLD-NAME", "dev:master-data--Wellbore:abc"));
        var renderer = Renderer(search);

        var first = renderer.Render(Row("OLD-NAME"));
        Assert.Equal("data.FacilityName", Assert.Single(first.Unanswered).Field);
        await search.AnswerAsync(first.Unanswered);

        // The name found nothing, so the alias is asked next, through the nested form the schema says it needs.
        var second = renderer.Render(Row("OLD-NAME"));
        var alias = Assert.Single(second.Unanswered);
        Assert.Equal("nested(data.NameAliases, (AliasName.keyword:\"OLD-NAME\"))", alias.Query);
        await search.AnswerAsync(second.Unanswered);

        var third = renderer.Render(Row("OLD-NAME"));
        Assert.False(third.IsHeld, string.Join("; ", third.Holds));
        Assert.Equal("dev:master-data--Wellbore:abc:", WellboreId(third));
    }

    [Fact]
    public async Task A_name_that_finds_the_record_is_not_followed_by_a_search_of_the_aliases()
    {
        var search = Platform(("data.FacilityName", "WB-1", "dev:master-data--Wellbore:abc"));
        await Settle(Renderer(search), search, Row("WB-1"));

        Assert.Equal("data.FacilityName", Assert.Single(search.Asked).Field);
    }

    [Fact]
    public async Task A_value_every_line_finds_nothing_for_holds_a_required_entry_and_says_what_was_asked()
    {
        var search = Platform();
        var (result, _) = await Settle(Renderer(search), search, Row("MISSING"));

        Assert.True(result.IsHeld);
        var hold = Assert.Single(result.Holds);
        Assert.Contains("no Wellbore on the platform", hold, StringComparison.Ordinal);
        Assert.Contains("data.FacilityName 'MISSING' found no record", hold, StringComparison.Ordinal);
        Assert.Contains("data.NameAliases.AliasName 'MISSING' found no record", hold, StringComparison.Ordinal);

        // Found-nothing is an answer: asking again on every render would turn one missing wellbore into a call per row.
        Assert.Equal(2, search.Asked.Count);
    }

    [Fact]
    public async Task A_value_every_line_finds_nothing_for_leaves_an_optional_entry_out()
    {
        var search = Platform();
        var (result, _) = await Settle(Renderer(search, OptionalByNameThenAlias), search, Row("MISSING"));

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Null(WellboreId(result));
    }

    [Fact]
    public async Task A_value_several_records_answer_to_holds_the_record_even_when_the_entry_is_optional()
    {
        var search = Platform(
            ("data.FacilityName", "TWIN", "dev:master-data--Wellbore:a"),
            ("data.FacilityName", "TWIN", "dev:master-data--Wellbore:b"),
            ("data.NameAliases.AliasName", "TWIN", "dev:master-data--Wellbore:c"));
        var (result, _) = await Settle(Renderer(search, OptionalByNameThenAlias), search, Row("TWIN"));

        var hold = Assert.Single(result.Holds);
        Assert.Contains("found 2 records (dev:master-data--Wellbore:a, dev:master-data--Wellbore:b)", hold, StringComparison.Ordinal);

        // Several answers to the name are doubt, not a miss: the alias is not asked to break the tie.
        Assert.Single(search.Asked);
    }

    [Fact]
    public async Task A_query_the_platform_refused_holds_the_record_with_what_the_platform_said()
    {
        var search = new RefusingSearch("Malformed query");
        var (result, _) = await Settle(Renderer(search, OptionalByNameThenAlias), search, Row("WB-1"));

        var hold = Assert.Single(result.Holds);
        Assert.Contains("was refused by the search service: Malformed query", hold, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_value_no_line_can_ask_for_holds_the_record_whatever_required_says()
    {
        // The keyword sub-field indexes a null property as the text "null", so neither line can look it up.
        var search = Platform();
        var (result, _) = await Settle(Renderer(search, OptionalByNameThenAlias), search, Row("null"));

        var hold = Assert.Single(result.Holds);
        Assert.Contains("cannot be searched for", hold, StringComparison.Ordinal);
        Assert.Contains("proves nothing about whether the record exists", hold, StringComparison.Ordinal);
        Assert.Empty(search.Asked);
    }

    [Fact]
    public async Task A_line_that_cannot_ask_and_a_line_that_finds_nothing_hold_even_an_optional_entry()
    {
        // The name can be asked for; the alias cannot, since the service would rewrite 'AND X:' inside a nested query.
        var search = Platform();
        var (result, _) = await Settle(Renderer(search, OptionalByNameThenAlias), search, Row("BRAND X:1"));

        var hold = Assert.Single(result.Holds);
        Assert.Contains("data.FacilityName 'BRAND X:1' found no record", hold, StringComparison.Ordinal);
        Assert.Contains("data.NameAliases.AliasName 'BRAND X:1' cannot be searched for", hold, StringComparison.Ordinal);
        Assert.Equal("data.FacilityName", Assert.Single(search.Asked).Field);
    }

    [Fact]
    public async Task A_line_that_cannot_ask_does_not_keep_a_later_line_from_finding_the_record()
    {
        const string AliasFirst = """
              WellboreID:
                $search: Wellbore
                $findBy:
                  - data.NameAliases.AliasName = wb
                  - data.FacilityName = wb
            """;
        var search = Platform(("data.FacilityName", "BRAND X:1", "dev:master-data--Wellbore:abc"));
        var (result, _) = await Settle(Renderer(search, AliasFirst), search, Row("BRAND X:1"));

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal("dev:master-data--Wellbore:abc:", WellboreId(result));
    }

    [Fact]
    public void A_value_that_already_is_an_osdu_id_of_the_kind_searched_names_its_record_without_asking()
    {
        var search = Platform();
        var result = Renderer(search).Render(Row("dev:master-data--Wellbore:abc"));

        Assert.False(result.IsIncomplete);
        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal("dev:master-data--Wellbore:abc:", WellboreId(result));
        Assert.Empty(search.Asked);
    }

    [Fact]
    public void An_osdu_id_of_another_kind_holds_the_record_rather_than_refer_to_the_wrong_kind_of_record()
    {
        var result = Renderer(Platform()).Render(Row("dev:master-data--Well:abc"));

        var hold = Assert.Single(result.Holds);
        Assert.Contains("of a master-data--Well record", hold, StringComparison.Ordinal);
        Assert.Contains("looks for master-data--Wellbore records", hold, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_value_asks_nothing_and_holds_a_required_entry()
    {
        var search = Platform();
        var (result, renders) = await Settle(Renderer(search), search, Row("   "));

        Assert.Equal(1, renders);
        Assert.Contains(result.Holds, h => h.Contains("dataset.wb is empty, so there is nothing to search for", StringComparison.Ordinal));
        Assert.Empty(search.Asked);
    }

    [Fact]
    public async Task What_was_consulted_is_kept_so_a_record_says_how_its_reference_resolved()
    {
        var search = Platform(("data.NameAliases.AliasName", "OLD", "dev:master-data--Wellbore:abc"));
        var (result, _) = await Settle(Renderer(search), search, Row("OLD"));

        Assert.Collection(
            result.SearchUsages,
            name => Assert.Equal(new SearchUsage(SearchedKind, "data.FacilityName", "OLD", "data.FacilityName.keyword:\"OLD\"", SearchOutcome.NotFound, null), name),
            alias => Assert.Equal(
                new SearchUsage(SearchedKind, "data.NameAliases.AliasName", "OLD", "nested(data.NameAliases, (AliasName.keyword:\"OLD\"))", SearchOutcome.Found, "dev:master-data--Wellbore:abc"),
                alias));
    }

    [Fact]
    public void A_mapping_that_searches_and_is_given_no_platform_finds_nothing_and_holds()
    {
        var mapping = Mapping(ByNameThenAlias);
        var result = new MappingRenderer(mapping, TestSchema.Build(), ReferenceSnapshot.Empty, TestSchema.Context(), Resolve(mapping)).Render(Row("WB-1"));

        Assert.False(result.IsIncomplete);
        Assert.True(result.IsHeld);
    }

    [Fact]
    public void A_search_the_render_was_not_given_the_schema_of_holds_rather_than_guess_at_a_query()
    {
        var mapping = Mapping(ByNameThenAlias);
        var result = new MappingRenderer(mapping, TestSchema.Build(), ReferenceSnapshot.Empty, TestSchema.Context(), searches: null, Platform()).Render(Row("WB-1"));

        Assert.Contains(result.Holds, h => h.Contains("has not been resolved against the schema search 'Wellbore' pins", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Renders_on_many_threads_over_one_renderer_keep_their_questions_apart()
    {
        // The plan renders on several threads over one renderer, so what one render asks must never reach another's result.
        var search = Platform();
        var renderer = Renderer(search);
        var rows = Enumerable.Range(0, 400).Select(i => $"WB-{i}").ToList();
        var results = new System.Collections.Concurrent.ConcurrentDictionary<string, RenderResult>();

        await Parallel.ForEachAsync(rows, new ParallelOptions { MaxDegreeOfParallelism = 8 }, (wellbore, _) =>
        {
            results[wellbore] = renderer.Render(Row(wellbore));
            return ValueTask.CompletedTask;
        });

        foreach (var wellbore in rows)
        {
            var question = Assert.Single(results[wellbore].Unanswered);
            Assert.Equal(wellbore, question.Value);
        }
    }

    [Fact]
    public void A_search_a_mapping_does_not_declare_is_refused_when_the_document_is_read()
    {
        var undeclared = TestSchema.MappingDocument(ByNameThenAlias);

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(undeclared, "thing.yaml"));

        Assert.Contains("declares no searches", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_declared_search_nothing_reads_is_refused_rather_than_left_to_cost_calls()
    {
        var refused = Assert.Throws<FlowValidationException>(() => Mapping("""
              Unit:
                $from: name
            """));

        Assert.Contains("nothing reads it", refused.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> UnanswerableEntries => new()
    {
        {
            """
              WellboreID:
                $search: Wellbore.data.FacilityName
                $findBy: data.FacilityName = wb
            """,
            "gives the id of the record it finds; 'Wellbore.data.FacilityName' is not a search name"
        },
        {
            """
              WellboreID:
                $search: Wellbore
                $findBy: FacilityName = wb
            """,
            "a search compares a property under data"
        },
        {
            """
              WellboreID:
                $search: Wellbore
                $findBy: data.Facility-Name = wb
            """,
            "a search compares a property under data"
        },
        {
            """
              WellboreID:
                $search: Wellbore
                $findBy: cache.Wellbore.data.FacilityName = wb
            """,
            "a line compares a field of the record the node reads, written as the record names it"
        },
        {
            """
              WellboreID:
                $search: Wellbore
                $findBy: data.FacilityName = wb
                $ignoreSeparators: true
            """,
            "only applies to a $cache node"
        },
        {
            """
              WellboreID:
                $search: Wellbore
                $findBy: data.FacilityName = 'Wellbore A/1-F-1'
                $modifiers: [trim]
            """,
            "what a search finds is never modified"
        },
    };

    [Theory]
    [MemberData(nameof(UnanswerableEntries))]
    public void A_search_entry_the_platform_could_not_answer_is_refused_where_it_is_written(string entry, string expected)
        => Assert.Contains(expected, Assert.Throws<FlowValidationException>(() => Mapping(entry)).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("osdu:wks:*:*", "only the version may be '*'")]
    [InlineData("osdu:wks:master-data--Well:*", "another entity type")]
    [InlineData("osdu:wks:master-data--Wellbore:1.2.0", "pin the schema of the version it searches")]
    [InlineData("not a kind", "is not an OSDU kind")]
    public void A_search_whose_kind_its_schema_cannot_describe_is_refused(string kind, string expected)
    {
        var schema = WellboreSchema();
        var document = TestSchema.MappingDocument(ByNameThenAlias).Replace("parameters:", $"""
            searches:
              Wellbore:
                kind: "{kind}"
                schema:
                  kind: {schema.Kind}
                  version: {schema.Version}

            parameters:
            """, StringComparison.Ordinal);

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(document, "thing.yaml"));

        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_search_that_pins_no_schema_is_refused_and_told_what_to_pin_and_where_it_is_read()
    {
        var document = TestSchema.MappingDocument(ByNameThenAlias).Replace("parameters:", """
            searches:
              Wellbore:
                kind: "osdu:wks:master-data--Wellbore:*"

            parameters:
            """, StringComparison.Ordinal);

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(document, "thing.yaml"));

        Assert.Contains("pins no schema", refused.Message, StringComparison.Ordinal);
        Assert.Contains("schema: { kind: osdu:wks:master-data--Wellbore:<version> }", refused.Message, StringComparison.Ordinal);
        Assert.Contains("read from the template folders on the mapping's path", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_searches_of_one_kind_are_refused_so_a_question_always_belongs_to_one_search()
    {
        var schema = WellboreSchema();
        var document = TestSchema.MappingDocument(ByNameThenAlias + """

              Unit:
                $search: Other
                $findBy: data.FacilityName = wb
            """).Replace("parameters:", SearchesBlock(schema) + $"""

              Other:
                kind: "{SearchedKind}"
                schema:
                  kind: {schema.Kind}
                  version: {schema.Version}

            parameters:
            """, StringComparison.Ordinal);

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(document, "thing.yaml"));

        Assert.Contains("both look in osdu:wks:master-data--Wellbore:*", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_value_found_only_regardless_of_case_is_asked_again_where_the_partition_keeps_a_lowercased_copy()
    {
        var search = new QuerySearch(("data.FacilityName.keywordLower:\"wb-1\"", ["dev:master-data--Wellbore:abc"]));
        var (result, _) = await Settle(Renderer(search, keywordLower: KeywordLowerOn), search, Row("wb-1"));

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal("dev:master-data--Wellbore:abc:", WellboreId(result));
        Assert.Equal(["data.FacilityName.keyword:\"wb-1\"", "data.FacilityName.keywordLower:\"wb-1\""], search.Asked);

        // The record says which question found its reference.
        Assert.Contains(result.SearchUsages, u => u.Query == "data.FacilityName.keywordLower:\"wb-1\"" && u.Outcome == SearchOutcome.Found);
    }

    [Fact]
    public async Task Several_records_that_answer_once_case_is_ignored_hold_the_record_as_a_cache_lookup_would()
    {
        var search = new QuerySearch(("data.FacilityName.keywordLower:\"wb-1\"", ["dev:master-data--Wellbore:a", "dev:master-data--Wellbore:b"]));
        var (result, _) = await Settle(Renderer(search, OptionalByNameThenAlias, KeywordLowerOn), search, Row("wb-1"));

        var hold = Assert.Single(result.Holds);
        Assert.Contains("found no record exactly, and once case is ignored it found 2 records", hold, StringComparison.Ordinal);

        // Doubt stops the lookup: the alias is not asked to break the tie.
        Assert.Equal(2, search.Asked.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(SystemPropertyState.Unknown)]
    [InlineData(SystemPropertyState.Disabled)]
    public async Task A_partition_not_known_to_keep_a_lowercased_copy_is_asked_exact_questions_alone(SystemPropertyState? keywordLower)
    {
        var search = new QuerySearch(("data.FacilityName.keywordLower:\"wb-1\"", ["dev:master-data--Wellbore:abc"]));
        var (result, _) = await Settle(Renderer(search, keywordLower: keywordLower), search, Row("wb-1"));

        Assert.True(result.IsHeld);
        Assert.Equal(["data.FacilityName.keyword:\"wb-1\"", "nested(data.NameAliases, (AliasName.keyword:\"wb-1\"))"], search.Asked);
    }

    [Fact]
    public async Task A_value_found_exactly_is_never_asked_again_regardless_of_case()
    {
        var search = new QuerySearch(
            ("data.FacilityName.keyword:\"WB-1\"", ["dev:master-data--Wellbore:exact"]),
            ("data.FacilityName.keywordLower:\"WB-1\"", ["dev:master-data--Wellbore:exact", "dev:master-data--Wellbore:other"]));
        var (result, _) = await Settle(Renderer(search, keywordLower: KeywordLowerOn), search, Row("WB-1"));

        // An exact answer is the answer: the codes that differ only by case are different records and are not asked about.
        Assert.Equal("dev:master-data--Wellbore:exact:", WellboreId(result));
        Assert.Equal(["data.FacilityName.keyword:\"WB-1\""], search.Asked);
    }

    [Fact]
    public async Task A_keyword_property_has_no_lowercased_copy_so_it_is_asked_exactly_alone()
    {
        const string ByLegacyRef = """
              WellboreID:
                $search: Wellbore
                $findBy: data.LegacyRef = wb
                $required: false
            """;
        var search = new QuerySearch();
        var (result, _) = await Settle(Renderer(search, ByLegacyRef, KeywordLowerOn), search, Row("srn:master-data/Well:a:"));

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal(["data.LegacyRef:\"srn:master-data/Well:a:\""], search.Asked);
    }

    [Fact]
    public async Task A_value_that_spells_null_in_another_case_is_asked_exactly_and_never_regardless_of_case()
    {
        // The lowercased copy holds 'null' for every record without a name, so 'NULL' asked that way would match them all.
        var search = new QuerySearch();
        var (result, _) = await Settle(Renderer(search, OptionalByNameThenAlias, KeywordLowerOn), search, Row("NULL"));

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.DoesNotContain(search.Asked, q => q.Contains("keywordLower", StringComparison.Ordinal));
        Assert.Contains("data.FacilityName.keyword:\"NULL\"", search.Asked);
    }

    [Fact]
    public async Task A_case_insensitive_question_the_platform_refused_holds_the_record_with_what_it_said()
    {
        var search = new QuerySearch(("data.FacilityName.keywordLower:\"wb-1\"", null));
        var (result, _) = await Settle(Renderer(search, OptionalByNameThenAlias, KeywordLowerOn), search, Row("wb-1"));

        var hold = Assert.Single(result.Holds);
        Assert.Contains("once case is ignored it was refused by the search service", hold, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_alias_is_asked_regardless_of_case_inside_its_nested_array_when_the_name_is_not_found_either_way()
    {
        var search = new QuerySearch(("nested(data.NameAliases, (AliasName.keywordLower:\"old-name\"))", ["dev:master-data--Wellbore:abc"]));
        var (result, _) = await Settle(Renderer(search, keywordLower: KeywordLowerOn), search, Row("old-name"));

        Assert.Equal("dev:master-data--Wellbore:abc:", WellboreId(result));
        Assert.Equal(
            [
                "data.FacilityName.keyword:\"old-name\"",
                "data.FacilityName.keywordLower:\"old-name\"",
                "nested(data.NameAliases, (AliasName.keyword:\"old-name\"))",
                "nested(data.NameAliases, (AliasName.keywordLower:\"old-name\"))",
            ],
            search.Asked);
    }

    [Fact]
    public async Task A_mapping_that_only_searches_is_pinned_to_the_partitions_system_properties_and_not_to_a_cache_version()
    {
        using var catalog = new OsduTestDatabase();
        var templates = catalog.Templates();
        await templates.SaveAsync(TestSchema.Build(), "tests", "tests");
        await templates.SaveAsync(WellboreSchema(), "tests", "tests");
        var caches = catalog.Caches();
        var directory = Samples.NewTempDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory, "Thing@1.0.0.yaml"), Document(ByNameThenAlias));
        var mappings = new MappingCatalog(directory, new DeliveryDocumentLoader());
        var secrets = new SecretResolver([new EnvSecretProvider()]);
        var resolver = new RenderResolver(mappings, caches, templates, secrets);
        var flow = Samples.Targeting(new FlowTarget
        {
            Endpoint = "https://osdu.example.test",
            Protocol = DeliveryProtocol.Storage,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [CacheScope.PartitionHeader] = "dev" },
        }) with
        {
            Render = new FlowRender
            {
                Mapping = "Thing@1.0.0",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = "dev" },
            },
        };
        var capture = new CacheCapture(null, "tests", "osdu");
        static ReferenceType Units(params string[] codes)
            => new("UnitOfMeasure", "reference-data--UnitOfMeasure", codes.Select(c => ReferenceItem.FromText($"dev:reference-data--UnitOfMeasure:{c}", new Dictionary<string, string> { ["Code"] = c })));
        static SystemPropertyReading Indexer(SystemPropertyState keywordLower, string source)
            => SystemPropertyReading.Read(SystemProperties.Indexer, [new SystemProperty(SystemProperties.Indexer, SystemProperties.KeywordLower, keywordLower, source, null)]);

        // The partition holds no version yet: nothing says its indexer keeps a lowercased copy, so lookups are exact.
        var unread = (await resolver.ResolveAsync(flow)).Context;
        Assert.Equal((ReferenceSnapshot.Empty.Version, (string?)null), (unread.CacheVersion, unread.CacheScope));
        Assert.Equal(SystemProperties.Pinned([]), unread.SystemProperties);
        Assert.False(unread.KeywordLower);

        await caches.MergeAsync("dev", "units", [Units("m")], capture, DateTimeOffset.UnixEpoch, [Indexer(SystemPropertyState.Enabled, "dataPartition")]);
        var on = (await resolver.ResolveAsync(flow)).Context;
        Assert.True(on.KeywordLower);
        Assert.Equal((ReferenceSnapshot.Empty.Version, (string?)null), (on.CacheVersion, on.CacheScope));

        // A capture that changes reference data, and what a service explains a setting with, is a new version, and the
        // mapping renders exactly as it did: none of its records renders again, and none of their questions is asked again.
        var units = await caches.MergeAsync(
            "dev", "units", [Units("m", "ft")], capture, DateTimeOffset.UnixEpoch.AddHours(1), [Indexer(SystemPropertyState.Enabled, "runtime")]);
        Assert.True(units.Written);
        Assert.Equal(on.Canonical(), (await resolver.ResolveAsync(flow)).Context.Canonical());

        // A capture that finds the setting changed moves the context, so every record is rendered under the new rule.
        await caches.MergeAsync("dev", "units", [Units("m", "ft")], capture, DateTimeOffset.UnixEpoch.AddHours(2), [Indexer(SystemPropertyState.Disabled, "runtime")]);
        var off = (await resolver.ResolveAsync(flow)).Context;
        Assert.False(off.KeywordLower);
        Assert.NotEqual(on.Canonical(), off.Canonical());

        // A version the flow pins is the one whose properties it renders under, and one the catalog does not hold is refused.
        var pinned = (await resolver.ResolveAsync(flow with { Render = flow.Render with { CacheVersion = units.Snapshot.Version } })).Context;
        Assert.True(pinned.KeywordLower);
        var missing = await Assert.ThrowsAsync<FlowValidationException>(
            () => resolver.ResolveAsync(flow with { Render = flow.Render with { CacheVersion = "20000101T000000Z" } }));
        Assert.Contains("pins version 20000101T000000Z of the cache of partition 'dev'", missing.Message, StringComparison.Ordinal);

        // A host without the module's database knows nothing of the partition's settings, and asks exact questions.
        Assert.False((await new RenderResolver(mappings, cache: null, templates, secrets).ResolveAsync(flow)).Context.KeywordLower);
    }

    /// <summary>
    /// A platform that answers by the query it is sent, which is what tells an exact question from one that ignores case:
    /// the ids a query finds, or null for a query it refuses.
    /// </summary>
    private sealed class QuerySearch(params (string Query, string[]? Ids)[] known) : IRecordSearch
    {
        private readonly Dictionary<SearchQuestion, SearchAnswer> _answers = [];

        public List<string> Asked { get; } = [];

        public bool TryAnswer(SearchQuestion question, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SearchAnswer? answer)
            => _answers.TryGetValue(question, out answer);

        public Task AnswerAsync(IReadOnlyCollection<SearchQuestion> questions, CancellationToken ct = default)
        {
            foreach (var question in questions.Where(q => !_answers.ContainsKey(q)))
            {
                Asked.Add(question.Query);
                var match = known.FirstOrDefault(k => string.Equals(k.Query, question.Query, StringComparison.Ordinal));
                if (match.Query is not null && match.Ids is null)
                {
                    _answers[question] = SearchAnswer.Refused("Malformed query");
                    continue;
                }

                var ids = match.Ids ?? [];
                _answers[question] = ids.Length switch
                {
                    0 => SearchAnswer.None,
                    1 => SearchAnswer.Found(ids[0]),
                    _ => SearchAnswer.Ambiguous(ids.Length, ids),
                };
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>A platform that refuses every query, saying why.</summary>
    private sealed class RefusingSearch(string reason) : IRecordSearch
    {
        private readonly Dictionary<SearchQuestion, SearchAnswer> _answers = [];

        public bool TryAnswer(SearchQuestion question, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SearchAnswer? answer)
            => _answers.TryGetValue(question, out answer);

        public Task AnswerAsync(IReadOnlyCollection<SearchQuestion> questions, CancellationToken ct = default)
        {
            foreach (var question in questions)
            {
                _answers[question] = SearchAnswer.Refused(reason);
            }

            return Task.CompletedTask;
        }
    }
}
