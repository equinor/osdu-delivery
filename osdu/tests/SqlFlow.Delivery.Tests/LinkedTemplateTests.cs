using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The link a custom template names its parent by (osdu/docs/reference/concepts/templates.md, Linked templates): read from
/// <c>x-osdu-inheriting-from-kind</c> as the OSDU data definitions write it, the one record kind there being the parent, the
/// abstract schemas a kind is built from left alone, and every link that cannot be read said so rather than guessed.
/// </summary>
public class LinkedTemplateTests
{
    private static JsonObject Schema(JsonNode? link)
    {
        var schema = new JsonObject { ["x-osdu-schema-source"] = LinkedTemplates.CustomKind };
        if (link is not null)
        {
            schema[TemplateParent.Key] = link;
        }

        return schema;
    }

    [Fact]
    public void A_template_without_the_key_is_linked_to_no_parent()
    {
        Assert.Null(TemplateParent.Read(Schema(null), out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void The_abstract_schemas_a_kind_is_built_from_are_no_parent()
    {
        // As the data definitions list them for a dataset: no group in the entity type, so no record is named.
        var parent = TemplateParent.Read(Schema(new JsonArray(LinkedTemplates.Link("Dataset", "osdu:wks:AbstractDataset:1.0.0"))), out var problem);

        Assert.Null(parent);
        Assert.Null(problem);
        Assert.Null(TemplateParent.Read(Schema(new JsonArray()), out _));
    }

    [Fact]
    public void The_one_record_kind_listed_is_the_parent_with_its_name_and_its_entity_type()
    {
        var parent = TemplateParent.Read(
            Schema(new JsonArray(LinkedTemplates.Link("Dataset", "osdu:wks:AbstractDataset:1.0.0"), LinkedTemplates.Link(" WellLog ", " osdu:wks:work-product-component--WellLog:1.4.0 "))),
            out var problem);

        Assert.Null(problem);
        Assert.Equal(("WellLog", "osdu:wks:work-product-component--WellLog:1.4.0", "work-product-component--WellLog"), (parent!.Name, parent.Kind, parent.EntityType));
    }

    [Fact]
    public void A_parent_written_without_a_name_is_named_by_its_entity_type()
    {
        var parent = TemplateParent.Read(Schema(new JsonArray(LinkedTemplates.Link(null, "osdu:wks:work-product-component--WellLog:1.4.0"))), out _);

        Assert.Equal("work-product-component--WellLog", parent!.Name);
    }

    [Fact]
    public void Two_record_kinds_are_no_parent_and_the_link_says_so()
    {
        var parent = TemplateParent.Read(
            Schema(new JsonArray(
                LinkedTemplates.Link("WellLog", "osdu:wks:work-product-component--WellLog:1.4.0"),
                LinkedTemplates.Link("Trajectory", "osdu:wks:work-product-component--WellboreTrajectory:1.3.0"))),
            out var problem);

        Assert.Null(parent);
        Assert.Equal(
            "x-osdu-inheriting-from-kind names 2 record kinds (osdu:wks:work-product-component--WellLog:1.4.0, osdu:wks:work-product-component--WellboreTrajectory:1.3.0), and a template is linked to one parent",
            problem);
    }

    public static TheoryData<string, string> UnreadableLinks => new()
    {
        { "\"osdu:wks:work-product-component--WellLog:1.4.0\"", "x-osdu-inheriting-from-kind is the value \"osdu:wks:work-product-component--WellLog:1.4.0\", and it lists the kinds a schema is linked to" },
        { "{ \"kind\": \"osdu:wks:work-product-component--WellLog:1.4.0\" }", "x-osdu-inheriting-from-kind is an object without a kind, and it lists the kinds a schema is linked to" },
        { "[ \"osdu:wks:work-product-component--WellLog:1.4.0\" ]", "x-osdu-inheriting-from-kind lists the value \"osdu:wks:work-product-component--WellLog:1.4.0\", and each entry is an object naming a kind" },
        { "[ { \"name\": \"WellLog\" } ]", "x-osdu-inheriting-from-kind lists an object without a kind, and each entry is an object naming a kind" },
        { "[ { \"kind\": 14 } ]", "x-osdu-inheriting-from-kind lists an object without a kind, and each entry is an object naming a kind" },
        { "[ { \"kind\": \"WellLog 1.4\" } ]", "x-osdu-inheriting-from-kind lists 'WellLog 1.4', which is not a kind: 'authority:source:entityType:major.minor.patch'" },
    };

    [Theory]
    [MemberData(nameof(UnreadableLinks))]
    public void A_link_that_cannot_be_read_names_no_parent_and_says_why(string link, string said)
    {
        Assert.Null(TemplateParent.Read(Schema(JsonNode.Parse(link)), out var problem));
        Assert.StartsWith(said, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_template_is_read_for_its_parent_and_an_ordinary_one_has_none()
    {
        var custom = LinkedTemplates.Custom();
        var parent = LinkedTemplates.Parent();

        Assert.Equal(LinkedTemplates.ParentKind, custom.Parent!.Kind);
        Assert.Null(custom.ParentProblem);
        Assert.Null(parent.Parent);
        Assert.Null(parent.ParentProblem);

        // The link is part of the schema, so a template linked elsewhere is another version.
        Assert.NotEqual(custom.Version, LinkedTemplates.Custom(LinkedTemplates.Link("Thing", "test:wks:work-product-component--Thing:2.0.0")).Version);
    }

    [Fact]
    public void A_template_folder_names_the_parent_each_file_is_linked_to()
    {
        var root = Samples.NewTempDirectory();
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "welldb", TemplateFolders.FolderName)).FullName;
            File.WriteAllText(Path.Combine(folder, TemplateSources.FileName(LinkedTemplates.ParentKind)), TemplateSources.FileText(LinkedTemplates.Parent()));
            File.WriteAllText(Path.Combine(folder, TemplateSources.FileName(LinkedTemplates.CustomKind)), TemplateSources.FileText(LinkedTemplates.Custom()));
            var twoParents = LinkedTemplates.Custom(
                "test:wks:work-product-component--OtherThing:1.0.0",
                LinkedTemplates.Link("Thing", LinkedTemplates.ParentKind),
                LinkedTemplates.Link("Log", "osdu:wks:work-product-component--WellLog:1.4.0"));
            File.WriteAllText(Path.Combine(folder, "other.json"), TemplateSources.FileText(twoParents));

            var files = TemplateFolders.Scan(root, CancellationToken.None).Folders.Single().Files.ToDictionary(f => f.Kind!, StringComparer.Ordinal);

            Assert.Equal(LinkedTemplates.ParentKind, files[LinkedTemplates.CustomKind].Parent);
            Assert.Null(files[LinkedTemplates.ParentKind].Parent);

            // A link that cannot be read leaves the file a template of no parent: a mapping reading its parent is refused.
            Assert.True(files["test:wks:work-product-component--OtherThing:1.0.0"].IsTemplate);
            Assert.Null(files["test:wks:work-product-component--OtherThing:1.0.0"].Parent);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
