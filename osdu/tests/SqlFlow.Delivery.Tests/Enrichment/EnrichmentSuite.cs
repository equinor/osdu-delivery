using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The suite of a delivery flow's <c>enrich:</c> steps (osdu/docs/reference/flow/enrich.md): how the document and a fragment
/// load and what they refuse, how a fragment adds to the record, how a chain renders, what its preflight refuses, and that a
/// change anywhere in the chain writes the record once. It is a suite of its own, run on demand
/// (<c>bash tools/test.sh --suite Enrichment</c>, CONTRIBUTING.md, Tests): the unit and full tiers leave out every test that
/// carries its trait, <c>[Trait("Suite", "Enrichment")]</c>. A class of it that needs SQL Server is an integration test of the
/// <see cref="SqlServerSuite"/> as well. What keeps a flow without steps exactly as it was is checked in the tiers on every
/// pull request (<see cref="EnrichmentGuardTests"/>).
/// </summary>
public static class EnrichmentSuite
{
    /// <summary>The trait an on-demand suite is named by, beside <see cref="TestTierTests.Category"/>.</summary>
    public const string Trait = "Suite";

    /// <summary>The value of <see cref="Trait"/> for this suite.</summary>
    public const string Name = "Enrichment";

    /// <summary>The name every test enrichment mapping is pinned under, with its version.</summary>
    public const string FragmentReference = "Extra@1.0.0";

    /// <summary>The kind the datasets an enrichment adds are registered as in these suites.</summary>
    public const string DatasetKind = "osdu:wks:dataset--File.Generic:1.0.0";

    /// <summary>
    /// An enrichment mapping over <see cref="TestSchema"/> (or <paramref name="kind"/>): the header, <c>enrichment: true</c>, the
    /// parameters <paramref name="parameters"/> (written as the block below <c>parameters:</c>, or empty for none) and the record
    /// <paramref name="record"/>, laid under <c>record:</c> as it stands.
    /// </summary>
    public static string FragmentDocument(string record, string name = "Extra", string version = "1.0.0", string parameters = "", string? kind = null)
    {
        var header = $"""
            documentType: mapping
            name: {name}
            version: {version}
            template:
              kind: {kind ?? TestSchema.Kind}
              version: {SchemaOf(kind ?? TestSchema.Kind).Version}
            enrichment: true

            """;
        if (parameters.Length > 0)
        {
            header += "parameters:\n" + Indent(parameters, 2);
        }

        return header + "record:\n" + Indent(record, 2);
    }

    /// <summary>An enrichment mapping over <see cref="TestSchema"/> (or <paramref name="kind"/>), read as a run reads it.</summary>
    public static MappingDefinition Fragment(string record, string name = "Extra", string parameters = "", string? kind = null)
        => new DeliveryDocumentLoader().ParseMapping(FragmentDocument(record, name, parameters: parameters, kind: kind), $"{name}.yaml");

    /// <summary>
    /// The flow's own test mapping document over <see cref="TestSchema"/>: its data properties <paramref name="data"/> after the
    /// base ones, the record-level properties <paramref name="record"/>, and the enrichments <paramref name="enrich"/> laid
    /// under <c>enrich:</c> as they stand (none when empty).
    /// </summary>
    public static string OwnerDocument(string data = "", string record = "", string enrich = "")
    {
        var document = TestSchema.MappingDocument(data, record: record);
        return enrich.Length == 0
            ? document
            : document.Replace("\nrecord:\n", "\nenrich:\n" + Indent(enrich, 2) + "record:\n", StringComparison.Ordinal);
    }

    /// <summary>The flow's own test mapping over <see cref="TestSchema"/>, read as a run reads it (<see cref="OwnerDocument"/>).</summary>
    public static MappingDefinition Owner(string data = "", string record = "", string enrich = "")
        => new DeliveryDocumentLoader().ParseMapping(OwnerDocument(data, record, enrich), "thing.yaml");

    /// <summary>
    /// An enrichment adding a record for each of its rows: of <paramref name="kind"/>, rendered by <paramref name="mapping"/>
    /// (or by nothing beside the delivery's own properties), registered with the files <paramref name="files"/> names, or
    /// written through storage when it names none.
    /// </summary>
    public static ResolvedStep RecordsStep(
        string name = "lasfile",
        EnrichRows rows = EnrichRows.One,
        IReadOnlyList<string>? key = null,
        FlowPayload? files = null,
        MappingDefinition? mapping = null,
        string kind = DatasetKind,
        bool readsRows = true,
        EnrichHold onHold = EnrichHold.Record,
        MappingDefinition? fragment = null)
    {
        var records = new EnrichRecords
        {
            Files = files,
            Kind = kind,
            Key = key ?? (rows == EnrichRows.Many ? ["file_name"] : []),
            Mapping = mapping?.Reference ?? "LasDataset@1.0.0",
        };
        return new ResolvedStep(
            new EnrichStep { Name = name, Mapping = fragment?.Reference, Records = records, ReadsRows = readsRows, Rows = rows, OnHold = onHold },
            fragment,
            TestSchema.Build(),
            fragment is null ? null : Renderer(fragment))
        {
            Records = new ResolvedRecords(records, Identity.TargetId.EntityTypeFromKind(kind), mapping, mapping is null ? null : Renderer(mapping)),
        };
    }

    /// <summary>
    /// <paramref name="flow"/> bound to the enrichments <paramref name="owner"/> declares (<see cref="EnrichBinding"/>), each
    /// enrichment mapping found among <paramref name="mappings"/> as a run finds it in the flow's mappings folder.
    /// </summary>
    public static FlowDefinition Bound(FlowDefinition flow, MappingDefinition owner, params MappingDefinition[] mappings)
        => EnrichBinding.Bind(flow, owner, reference => mappings.FirstOrDefault(m => m.Reference == reference)
            ?? throw new Core.FlowValidationException($"Mapping '{reference}' was not found among the test's mappings."));

    /// <summary>
    /// Writes the sample well log mapping <c>WellLog@1.4.0</c> into <paramref name="folder"/> with the enrichments
    /// <paramref name="enrich"/> under its <c>enrich:</c> (as the block stands, without the key) and the data properties
    /// <paramref name="data"/> first under its <c>record.data</c>: the well log mapping as a mapping adding to its records declares it.
    /// </summary>
    public static void WriteWellLogMapping(string folder, string enrich, string data = "")
    {
        var lines = File.ReadAllText(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml")).ReplaceLineEndings("\n").Split('\n').ToList();
        var at = lines.FindIndex(line => line == "  data:");
        if (at < 0)
        {
            throw new InvalidOperationException("The sample well log mapping has no record.data line to add properties under.");
        }

        lines.InsertRange(at + 1, Indent(data, 4).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        var text = string.Join("\n", lines).TrimEnd('\n') + "\nenrich:\n" + Indent(enrich, 2);
        File.WriteAllText(Path.Combine(folder, "WellLog@1.4.0.yaml"), text);
    }

    /// <summary>
    /// Writes an enrichment mapping <c>{name}@1.0.0</c> over <paramref name="kind"/> into <paramref name="folder"/>, pinned to the
    /// sample template of the kind, rendering <paramref name="record"/> (laid under <c>record:</c> as it stands).
    /// </summary>
    public static void WriteEnrichmentMapping(string folder, string name, string kind, string record)
        => File.WriteAllText(Path.Combine(folder, $"{name}@1.0.0.yaml"), $"""
            documentType: mapping
            name: {name}
            version: 1.0.0
            template:
              kind: {kind}
              version: {Samples.SampleTemplate(kind).Version}
            enrichment: true
            record:

            """ + Indent(record, 2));

    /// <summary>The files every test enrichment's rows name: a folder per row under <c>../data/las</c>, and the hash beside it.</summary>
    public static FlowPayload LasFiles(string root = "../data/las") => new() { Root = root, LocationColumn = "las_folder", Pattern = "*.las", HashColumn = "las_hash" };

    /// <summary>A renderer over <paramref name="mapping"/> under the test context, with the test cache.</summary>
    public static MappingRenderer Renderer(MappingDefinition mapping, IRecordSearch? search = null)
        => new(mapping, SchemaOf(mapping.Kind), TestSchema.References(), TestSchema.Context(mapping.Reference), searches: null, search);

    /// <summary>The template a test mapping of <paramref name="kind"/> fills: <see cref="TestSchema"/>, or the sample template of another kind.</summary>
    public static SchemaSnapshot SchemaOf(string kind)
        => string.Equals(kind, TestSchema.Kind, StringComparison.Ordinal) ? TestSchema.Build() : Samples.SampleTemplate(kind);

    /// <summary>A step of the chain over <paramref name="fragment"/>, its rows read under the step's name.</summary>
    public static ResolvedStep Step(string name, MappingDefinition fragment, bool readsRows = true, EnrichRows rows = EnrichRows.One, IReadOnlyList<string>? after = null)
        => new(
            new EnrichStep { Name = name, Mapping = fragment.Reference, ReadsRows = readsRows, Rows = rows, After = after ?? [] },
            fragment,
            TestSchema.Build(),
            Renderer(fragment));

    /// <summary>A record of the test dataset: its own row, and the rows of each step by the step's name.</summary>
    public static SourceRecord Record(string name, double depth, params (string Step, IReadOnlyDictionary<string, string?>[] Rows)[] steps)
    {
        var scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (step, rows) in steps)
        {
            scopes[step] = rows.Select(SourceRow.FromStrings).ToList();
        }

        return new SourceRecord
        {
            Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["name"] = name, ["depth"] = depth.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
            Scopes = scopes,
        };
    }

    /// <summary>One row of a step's dataset.</summary>
    public static IReadOnlyDictionary<string, string?> Row(params (string Column, string? Value)[] values)
        => values.ToDictionary(v => v.Column, v => v.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>The value at <paramref name="path"/> (property names joined by dots) of <paramref name="document"/>, or null.</summary>
    public static JsonNode? At(JsonObject document, string path)
    {
        JsonNode? node = document;
        foreach (var step in path.Split('.'))
        {
            if (node is not JsonObject holder || !holder.TryGetPropertyValue(step, out node))
            {
                return null;
            }
        }

        return node;
    }

    private static string Indent(string text, int spaces)
    {
        var pad = new string(' ', spaces);
        return string.Concat(text.ReplaceLineEndings("\n").Split('\n').Where(line => line.Length > 0).Select(line => pad + line + "\n"));
    }
}
