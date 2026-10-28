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

    /// <summary>The name every test fragment is pinned under, with its version.</summary>
    public const string FragmentReference = "Extra@1.0.0";

    /// <summary>
    /// A fragment over <see cref="TestSchema"/>: the header, <c>fragment: true</c>, the parameters <paramref name="parameters"/>
    /// (written as the block below <c>parameters:</c>, or empty for none) and the record <paramref name="record"/>, laid under
    /// <c>record:</c> as it stands.
    /// </summary>
    public static string FragmentDocument(string record, string name = "Extra", string version = "1.0.0", string parameters = "")
    {
        var header = $"""
            documentType: mapping
            name: {name}
            version: {version}
            template:
              kind: {TestSchema.Kind}
              version: {TestSchema.Build().Version}
            fragment: true

            """;
        if (parameters.Length > 0)
        {
            header += "parameters:\n" + Indent(parameters, 2);
        }

        return header + "record:\n" + Indent(record, 2);
    }

    /// <summary>A fragment over <see cref="TestSchema"/>, read as a run reads it.</summary>
    public static MappingDefinition Fragment(string record, string name = "Extra", string parameters = "")
        => new DeliveryDocumentLoader().ParseMapping(FragmentDocument(record, name, parameters: parameters), $"{name}.yaml");

    /// <summary>The flow's own test mapping over <see cref="TestSchema"/>, its data properties <paramref name="data"/> after the base ones.</summary>
    public static MappingDefinition Owner(string data = "", string record = "")
        => new DeliveryDocumentLoader().ParseMapping(TestSchema.MappingDocument(data, record: record), "thing.yaml");

    /// <summary>A renderer over <paramref name="mapping"/> under the test context, with the test cache.</summary>
    public static MappingRenderer Renderer(MappingDefinition mapping, IRecordSearch? search = null)
        => new(mapping, TestSchema.Build(), TestSchema.References(), TestSchema.Context(mapping.Reference), searches: null, search);

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
