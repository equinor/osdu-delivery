using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Model;
using SqlFlow.Execution;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The <c>scanSourceColumns</c> compute task on a node, through the executor the engine's own registration builds: the
/// file flow's document as the catalog stores it, its location naming a reference that only the values supplied with the
/// task resolve (as a run of the flow is supplied them, never this process's environment), a relative location resolved
/// against the flow's file in its checkout, and the answer set against the target the task carries. A document that is
/// not a file flow's, and a relative location with no checkout, are refused.
/// </summary>
public sealed class SourceColumnScanTaskTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_scantask_" + Guid.NewGuid().ToString("N"));

    /// <summary>The reference the flow's location names, unique per test so no process environment can hold it.</summary>
    private readonly string _landing = "SCANTASK_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    public SourceColumnScanTaskTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "wells"));
        Directory.CreateDirectory(Path.Combine(_dir, "flows"));
        Write("wells/wells_1.csv", "well_id,well name,elevation\nW1,One,10\n", DateTime.UtcNow.AddDays(-2));
        Write("wells/wells_2.csv", "well_id,well name,elevation,spud_date\nW2,Two,11,2026-01-01\n", DateTime.UtcNow.AddDays(-1));
    }

    [Fact]
    public async Task AScan_ReadsTheLocationItsFlowNames_WithTheValuesSuppliedWithTheTask()
    {
        Assert.Null(Environment.GetEnvironmentVariable(_landing));
        using var provider = Provider();
        var payload = Scan($"${{env:{_landing}}}/wells") with
        {
            References = new Dictionary<string, string>(StringComparer.Ordinal) { [_landing] = _dir },
            Columns = ["spud_date", "Well Name", "depth"],
            ScanTarget = new SourceScanTarget("key", "Landing", "pre", "Wells", ["well_id", "well_name", "elevation", "legacy_code", "FileName_DW"]),
        };

        var report = await RunAsync(provider, payload);

        Assert.Equal(("pre_wells", "csv", $"${{env:{_landing}}}/wells"), (report.Flow, report.SourceType, report.Location));
        Assert.Equal((2, 2), (report.FilesMatched, report.FilesRead));
        Assert.Equal(["wells_2.csv", "wells_1.csv"], report.Files.Select(f => f.Name));
        Assert.Equal(["well_id", "well_name", "elevation", "spud_date"], report.Columns.Select(c => c.Name));
        Assert.Equal([2, 2, 2, 1], report.Columns.Select(c => c.Files));
        Assert.Equal(["spud_date"], report.NewColumns);
        Assert.Equal(["legacy_code"], report.NotInFiles);
        Assert.Collection(report.Expected,
            spud =>
            {
                Assert.Equal(("spud_date", 1, false, false), (spud.Name, spud.Files, spud.InEveryFile, spud.InTarget));
                Assert.Equal(["wells_1.csv"], spud.MissingFrom);
            },
            name => Assert.Equal(("Well Name", 2, true), (name.Name, name.Files, name.InEveryFile)),
            depth => Assert.Equal(("depth", 0, false), (depth.Name, depth.Files, depth.InEveryFile)));
    }

    [Fact]
    public async Task WithoutTheSuppliedValues_TheFlowsReferenceDoesNotResolve()
    {
        using var provider = Provider();

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => RunAsync(provider, Scan($"${{env:{_landing}}}/wells")));

        Assert.Contains("names a reference that does not resolve", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARelativeLocation_ResolvesAgainstTheFlowsFileInItsCheckout_AndIsRefusedWithoutOne()
    {
        using var provider = Provider();

        var report = await RunAsync(provider, Scan("../wells") with { FlowFile = Path.Combine(_dir, "flows", "pre_wells.yaml") });
        Assert.Equal((2, "../wells"), (report.FilesMatched, report.Location));

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => RunAsync(provider, Scan("../wells")));
        Assert.Contains("names no checkout", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADocumentThatIsNotAFileFlows_IsRefused()
    {
        using var provider = Provider();
        var payload = new ComputeTaskPayload
        {
            Operation = ComputeOperations.ScanSourceColumns,
            SourceRef = "proc",
            Flow = "flowType: sp\nname: proc\nconnections:\n  dwh: ${env:DW}\nprocedure:\n  server: dwh\n  object: Warehouse.dbo.usp_Load\n",
        };

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => RunAsync(provider, payload));
        Assert.Contains("is not a file flow", ex.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static ComputeTaskPayload Scan(string location) => new()
    {
        Operation = ComputeOperations.ScanSourceColumns,
        SourceRef = "pre_wells",
        Flow = $$"""
            name: pre_wells
            source:
              type: csv
              location: {{location}}
              options:
                srcFile: "*.csv"
            target:
              connection: ${env:DW}
              schema: pre
              table: Wells
            """,
    };

    /// <summary>The task as the node runs it: validated as the queue row is, then executed.</summary>
    private static async Task<SourceColumnsReport> RunAsync(ServiceProvider provider, ComputeTaskPayload payload)
    {
        var executor = provider.GetRequiredService<ComputeTaskExecutor>();
        var queued = ComputeTaskPayload.FromJson(payload.ToJson(), executor.RegisteredOperations);
        var json = await executor.ExecuteAsync(queued, CancellationToken.None);
        return JsonSerializer.Deserialize<SourceColumnsReport>(json, Web)!;
    }

    private static ServiceProvider Provider() => new ServiceCollection().AddLogging().AddSqlFlowEngine().BuildServiceProvider();

    private void Write(string relative, string content, DateTime modifiedUtc)
    {
        var path = Path.Combine(_dir, relative);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
    }
}
