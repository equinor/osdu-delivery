using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Core;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// An assertion flow's <c>mapping</c> subject (osdu/docs/reference/flow/assertion.md, osdu/docs/reference/flow/mapping-assertions.md):
/// how it is written and refused, how a test holding it fits its template once a run has read the mapping, and how its
/// records are judged and counted.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MappingAssertionFlowTests
{
    private static readonly DeliveryDocumentLoader Loader = new();

    private static string Flow(string assertion, string testKeys = "", string kind = TestSchema.Kind) => $$"""
        flowType: assertion
        name: t
        partitions: [dev]
        source: { endpoint: http://localhost }
        tests:
          - name: one
            kind: "{{kind}}"
        {{(testKeys.Length == 0 ? string.Empty : "    " + testKeys)}}
            assert:
              - {{assertion}}
        """;

    private static AssertionFlowDefinition Parse(string yaml) => Loader.ParseAssertion(yaml, "flow.yaml");

    /// <summary>The test mapping over the test template, with <paramref name="data"/> as its data.</summary>
    private static MappingDefinition Mapping(string data) => TestSchema.Mapping(data);

    private static (AssertionTest Test, MappingRulesAssertion Rules) Bound(MappingDefinition? mapping, string? unread = null)
    {
        var test = Assert.Single(Parse(Flow("mapping: Thing@1.0.0")).Tests);
        var rules = (MappingRulesAssertion)Assert.Single(test.Assertions);
        var bound = rules with { Definition = mapping, Unread = unread };
        return (test with { Assertions = [bound] }, bound);
    }

    private static IReadOnlyList<string> Problems(AssertionTest test) => AssertionTemplates.Check(test, OsduTemplate.From(TestSchema.Build()));

    [Fact]
    public void A_test_holds_its_records_to_a_mapping_named_by_its_reference()
    {
        var rules = Assert.IsType<MappingRulesAssertion>(Assert.Single(Assert.Single(Parse(Flow("mapping: Thing@1.0.0")).Tests).Assertions));

        Assert.Equal("Thing@1.0.0", rules.Mapping);
        Assert.Equal("mapping", rules.Type);
        Assert.Equal("meets what mapping Thing@1.0.0 asserts", rules.Label);
        Assert.Null(rules.Definition);
        Assert.True(AssertionTemplates.ReadsFields(Assert.Single(Parse(Flow("mapping: Thing@1.0.0")).Tests)));
    }

    [Theory]
    [InlineData("mapping: Thing", "names the mapping whose assertions the records are held to as Name@version")]
    [InlineData("mapping: ../Thing@1.0.0", "names the mapping whose assertions the records are held to as Name@version")]
    [InlineData("{ mapping: Thing@1.0.0, equals: x }", "is a mapping assertion, which does not take equals")]
    [InlineData("{ mapping: Thing@1.0.0, conforms: true }", "names conforms and mapping; an assertion has one subject")]
    public void A_mapping_subject_that_cannot_name_one_mapping_is_refused(string assertion, string expected)
        => Assert.Contains(expected, Assert.Throws<FlowValidationException>(() => Parse(Flow(assertion))).Message);

    [Fact]
    public void A_mapping_subject_reads_records_as_storage_holds_them()
        => Assert.Contains(
            "which needs each record as storage holds it; the test reads the index (read: index)",
            Assert.Throws<FlowValidationException>(() => Parse(Flow("mapping: Thing@1.0.0", "read: index"))).Message);

    [Fact]
    public void A_test_whose_mapping_fits_its_template_has_no_problem()
    {
        var (test, _) = Bound(Mapping("""
            Count:
              $from: count
              $assert:
                - between: [0, 250]
            """));

        Assert.Empty(Problems(test));
    }

    [Fact]
    public void A_mapping_the_run_could_not_read_makes_the_test_not_fit_saying_why()
    {
        var (test, _) = Bound(null, "no mapping Thing@1.0.0 is synced; sync the repository that declares it, then run again");

        Assert.Equal("'meets what mapping Thing@1.0.0 asserts': no mapping Thing@1.0.0 is synced; sync the repository that declares it, then run again.", Assert.Single(Problems(test)));
    }

    [Fact]
    public void A_mapping_of_another_kind_or_with_nothing_to_judge_on_a_record_does_not_fit()
    {
        var (other, _) = Bound(Mapping("Count: { $from: count, $assert: [ { atLeast: 0 } ] }"));
        Assert.Contains("renders test:wks:work-product-component--Thing:1.0.0, and the test reads test:wks:master-data--Other:1.0.0", Assert.Single(AssertionTemplates.Check(
            other with { Kind = "test:wks:master-data--Other:1.0.0" }, OsduTemplate.From(TestSchema.Build()))), StringComparison.Ordinal);

        var (incoming, _) = Bound(Mapping("""
            Count:
              $from: count
              $assert:
                - stage: incoming
                  exists: true
            """));
        Assert.Contains("asserts nothing of the values a record carries (stage: record)", Assert.Single(Problems(incoming)), StringComparison.Ordinal);
    }

    [Fact]
    public void An_assertion_of_the_mapping_that_does_not_suit_its_property_does_not_fit()
    {
        var (test, _) = Bound(Mapping("""
            Count:
              $from: count
              $assert:
                - matches: '^[0-9]+$'
            """));

        Assert.Contains("record.data.Count.$assert[0]: 'data.Count' is an integer in", Assert.Single(Problems(test)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_record_is_judged_and_the_records_failing_are_counted_by_the_assertions_they_fail()
    {
        var (_, rules) = Bound(Mapping("""
            Count:
              $from: count
              $assert:
                - atMost: 250
                  name: count-range
            Symbol:
              $from: symbol
              $required: false
              $assert:
                - in: [GR, SP]
                  onFail: report
            """));
        var evaluator = new MappingRulesEvaluator(0, rules, 20);

        evaluator.Observe("dev:x:1", Record("""{ "Count": 10, "Symbol": "GR" }"""));
        evaluator.Observe("dev:x:2", Record("""{ "Count": 300, "Symbol": "CALI" }"""));
        evaluator.Observe("dev:x:3", Record("""{ "Count": 400 }"""));
        var outcome = await evaluator.CompleteAsync(null!, new TestSubject(3, 3, false, null, []), CancellationToken.None);

        Assert.Equal(TestOutcomes.Failed, outcome.Outcome);
        Assert.Equal((3L, 2L), (outcome.Checked, outcome.Failing));
        Assert.Contains("1 of 3 record(s) meet it", outcome.Actual, StringComparison.Ordinal);
        Assert.Contains("failed most: data.Count \"count-range\" (2), data.Symbol \"in [\"GR\", \"SP\"]\" (1)", outcome.Actual, StringComparison.Ordinal);
        Assert.Equal(["dev:x:2", "dev:x:3"], outcome.Examples.Select(e => e.Id));
        Assert.StartsWith("data.Count fails \"count-range\": is 300", outcome.Examples[0].Reason, StringComparison.Ordinal);
        Assert.EndsWith("(and 1 more)", outcome.Examples[0].Reason, StringComparison.Ordinal);
    }

    private static JsonObject Record(string data)
        => new()
        {
            ["id"] = "dev:work-product-component--Thing:1",
            ["kind"] = TestSchema.Kind,
            ["data"] = JsonNode.Parse(data),
        };
}

/// <summary>
/// The mappings the repository sync keeps, as the engine reads them where it has no repository tree: by id, and by reference
/// among every repository that declares it, an unreadable or ambiguous one answered with why.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class SyncedMappingTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();

    private EngineContext Context() => Samples.Engine(_db.Ledger(), new TestClock());

    private async Task<DeliveryMapping> SyncAsync(Guid repoId, string reference, string yaml, string status = "valid", string? message = null)
    {
        var row = new DeliveryMapping
        {
            Id = Catalog.DeliveryCatalogSync.MappingId(repoId, reference),
            RepoId = repoId,
            Reference = reference,
            Name = reference.Split('@')[0],
            Version = reference.Split('@')[1],
            Kind = TestSchema.Kind,
            TemplateVersion = TestSchema.Build().Version,
            RelativePath = $"mappings/{reference}.yaml",
            ContentHash = Hashing.ContentHash.Of(yaml),
            Yaml = yaml,
            Status = status,
            Message = message,
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow,
        };
        await using var db = _db.CreateDbContext();
        db.DeliveryMappings.Add(row);

        // The repository's template folder holds the version the mapping names, as the sync recorded it beside the mapping.
        var path = "templates/" + TemplateSources.FileName(TestSchema.Kind);
        if (!await db.DeliveryRepositoryTemplates.AnyAsync(t => t.RepoId == repoId && t.Path == path))
        {
            db.DeliveryRepositoryTemplates.Add(new DeliveryRepositoryTemplate
            {
                Id = Guid.NewGuid(),
                RepoId = repoId,
                Path = path,
                Folder = "templates",
                Kind = TestSchema.Kind,
                Version = TestSchema.Build().Version,
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
        return row;
    }

    private static string Document(string version, string data = "Count: { $from: count, $assert: [ { atLeast: 0 } ] }")
        => TestSchema.MappingDocument(data).Replace("version: 1.0.0", $"version: {version}", StringComparison.Ordinal);

    [Fact]
    public async Task A_synced_mapping_is_read_by_its_id_and_by_its_reference()
    {
        var version = "9." + Guid.NewGuid().ToString("N")[..8] + ".0";
        var row = await SyncAsync(Guid.NewGuid(), $"Thing@{version}", Document(version));

        var (byId, idProblem) = await SyncedMappings.ByIdAsync(Context(), row.Id, CancellationToken.None);
        Assert.Null(idProblem);
        Assert.Equal($"Thing@{version}", byId!.Reference);
        Assert.Single(byId.Assertions());

        var (byReference, referenceProblem) = await SyncedMappings.ByReferenceAsync(Context(), $"Thing@{version}", CancellationToken.None);
        Assert.Null(referenceProblem);
        Assert.Equal(byId.Fingerprint, byReference!.Fingerprint);
    }

    [Fact]
    public async Task A_reference_two_repositories_declare_alike_is_read_and_one_they_declare_differently_is_not()
    {
        var version = "9." + Guid.NewGuid().ToString("N")[..8] + ".0";
        var reference = $"Thing@{version}";
        await SyncAsync(Guid.NewGuid(), reference, Document(version));
        await SyncAsync(Guid.NewGuid(), reference, Document(version));
        Assert.NotNull((await SyncedMappings.ByReferenceAsync(Context(), reference, CancellationToken.None)).Mapping);

        await SyncAsync(Guid.NewGuid(), reference, Document(version, "Count: { $from: count, $assert: [ { atLeast: 1 } ] }"));
        var (mapping, problem) = await SyncedMappings.ByReferenceAsync(Context(), reference, CancellationToken.None);
        Assert.Null(mapping);
        Assert.Contains($"mapping {reference} is synced from 3 repositories whose documents differ", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mapping_none_syncs_or_that_does_not_load_is_answered_with_why()
    {
        var version = "9." + Guid.NewGuid().ToString("N")[..8] + ".0";
        Assert.Equal(
            $"no mapping Thing@{version} is synced; sync the repository that declares it, then run again",
            (await SyncedMappings.ByReferenceAsync(Context(), $"Thing@{version}", CancellationToken.None)).Problem);

        var broken = await SyncAsync(Guid.NewGuid(), $"Thing@{version}", "documentType: mapping", "invalid", "name is required");
        Assert.Contains("does not load: name is required", (await SyncedMappings.ByIdAsync(Context(), broken.Id, CancellationToken.None)).Problem, StringComparison.Ordinal);
        Assert.Contains("is synced under", (await SyncedMappings.ByIdAsync(Context(), Guid.NewGuid(), CancellationToken.None)).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_sync_counts_a_mappings_assertions_in_its_summary_so_a_picker_can_say_which_state_any()
    {
        var version = "9." + Guid.NewGuid().ToString("N")[..8] + ".0";
        var root = Samples.NewTempDirectory();
        var repoId = Guid.NewGuid();
        try
        {
            // The repository holds the template its mapping fills, which the mapping names by its kind alone.
            Directory.CreateDirectory(Path.Combine(root, "mappings"));
            Directory.CreateDirectory(Path.Combine(root, "templates"));
            await File.WriteAllTextAsync(Path.Combine(root, "templates", TemplateSources.FileName(TestSchema.Kind)), TemplateFolderFixtures.ThingFile());
            await File.WriteAllTextAsync(
                Path.Combine(root, "mappings", $"Thing@{version}.yaml"),
                Document(version, "Count: { $from: count, $assert: [ { atLeast: 0 }, { atMost: 250, onFail: report } ] }")
                    .ReplaceLineEndings("\n")
                    .Replace($"  version: {TestSchema.Build().Version}\n", string.Empty, StringComparison.Ordinal));

            await using (var db = _db.CreateDbContext())
            {
                await new Catalog.DeliveryCatalogSync(new DeliveryDocumentLoader())
                    .ReconcileAsync(db, repoId, root, DateTime.UtcNow, new List<string>(), CancellationToken.None);
            }

            await using (var db = _db.CreateDbContext())
            {
                var row = await db.DeliveryMappings.AsNoTracking().SingleAsync(m => m.RepoId == repoId);
                Assert.Equal("valid", row.Status);
                Assert.Equal(2, JsonNode.Parse(row.SummaryJson)!["assertions"]!.GetValue<int>());
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}
