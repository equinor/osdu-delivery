using SqlFlow.Delivery.Data;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>How a mapping addresses a template variable: <c>osdu.</c>, the record path, and <c>[]</c> for the step into an array.</summary>
[Trait("Category", "Integration")]
public class TemplatePathTests
{
    [Fact]
    public void Parses_a_plain_path_and_a_repeated_one()
    {
        Assert.True(TemplatePath.TryParse("osdu.data.Name", out var plain, out _));
        Assert.Equal("data.Name", plain!.SchemaPath);
        Assert.False(plain.IsRepeated);
        Assert.Null(plain.Repeater);
        Assert.Equal("data", plain.Root);
        Assert.Equal("osdu.data", plain.Parent!.Text);

        Assert.True(TemplatePath.TryParse(" osdu.data.Curves[].CurveUnit ", out var repeated, out _));
        Assert.Equal("osdu.data.Curves[].CurveUnit", repeated!.Text);
        Assert.Equal("data.Curves.CurveUnit", repeated.SchemaPath);
        Assert.True(repeated.IsRepeated);
        Assert.Equal("osdu.data.Curves", repeated.Repeater!.Text);
        Assert.Equal(["CurveUnit"], repeated.WithinItem);
        Assert.Equal(repeated, TemplatePath.TryParse("osdu.data.Curves[].CurveUnit", out var again, out _) ? again : null);
    }

    [Theory]
    [InlineData(null, "a target is required")]
    [InlineData("data.Name", "must start with 'osdu.'")]
    [InlineData("osdu.data.Curves[]", "names the items of an array")]
    [InlineData("osdu.data.Curves[].Points[].X", "steps into more than one array")]
    [InlineData("osdu.data..Name", "invalid property name")]
    [InlineData("osdu.data.Na me", "invalid property name")]
    public void Refuses_what_is_not_a_template_path(string? text, string reason)
    {
        Assert.False(TemplatePath.TryParse(text, out var path, out var error));
        Assert.Null(path);
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }
}

/// <summary>The template built from an OSDU schema: every property a variable, with what the schema says about it.</summary>
[Trait("Category", "Integration")]
public class OsduTemplateTests
{
    private static OsduTemplate WellLog() => OsduTemplate.From(Samples.SampleTemplate(Samples.WellLogKind));

    private static TemplateVariable Variable(OsduTemplate template, string path)
        => template.Find(TemplatePath.TryParse(path, out var parsed, out _) ? parsed! : throw new ArgumentException(path))
            ?? throw new Xunit.Sdk.XunitException($"the template has no variable {path}");

    [Fact]
    public void Every_property_of_the_well_log_schema_is_a_variable()
    {
        var template = WellLog();
        Assert.Equal(Samples.WellLogKind, template.Kind);
        Assert.Equal("26a3c3441882db4f", template.Version);

        // The record root, the inherited parts of data and WellLog's own properties are all there.
        foreach (var path in new[] { "osdu.acl.owners", "osdu.legal.legaltags", "osdu.data.Name", "osdu.data.ExistenceKind", "osdu.data.Datasets", "osdu.data.LogRun", "osdu.data.Curves[].Mnemonic", "osdu.data.VerticalMeasurement.VerticalMeasurementTypeID" })
        {
            Variable(template, path);
        }

        Assert.Equal(template.Variables.Count, template.Variables.Select(v => v.Path.Text).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Variables_carry_their_shape_type_requiredness_relationships_and_description()
    {
        var template = WellLog();

        var curves = Variable(template, "osdu.data.Curves");
        Assert.Equal(TemplateVariableShape.GroupList, curves.Shape);

        var unit = Variable(template, "osdu.data.Curves[].CurveUnit");
        Assert.Equal(TemplateVariableShape.Value, unit.Shape);
        Assert.Equal("string", unit.Type);
        Assert.Contains("reference-data--UnitOfMeasure", unit.Relationships);

        var wellbore = Variable(template, "osdu.data.WellboreID");
        Assert.Equal(["master-data--Wellbore"], wellbore.Relationships);

        var depth = Variable(template, "osdu.data.TopMeasuredDepth");
        Assert.Equal("number", depth.Type);
        Assert.Equal("UOM:length", depth.UnitContext);

        Assert.StartsWith("Log Run", Variable(template, "osdu.data.LogRun").Description, StringComparison.Ordinal);
        Assert.True(Variable(template, "osdu.acl").Required);
        Assert.True(Variable(template, "osdu.acl.owners").Required);
        Assert.Equal(TemplateVariableShape.ValueList, Variable(template, "osdu.acl.owners").Shape);
        Assert.Equal(TemplateVariableShape.Group, Variable(template, "osdu.data.VerticalMeasurement").Shape);
    }

    [Fact]
    public void The_engine_and_OSDU_own_their_variables_and_free_keys_and_choices_of_shape_are_marked()
    {
        var template = WellLog();
        Assert.Equal(TemplateVariableRole.Engine, Variable(template, "osdu.id").Role);
        Assert.Equal(TemplateVariableRole.Engine, Variable(template, "osdu.kind").Role);
        Assert.Equal(TemplateVariableRole.Osdu, Variable(template, "osdu.version").Role);
        Assert.Equal(TemplateVariableRole.Osdu, Variable(template, "osdu.modifyUser").Role);
        Assert.DoesNotContain(template.Fillable, v => v.Role != TemplateVariableRole.Mapping);

        var tags = Variable(template, "osdu.tags");
        Assert.Equal("string", tags.KeyValueType);
        var tag = Variable(template, "osdu.tags.WellLogNativeUID");
        Assert.Equal(TemplateVariableShape.Value, tag.Shape);
        Assert.Equal("string", tag.Type);

        Assert.Equal(TemplateVariableShape.Whole, Variable(template, "osdu.meta").Shape);

        // A list of objects inside a repeated item is listed, and marked as out of a mapping's reach.
        var reviewers = Variable(template, "osdu.data.TechnicalAssurances[].Reviewers");
        Assert.True(reviewers.Nested);
        Assert.DoesNotContain(template.Fillable, v => v.Path.Text == "osdu.data.TechnicalAssurances[].Reviewers");
    }
}

/// <summary>What the repository sync records of the template a mapping names, whether the mapping loads or not.</summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class MappingTemplateRecordTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_mapping_document_that_fails_to_load_still_records_the_template_it_names()
    {
        var root = Path.Combine(Path.GetTempPath(), "sqlflow-template-pin-" + Guid.NewGuid().ToString("N"));
        var repoId = Guid.NewGuid();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "mappings"));
            // The unknown key fails the strict parse; the template block is still readable.
            await File.WriteAllTextAsync(Path.Combine(root, "mappings", "Thing@1.0.0.yaml"), $"""
                documentType: mapping
                name: Thing
                version: 1.0.0
                template:
                  kind: {TestSchema.Kind}
                  version: {TestSchema.Template.Version}
                notAKey: true
                """);
            // Unreadable YAML names no template at all.
            await File.WriteAllTextAsync(Path.Combine(root, "mappings", "Broken.yaml"), "documentType: mapping\ntemplate: [unclosed\n");

            await SyncAsync(repoId, root);
            await using (var db = _db.CreateDbContext())
            {
                var rows = await db.DeliveryMappings.AsNoTracking().OrderBy(m => m.RelativePath).ToListAsync();
                Assert.Equal(2, rows.Count);
                Assert.All(rows, r => Assert.Equal("invalid", r.Status));
                Assert.Equal(string.Empty, rows[0].TemplateVersion);
                Assert.Equal(TestSchema.Kind, rows[1].Kind);
                Assert.Equal(TestSchema.Template.Version, rows[1].TemplateVersion);
            }

            // A second sync of the unchanged documents keeps what the row records.
            await SyncAsync(repoId, root);
            await using (var db = _db.CreateDbContext())
            {
                var thing = await db.DeliveryMappings.AsNoTracking().SingleAsync(m => m.RelativePath == "mappings/Thing@1.0.0.yaml");
                Assert.Equal(("invalid", TestSchema.Template.Version), (thing.Status, thing.TemplateVersion));
            }

            // Once the document is gone from the repository, so is its row.
            File.Delete(Path.Combine(root, "mappings", "Thing@1.0.0.yaml"));
            await SyncAsync(repoId, root);
            await using (var db = _db.CreateDbContext())
            {
                Assert.DoesNotContain(await db.DeliveryMappings.AsNoTracking().ToListAsync(), m => m.RelativePath == "mappings/Thing@1.0.0.yaml");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private async Task SyncAsync(Guid repoId, string root)
    {
        await using var db = _db.CreateDbContext();
        await new DeliveryCatalogSync(new DeliveryDocumentLoader())
            .ReconcileAsync(db, repoId, root, DateTime.UtcNow, new List<string>(), CancellationToken.None);
    }
}

/// <summary>A bundled schema file, as the import accepts it.</summary>
[Trait("Category", "Integration")]
public class TemplateSourcesTests
{
    private const string Kind = "test:wks:work-product-component--Thing:1.0.0";

    [Fact]
    public async Task A_bundled_schema_keeps_the_order_it_was_written_in()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["master-data/Thing.1.0.0.json"] = """{"title":"Thing","definitions":{"Own":{"type":"string"}},"type":"object","properties":{"data":{"allOf":[{"$ref":"../abstract/Zulu.1.0.0.json"},{"$ref":"../abstract/Alpha.1.0.0.json"}]}}}""",
            ["abstract/Zulu.1.0.0.json"] = """{"$id":"https://example.test/Zulu.1.0.0.json","type":"object","properties":{"Z":{"type":"string"}}}""",
            ["abstract/Alpha.1.0.0.json"] = """{"$id":"https://example.test/Alpha.1.0.0.json","type":"object","properties":{"A":{"type":"string"}}}""",
        };

        var bundled = await SchemaBundler.BundleTreeAsync(
            "master-data/Thing.1.0.0.json", (path, _) => Task.FromResult((JsonObject)JsonNode.Parse(files[path])!));

        // The definitions stay where the schema had them; its own come first, then each file in the order it is referred to.
        Assert.Equal(["title", "definitions", "type", "properties"], bundled.Select(p => p.Key));
        Assert.Equal(["Own", "Zulu.1.0.0", "Alpha.1.0.0"], Assert.IsType<JsonObject>(bundled["definitions"]).Select(d => d.Key));
    }

    [Fact]
    public void The_sample_schemas_are_saved_as_the_versions_the_sample_mappings_pin()
    {
        Assert.Equal("26a3c3441882db4f", Samples.SampleTemplate(Samples.WellLogKind).Version);
        Assert.Equal("58d6bdbd9d066a06", Samples.SampleTemplate(Samples.WellboreKind).Version);
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("""{"type":"object","properties":{"data":{"$ref":"osdu:wks:AbstractCommon:1.0.0"}}}""", "outside itself")]
    [InlineData("""{"type":"object","properties":{"data":{"$ref":"#/definitions/Missing"}},"definitions":{}}""", "does not contain")]
    [InlineData("""{"x-osdu-schema-source":"test:wks:work-product-component--Other:1.0.0","type":"object","properties":{"data":{"type":"object"}}}""", "describes 'test:wks:work-product-component--Other:1.0.0'")]
    [InlineData("""{"type":"object","properties":{"acl":{"type":"object"}}}""", "declares no 'data' property")]
    public void A_bundled_file_that_is_not_a_record_schema_is_refused(string json, string reason)
    {
        var ex = Assert.Throws<DeliveryException>(() => TemplateSources.FromBundledJson(json, Kind, DateTimeOffset.UnixEpoch, "upload"));
        Assert.StartsWith("upload:", ex.Message, StringComparison.Ordinal);
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string> SampleKinds
    {
        get
        {
            var kinds = new TheoryData<string>();
            foreach (var kind in Samples.TemplateKinds())
            {
                kinds.Add(kind);
            }

            return kinds;
        }
    }

    [Theory]
    [MemberData(nameof(SampleKinds))]
    public void A_template_as_a_file_is_the_form_the_samples_keep_and_imports_as_the_same_version(string kind)
    {
        var name = kind.Replace(':', '_') + ".json";
        var file = File.Exists(Path.Combine(Samples.TemplateFiles, name))
            ? Path.Combine(Samples.TemplateFiles, name)
            : Path.Combine(Samples.FixtureTemplateFiles, name);
        var saved = Samples.SampleTemplate(kind);
        var text = TemplateSources.FileText(saved);
        Assert.Equal(name, TemplateSources.FileName(kind));

        // The file keeps the keys in the order the schema was written, so a sample schema comes back as its own file. A
        // checkout may have changed its line endings, and a few fixtures end in a newline the samples leave out; neither
        // says anything about the schema.
        Assert.Equal(File.ReadAllText(file).ReplaceLineEndings("\n").TrimEnd('\n'), text.ReplaceLineEndings("\n"));
        Assert.Equal(saved.Version, TemplateSources.FromBundledJson(text, kind, DateTimeOffset.UnixEpoch, name).Version);
    }

    [Fact]
    public void A_file_whose_keys_are_in_another_order_imports_as_the_same_version()
    {
        // capture --out wrote the canonical form before it wrote this one; a copy a repository kept from then still
        // imports as the version its mappings pin.
        var saved = Samples.SampleTemplate(Samples.WellLogKind);
        var canonical = CanonicalJson.Pretty(saved.Root);
        Assert.NotEqual(TemplateSources.FileText(saved), canonical);
        Assert.Equal(saved.Version, TemplateSources.FromBundledJson(canonical, Samples.WellLogKind, DateTimeOffset.UnixEpoch, "canonical").Version);
    }
}

