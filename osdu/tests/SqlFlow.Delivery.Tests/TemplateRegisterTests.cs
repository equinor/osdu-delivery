using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Cli;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// <c>sqlflow template register</c> (osdu/docs/reference/cli/template-register.md) against the fake platform's Schema
/// service, which answers a write as the service's own code does: what the run finds in a repository, what it says of each
/// custom kind and proposes, what it writes when asked and how it reads each write back, what it refuses to write, and how
/// it reads its connection from an env file without touching the environment other suites share.
/// </summary>
public sealed class TemplateRegisterTests : IDisposable
{
    private const string Facility10 = "acme:wks:master-data--Facility:1.0.0";
    private const string Facility11 = "acme:wks:master-data--Facility:1.1.0";
    private const string WellLog = "acme:wks:work-product-component--WellLogDetail:1.0.0";
    private const string ClientSecret = "register-client-secret-value";

    private readonly string _root = Samples.NewTempDirectory();
    private readonly FakeOsduPlatform _platform = new();
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);

    public TemplateRegisterTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        WriteEnv(
            ("OSDU_URL", FakeOsduPlatform.Endpoint),
            ("OSDU_TOKEN_URL", FakeOsduPlatform.Endpoint + FakeOsduPlatform.TokenPath),
            ("OSDU_SCOPE", "api://osdu/.default"),
            ("OSDU_CLIENT_ID", "register-client"),
            ("OSDU_CLIENT_SECRET", ClientSecret),
            ("OSDU_DATA_PARTITION", "dev"));
    }

    public void Dispose()
    {
        _platform.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_dry_run_says_where_each_custom_kind_stands_and_what_it_would_write_and_writes_nothing()
    {
        Write("facility/templates/acme_wks_master-data--Facility_1.0.0.json", Schema(Facility10));
        Write("facility/templates/acme_wks_master-data--Facility_1.1.0.json", Schema(Facility11, extra: "Operator"));
        Write("logs/templates/acme_wks_work-product-component--WellLogDetail_1.0.0.json", Schema(WellLog, depthType: "string"));
        Write("logs/templates/osdu_wks_master-data--Wellbore_1.3.0.json", Schema("osdu:wks:master-data--Wellbore:1.3.0"));
        Hold(WellLog, Schema(WellLog), "DEVELOPMENT", "INTERNAL");

        var (exit, output, notes) = await RunAsync(null, "--dry-run");
        if (Environment.GetEnvironmentVariable("TEMPLATE_REGISTER_SAMPLE") is { } sample)
        {
            File.WriteAllText(sample, output);
        }

        Assert.Equal(2, exit);
        var text = output.ReplaceLineEndings("\n");
        Assert.Contains("Custom schemas of " + _root + ", against OSDU partition dev", text, StringComparison.Ordinal);
        Assert.Contains("  platform     http://localhost/", text, StringComparison.Ordinal);

        // Every custom kind, its family's latest marked; the OSDU kind is never listed.
        Assert.Contains($"  1  {Facility10}\n", text, StringComparison.Ordinal);
        Assert.Contains($"  2  {Facility11}  latest\n", text, StringComparison.Ordinal);
        Assert.Contains($"  3  {WellLog}  latest\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("osdu:wks:master-data--Wellbore", text, StringComparison.Ordinal);

        // An older version is offered, not proposed; the latest unregistered one is registered; a development schema that
        // differs is updated, and a type it changes is said to need a reindex.
        Assert.Contains("proposed  nothing: not the latest version of its family", text, StringComparison.Ordinal);
        Assert.Contains("OSDU holds no schema of this kind.", text, StringComparison.Ordinal);
        Assert.Contains("  templates    4 in 2 template folder(s), 3 of a custom kind", text, StringComparison.Ordinal);
        Assert.Contains("     proposed  register\n", text, StringComparison.Ordinal);
        Assert.Contains("OSDU holds another schema of this kind in development: 1 property differs from it to the file (1 breaking).", text, StringComparison.Ordinal);
        Assert.Contains("     changes   osdu.data.Depth: type number -> string (breaking)\n", text, StringComparison.Ordinal);
        Assert.Contains("The type of osdu.data.Depth changes.", text, StringComparison.Ordinal);
        Assert.Contains("reindexes the kind with force_clean", text, StringComparison.Ordinal);
        Assert.Contains("     proposed  update\n", text, StringComparison.Ordinal);
        Assert.Contains("Proposed in partition dev: register 2, update 3.", text, StringComparison.Ordinal);
        Assert.Contains("Dry run: nothing was written.", text, StringComparison.Ordinal);

        Assert.Empty(_platform.SchemaWrites());
        Assert.Empty(notes);
    }

    [Fact]
    public async Task A_dry_run_in_json_carries_every_kind_and_what_it_would_write()
    {
        Write("templates/acme_wks_master-data--Facility_1.0.0.json", Schema(Facility10));
        Write("templates/acme_wks_work-product-component--WellLogDetail_1.0.0.json", Schema(WellLog));
        Hold(WellLog, Schema(WellLog), "PUBLISHED", "INTERNAL");

        var (exit, output, _) = await RunAsync(null, "--dry-run", "--json");

        Assert.Equal(2, exit);
        var json = JsonNode.Parse(output)!;
        Assert.Equal(("dev", false), (json["partition"]!.GetValue<string>(), json["written"]!.GetValue<bool>()));
        var kinds = json["kinds"]!.AsArray();
        Assert.Equal(2, kinds.Count);
        var expected = new (string Kind, string State, string? Proposed)[]
        {
            (Facility10, SchemaRegistrationStates.Unregistered, SchemaRegistrationActions.Register),
            (WellLog, SchemaRegistrationStates.InStep, null),
        };
        Assert.Equal(expected, kinds.Select(k => (k!["kind"]!.GetValue<string>(), k["state"]!.GetValue<string>(), k["proposed"]?.GetValue<string>())));
        Assert.Equal("PUBLISHED", kinds[1]!["status"]!.GetValue<string>());
        Assert.Empty(_platform.SchemaWrites());
    }

    [Fact]
    public async Task A_confirmed_run_registers_in_development_reads_each_back_and_the_next_run_finds_them_in_step()
    {
        var facility = Schema(Facility11, nullDefault: true);
        Write("templates/acme_wks_master-data--Facility_1.1.0.json", facility);

        var (exit, output, notes) = await RunAsync(null, "--confirm", "dev");

        Assert.Equal(0, exit);
        Assert.Empty(notes);
        var text = output.ReplaceLineEndings("\n");
        Assert.Contains($"  OK      1  {Facility11}\n          Registered {Facility11} in development in partition dev.", text, StringComparison.Ordinal);
        Assert.Contains("1 written, 0 failed.", text, StringComparison.Ordinal);

        // One PUT, in development, naming the kind's identity in numbers and carrying the file's schema as an object.
        var write = Assert.Single(_platform.SchemaWrites());
        Assert.Equal("DEVELOPMENT", write["schemaInfo"]!["status"]!.GetValue<string>());
        var identity = write["schemaInfo"]!["schemaIdentity"]!;
        Assert.Equal(
            ("acme", "wks", "master-data--Facility", 1L, 1L, 0L),
            (identity["authority"]!.GetValue<string>(), identity["source"]!.GetValue<string>(), identity["entityType"]!.GetValue<string>(),
                identity["schemaVersionMajor"]!.GetValue<long>(), identity["schemaVersionMinor"]!.GetValue<long>(), identity["schemaVersionPatch"]!.GetValue<long>()));
        Assert.Equal(Facility11, write["schema"]!["x-osdu-schema-source"]!.GetValue<string>());
        Assert.Equal(("DEVELOPMENT", "INTERNAL"), _platform.SchemaInfos[Facility11]);

        // The client signed in at the env file's token endpoint, and every request named the partition, carried the token
        // and the run's correlation id.
        Assert.Single(_platform.Calls, c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath == FakeOsduPlatform.TokenPath);
        var put = Assert.Single(_platform.Calls, c => c.Method == HttpMethod.Put);
        Assert.Equal(("dev", "Bearer fake-platform-token"), (put.Headers["data-partition-id"], put.Headers["Authorization"]));
        Assert.Contains("schemaregister", put.Headers["correlation-id"], StringComparison.Ordinal);

        // The service stores the schema re-serialised (a null member dropped, keys reordered): still the file's schema.
        (exit, output, _) = await RunAsync(null, "--dry-run");
        Assert.Equal(0, exit);
        Assert.Contains("OSDU holds this schema in development: it can still be replaced, or published to make it final.", output, StringComparison.Ordinal);
        Assert.Single(_platform.SchemaWrites());
    }

    [Fact]
    public async Task Publishing_a_new_kind_registers_it_in_development_first_and_takes_the_word_typed_back()
    {
        Write("templates/acme_wks_master-data--Facility_1.1.0.json", Schema(Facility11));

        var (exit, output, _) = await RunAsync("y\npublish\n", "--publish");

        Assert.Equal(0, exit);
        Assert.Contains("Publishing makes a schema final", output, StringComparison.Ordinal);
        Assert.Equal(["DEVELOPMENT", "PUBLISHED"], _platform.SchemaWrites().Select(w => w["schemaInfo"]!["status"]!.GetValue<string>()));
        Assert.Equal(("PUBLISHED", "INTERNAL"), _platform.SchemaInfos[Facility11]);
        Assert.Contains($"Published {Facility11} in partition dev.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_publish_not_typed_back_keeps_the_schema_in_development()
    {
        Write("templates/acme_wks_master-data--Facility_1.1.0.json", Schema(Facility11));

        var (exit, output, _) = await RunAsync("y\nyes\n", "--publish");

        Assert.Equal(0, exit);
        Assert.Contains("Kept in development: nothing is published.", output, StringComparison.Ordinal);
        Assert.Equal(["DEVELOPMENT"], _platform.SchemaWrites().Select(w => w["schemaInfo"]!["status"]!.GetValue<string>()));
        Assert.Equal(("DEVELOPMENT", "INTERNAL"), _platform.SchemaInfos[Facility11]);
    }

    [Fact]
    public async Task Choosing_kind_by_kind_writes_only_what_was_chosen()
    {
        Write("templates/acme_wks_master-data--Facility_1.0.0.json", Schema(Facility10));
        Write("templates/acme_wks_work-product-component--WellLogDetail_1.0.0.json", Schema(WellLog));

        var (exit, output, _) = await RunAsync("c\ns\nr\n");

        Assert.Equal(0, exit);
        Assert.Contains($"  1  {Facility10} (not registered): [r]egister, [p]ublish, [s]kip [register]: s", output, StringComparison.Ordinal);
        var write = Assert.Single(_platform.SchemaWrites());
        Assert.Equal(WellLog, write["schema"]!["x-osdu-schema-source"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_answer_no_input_gives_writes_nothing()
    {
        Write("templates/acme_wks_master-data--Facility_1.0.0.json", Schema(Facility10));

        var (exit, output, _) = await RunAsync(string.Empty);
        Assert.Equal(0, exit);
        Assert.Contains("Nothing was written.", output, StringComparison.Ordinal);

        (exit, output, _) = await RunAsync("c\n");
        Assert.Equal(0, exit);
        Assert.Contains("Nothing was written: the input ended before every kind was answered.", output, StringComparison.Ordinal);
        Assert.Empty(_platform.SchemaWrites());
    }

    [Fact]
    public async Task A_published_schema_that_differs_is_never_written_and_the_run_says_to_give_the_kind_a_new_version()
    {
        Write("templates/acme_wks_work-product-component--WellLogDetail_1.0.0.json", Schema(WellLog, depthType: "string"));
        Hold(WellLog, Schema(WellLog), "PUBLISHED", "INTERNAL");

        var (exit, output, _) = await RunAsync(null, "--confirm", "dev");

        Assert.Equal(2, exit);
        Assert.Contains("published, which never changes", output, StringComparison.Ordinal);
        Assert.Contains("a higher major version, since the Schema service refuses breaking changes within one", output, StringComparison.Ordinal);
        Assert.Contains("Needs you: 1 (differs, published)", output, StringComparison.Ordinal);
        Assert.Empty(_platform.SchemaWrites());
    }

    [Fact]
    public async Task A_kind_the_platform_shares_is_never_written()
    {
        Write("templates/acme_wks_work-product-component--WellLogDetail_1.0.0.json", Schema(WellLog, extra: "Mine"));
        Hold(WellLog, Schema(WellLog), "PUBLISHED", "SHARED");

        var (exit, output, _) = await RunAsync(null, "--confirm", "dev");

        Assert.Equal(2, exit);
        Assert.Contains("The platform's shared schemas hold another schema of", output, StringComparison.Ordinal);
        Assert.Empty(_platform.SchemaWrites());
    }

    [Fact]
    public async Task A_refused_write_says_what_the_service_said_and_what_to_do()
    {
        Write("templates/acme_wks_master-data--Facility_1.0.0.json", Schema(Facility10));
        _platform.SchemaWriteRefusals[Facility10] = (HttpStatusCode.Unauthorized, "User is unauthorized to perform this action");

        var (exit, output, _) = await RunAsync(null, "--confirm", "dev");

        Assert.Equal(1, exit);
        var text = output.ReplaceLineEndings("\n");
        Assert.Contains($"  FAILED  1  {Facility10}\n          OSDU refused it (HTTP 401): User is unauthorized to perform this action.", text, StringComparison.Ordinal);
        Assert.Contains("not in service.schema-service.editors in partition dev", text, StringComparison.Ordinal);
        Assert.Contains("0 written, 1 failed.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_breaking_change_the_service_finds_is_said_in_its_words()
    {
        Write("templates/acme_wks_master-data--Facility_1.1.0.json", Schema(Facility11));
        _platform.SchemaWriteRefusals[Facility11] = (HttpStatusCode.BadRequest,
            "Minor version validation failed. Breaking changes were found; analysed versions 1.1.0 and 1.0.0. Updating the schema version to a higher major version is required.");

        var (exit, output, _) = await RunAsync(null, "--confirm", "dev");

        Assert.Equal(1, exit);
        Assert.Contains("OSDU refused it (HTTP 400): Minor version validation failed.", output, StringComparison.Ordinal);
        Assert.Contains("compares a schema with the nearest versions of its family in the same major version", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_write_whose_answer_was_lost_reads_back_as_done()
    {
        Write("templates/acme_wks_master-data--Facility_1.1.0.json", Schema(Facility11));
        Hold(Facility11, Schema(Facility11), "DEVELOPMENT", "INTERNAL");
        _platform.SchemaWriteLostAnswers.Add(Facility11);

        // A publish the service took, whose answer was lost, is sent again and refused as no longer in development.
        var (exit, output, _) = await RunAsync(null, "--publish", "--confirm", "dev");

        Assert.Equal(0, exit);
        Assert.Equal(2, _platform.SchemaWrites().Count);
        Assert.Contains($"Published {Facility11} in partition dev: the service holds it as asked, though it answered", output, StringComparison.Ordinal);
        Assert.Equal(("PUBLISHED", "INTERNAL"), _platform.SchemaInfos[Facility11]);
    }

    [Fact]
    public async Task Two_files_holding_different_schemas_of_one_kind_are_never_written()
    {
        Write("a/templates/acme_wks_master-data--Facility_1.0.0.json", Schema(Facility10));
        Write("b/templates/acme_wks_master-data--Facility_1.0.0.json", Schema(Facility10, extra: "Other"));

        var (exit, output, _) = await RunAsync(null, "--confirm", "dev");

        Assert.Equal(2, exit);
        Assert.Contains($"the repository holds 2 different schemas of {Facility10}: a/templates/", output, StringComparison.Ordinal);
        Assert.Contains("Needs you: 1 (two schemas in the repository)", output, StringComparison.Ordinal);
        Assert.Empty(_platform.SchemaWrites());
    }

    [Fact]
    public async Task The_env_file_is_the_input_whatever_the_process_environment_holds()
    {
        Write("templates/acme_wks_master-data--Facility_1.0.0.json", Schema(Facility10));
        _environment["OSDU_DATA_PARTITION"] = "prod";
        _environment["OSDU_URL"] = "https://elsewhere.example.test";

        var (exit, output, notes) = await RunAsync(null, "--confirm", "dev");

        Assert.Equal(0, exit);
        Assert.Contains("OSDU_DATA_PARTITION is set to another value in the process environment", notes, StringComparison.Ordinal);
        Assert.Contains("OSDU_URL is set to another value in the process environment", notes, StringComparison.Ordinal);
        Assert.All(_platform.Calls, c => Assert.Equal("localhost", c.Uri.Host));

        // No value is ever written out: not the secret, nor the values the process environment held.
        Assert.DoesNotContain(ClientSecret, output + notes, StringComparison.Ordinal);
        Assert.DoesNotContain("elsewhere.example.test", output + notes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_env_file_named_elsewhere_is_read_and_a_missing_value_is_named()
    {
        Write("templates/acme_wks_master-data--Facility_1.0.0.json", Schema(Facility10));
        File.Delete(Path.Combine(_root, ".sqlflow", "env"));
        var other = Path.Combine(_root, "other.env");
        File.WriteAllText(other, $"OSDU_URL={FakeOsduPlatform.Endpoint}\nOSDU_DATA_PARTITION=dev\nOSDU_CLIENT_ID=c\nOSDU_CLIENT_SECRET=s\nOSDU_TOKEN_URL={FakeOsduPlatform.Endpoint}{FakeOsduPlatform.TokenPath}\n");

        var (exit, _, notes) = await RunAsync(null, "--env-file", other, "--dry-run");

        Assert.Equal(1, exit);
        Assert.Contains($"The connection to OSDU needs OSDU_SCOPE, which {other} or the process environment does not set.", notes, StringComparison.Ordinal);
        Assert.Empty(_platform.Calls);

        (exit, _, notes) = await RunAsync(null, "--env-file", Path.Combine(_root, "absent.env"));
        Assert.Equal(1, exit);
        Assert.Contains("which is not a file", notes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repository_without_a_custom_kind_asks_nothing_of_osdu()
    {
        Write("templates/osdu_wks_master-data--Wellbore_1.3.0.json", Schema("osdu:wks:master-data--Wellbore:1.3.0"));

        var (exit, output, _) = await RunAsync(null, "--dry-run");

        Assert.Equal(0, exit);
        Assert.Contains("is of a custom kind (an authority other than 'osdu'), so there is nothing to register", output, StringComparison.Ordinal);
        Assert.Empty(_platform.Calls);
    }

    [Fact]
    public async Task A_schema_service_the_client_may_not_read_stops_the_run_with_one_message()
    {
        Write("templates/acme_wks_master-data--Facility_1.0.0.json", Schema(Facility10));
        Write("templates/acme_wks_work-product-component--WellLogDetail_1.0.0.json", Schema(WellLog));
        _platform.SchemaListingRefusals["PUBLISHED INTERNAL"] = HttpStatusCode.Forbidden;

        var (exit, _, notes) = await RunAsync(null, "--dry-run");

        Assert.Equal(1, exit);
        Assert.Single(notes.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("OSDU's Schema service could not be read in partition dev", notes, StringComparison.Ordinal);
        Assert.Contains("service.schema-service.viewers in partition dev", notes, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--dry-run --confirm dev", "--dry-run writes nothing and --confirm writes without asking")]
    [InlineData("--json", "--json writes one JSON document and asks nothing")]
    [InlineData("--confirm prod", "--confirm names 'prod', and the schemas would be written in partition dev")]
    [InlineData("--all --kind acme:wks:master-data--Facility:1.0.0", "--all takes every custom kind, and --kind names some")]
    [InlineData("--kind osdu:wks:master-data--Wellbore:1.3.0", "is an OSDU kind")]
    [InlineData("--kind acme:wks:master-data--Facility:9.0.0", "which no template folder under")]
    [InlineData("--kind not-a-kind", "--kind 'not-a-kind' is not a kind")]
    public async Task What_cannot_be_done_as_asked_is_refused_before_osdu_is_asked(string arguments, string message)
    {
        Write("templates/acme_wks_master-data--Facility_1.0.0.json", Schema(Facility10));

        var (exit, _, notes) = await RunAsync(null, arguments.Split(' '));

        Assert.Equal(1, exit);
        Assert.Contains(message, notes, StringComparison.Ordinal);
        Assert.Empty(_platform.Calls);
    }

    [Fact]
    public void Kinds_under_an_authority_other_than_osdu_are_custom_and_versions_order_as_numbers()
    {
        Assert.True(CustomTemplates.IsCustom("acme:wks:master-data--Facility:1.0.0"));
        Assert.False(CustomTemplates.IsCustom("osdu:wks:master-data--Wellbore:1.3.0"));
        Assert.False(CustomTemplates.IsCustom("OSDU:wks:master-data--Wellbore:1.3.0"));
        Assert.False(CustomTemplates.IsCustom("acme:wks:master-data--Facility"));
        Assert.True(KindVersion.Parse("1.10.0") > KindVersion.Parse("1.9.0"));
        Assert.Null(KindVersion.Parse("1.0"));
        Assert.Null(KindVersion.Parse("1.a.0"));
        Assert.Null(KindVersion.Parse("-1.0.0"));
    }

    [Fact]
    public void A_scan_lists_each_custom_kind_once_marks_the_latest_and_says_when_its_files_disagree()
    {
        Write("a/templates/f10.json", Schema(Facility10));
        Write("b/templates/f10-copy.json", Schema(Facility10));
        Write("a/templates/f11.json", Schema(Facility11));
        Write("a/templates/f11-other.json", Schema(Facility11, extra: "Other"));
        Write("a/templates/w.json", Schema(WellLog));
        Write("a/templates/x-upper.json", Schema("ACME:wks:work-product-component--WellLogDetail:1.0.0"));

        var custom = CustomTemplates.Of(TemplateFolders.Scan(_root, CancellationToken.None));

        Assert.Equal([Facility10, Facility11, WellLog], custom.Select(t => t.Kind));
        Assert.Equal([false, true, true], custom.Select(t => t.Latest));
        Assert.Null(custom[0].Conflict);
        Assert.Equal(2, custom[0].Files.Count);
        Assert.Equal("a/templates/f10.json", custom[0].File!.Shown);
        Assert.Contains("holds 2 different schemas", custom[1].Conflict, StringComparison.Ordinal);
        Assert.Null(custom[1].File);
        Assert.Contains("spelled in different cases", custom[2].Conflict, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reference_the_service_would_not_resolve_is_named_before_anything_is_sent()
    {
        var schema = JsonNode.Parse("""
            {
              "properties": {
                "a": { "$ref": "#/definitions/Known.1.0.0" },
                "b": { "$ref": "#/definitions/Known.1.0.0/properties/Name" },
                "c": { "$ref": "osdu:wks:AbstractCommonResources:1.0.0" },
                "d": { "$ref": "https://example.org/schema.json" }
              },
              "definitions": { "Known.1.0.0": { "type": "object" } }
            }
            """)!.AsObject();

        var problems = SchemaRegistrar.ReferenceProblems(schema);

        Assert.Equal(2, problems.Count);
        Assert.Contains("'#/definitions/Known.1.0.0/properties/Name' names 'Name', which is not one of its definitions", problems);
        Assert.Contains("'osdu:wks:AbstractCommonResources:1.0.0' refers to a schema outside the file, which OSDU would have to hold already", problems);
        Assert.Contains("which has no 'definitions'", Assert.Single(SchemaRegistrar.ReferenceProblems(JsonNode.Parse("""{ "$ref": "#/$defs/X" }""")!.AsObject())), StringComparison.Ordinal);
    }

    [Fact]
    public void Two_schemas_that_differ_outside_their_properties_are_told_apart_by_path()
    {
        var before = JsonNode.Parse("""{ "a": 1, "x-osdu-indexing": { "type": "flattened" }, "n": null }""");
        var after = JsonNode.Parse("""{ "x-osdu-indexing": { "type": "nested" }, "a": 1 }""");

        Assert.Equal(["$.x-osdu-indexing.type"], SchemaRegistrar.JsonDifferences(before, after, 5));
        Assert.Empty(SchemaRegistrar.JsonDifferences(JsonNode.Parse("""{ "a": 1, "b": null }"""), JsonNode.Parse("""{ "a": 1 }"""), 5));
    }

    [Fact]
    public void A_refusal_body_is_read_in_the_services_words()
    {
        Assert.Equal("Only schema in developement stage can be updated",
            SchemaRegistrar.ServiceMessage(Encoding.UTF8.GetBytes("""{"error":{"code":400,"message":"Only schema in developement stage can be updated.","errors":[]}}""")));
        Assert.Equal("Schema not found", SchemaRegistrar.ServiceMessage(Encoding.UTF8.GetBytes("""{"code":404,"reason":"Not found","message":"Schema not found"}""")));
        Assert.Equal("Bad gateway", SchemaRegistrar.ServiceMessage(Encoding.UTF8.GetBytes("Bad gateway")));
        Assert.Equal("no reason given", SchemaRegistrar.ServiceMessage([]));
    }

    [Fact]
    public async Task An_env_files_references_resolve_from_the_file_and_a_reference_in_it_from_the_providers()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OSDU_CLIENT_ID"] = "from-file",
            ["OSDU_CLIENT_SECRET"] = "${env:TEMPLATE_REGISTER_TEST_INNER}",
        };
        var inner = new RecordingResolver();
        var resolver = new EnvFileReferences(values, inner);

        Assert.Equal("from-file", resolver.Resolve("${env:OSDU_CLIENT_ID}"));
        Assert.Equal("inner:${env:TEMPLATE_REGISTER_TEST_INNER}", await resolver.ResolveAsync("${env:OSDU_CLIENT_SECRET}"));
        Assert.Equal("inner:${env:OSDU_SCOPE}", resolver.Resolve("${env:OSDU_SCOPE}"));
        Assert.Equal("inner:prefix ${env:OSDU_CLIENT_ID}", resolver.Resolve("prefix ${env:OSDU_CLIENT_ID}"));
    }

    /// <summary>A record schema of <paramref name="kind"/> as a template folder keeps it: bundled, a local reference into its definitions.</summary>
    private static string Schema(string kind, string? extra = null, string depthType = "number", bool nullDefault = false)
    {
        var data = new JsonObject
        {
            ["Name"] = nullDefault ? new JsonObject { ["type"] = "string", ["default"] = null } : new JsonObject { ["type"] = "string" },
            ["Depth"] = new JsonObject { ["type"] = depthType },
        };
        if (extra is not null)
        {
            data[extra] = new JsonObject { ["type"] = "string" };
        }

        return new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["x-osdu-schema-source"] = kind,
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["kind"] = new JsonObject { ["type"] = "string" },
                ["acl"] = new JsonObject { ["$ref"] = "#/definitions/AbstractAccessControlList.1.0.0" },
                ["data"] = new JsonObject { ["type"] = "object", ["properties"] = data },
            },
            ["definitions"] = new JsonObject
            {
                ["AbstractAccessControlList.1.0.0"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["owners"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } },
                },
            },
        }.ToJsonString();
    }

    private void Hold(string kind, string schema, string status, string scope)
    {
        _platform.Schemas[kind] = JsonNode.Parse(schema)!.AsObject();
        _platform.SchemaInfos[kind] = (status, scope);
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void WriteEnv(params (string Name, string Value)[] values)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".sqlflow"));
        File.WriteAllText(Path.Combine(_root, ".sqlflow", "env"), string.Concat(values.Select(v => $"{v.Name}={v.Value}\n")));
    }

    /// <summary>
    /// The verb as the command line runs it, answered by <paramref name="input"/> (null for a command nothing answers), over
    /// the fake platform, from the repository's folder, with the suite's own process environment.
    /// </summary>
    private async Task<(int Exit, string Output, string Notes)> RunAsync(string? input, params string[] arguments)
    {
        var declared = new DeliveryCliModule().Verbs.Single(v => v.Name == "template");
        var stores = Samples.Stores();
        var engine = new EngineContext(
            new DeliveryDocumentLoader(),
            new MemoryIngestionTables(),
            new StoragePayloadFiles(stores),
            stores,
            new SecretResolver([new EnvSecretProvider()]),
            Ledger: null,
            TimeProvider.System,
            NullLoggerFactory.Instance,
            new DefaultProtocolFactory(),
            CompositeDeliveryListener.Empty) with
        { Clients = new TargetClients(TimeProvider.System, _platform, allowLoopback: true) };
        await using var services = new ServiceCollection().AddSingleton(engine).BuildServiceProvider();
        await using var output = new StringWriter();
        await using var notes = new StringWriter();
        using var answers = input is null ? null : new StringReader(input);
        var command = new ModuleCommand(
            services,
            new CliArguments(["template", "register", .. arguments], declared.ValueOptions),
            output,
            notes,
            reason =>
            {
                notes.WriteLine($"USAGE  {reason}");
                return 1;
            },
            CancellationToken.None,
            answers,
            Interactive: false);
        try
        {
            var exit = await DeliveryTemplateRegister.RegisterAsync(command, name => _environment.GetValueOrDefault(name), _root);
            return (exit, output.ToString(), notes.ToString());
        }
        finally
        {
            engine.Clients!.Dispose();
        }
    }

    private sealed class RecordingResolver : ISecretResolver
    {
        public string Resolve(string value) => "inner:" + value;

        public Task<string> ResolveAsync(string value, CancellationToken ct = default) => Task.FromResult("inner:" + value);
    }
}
