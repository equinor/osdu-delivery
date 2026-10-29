using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Expressions;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The key census files the editor documents and checks the module's documents with (<c>osdu/docs/census</c>): every key
/// a loader accepts is in its census, and the census documents no key a loader would refuse. The flow kinds are compared
/// with the YAML models their strict loaders read; the mapping's modifiers and the dictionary document, which their
/// mappers parse by hand, with the names those mappers accept.
/// </summary>
public sealed class EditorCensusTests
{
    /// <summary>The census files, as the build copies them beside the tests.</summary>
    private static string CensusDirectory => Path.Combine(AppContext.BaseDirectory, "census");

    /// <summary>The platform envelope: the host reads it from every flow, and the editor documents it as SQLFlow does.</summary>
    private static readonly string[] Envelope = ["schedule", "mode", "lifecycle"];

    private static JsonObject Census(string file)
    {
        var path = Path.Combine(CensusDirectory, file);
        Assert.True(File.Exists(path), $"The census file {file} is missing from {CensusDirectory}.");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static List<JsonObject> Keys(JsonObject census) => census["keys"]!.AsArray().Select(k => k!.AsObject()).ToList();

    private static SortedSet<string> Paths(JsonObject census) => new(Keys(census).Select(k => k["path"]!.GetValue<string>()), StringComparer.Ordinal);

    [Theory]
    [InlineData("keys.delivery.json", "delivery")]
    [InlineData("keys.cache.json", "cache")]
    [InlineData("keys.retrieval.json", "retrieval")]
    [InlineData("keys.assertion.json", "assertion")]
    [InlineData("keys.dimension.json", "dimension")]
    [InlineData("keys.inventory.json", "inventory")]
    [InlineData("keys.removal.json", "removal")]
    [InlineData("keys.download.json", "download")]
    [InlineData("keys.mapping.json", "mapping")]
    [InlineData("keys.dictionary.json", "dictionary")]
    [InlineData("keys.repository.json", "repository")]
    public void A_census_file_names_its_kind_and_documents_every_key_it_lists(string file, string kind)
    {
        var census = Census(file);
        var named = census["flowType"]?.GetValue<string>() ?? census["documentType"]?.GetValue<string>();
        Assert.Equal(kind, named);
        Assert.False(census.ContainsKey("flowType") && census.ContainsKey("documentType"), $"{file} names both a flowType and a documentType.");
        Assert.True(census["strictKeys"]?.GetValue<bool>() ?? false, $"{file}: every loader of the module refuses an unknown key, so its census says so.");

        var keys = Keys(census);
        Assert.NotEmpty(keys);
        Assert.Equal(keys.Count, census["keyCount"]?.GetValue<int>());
        foreach (var key in keys)
        {
            var path = key["path"]?.GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(path), $"{file} has an entry without a path.");
            Assert.False(string.IsNullOrWhiteSpace(key["type"]?.GetValue<string>()), $"{file}: {path} has no type.");
            Assert.False(string.IsNullOrWhiteSpace(key["description"]?.GetValue<string>()), $"{file}: {path} has no description.");

            // The language engine reads these as text. A number or a boolean there fails the parse of the whole file,
            // and the kind is then neither documented nor checked in the editor, the language server or the MCP server.
            foreach (var textual in new[] { "default", "appliesWhen", "definedIn", "validation" })
            {
                Assert.True(
                    key[textual] is null || key[textual]!.GetValueKind() == System.Text.Json.JsonValueKind.String,
                    $"{file}: {path} writes its {textual} as a JSON {key[textual]?.GetValueKind()}; write it as text (\"false\", \"1000\").");
            }

            Assert.True(
                key["enumValues"] is null || key["enumValues"]!.AsArray().All(v => v?.GetValueKind() == System.Text.Json.JsonValueKind.String),
                $"{file}: {path} lists an enum value that is not text.");
        }

        Assert.Equal(keys.Count, Paths(census).Count);
        var text = File.ReadAllText(Path.Combine(CensusDirectory, file));
        // The project's writing style forbids the em dash in any text it ships, and an en dash standing in for one.
        Assert.DoesNotContain('\u2014', text);
        Assert.DoesNotContain('\u2013', text);
    }

    [Theory]
    [InlineData("keys.delivery.json", typeof(FlowYaml))]
    [InlineData("keys.cache.json", typeof(CacheYaml))]
    [InlineData("keys.retrieval.json", typeof(RetrievalYaml))]
    [InlineData("keys.assertion.json", typeof(AssertionYaml))]
    [InlineData("keys.dimension.json", typeof(DimensionFlowYaml))]
    [InlineData("keys.inventory.json", typeof(InventoryFlowYaml))]
    [InlineData("keys.removal.json", typeof(RemovalFlowYaml))]
    [InlineData("keys.download.json", typeof(DownloadFlowYaml))]
    public void A_flow_kinds_census_documents_exactly_the_keys_its_loader_accepts(string file, Type model)
    {
        var accepted = YamlKeyPaths.Of(model);
        var census = Census(file);
        Assert.True(census["includeEnvelope"]?.GetValue<bool>() ?? false, $"{file}: the platform envelope belongs to every flow kind.");

        var expected = accepted.Paths.Keys.Where(p => !Envelope.Contains(p.Split('.')[0])).ToHashSet(StringComparer.Ordinal);
        var documented = Paths(census);
        var extras = documented.Where(p => !expected.Contains(p) && !UnderFreeForm(p, accepted)).ToList();
        Assert.True(extras.Count == 0, $"{file} documents keys the loader refuses: {string.Join(", ", extras)}");
        var missing = expected.Where(p => !documented.Contains(p)).ToList();
        Assert.True(missing.Count == 0, $"{file} leaves out keys the loader accepts: {string.Join(", ", missing)}");

        // What the author writes below a free-form value is either free, or documented key by key.
        foreach (var key in Keys(census).Where(k => k["freeForm"]?.GetValue<bool>() ?? false))
        {
            Assert.Contains(key["path"]!.GetValue<string>(), accepted.FreeForm);
        }
    }

    [Fact]
    public void The_repository_census_documents_exactly_the_keys_its_loader_accepts()
    {
        var accepted = YamlKeyPaths.Of(typeof(RepositoryYaml)).Paths.Keys.ToHashSet(StringComparer.Ordinal);
        Assert.Equal(accepted, Paths(Census("keys.repository.json")).ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void The_mapping_census_documents_the_mapping_its_loader_reads_and_every_modifier_setting()
    {
        var accepted = YamlKeyPaths.Of(typeof(MappingYaml));
        var documented = Paths(Census("keys.mapping.json"));

        var missing = accepted.Paths.Keys.Where(p => !documented.Contains(p)).ToList();
        Assert.True(missing.Count == 0, $"keys.mapping.json leaves out keys the loader accepts: {string.Join(", ", missing)}");

        // The record is a tree the loader reads node by node, so the census documents it as free-form, names the record's
        // own blocks for completion, and nothing else the loader would refuse.
        Assert.Contains("record.<name>", accepted.FreeForm);
        string[] blocks = ["record.acl", "record.legal", "record.tags", "record.data"];
        var extras = documented.Where(p => !accepted.Paths.ContainsKey(p) && !blocks.Contains(p)).ToList();
        Assert.True(extras.Count == 0, $"keys.mapping.json documents keys the loader refuses: {string.Join(", ", extras)}");
        Assert.All(blocks, block => Assert.Contains(block, documented));

        // A node's grammar is what MappingMapper parses by hand, so the free-form entry names every word of the mapping
        // language, every modifier and every setting a modifier takes, and the editor's hover says all of it.
        var node = Keys(Census("keys.mapping.json")).Single(k => k["path"]!.GetValue<string>() == "record.<name>");
        Assert.True(node["freeForm"]?.GetValue<bool>() ?? false, "record.<name> takes any node below it.");
        var grammar = node["description"]!.GetValue<string>();
        var words = MappingMapper.NodeKeys
            .Concat(MappingMapper.ModifierNames)
            .Concat(MappingMapper.SplitSettings)
            .Concat(MappingMapper.NumberSettings)
            .Concat(MappingMapper.ReplaceSettings)
            .Append(MappingMapper.DatasetReference)
            .Append(MappingMapper.CacheReference)
            .Append("{$param.<name>}")
            .Append("{$value}")
            .Append("$$<name>")
            .Append("$param.<name>")
            .Append("iif(condition, value, other)")
            .Concat(ExpressionFunctions.All.Select(f => f.Signature))
            // An assertion's words: its conditions are the assertion flows' own, without resolves, and its settings.
            .Concat(ConditionReader.Operators.Where(o => o != "resolves"))
            .Concat(MappingMapper.AssertionSettings)
            .Concat(MappingMapper.AssertionFilterKeys)
            .Concat(MappingMapper.AssertionStages)
            .Concat(MappingMapper.AssertionActions)
            .Concat(MappingMapper.AssertionQuantities);
        var unnamed = words.Where(word => !grammar.Contains(word, StringComparison.Ordinal)).ToList();
        Assert.True(unnamed.Count == 0, $"record.<name> does not describe: {string.Join(", ", unnamed)}");
    }

    [Fact]
    public void The_dimension_census_documents_every_clean_step_and_every_setting_a_step_takes()
    {
        var census = Keys(Census("keys.dimension.json"));
        var step = census.Single(k => k["path"]!.GetValue<string>() == "dimensions[].clean[]");
        Assert.True(step["freeForm"]?.GetValue<bool>() ?? false, "a clean step is a name or a one-key map the mapper reads by hand.");
        var described = step["description"]!.GetValue<string>();
        var unnamed = DimensionMapper.StepNames.Where(name => !described.Contains($"`{name}`", StringComparison.Ordinal)).ToList();
        Assert.True(unnamed.Count == 0, $"dimensions[].clean[] does not describe: {string.Join(", ", unnamed)}");

        var documented = Paths(Census("keys.dimension.json"));
        Assert.All(DimensionMapper.ReplaceSettings, setting => Assert.Contains($"dimensions[].clean[].replace.{setting}", documented));
        Assert.All(DimensionMapper.MapSettings, setting => Assert.Contains($"dimensions[].clean[].map.{setting}", documented));
    }

    [Fact]
    public void The_dictionary_census_documents_the_keys_its_mapper_reads()
    {
        var documented = Paths(Census("keys.dictionary.json"));
        var topLevel = documented.Where(p => !p.Contains('.', StringComparison.Ordinal) && !p.Contains('[', StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(DictionaryMapper.Keys.ToHashSet(StringComparer.Ordinal), topLevel);
        Assert.Equal(
            ["documentType", "name", "description", "key", "fields", "fields[]", "entries", "entries.<key>", "entries.<key>.<field>"],
            Keys(Census("keys.dictionary.json")).Select(k => k["path"]!.GetValue<string>()));
    }

    /// <summary>Whether <paramref name="path"/> lies below a value the loader reads as a plain object of the author's own shape.</summary>
    private static bool UnderFreeForm(string path, YamlKeyPaths accepted)
        => accepted.FreeForm.Any(free => path.StartsWith(free + ".", StringComparison.Ordinal) || path.StartsWith(free + "[", StringComparison.Ordinal));
}
