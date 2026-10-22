using System.Text.Json.Nodes;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Rendering;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The record a row renders compared with the one a test writes out: the assertion the suites make of what a mapping
/// delivers. The two are compared canonically, and a list held empty at any depth counts as left out, so an expected
/// record is written without the lists the engine writes empty (a list the mapping defines and nothing fills, the
/// record's own <c>meta</c>).
/// </summary>
public static class RenderedRecord
{
    /// <summary>
    /// The records the suites keep for rows of the sample and fixture mappings, a folder per mapping (WellLog@1.4.0) and a
    /// file per row: what the row renders to under the sample cache and the parameters the samples deliver with.
    /// </summary>
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "Fixtures", "rendered");

    /// <summary>The record the suites keep for row <paramref name="name"/> of <paramref name="mapping"/> (WellLog@1.4.0, LOG-0003_1).</summary>
    public static string Expected(string mapping, string name) => File.ReadAllText(Path.Combine(Folder, mapping, name + ".json"));

    /// <summary>The names of the rows of <paramref name="mapping"/> the suites keep a record for, in order.</summary>
    public static IReadOnlyList<string> Names(string mapping)
        => Directory.GetFiles(Path.Combine(Folder, mapping), "*.json").Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Asserts that <paramref name="result"/> is the deliverable record <paramref name="expected"/> writes as JSON: finished,
    /// held by nothing, and the same record.
    /// </summary>
    public static void AssertIs(string expected, RenderResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expected);
        ArgumentNullException.ThrowIfNull(result);
        Assert.False(result.IsIncomplete, "The render still waits for answers to: " + string.Join("; ", result.Unanswered.Select(MappingRenderer.Unanswerable)));
        Assert.False(result.IsHeld, "The record is held: " + string.Join("; ", result.Holds));
        Assert.Equal(CanonicalJson.ToString(Comparable(JsonNode.Parse(expected))), CanonicalJson.ToString(Comparable(result.Document)));
    }

    /// <summary>
    /// Asserts that <paramref name="result"/> is the record the suites keep for row <paramref name="name"/> of
    /// <paramref name="mapping"/>, as <see cref="AssertIs"/> does. When it is not, the record the row rendered to is written
    /// beside the test binaries (<see cref="RenderedFolder"/>, the same layout as the kept records) and the failure names the
    /// file, so a change that is meant (a mapping, a template, the sample data) is taken over by copying that file onto the
    /// kept one after reading what moved.
    /// </summary>
    public static void AssertIsKept(string mapping, string name, RenderResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapping);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(result);
        Assert.False(result.IsIncomplete, "The render still waits for answers to: " + string.Join("; ", result.Unanswered.Select(MappingRenderer.Unanswerable)));
        Assert.False(result.IsHeld, "The record is held: " + string.Join("; ", result.Holds));
        var kept = Path.Combine(Folder, mapping, name + ".json");
        var expected = File.Exists(kept) ? CanonicalJson.ToString(Comparable(JsonNode.Parse(File.ReadAllText(kept)))) : null;
        var actual = CanonicalJson.ToString(Comparable(result.Document));
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return;
        }

        var written = Path.Combine(RenderedFolder, mapping, name + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(written)!);
        // Written as the comparison reads it (canonical, the lists held empty left out), so the file is the kept record as is.
        File.WriteAllText(written, Comparable(result.Document)!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        Assert.Fail(
            (expected is null
                ? $"The suites keep no record for row {name} of {mapping} (Fixtures/rendered/{mapping}/{name}.json). "
                : $"Row {name} of {mapping} no longer renders the record the suites keep for it (Fixtures/rendered/{mapping}/{name}.json). ") +
            $"What it renders now is in {written}; when the change is meant, read what moved and copy that file over the kept one.");
    }

    /// <summary>Where <see cref="AssertIsKept"/> writes a record that differs from the one kept for it.</summary>
    public static string RenderedFolder => Path.Combine(AppContext.BaseDirectory, "rendered-now");

    /// <summary>A record as <see cref="AssertIs"/> compares it: canonical, without the lists it holds empty at any depth.</summary>
    public static JsonNode? Comparable(JsonNode? record)
    {
        var normal = CanonicalJson.Normalize(record);
        WithoutEmptyLists(normal);
        return normal;
    }

    private static void WithoutEmptyLists(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject value:
                foreach (var name in value.Where(p => p.Value is JsonArray { Count: 0 }).Select(p => p.Key).ToList())
                {
                    value.Remove(name);
                }

                foreach (var (_, child) in value)
                {
                    WithoutEmptyLists(child);
                }

                break;
            case JsonArray items:
                foreach (var item in items)
                {
                    WithoutEmptyLists(item);
                }

                break;
        }
    }
}
