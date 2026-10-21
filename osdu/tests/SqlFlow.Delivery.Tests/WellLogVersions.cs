using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The two versions of the WellLog schema the suites deliver the sample rows as: the sample flow's 1.4.0, and 1.5.0, the
/// next version the Open Group publishes, saved from the data definitions as a fixture (Fixtures/templates). A pipeline on
/// 1.5.0 is the sample flow with the sample mapping pinned to it; delivering to a partition of its own, it reads that
/// partition's cache, which holds the sample records under that partition's ids.
/// </summary>
internal static class WellLogVersions
{
    public const string CurrentKind = "osdu:wks:work-product-component--WellLog:1.4.0";

    public const string NextKind = "osdu:wks:work-product-component--WellLog:1.5.0";

    public const string CurrentMapping = "WellLog@1.4.0";

    public const string NextMapping = "WellLog@1.5.0";

    /// <summary>The content version of the WellLog 1.5.0 fixture, as <c>sqlflow template capture</c> wrote it.</summary>
    public const string NextTemplateVersion = "2f8a99cb38d32480";

    private const string CurrentTemplateVersion = "26a3c3441882db4f";

    /// <summary>
    /// <paramref name="flow"/> as a pipeline named <paramref name="name"/> that renders WellLog 1.5.0 and delivers to
    /// <paramref name="partition"/>, with its mapping written under <paramref name="root"/>.
    /// </summary>
    public static FlowDefinition OnNextVersion(FlowDefinition flow, string name, string partition, string root)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var mappings = Path.Combine(root, "next-mappings-" + FolderName(partition));
        Directory.CreateDirectory(mappings);
        File.WriteAllText(Path.Combine(mappings, NextMapping + ".yaml"), NextMappingDocument());
        if (flow.DeclaresPartitions)
        {
            // A flow that names its partitions delivers to the one a run binds it to: the pipeline names the partition it
            // delivers to, keeping its own ledger there, and the kind supplies it as the partition every id is minted in.
            var headers = flow.Target.Headers
                .Where(h => !h.Key.Equals(CacheScope.PartitionHeader, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);
            return (flow with
            {
                Name = name,
                Partitions = [new DeclaredPartition(partition, KeepsLedger: true)],
                Partition = null,
                Render = flow.Render with { Mapping = NextMapping, MappingsDirectory = mappings },
                Target = flow.Target with { Headers = headers },
            }).ForPartition(partition);
        }

        return flow with
        {
            Name = name,
            Render = flow.Render with
            {
                Mapping = NextMapping,
                MappingsDirectory = mappings,
                Parameters = new Dictionary<string, string>(flow.Render.Parameters, StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = partition },
            },
            Target = flow.Target with
            {
                Headers = new Dictionary<string, string>(flow.Target.Headers, StringComparer.OrdinalIgnoreCase) { [CacheScope.PartitionHeader] = partition },
            },
        };
    }

    /// <summary>
    /// Imports the sample lookup tables and the sample reference data as the cache of <paramref name="partition"/>, as the
    /// captures of cache flows named <paramref name="flowName"/> + "-lookups" and + "-reference". The well log mapping
    /// (1.4.0 and 1.5.0 alike) builds every reference id it writes from the tables, and finds each in the reference data or
    /// among the partition's units, so they are the cache content a pipeline on either version reads.
    /// </summary>
    public static async Task ImportPartitionCacheAsync(ICacheStore caches, string partition, string flowName)
    {
        ArgumentNullException.ThrowIfNull(caches);
        var lookups = new SnapshotBuilder(
            caches, partition, flowName + "-lookups", new TestClock(Samples.SampleCacheCaptured.AddMinutes(-1)), Samples.Logger<SnapshotBuilder>());
        await lookups.WriteAsync(Samples.SampleLookups(), new CacheCapture(null, "tests", "sample lookups for " + partition), []);

        // The sample records carry the sample partition's ids; the partition under test holds the same codes under its own.
        var reference = new DeliveryDocumentLoader().LoadCache(Samples.ReferenceCacheFlow);
        var folder = Directory.CreateTempSubdirectory("osdu-reference-records-");
        try
        {
            foreach (var file in Directory.GetFiles(Samples.SampleCacheRecords, "*.json"))
            {
                var text = await File.ReadAllTextAsync(file);
                await File.WriteAllTextAsync(
                    Path.Combine(folder.FullName, Path.GetFileName(file)),
                    text.Replace($"\"{Samples.SamplePartition}:", $"\"{partition}:", StringComparison.Ordinal));
            }

            var references = new SnapshotBuilder(
                caches, partition, flowName + "-reference", new TestClock(Samples.SampleCacheCaptured.AddSeconds(-30)), Samples.Logger<SnapshotBuilder>());
            await references.ImportDirectoryAsync(folder.FullName, reference.Types, new CacheCapture(null, "tests", "sample reference data for " + partition));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The sample WellLog 1.4.0 mapping pinned to WellLog 1.5.0, with its entries untouched. Every edit is checked to have
    /// applied.
    /// </summary>
    private static string NextMappingDocument()
    {
        var text = File.ReadAllText(Path.Combine(Samples.Mappings, CurrentMapping + ".yaml")).ReplaceLineEndings("\n");
        text = Replace(text, "# Mapping: well database logs into the WellLog 1.4.0 template", "# Mapping: well database logs into the WellLog 1.5.0 template", 1);
        text = Replace(text, "\nversion: 1.4.0\n", "\nversion: 1.5.0\n", 1);
        return Replace(text, $"  kind: {CurrentKind}\n  version: {CurrentTemplateVersion}\n", $"  kind: {NextKind}\n  version: {NextTemplateVersion}\n", 1);
    }

    /// <summary>
    /// A partition as a folder name. An estate may name its partition as a ${env:...} reference, and a reference holds
    /// characters no file system takes, so anything but a letter, digit, hyphen, underscore or dot becomes a hyphen.
    /// </summary>
    private static string FolderName(string partition)
        => string.Concat(partition.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-'));

    /// <summary>Replaces <paramref name="from"/>, which must occur <paramref name="expected"/> times.</summary>
    private static string Replace(string text, string from, string to, int expected)
    {
        var found = text.Split(from).Length - 1;
        Assert.True(
            found == expected,
            $"The sample WellLog mapping holds '{from.ReplaceLineEndings(" ")}' {found} time(s), not {expected.ToString(System.Globalization.CultureInfo.InvariantCulture)}: the derived 1.5.0 mapping no longer follows it.");
        return text.Replace(from, to, StringComparison.Ordinal);
    }
}
