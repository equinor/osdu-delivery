using SqlFlow.Core;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Templates;
using SqlFlow.Yaml;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Loads the module's documents: delivery, retrieval, cache, assertion, dimension, inventory and removal flows (behind the platform's
/// envelope probe, which dispatches on flowType), and the mappings and dictionaries those flows name. Unknown keys are a hard
/// parse error (osdu/docs/reference/flow/overview.md, Unknown and repeated keys are errors), and so is a literal secret
/// (<see cref="DocumentSecrets"/>); every failure is a <see cref="FlowValidationException"/> prefixed with the file
/// path.
/// </summary>
public sealed class DeliveryDocumentLoader
{
    private readonly IDeserializer _probe = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    // Strict: no IgnoreUnmatchedProperties. A misspelled key fails here rather than being silently ignored, and a key written
    // twice in one map fails rather than the second silently replacing the first (a record property laid out twice in a
    // mapping, a setting given twice in a flow).
    private readonly IDeserializer _strict = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        // Where a document holds a value of any shape (a literal in a mapping's record, a modifier's settings), an unquoted
        // scalar keeps the type YAML reads it as, so Count: 5 is a number and Count: "5" is text.
        .WithAttemptingUnquotedStringTypeDeserialization()
        .WithExactDecimals()
        .WithDuplicateKeyChecking()
        // A run value keeps the text its scalar is written with, and a sequence of them is a list (RunValueYaml).
        .WithTypeConverter(new RunValueYamlConverter())
        .Build();

    /// <summary>Loads a delivery flow document as a source: every interface it declares, or the one its single form is.</summary>
    public SourceDefinition LoadSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Flow file not found: '{path}'.");
        }

        return ParseSource(File.ReadAllText(path), path);
    }

    /// <summary>Loads a delivery flow that delivers one interface: a document in the single form, or one declaring one interface.</summary>
    public FlowDefinition LoadFlow(string path) => LoadFlow(path, null);

    /// <summary>Loads one interface of a delivery flow document; a null name takes the document's only one.</summary>
    public FlowDefinition LoadFlow(string path, string? interfaceName) => OneInterface(LoadSource(path), interfaceName, path);

    public RetrievalDefinition LoadRetrieval(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Flow file not found: '{path}'.");
        }

        return ParseRetrieval(File.ReadAllText(path), path);
    }

    public CacheDefinition LoadCache(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Cache flow file not found: '{path}'.");
        }

        return ParseCache(File.ReadAllText(path), path);
    }

    public AssertionFlowDefinition LoadAssertion(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Assertion flow file not found: '{path}'.");
        }

        return ParseAssertion(File.ReadAllText(path), path);
    }

    public DimensionFlowDefinition LoadDimension(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Dimension flow file not found: '{path}'.");
        }

        return ParseDimension(File.ReadAllText(path), path);
    }

    public MappingDefinition LoadMapping(string path) => LoadMapping(path, path);

    /// <summary>Loads the mapping file at <paramref name="path"/>, naming it <paramref name="source"/> in every message.</summary>
    public MappingDefinition LoadMapping(string path, string source) => LoadMapping(path, source, FolderTemplates.Default);

    /// <summary>
    /// Loads the mapping file at <paramref name="path"/>, naming it <paramref name="source"/> in every message. A template or
    /// a search schema it names by kind alone is settled from the template folders on the file's path, read by
    /// <paramref name="templates"/>; a version it names is taken as written, and checked when its template is read for the
    /// render (osdu/docs/reference/concepts/templates.md, Template folders).
    /// </summary>
    public MappingDefinition LoadMapping(string path, string source, FolderTemplates templates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(templates);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Mapping file not found: '{source}'.");
        }

        var full = Path.GetFullPath(path);
        return ParseMapping(File.ReadAllText(full), source, templates.ForKindsAlone(full), full);
    }

    /// <summary>The flow types the loader reads, each the <c>flowType</c> of one of its documents, in the order a message lists them.</summary>
    public static IReadOnlyList<string> FlowTypes { get; } =
    [
        FlowDefinition.FlowTypeName,
        RetrievalDefinition.FlowTypeName,
        CacheDefinition.FlowTypeName,
        AssertionFlowDefinition.FlowTypeName,
        DimensionFlowDefinition.FlowTypeName,
        InventoryFlowDefinition.FlowTypeName,
        RemovalFlowDefinition.FlowTypeName,
        DownloadFlowDefinition.FlowTypeName,
    ];

    /// <summary>The discriminator of a document: one of <see cref="FlowTypes"/> for a flow, "mapping", "dictionary" or "repository" for a document.</summary>
    public string Probe(string yaml, string source = "<inline>")
    {
        var probe = Deserialize<DocumentProbeYaml>(_probe, yaml, source);
        if (!string.IsNullOrWhiteSpace(probe?.FlowType))
        {
            return probe!.FlowType!;
        }

        if (!string.IsNullOrWhiteSpace(probe?.DocumentType))
        {
            return probe!.DocumentType!;
        }

        throw new FlowValidationException($"{source}: the document declares no 'flowType' ({string.Join(", ", FlowTypes)}) and no 'documentType' ({MappingDefinition.DocumentTypeName}, {DictionaryDefinition.DocumentTypeName}, {RepositoryDefinition.DocumentTypeName}).");
    }

    /// <summary>Parses a delivery flow document as a source: every interface it declares, or the one its single form is.</summary>
    public SourceDefinition ParseSource(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var kind = Probe(yaml, source);
        if (!kind.Equals(FlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException($"{source}: expected 'flowType: {FlowDefinition.FlowTypeName}', found '{kind}'.");
        }

        var y = Deserialize<FlowYaml>(_strict, yaml, source) ?? throw new FlowValidationException($"{source}: the document is empty.");
        DocumentSecrets.Check(y, source);
        return FlowMapper.Map(y, source);
    }

    /// <summary>Parses a delivery flow that delivers one interface: a document in the single form, or one declaring one interface.</summary>
    public FlowDefinition ParseFlow(string yaml, string source = "<inline>") => ParseFlow(yaml, source, null);

    /// <summary>Parses one interface of a delivery flow document; a null name takes the document's only one.</summary>
    public FlowDefinition ParseFlow(string yaml, string source, string? interfaceName)
        => OneInterface(ParseSource(yaml, source), interfaceName, source);

    private static FlowDefinition OneInterface(SourceDefinition parsed, string? interfaceName, string source)
    {
        try
        {
            return parsed.Interface(interfaceName);
        }
        catch (DeliveryException ex)
        {
            throw new FlowValidationException($"{source}: {ex.Message}", ex);
        }
    }

    public RetrievalDefinition ParseRetrieval(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var kind = Probe(yaml, source);
        if (!kind.Equals(RetrievalDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException($"{source}: expected 'flowType: {RetrievalDefinition.FlowTypeName}', found '{kind}'.");
        }

        var y = Deserialize<RetrievalYaml>(_strict, yaml, source) ?? throw new FlowValidationException($"{source}: the document is empty.");
        DocumentSecrets.Check(y, source);
        return RetrievalMapper.Map(y, source);
    }

    public CacheDefinition ParseCache(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var kind = Probe(yaml, source);
        if (!kind.Equals(CacheDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException($"{source}: expected 'flowType: {CacheDefinition.FlowTypeName}', found '{kind}'.");
        }

        var y = Deserialize<CacheYaml>(_strict, yaml, source) ?? throw new FlowValidationException($"{source}: the document is empty.");
        DocumentSecrets.Check(y, source);
        return CacheMapper.Map(y, source);
    }

    /// <summary>Parses an assertion flow document (<c>flowType: assertion</c>): the tests of what an OSDU partition holds.</summary>
    public AssertionFlowDefinition ParseAssertion(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var kind = Probe(yaml, source);
        if (!kind.Equals(AssertionFlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException($"{source}: expected 'flowType: {AssertionFlowDefinition.FlowTypeName}', found '{kind}'.");
        }

        var y = Deserialize<AssertionYaml>(_strict, yaml, source) ?? throw new FlowValidationException($"{source}: the document is empty.");
        DocumentSecrets.Check(y, source);
        return AssertionMapper.Map(y, source);
    }

    /// <summary>Parses a dimension flow document (<c>flowType: dimension</c>): the distinct values of OSDU attributes, cleaned into members.</summary>
    public DimensionFlowDefinition ParseDimension(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var kind = Probe(yaml, source);
        if (!kind.Equals(DimensionFlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException($"{source}: expected 'flowType: {DimensionFlowDefinition.FlowTypeName}', found '{kind}'.");
        }

        var y = Deserialize<DimensionFlowYaml>(_strict, yaml, source) ?? throw new FlowValidationException($"{source}: the document is empty.");
        DocumentSecrets.Check(y, source);
        return DimensionMapper.Map(y, source);
    }

    /// <summary>Parses an inventory flow document (<c>flowType: inventory</c>): every id OSDU kinds hold, compared with the ledgers of their partition.</summary>
    public InventoryFlowDefinition ParseInventory(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var kind = Probe(yaml, source);
        if (!kind.Equals(InventoryFlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException($"{source}: expected 'flowType: {InventoryFlowDefinition.FlowTypeName}', found '{kind}'.");
        }

        var y = Deserialize<InventoryFlowYaml>(_strict, yaml, source) ?? throw new FlowValidationException($"{source}: the document is empty.");
        DocumentSecrets.Check(y, source);
        return InventoryMapper.Map(y, source);
    }

    public InventoryFlowDefinition LoadInventory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Inventory flow file not found: '{path}'.");
        }

        return ParseInventory(File.ReadAllText(path), path);
    }

    /// <summary>Parses a removal flow (<c>flowType: removal</c>): the keys a source holds, compared with delivery flows' ledgers.</summary>
    public RemovalFlowDefinition ParseRemoval(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var kind = Probe(yaml, source);
        if (!kind.Equals(RemovalFlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException($"{source}: expected 'flowType: {RemovalFlowDefinition.FlowTypeName}', found '{kind}'.");
        }

        var y = Deserialize<RemovalFlowYaml>(_strict, yaml, source) ?? throw new FlowValidationException($"{source}: the document is empty.");
        DocumentSecrets.Check(y, source);
        return RemovalMapper.Map(y, source);
    }

    public RemovalFlowDefinition LoadRemoval(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Removal flow file not found: '{path}'.");
        }

        return ParseRemoval(File.ReadAllText(path), path);
    }

    /// <summary>Parses a download flow document (<c>flowType: download</c>): the records of OSDU kinds, written to a folder a file each.</summary>
    public DownloadFlowDefinition ParseDownload(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var kind = Probe(yaml, source);
        if (!kind.Equals(DownloadFlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException($"{source}: expected 'flowType: {DownloadFlowDefinition.FlowTypeName}', found '{kind}'.");
        }

        var y = Deserialize<DownloadFlowYaml>(_strict, yaml, source) ?? throw new FlowValidationException($"{source}: the document is empty.");
        DocumentSecrets.Check(y, source);
        return DownloadMapper.Map(y, source);
    }

    public DownloadFlowDefinition LoadDownload(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Download flow file not found: '{path}'.");
        }

        return ParseDownload(File.ReadAllText(path), path);
    }

    /// <summary>Loads the dictionary file at <paramref name="path"/>, naming it <paramref name="source"/> in every message.</summary>
    public DictionaryDefinition LoadDictionary(string path, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Dictionary file not found: '{source}'.");
        }

        return ParseDictionary(File.ReadAllText(path), source);
    }

    /// <summary>Parses a dictionary document (<c>documentType: dictionary</c>): one lookup table a cache flow holds.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "An instance member beside the loader's other Parse methods: every document type is read through the one loader instance a host registers.")]
    public DictionaryDefinition ParseDictionary(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);

        // The dictionary reader checks documentType itself, node by node, so a malformed file is refused with the line
        // it is wrong on rather than by the typed probe every other document goes through.
        return DictionaryMapper.Map(yaml, source);
    }

    /// <summary>Loads the repository document at <paramref name="path"/>, naming it <paramref name="source"/> in every message.</summary>
    public RepositoryDefinition LoadRepository(string path, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Repository document not found: '{source}'.");
        }

        return ParseRepository(File.ReadAllText(path), source);
    }

    /// <summary>
    /// Parses a repository document (<c>documentType: repository</c>, osdu/docs/reference/flow/repository.md): the code the runs
    /// of the flows under it carry in their correlation id. A file is found by its name, so one named otherwise is refused.
    /// </summary>
    public RepositoryDefinition ParseRepository(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var kind = Probe(yaml, source);
        if (!kind.Equals(RepositoryDefinition.DocumentTypeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException($"{source}: expected 'documentType: {RepositoryDefinition.DocumentTypeName}', found '{kind}'.");
        }

        if (source != "<inline>" && !RepositoryDefinition.IsFileName(Path.GetFileName(source)))
        {
            throw new FlowValidationException(
                $"{source}: a repository document is found by its name, walking up from each flow's folder; name the file {RepositoryDefinition.FileName}.");
        }

        var y = Deserialize<RepositoryYaml>(_strict, yaml, source) ?? throw new FlowValidationException($"{source}: the document is empty.");
        var code = y.Code ?? throw new FlowValidationException($"{source}: code is required: the repository's code, which every run of its flows carries in its correlation id.");
        if (!OsduCorrelation.IsCode(code))
        {
            throw new FlowValidationException($"{source}: code '{code}' is not a repository code: {OsduCorrelation.CodeRule}.");
        }

        var team = string.IsNullOrWhiteSpace(y.Team) ? null : y.Team.Trim();
        if (team is not null && (team.Length > RepositoryDefinition.MaxTeamLength || team.Any(char.IsControl)))
        {
            throw new FlowValidationException(
                $"{source}: team '{team}' is not a team name: it is one line of at most {RepositoryDefinition.MaxTeamLength} characters, the name compute usage is summed under.");
        }

        return new RepositoryDefinition { Code = code, Description = string.IsNullOrWhiteSpace(y.Description) ? null : y.Description.Trim(), Team = team };
    }

    public MappingDefinition ParseMapping(string yaml, string source = "<inline>") => ParseMapping(yaml, source, pins: null, location: null);

    /// <summary>
    /// Reads a mapping document, settling the templates it names with <paramref name="pins"/>: the template folders its
    /// repository holds on its path. Without pins, a template has to be named with its version. <paramref name="location"/>
    /// is the file it was read from, when it was read from disk.
    /// </summary>
    public MappingDefinition ParseMapping(string yaml, string source, ITemplatePins? pins, string? location = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var kind = Probe(yaml, source);
        if (!kind.Equals(MappingDefinition.DocumentTypeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException($"{source}: expected 'documentType: {MappingDefinition.DocumentTypeName}', found '{kind}'.");
        }

        RefuseFixtures(yaml, source);
        var y = Deserialize<MappingYaml>(_strict, yaml, source) ?? throw new FlowValidationException($"{source}: the document is empty.");

        // A modifier's text written as a number YAML would read back otherwise (01, 1.0) is refused before the mapping is
        // read, rather than delivered as the number's text.
        ModifierTexts.Refuse(yaml, Deserialize<object>(_strict, yaml, source), source);
        return MappingMapper.Map(y, source, pins) with { Fingerprint = MappingFingerprint.Of(yaml, source), Location = location };
    }

    /// <summary>
    /// Refuses a mapping written when mappings carried example rows and the records they had to render to. It is told to
    /// delete those blocks rather than given the parser's unknown key, which would leave the author looking for a new name.
    /// </summary>
    private void RefuseFixtures(string yaml, string source)
    {
        var keys = Deserialize<Dictionary<string, object?>>(_probe, yaml, source);
        var present = FixtureKeys.Where(key => keys?.ContainsKey(key) == true).ToList();
        if (present.Count > 0)
        {
            throw new FlowValidationException(
                $"{source}: a mapping holds no fixtures; delete its {string.Join(" and ", present)} block{(present.Count > 1 ? "s" : string.Empty)}. Every record a run renders is checked against the template, and a plan run shows which records a change would deliver again.");
        }
    }

    /// <summary>The top-level keys of a mapping that carried fixtures.</summary>
    private static readonly string[] FixtureKeys = ["fixtureDefaults", "fixtures"];

    /// <summary>
    /// The kind a mapping document names in its <c>template</c> block, with or without a version, read without validating
    /// anything else; null when the document is not readable YAML or names no kind. What a document that fails to load is
    /// listed under.
    /// </summary>
    public string? TemplateKindNamedBy(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var kind = ProbeTemplate(yaml)?.Kind?.Trim();
        return string.IsNullOrEmpty(kind) ? null : kind;
    }

    /// <summary>
    /// The template a mapping document names in its <c>template</c> block, read without validating anything else, or
    /// null when the document is not readable YAML or names no complete template. A document that fails to load still
    /// pins what it names, so the template cannot be deleted from under a mapping that is only waiting for a fix.
    /// </summary>
    public TemplateReference? TemplateNamedBy(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var template = ProbeTemplate(yaml);
        var kind = template?.Kind?.Trim();
        var version = template?.Version?.Trim();
        return string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(version) ? null : new TemplateReference(kind, version);
    }

    /// <summary>The <c>template</c> block of a mapping document, or null when it is not readable YAML or has none of the right shape.</summary>
    private MappingTemplateYaml? ProbeTemplate(string yaml)
    {
        try
        {
            return _probe.Deserialize<TemplateProbeYaml>(yaml)?.Template;
        }
        catch (YamlException)
        {
            // Unreadable YAML, or a template block of the wrong shape, names no template. ParseMapping reports why.
            return null;
        }
    }

    private static T? Deserialize<T>(IDeserializer deserializer, string yaml, string source)
    {
        try
        {
            return deserializer.Deserialize<T>(yaml);
        }
        catch (YamlException ex)
        {
            var detail = ex.InnerException?.Message is { } inner && !ex.Message.Contains(inner, StringComparison.Ordinal)
                ? $"{ex.Message} ({inner})"
                : ex.Message;
            throw new FlowValidationException($"{source}: invalid YAML at line {ex.Start.Line}, column {ex.Start.Column} - {detail}", ex);
        }
    }

    /// <summary>Only the template block of a mapping document; every other key is ignored.</summary>
    private sealed class TemplateProbeYaml
    {
        public MappingTemplateYaml? Template { get; set; }
    }
}
