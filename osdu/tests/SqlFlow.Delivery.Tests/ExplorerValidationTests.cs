using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;
using Xunit;
using static SqlFlow.Delivery.Tests.ValidationFixtures;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The explorer's checks of what OSDU holds against what OSDU expects (osdu/docs/reference/concepts/explorer.md,
/// Validate): a kind's schema read from the partition's Schema service and bundled, whatever references it makes to
/// other schemas; one record checked at its latest or a version, against that schema or a template a synced repository
/// holds, read from its file, with the
/// records it refers to looked up in storage; and the records a search finds checked up to a bound, counted by outcome
/// and rule.
/// </summary>
public sealed class ExplorerValidationTests : IDisposable
{
    private readonly FakeOsduPlatform _platform = new();
    private readonly HttpRuntime _runtime;
    private readonly OsduHttpClient _client;
    private readonly TestClock _clock = new();
    private readonly List<string> _repositories = [];

    public ExplorerValidationTests()
    {
        _runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), _clock, _platform, allowLoopback: true);
        _client = new OsduHttpClient(
            _runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
    }

    /// <summary>
    /// A synced repository whose template folder holds <paramref name="schemas"/> as files, each naming its kind as a template
    /// file does, read as the explorer reads them: from the files, in the folder the repository was synced into. Answers the
    /// templates and the schemas as the files hold them.
    /// </summary>
    private (RepositoryTemplates Templates, IReadOnlyList<SchemaSnapshot> Held) Repository(params SchemaSnapshot[] schemas)
    {
        var root = Samples.NewTempDirectory();
        _repositories.Add(root);
        var repo = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(root, "templates"));
        var files = new List<RepositoryTemplateFile>();
        var held = new List<SchemaSnapshot>();
        foreach (var schema in schemas)
        {
            var named = SchemaSnapshot.Parse(schema.Kind, TemplateSources.WithKind(schema.Root, schema.Kind).ToJsonString(), schema.CapturedUtc);
            var name = $"{schema.Kind.Replace(':', '_')}.json";
            File.WriteAllText(Path.Combine(root, "templates", name), TemplateSources.FileText(named));
            files.Add(new RepositoryTemplateFile(repo, $"templates/{name}", "templates", named.Kind, named.Version, null, DateTime.UtcNow));
            held.Add(named);
        }

        return (RepositoryTemplates.Of(files, RepositoryFolders.Of(repo, root)), held);
    }

    /// <summary>The data definitions' example records, as a host that keeps a copy of them answers; each kind asked is noted.</summary>
    private sealed class Examples(Func<string, OfficialExample?> answer) : IOfficialExamples
    {
        public List<string> Asked { get; } = [];

        public Task<OfficialExample?> ExampleAsync(string kind, CancellationToken ct = default)
        {
            Asked.Add(kind);
            return Task.FromResult(answer(kind));
        }
    }

    /// <summary>The fixture's kind as one of OSDU's own, whose example the data definitions publish.</summary>
    private const string OsduKind = "osdu:wks:master-data--Thing:1.0.0";

    private static OfficialExample ExampleOf(string kind) => new(
        kind,
        "v0.30.0",
        "Examples/master-data/Thing.1.0.0.json",
        new Uri("https://community.opengroup.org/osdu/data/data-definitions/-/blob/v0.30.0/Examples/master-data/Thing.1.0.0.json"),
        JsonNode.Parse("""{ "kind": "osdu:wks:master-data--Thing:1.0.0", "data": { "Status": "Active", "Code": "EX-1" } }""")!.AsObject());

    private ExplorerChecks Checks(RepositoryTemplates? templates = null, IOfficialExamples? examples = null)
        => new(_client, new OsduRecordProtocol(_client, new ProtocolOptions(), _clock), OsduRecordProtocol.DefaultVerifyBatchPath, templates, _clock, examples: examples);

    /// <summary>The fixture's record stored under OSDU's own kind, with its schema in the Schema service under that kind.</summary>
    private string StoredAsOsdu(Action<JsonObject> change)
    {
        var id = Stored(r =>
        {
            r["kind"] = OsduKind;
            change(r);
        });
        _platform.Schemas[OsduKind] = JsonNode.Parse(SchemaJson)!.AsObject();
        return id;
    }

    private SchemaServiceReader Reader() => new(_client, _clock);

    /// <summary>Puts the fixture's record and the three records it refers to in storage, and its schema in the Schema service.</summary>
    private string Stored(Action<JsonObject>? change = null, bool references = true)
    {
        var record = change is null ? ValidRecord() : Record(change);
        _platform.Put(record);
        _platform.Schemas[Kind] = JsonNode.Parse(SchemaJson)!.AsObject();
        if (references)
        {
            foreach (var id in new[] { "dev:master-data--Well:W-1", "dev:reference-data--MeasurementType:KB", "dev:master-data--Field:F-1" })
            {
                _platform.Put(FakeOsduPlatform.Record(id, "osdu:wks:" + id.Split(':')[1] + ":1.0.0"));
            }
        }

        return record["id"]!.GetValue<string>();
    }

    private static ValidationVerdict VerdictOf(ExplorerValidation answer) => ValidationVerdict.FromJson(answer.Verdict) ?? throw new InvalidOperationException(answer.Problem);

    [Fact]
    public async Task A_schema_the_service_holds_bundled_is_read_as_it_is()
    {
        _platform.Schemas[Kind] = JsonNode.Parse(SchemaJson)!.AsObject();

        var read = await Reader().ReadAsync(Kind);

        Assert.NotNull(read.Schema);
        Assert.Equal([Kind], read.Read);
        Assert.Empty(read.Unresolved);
        Assert.Equal(Schema.Version, read.Schema!.Version);
        Assert.Empty(RecordValidator.Check(ValidRecord(), SchemaRules.Of(read.Schema)).Problems);
    }

    [Fact]
    public async Task A_schema_referring_to_other_schemas_by_id_reads_each_once_and_bundles_them_with_their_own_definitions()
    {
        _platform.Schemas["test:wks:master-data--Thing:2.0.0"] = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "acl": { "$ref": "osdu:wks:AbstractAccessControlList:1.0.0" },
                "data": { "allOf": [ { "$ref": "osdu:wks:AbstractCommon:1.0.0" }, { "properties": { "Name": { "type": "string" } } } ] },
                "meta": { "$ref": "osdu:wks:AbstractCommon:1.0.0#/definitions/Code.1.0.0" }
              }
            }
            """)!.AsObject();
        _platform.Schemas["osdu:wks:AbstractCommon:1.0.0"] = JsonNode.Parse("""
            {
              "type": "object",
              "definitions": { "Code.1.0.0": { "type": "string", "pattern": "^[A-Z]+$" } },
              "properties": { "Code": { "$ref": "#/definitions/Code.1.0.0" }, "Acl": { "$ref": "osdu:wks:AbstractAccessControlList:1.0.0" } }
            }
            """)!.AsObject();
        _platform.Schemas["osdu:wks:AbstractAccessControlList:1.0.0"] = JsonNode.Parse("""
            { "type": "object", "properties": { "owners": { "type": "array", "items": { "type": "string" } } }, "required": ["owners"] }
            """)!.AsObject();

        var read = await Reader().ReadAsync("test:wks:master-data--Thing:2.0.0");

        Assert.Equal(["test:wks:master-data--Thing:2.0.0", "osdu:wks:AbstractAccessControlList:1.0.0", "osdu:wks:AbstractCommon:1.0.0"], read.Read);
        Assert.Empty(read.Unresolved);
        var rules = SchemaRules.Of(read.Schema!);
        Assert.DoesNotContain(rules.Notes, n => n.Contains("does not hold", StringComparison.Ordinal));

        var found = RecordValidator.Check(JsonNode.Parse("""{ "acl": {}, "data": { "Code": "lower", "Acl": { "owners": [1] } }, "meta": "x1" }""")!, rules);
        Assert.Equal(
            [("acl.owners", "required"), ("data.Acl.owners", "type"), ("data.Code", "pattern"), ("meta", "pattern")],
            found.Problems.Select(p => (p.At.Replace("[]", string.Empty, StringComparison.Ordinal), p.Rule)).Order());
    }

    [Fact]
    public async Task Schemas_referring_to_each_other_are_each_read_once_and_the_kind_s_own_schema_is_the_bundle_s_root()
    {
        _platform.Schemas["osdu:wks:master-data--Loop:1.0.0"] = JsonNode.Parse("""{ "type": "object", "properties": { "Other": { "$ref": "osdu:wks:AbstractOther:1.0.0" } } }""")!.AsObject();
        _platform.Schemas["osdu:wks:AbstractOther:1.0.0"] = JsonNode.Parse("""{ "type": "object", "properties": { "Back": { "$ref": "osdu:wks:master-data--Loop:1.0.0" }, "N": { "type": "integer" } } }""")!.AsObject();

        var read = await Reader().ReadAsync("osdu:wks:master-data--Loop:1.0.0");

        Assert.Equal(2, read.Read.Count);
        var found = RecordValidator.Check(JsonNode.Parse("""{ "Other": { "Back": { "Other": { "N": "x" } } } }""")!, SchemaRules.Of(read.Schema!));
        Assert.Equal("Other.Back.Other.N", Assert.Single(found.Problems).Path);
    }

    [Theory]
    [InlineData("osdu:wks:AbstractGone:1.0.0", "holds no schema 'osdu:wks:AbstractGone:1.0.0'")]
    [InlineData("https://schema.osdu.opengroup.org/json/abstract/AbstractCommonResources.1.0.0.json", "is not to a schema by its id")]
    [InlineData("#/properties/data", "points into the schema itself")]
    [InlineData("osdu:wks:AbstractCommon:1.0.0#/properties/Code", "points into a part of 'osdu:wks:AbstractCommon:1.0.0' other than its definitions")]
    public async Task A_reference_the_reader_cannot_resolve_is_named_and_the_part_it_describes_is_not_checked(string reference, string why)
    {
        _platform.Schemas["test:wks:master-data--Odd:1.0.0"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["Part"] = new JsonObject { ["$ref"] = reference }, ["N"] = new JsonObject { ["type"] = "integer" } },
        };
        _platform.Schemas["osdu:wks:AbstractCommon:1.0.0"] = JsonNode.Parse("""{ "type": "object", "properties": { "Code": { "type": "string" } } }""")!.AsObject();

        var read = await Reader().ReadAsync("test:wks:master-data--Odd:1.0.0");

        Assert.Contains(read.Unresolved, u => u.Contains(why, StringComparison.Ordinal));
        var found = RecordValidator.Check(JsonNode.Parse("""{ "Part": "x", "N": "not a number" }""")!, SchemaRules.Of(read.Schema!));
        Assert.Equal("N", Assert.Single(found.Problems).At);
        Assert.Equal("Part", Assert.Single(found.Unverified).At);
    }

    [Fact]
    public async Task A_kind_the_service_holds_no_schema_of_reads_as_none_and_an_answer_that_is_not_json_fails_the_read()
    {
        var none = await Reader().ReadAsync("osdu:wks:master-data--Nothing:1.0.0");
        Assert.Null(none.Schema);
        Assert.Empty(none.Read);

        _platform.SchemaTexts["osdu:wks:master-data--Broken:1.0.0"] = "<html>not a schema</html>";
        var broken = await Assert.ThrowsAsync<DeliveryException>(() => Reader().ReadAsync("osdu:wks:master-data--Broken:1.0.0"));
        Assert.Contains("not JSON", broken.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chain_of_references_past_the_bound_reads_at_most_the_bound_and_names_what_it_did_not_read()
    {
        for (var i = 0; i < SchemaServiceReader.MaxSchemas + 5; i++)
        {
            _platform.Schemas[$"osdu:wks:AbstractStep{i}:1.0.0"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["Next"] = new JsonObject { ["$ref"] = $"osdu:wks:AbstractStep{i + 1}:1.0.0" } },
            };
        }

        var read = await Reader().ReadAsync("osdu:wks:AbstractStep0:1.0.0");

        Assert.Equal(SchemaServiceReader.MaxSchemas, read.Read.Count);
        Assert.Contains(read.Unresolved, u => u.Contains("at most 200 schemas", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_schema_writing_defs_where_draft07_writes_definitions_is_bundled_all_the_same()
    {
        _platform.Schemas["test:wks:master-data--Defs:1.0.0"] = JsonNode.Parse("""
            { "type": "object", "properties": { "Code": { "$ref": "#/$defs/Code" } }, "$defs": { "Code": { "type": "string", "maxLength": 2 } } }
            """)!.AsObject();

        var read = await Reader().ReadAsync("test:wks:master-data--Defs:1.0.0");

        Assert.Empty(read.Unresolved);
        Assert.Equal("maxLength", Assert.Single(RecordValidator.Check(JsonNode.Parse("""{ "Code": "long" }""")!, SchemaRules.Of(read.Schema!)).Problems).Rule);
    }

    [Fact]
    public async Task A_record_meeting_its_schema_with_every_record_it_refers_to_in_OSDU_is_valid_against_the_Schema_service_s()
    {
        var id = Stored();

        var answer = await Checks().RecordAsync(id, null, ExplorerSchemaSource.Osdu, null);

        Assert.True(answer.Found);
        Assert.Null(answer.Problem);
        Assert.Equal(Kind, answer.Kind);
        Assert.Equal(VerdictSchema.SchemaService, answer.Schema!.Source);
        Assert.Equal([Kind], answer.Schema.Read);
        var verdict = VerdictOf(answer);
        Assert.Equal(ValidationOutcome.Valid, verdict.Outcome);
        Assert.Equal(3, verdict.References.InOsdu);
        Assert.Equal(_platform.Records[id]["version"]!.GetValue<long>(), answer.Version);
    }

    [Fact]
    public async Task A_record_breaking_its_schema_or_naming_a_record_OSDU_does_not_hold_is_invalid_where_it_breaks()
    {
        var id = Stored(r => DataOf(r)["Status"] = "Planned", references: false);

        var verdict = VerdictOf(await Checks().RecordAsync(id, null, ExplorerSchemaSource.Osdu, null));

        Assert.Equal(ValidationOutcome.Invalid, verdict.Outcome);
        Assert.Contains(verdict.Problems, p => p.Rule == "enum" && p.Path == "data.Status");
        Assert.Equal(3, verdict.References.Missing);
        Assert.Contains(verdict.Problems, p => p.Rule == "reference" && p.Path == "data.WellID" && p.Message.Contains("storage service holds no such record", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_record_is_checked_at_the_version_asked_and_one_storage_does_not_hold_says_so()
    {
        var id = Stored();
        var held = _platform.Records[id]["version"]!.GetValue<long>();

        Assert.Equal(held, (await Checks().RecordAsync(id, held, ExplorerSchemaSource.Osdu, null)).Version);
        var other = await Checks().RecordAsync(id, held + 7, ExplorerSchemaSource.Osdu, null);
        Assert.False(other.Found);
        Assert.Contains($"no version {held + 7}", other.Problem, StringComparison.Ordinal);

        var missing = await Checks().RecordAsync("dev:master-data--Thing:Gone", null, ExplorerSchemaSource.Osdu, null);
        Assert.False(missing.Found);
        Assert.Contains("holds no record dev:master-data--Thing:Gone", missing.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_kind_the_Schema_service_does_not_hold_offers_the_repositories_templates_and_checks_against_one_asked_for()
    {
        _platform.Put(ValidRecord());
        var (templates, held) = Repository(Schema);
        var version = Assert.Single(held).Version;

        var unheld = await Checks(templates).RecordAsync("dev:master-data--Thing:T-1", null, ExplorerSchemaSource.Osdu, null);
        Assert.Null(unheld.Verdict);
        Assert.Contains($"holds no schema of {Kind}", unheld.Problem, StringComparison.Ordinal);
        Assert.Equal([version], unheld.TemplateVersions);

        var checkedAgainstTemplate = await Checks(templates).RecordAsync("dev:master-data--Thing:T-1", null, ExplorerSchemaSource.Template, null);
        Assert.Equal(VerdictSchema.Template, checkedAgainstTemplate.Schema!.Source);
        Assert.Equal(version, VerdictOf(checkedAgainstTemplate).Schema!.Version);
        Assert.Equal(version, VerdictOf(await Checks(templates).RecordAsync("dev:master-data--Thing:T-1", null, ExplorerSchemaSource.Template, version)).Schema!.Version);

        var unknownVersion = await Checks(templates).RecordAsync("dev:master-data--Thing:T-1", null, ExplorerSchemaSource.Template, "0000000000000000");
        Assert.Contains($"No synced repository holds template {Kind} version 0000000000000000 in a template folder; they hold version {version}", unknownVersion.Problem, StringComparison.Ordinal);

        var noneHeld = await Checks(Repository().Templates).RecordAsync("dev:master-data--Thing:T-1", null, ExplorerSchemaSource.Template, null);
        Assert.StartsWith($"No synced repository holds a template of {Kind} in a template folder.", noneHeld.Problem, StringComparison.Ordinal);
        Assert.EndsWith("or check the record against the Schema service's.", noneHeld.Problem, StringComparison.Ordinal);

        // A host with no module database has no repository's templates to offer, and says so.
        var none = await Checks().RecordAsync("dev:master-data--Thing:T-1", null, ExplorerSchemaSource.Template, null);
        Assert.Equal("No repository's template can be read here: this host keeps no module database.", none.Problem);
        Assert.Empty(none.TemplateVersions);
    }

    [Fact]
    public async Task A_record_naming_no_kind_is_found_and_not_checked()
    {
        var record = ValidRecord();
        record["kind"] = "";
        _platform.Put(record);

        var answer = await Checks().RecordAsync("dev:master-data--Thing:T-1", null, ExplorerSchemaSource.Osdu, null);

        Assert.True(answer.Found);
        Assert.Contains("names no kind", answer.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_records_a_search_finds_are_read_checked_and_counted_by_outcome_and_rule_with_an_example_of_each()
    {
        Stored();
        _platform.Put(Record(r => { r["id"] = "dev:master-data--Thing:T-2"; DataOf(r)["Status"] = "Planned"; }));
        _platform.Put(Record(r => { r["id"] = "dev:master-data--Thing:T-3"; DataOf(r)["Status"] = "Drilling"; DataOf(r)["Code"] = "x"; }));
        _platform.Put(FakeOsduPlatform.Record("dev:master-data--Other:O-1", "osdu:wks:master-data--Other:1.0.0"));
        _platform.Search = (_, _) => ["dev:master-data--Thing:T-1", "dev:master-data--Thing:T-2", "dev:master-data--Thing:T-3", "dev:master-data--Other:O-1", "dev:master-data--Thing:Gone"];

        var list = await Checks().ListAsync(new RecordExplorer(_client, "dev", NullLogger.Instance), new ExplorerSearch { Kind = "*:*:*:*" }, ExplorerChecks.MaxRecords, ExplorerSchemaSource.Osdu);

        Assert.Equal(5, list.Matched);
        Assert.Equal(4, list.Read);
        Assert.Equal(["dev:master-data--Thing:Gone"], list.NotFound);
        Assert.Equal((1L, 2L, 0L, 1L), (list.Valid, list.Invalid, list.Unverified, list.NotChecked));
        var status = list.Rules[0];
        Assert.Equal(("data.Status", "enum", 2L), (status.At, status.Rule, status.Records));
        Assert.Equal("dev:master-data--Thing:T-2", status.ExampleId);
        Assert.Contains(list.Rules, r => r.At == "data.Code" && r.Rule == "pattern" && r.Records == 1);
        var other = Assert.Single(list.Unavailable);
        Assert.Equal(("osdu:wks:master-data--Other:1.0.0", 1L), (other.Kind, other.Records));
        Assert.Equal(["valid", "invalid", "invalid", "notValidated"], list.Records.Select(r => r.Outcome));
        Assert.StartsWith("data.Status enum: ", list.Records[1].First, StringComparison.Ordinal);
        Assert.False(list.Cut);

        // The Schema service is asked once for each kind, however many records of it are checked.
        Assert.Single(_platform.Calls, c => Uri.UnescapeDataString(c.Uri.AbsolutePath).EndsWith(Kind, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_search_finding_more_than_a_check_reads_is_cut_at_the_bound_and_says_so()
    {
        Stored();
        _platform.Search = (_, _) => Enumerable.Range(0, 5).Select(i => i == 0 ? "dev:master-data--Thing:T-1" : $"dev:master-data--Thing:Gone{i}").ToList();

        var list = await Checks().ListAsync(new RecordExplorer(_client, "dev", NullLogger.Instance), new ExplorerSearch(), 2, ExplorerSchemaSource.Osdu);

        Assert.True(list.Cut);
        Assert.Equal(2, list.Asked);
        Assert.Equal(1, list.Read);
        Assert.Single(list.NotFound);
    }

    [Fact]
    public async Task Each_problem_comes_with_what_was_found_what_the_schema_takes_there_and_how_to_fix_it_quoting_OSDUs_example()
    {
        var id = StoredAsOsdu(r => { DataOf(r)["Status"] = "Planned"; DataOf(r)["Code"] = "x"; });
        var examples = new Examples(ExampleOf);

        var answer = await Checks(examples: examples).RecordAsync(id, null, ExplorerSchemaSource.Osdu, null);

        var verdict = VerdictOf(answer);
        var guidance = answer.Guidance!;
        Assert.Equal(verdict.Problems.Select(p => (p.Path, p.Rule)), guidance.Problems.Select(g => (g.Path, g.Rule)));
        var status = Assert.Single(guidance.Problems, g => g.Path == "data.Status");
        Assert.Equal("'Planned'", status.Found);
        Assert.Equal("Use one of the values the schema allows: Active, Retired.", status.Advice);
        var expected = guidance.Expectations[status.Expected!];
        Assert.Equal("one of 2 values", expected.Summary);
        Assert.Equal("Active", expected.OsduExample);
        var code = Assert.Single(guidance.Problems, g => g.Path == "data.Code");
        Assert.Equal("Change Code so it matches ^[A-Z]{2}-[0-9]+$. For example: EX-1", code.Advice);
        Assert.Equal("Examples/master-data/Thing.1.0.0.json", guidance.Example!.Path);
        Assert.Null(guidance.ExampleNote);
        Assert.Equal([OsduKind], examples.Asked);
    }

    [Fact]
    public async Task A_valid_record_asks_for_no_example_and_a_kind_not_of_OSDU_has_none_to_look_for()
    {
        var examples = new Examples(ExampleOf);
        var valid = await Checks(examples: examples).RecordAsync(StoredAsOsdu(_ => { }), null, ExplorerSchemaSource.Osdu, null);
        Assert.Equal(ValidationOutcome.Valid, VerdictOf(valid).Outcome);
        Assert.Empty(valid.Guidance!.Problems);
        Assert.Empty(examples.Asked);

        // The fixture's own kind is not one of OSDU's: its problems are guided by the schema alone, and nothing is said of an example.
        var other = await Checks(examples: examples).RecordAsync(Stored(r => DataOf(r)["Status"] = "Planned"), null, ExplorerSchemaSource.Osdu, null);
        Assert.NotNull(Assert.Single(other.Guidance!.Problems, g => g.Rule == "enum").Advice);
        Assert.Null(other.Guidance.Example);
        Assert.Null(other.Guidance.ExampleNote);
        Assert.Empty(examples.Asked);
    }

    [Fact]
    public async Task An_example_the_data_definitions_do_not_publish_or_cannot_give_is_said_and_the_problems_are_guided_all_the_same()
    {
        var none = await Checks(examples: new Examples(_ => null)).RecordAsync(StoredAsOsdu(r => DataOf(r)["Status"] = "Planned"), null, ExplorerSchemaSource.Osdu, null);
        Assert.Equal($"The OSDU data definitions publish no example record of {OsduKind}.", none.Guidance!.ExampleNote);
        Assert.NotNull(Assert.Single(none.Guidance.Problems, g => g.Rule == "enum").Advice);

        var unreachable = new Examples(_ => throw new DataDefinitionsException("The OSDU data definitions at https://example.org/ did not answer in time for Examples/x.", notFound: false));
        var failed = await Checks(examples: unreachable).RecordAsync(StoredAsOsdu(r => DataOf(r)["Status"] = "Planned"), null, ExplorerSchemaSource.Osdu, null);
        Assert.StartsWith($"OSDU's example record of {OsduKind} could not be read: The OSDU data definitions at https://example.org/ did not answer in time", failed.Guidance!.ExampleNote, StringComparison.Ordinal);
        Assert.Null(failed.Guidance.Example);
        Assert.NotNull(Assert.Single(failed.Guidance.Problems, g => g.Rule == "enum").Advice);
    }

    [Fact]
    public async Task A_stored_record_holding_meta_null_has_no_meta_rather_than_a_problem_and_the_check_says_so()
    {
        var id = Stored(r => r["meta"] = null);

        var answer = await Checks().RecordAsync(id, null, ExplorerSchemaSource.Osdu, null);

        var verdict = VerdictOf(answer);
        Assert.Equal(ValidationOutcome.Valid, verdict.Outcome);
        Assert.Contains(verdict.Notes, n => n.StartsWith("meta is null", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_rules_a_list_breaks_say_what_the_schema_takes_and_how_to_fix_their_example_and_empty_blocks_are_counted()
    {
        Stored(r => r["meta"] = null);
        _platform.Put(Record(r => { r["id"] = "dev:master-data--Thing:T-2"; DataOf(r)["Status"] = "Planned"; }));
        _platform.Search = (_, _) => ["dev:master-data--Thing:T-1", "dev:master-data--Thing:T-2"];

        var list = await Checks().ListAsync(new RecordExplorer(_client, "dev", NullLogger.Instance), new ExplorerSearch(), ExplorerChecks.MaxRecords, ExplorerSchemaSource.Osdu);

        var status = Assert.Single(list.Rules);
        Assert.Equal("one of 2 values", status.Expected);
        Assert.Equal("Use one of the values the schema allows: Active, Retired.", status.Advice);
        Assert.Contains("1 record(s) hold meta null or empty, which is how a stored record with no meta can read, so it was read as absent.", list.Notes);
        Assert.Equal((1L, 1L), (list.Valid, list.Invalid));
    }

    /// <summary>A mapping over the fixture's kind asserting on two properties of the record and one of the row.</summary>
    private static MappingDefinition Asserting(string kind = Kind) => new DeliveryDocumentLoader().ParseMapping($$"""
        documentType: mapping
        name: Thing
        version: 2.0.0
        template:
          kind: {{kind}}
          version: {{Schema.Version}}
        dataset:
          system: test
          key: [code]
        parameters:
          dataPartition: { required: true }
        record:
          acl:
            owners: [owners@x]
            viewers: [viewers@x]
          legal:
            legaltags: [tag]
            otherRelevantDataCountries: [US]
          data:
            Name:
              $from: name
              $assert:
                - length: { atMost: 4 }
                  name: short-name
            Depth:
              $from: depth
              $assert:
                - between: [0, 1000]
                  onFail: report
            Code:
              $from: code
              $assert:
                - stage: incoming
                  matches: '^[A-Z]'
        """, "Thing@2.0.0.yaml");

    [Fact]
    public async Task A_record_is_held_to_a_mappings_record_stage_assertions_and_the_ones_it_cannot_judge_are_named()
    {
        var id = Stored();

        var verdict = VerdictOf(await Checks().RecordAsync(id, null, ExplorerSchemaSource.Osdu, null, new ExplorerAssertions(Asserting(), null)));

        Assert.Equal(ValidationOutcome.Valid, verdict.Outcome);
        var findings = verdict.Assertions!;
        Assert.Equal("Thing@2.0.0", findings.Mapping);
        Assert.Equal((2L, 2L), (findings.Checked, findings.Failed));
        Assert.Equal((1L, 1L), (findings.Held, findings.Reported));
        Assert.Equal(["data.Name", "data.Depth"], findings.Failures.Select(f => f.At));
        Assert.Equal("Alpha", findings.Failures[0].Value);
        Assert.Contains("1 assertion(s) of Thing@2.0.0 judge the value a row gives before the mapping's modifiers, which OSDU does not hold, so they were not judged", verdict.Notes);
    }

    [Fact]
    public async Task A_record_no_schema_can_be_had_for_is_still_held_to_a_mappings_assertions()
    {
        var id = Stored();

        var answer = await Checks(Repository().Templates).RecordAsync(id, null, ExplorerSchemaSource.Template, null, new ExplorerAssertions(Asserting(), null));

        Assert.NotNull(answer.Problem);
        var verdict = VerdictOf(answer);
        Assert.Equal(ValidationOutcome.NotValidated, verdict.Outcome);
        Assert.Contains(answer.Problem, verdict.Notes);
        Assert.Equal((2L, 1L), (verdict.Assertions!.Failed, verdict.Assertions.Held));

        // Without a mapping asked for, nothing was checked and there is no verdict.
        Assert.Null((await Checks(Repository().Templates).RecordAsync(id, null, ExplorerSchemaSource.Template, null)).Verdict);
    }

    [Fact]
    public async Task A_mapping_that_could_not_be_read_is_said_and_the_schemas_verdict_stands()
    {
        var id = Stored();

        var verdict = VerdictOf(await Checks().RecordAsync(id, null, ExplorerSchemaSource.Osdu, null, new ExplorerAssertions(null, "no mapping is synced under it")));

        Assert.Equal(ValidationOutcome.Valid, verdict.Outcome);
        Assert.Null(verdict.Assertions);
        Assert.Contains("No assertions were judged: no mapping is synced under it.", verdict.Notes);
    }

    [Fact]
    public void A_record_of_another_type_is_not_judged_and_one_of_another_version_is_judged_saying_so()
    {
        Assert.Null(MappingAssertionJudge.OfStored(ValidRecord(), Asserting("test:wks:master-data--Other:1.0.0"), out var other));
        Assert.Equal("mapping Thing@2.0.0 renders test:wks:master-data--Other:1.0.0, and the record is test:wks:master-data--Thing:1.0.0, so its assertions were not judged", Assert.Single(other));

        var later = MappingAssertionJudge.OfStored(ValidRecord(), Asserting("test:wks:master-data--Thing:1.1.0"), out var version);
        Assert.Equal(2, later!.Failed);
        Assert.Contains(version, n => n.Contains("its assertions were judged on what the record holds at the same properties", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_records_a_search_finds_are_counted_by_the_assertions_they_fail_with_an_example_of_each()
    {
        Stored();
        _platform.Put(Record(r => { r["id"] = "dev:master-data--Thing:T-2"; DataOf(r)["Name"] = "Ab"; DataOf(r)["Depth"] = 5; }));
        _platform.Put(FakeOsduPlatform.Record("dev:master-data--Other:O-1", "osdu:wks:master-data--Other:1.0.0"));
        _platform.Search = (_, _) => ["dev:master-data--Thing:T-1", "dev:master-data--Thing:T-2", "dev:master-data--Other:O-1"];

        var list = await Checks().ListAsync(
            new RecordExplorer(_client, "dev", NullLogger.Instance), new ExplorerSearch(), ExplorerChecks.MaxRecords, ExplorerSchemaSource.Osdu, new ExplorerAssertions(Asserting(), null));

        Assert.Equal("Thing@2.0.0", list.Mapping);
        Assert.Equal((2L, 1L), (list.Asserted, list.FailingAssertions));
        var name = Assert.Single(list.Assertions, a => a.At == "data.Name");
        Assert.Equal(("short-name", "hold", 1L, 1L, "dev:master-data--Thing:T-1", "Alpha"), (name.Assertion, name.OnFail, name.Records, name.Failures, name.ExampleId, name.ExampleValue));
        Assert.Contains(list.Assertions, a => a.At == "data.Depth" && a.OnFail == "report");
        Assert.Equal(2, list.Records.Single(r => r.Id == "dev:master-data--Thing:T-1").AssertionFailures);
        Assert.Equal(0, list.Records.Single(r => r.Id == "dev:master-data--Thing:T-2").AssertionFailures);
        Assert.Contains(list.Notes, n => n.Contains("and the record is osdu:wks:master-data--Other:1.0.0, so its assertions were not judged", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        _runtime.Dispose();
        _platform.Dispose();
        foreach (var root in _repositories)
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
