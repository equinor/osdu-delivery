using System.Runtime.CompilerServices;
using SqlFlow.Core.Lineage;
using SqlFlow.Lineage;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The <c>samples/lineage-described</c> estate as its README and the reference pages describe it: one landing flow in
/// wave 1, and two lineage descriptions that chain from the order system to the files the flow lands. The figures the
/// documentation prints (two steps, three described objects, nine mapped columns) are the ones asserted here, so the
/// pages and the sample cannot drift apart unnoticed.
/// </summary>
public sealed class LineageDescribedSampleTests
{
    [Fact]
    public async Task TheSample_IsOneFlowInWaveOne_WithTwoDescribedStepsBackToTheOrderSystem()
    {
        var report = await LineageService.ComputeAsync(new LineageOptions
        {
            FlowDirectory = SampleDirectory(),
            IncludeObserved = false,
        });

        // The plan is the estate's own flow, alone.
        Assert.Equal("described-land-orders", Assert.Single(report.Flows).Name);
        Assert.Equal(["described-land-orders"], Assert.Single(report.ExecutionPlan.Waves).Flows);
        Assert.Empty(report.Cycles);
        Assert.DoesNotContain(report.Edges, e => LineageRelations.IsDescribed(e.Relation));

        // The described lineage, beside it.
        var described = report.Described;
        Assert.Empty(described.Warnings);
        Assert.Equal(["orders-u1-curate-lin", "orders-u2-publish-lin"], described.Steps.Select(s => s.Name));
        Assert.All(described.Steps, s => Assert.Equal("lakehouse", s.System));
        Assert.Equal(3, described.Objects.Count);
        Assert.Equal(9, described.Columns.Count);

        // The order system is where the data starts; the curated table is written by one step and read by the next.
        Assert.Equal(
            ["sales.exchange_rates", "sales.orders"],
            described.Objects.Where(o => o.Boundary == LineageDescribedBoundary.Origin).Select(o => o.Name).Order(StringComparer.Ordinal));
        var curated = Assert.Single(described.Objects, o => o.Boundary == LineageDescribedBoundary.None);
        Assert.Equal("curated.orders", curated.Name);
        Assert.Contains(described.Edges, e => e.Flow == "orders-u1-curate-lin"
            && e.Relation == LineageRelation.DescribedWrites && e.ObjectKey == curated.Key);
        Assert.Contains(described.Edges, e => e.Flow == "orders-u2-publish-lin"
            && e.Relation == LineageRelation.DescribedReads && e.ObjectKey == curated.Key);

        // The second step names the landing flow, so it is connected to the very object that flow reads.
        var landed = Assert.Single(report.Edges, e => e.Flow == "described-land-orders" && e.Relation == LineageRelation.Reads).ObjectKey;
        Assert.Contains(described.Edges, e => e.Flow == "orders-u2-publish-lin"
            && e.Relation == LineageRelation.DescribedWrites && e.ObjectKey == landed);
        Assert.Contains(described.Columns, c => c.TargetObjectKey == landed && c.TargetColumn == "exported_at" && c.Sources.Count == 0);
    }

    [Fact]
    public void EveryDocumentOfTheSample_Loads_AndADescriptionIsNeverRun()
    {
        var loader = YamlDocumentLoader.CreateDefault();
        var files = Directory.EnumerateFiles(SampleDirectory(), "*.yaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(3, files.Count);

        foreach (var file in files)
        {
            var document = loader.LoadFile(file);
            if (document is LineageDescriptionFlowDocument description)
            {
                Assert.Equal(LineageDescriptionFlow.FlowType, description.Kind);
                Assert.NotNull(YamlDocumentLoader.NeverRunReason(description.Kind));
            }
        }
    }

    /// <summary>The sample folder, found from the test binaries (a build inside the repository) or, when they were
    /// built elsewhere, from this source file's own location.</summary>
    private static string SampleDirectory([CallerFilePath] string sourceFile = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(sourceFile) })
        {
            for (var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "samples", "lineage-described");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate 'samples/lineage-described' above the test output ('{AppContext.BaseDirectory}') or the test source ('{sourceFile}').");
    }
}
