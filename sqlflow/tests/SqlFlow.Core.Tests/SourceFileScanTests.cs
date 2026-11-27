using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;
using SqlFlow.Sources;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// A scan of a file source's columns, file by file (<see cref="ISourceReader.ScanFileColumnsAsync"/>): the newest files the
/// run's own selection matches, each with its header's columns named as a load names them; a file whose header cannot be
/// read listed with its error while the rest are still read; and a selection that matches nothing answered with the reason
/// rather than a failure.
/// </summary>
public sealed class SourceFileScanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_scan_" + Guid.NewGuid().ToString("N"));

    public SourceFileScanTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Scan_ReadsTheNewestFiles_EachWithItsOwnColumns()
    {
        Write("wells_1.csv", "well_id,well name\nW1,One\n", DaysAgo(3));
        Write("wells_2.csv", "well_id,well name,elevation\nW2,Two,10.5\n", DaysAgo(2));
        Write("wells_3.csv", "well_id,well name,elevation,spud_date\nW3,Three,11,2026-01-01\n", DaysAgo(1));
        var reader = new CsvSourceReader(new LocalFileLifecycle(), [new LocalFileStore()]);
        var source = Folder();

        var scan = await reader.ScanFileColumnsAsync(source, maxFiles: 2);

        Assert.Equal(3, scan.FilesMatched);
        Assert.Null(scan.NoFilesReason);
        Assert.Equal(["wells_3.csv", "wells_2.csv"], scan.Files.Select(f => f.Name));
        Assert.All(scan.Files, f => Assert.Null(f.Error));
        Assert.Equal(["well_id", "well_name", "elevation", "spud_date"], scan.Files[0].Columns.Select(c => c.Name));
        Assert.Equal(["well_id", "well name", "elevation", "spud_date"], scan.Files[0].HeaderNames);
        Assert.Equal(3, scan.Files[1].Columns.Count);

        // The columns a load adds itself are the ones the union has beyond what any file carries.
        var union = await new CsvSourceReader(new LocalFileLifecycle(), [new LocalFileStore()]).GetColumnsAsync(Folder());
        var fromFiles = new[] { "well_id", "well_name", "elevation", "spud_date" };
        Assert.Equal(union.Select(c => c.Name).Except(fromFiles), scan.Generated);
    }

    [Fact]
    public async Task Scan_ListsAFileItCannotRead_WithItsError_AndReadsTheRest()
    {
        Write("a.csv", "id,name\n1,x\n", DaysAgo(2));
        Write("broken.csv", "id,name\n2,y\n", DaysAgo(1));
        var reader = new CsvSourceReader(new LocalFileLifecycle(), [new RefusingStore("broken.csv")]);

        var scan = await reader.ScanFileColumnsAsync(Folder(), maxFiles: 10);

        Assert.Equal(["broken.csv", "a.csv"], scan.Files.Select(f => f.Name));
        var broken = scan.Files[0];
        Assert.Empty(broken.Columns);
        Assert.Contains("refused", broken.Error, StringComparison.Ordinal);
        Assert.Equal(["id", "name"], scan.Files[1].Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task Scan_OfASelectionThatMatchesNothing_SaysWhy()
    {
        var reader = new CsvSourceReader(new LocalFileLifecycle(), [new LocalFileStore()]);

        var scan = await reader.ScanFileColumnsAsync(Folder(), maxFiles: 5);

        Assert.Equal(0, scan.FilesMatched);
        Assert.Empty(scan.Files);
        Assert.False(string.IsNullOrWhiteSpace(scan.NoFilesReason));
    }

    [Fact]
    public async Task Scan_TakesAtLeastOneFile()
    {
        var reader = new CsvSourceReader(new LocalFileLifecycle(), [new LocalFileStore()]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ScanFileColumnsAsync(Folder(), maxFiles: 0));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private SourceSpec Folder()
        => new() { Type = "csv", Location = _dir, Options = new Dictionary<string, string?> { ["srcFile"] = "*.csv" } };

    private void Write(string name, string content, DateTime modifiedUtc)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
    }

    private static DateTime DaysAgo(int days) => DateTime.UtcNow.AddDays(-days);

    /// <summary>The local disk, refusing to open one file, as a store does a file it may not read.</summary>
    private sealed class RefusingStore(string refused) : IFileStore
    {
        private readonly LocalFileStore _local = new();

        public bool CanHandle(string location) => _local.CanHandle(location);

        public Task<IReadOnlyList<FileRef>> ListAsync(string location, FileDiscovery discovery, CancellationToken ct = default)
            => _local.ListAsync(location, discovery, ct);

        public Task<Stream> OpenReadAsync(FileRef file, CancellationToken ct = default)
            => string.Equals(file.Name, refused, StringComparison.Ordinal)
                ? throw new IOException($"The store refused to open '{file.Name}'.")
                : _local.OpenReadAsync(file, ct);
    }
}
