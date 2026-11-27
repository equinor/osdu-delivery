namespace SqlFlow.Core.Model;

/// <summary>One file a source scan read: its name, path, last-modified time and size, its columns in order as a load names
/// them, and why its header could not be read, if it could not.</summary>
public sealed record ScannedSourceFile(
    string? Name, string? Path, DateTimeOffset? Modified, long? SizeBytes, IReadOnlyList<string> Columns, string? Error);

/// <summary>One column the scanned files carry: in how many of them, the type a self-describing format declares (null for
/// text formats), the oldest and newest of those files by their modified time, and whether the table the flow loads into
/// has it (null when the catalog holds no columns of that table).</summary>
public sealed record SourceColumnPresence(
    string Name, int Files, string? Type, DateTimeOffset? FirstSeen, DateTimeOffset? LastSeen, bool? InTarget);

/// <summary>The table a file flow loads into as the catalog last read it: its catalog key, database, schema, name and
/// columns in order.</summary>
public sealed record SourceScanTarget(string ObjectKey, string? Database, string Schema, string Table, IReadOnlyList<string> Columns);

/// <summary>One column the caller asked about: in how many scanned files it is, whether every one of them carries it,
/// whether the target table has it, and the files it is missing from (at most <see cref="SourceColumnsReport.MaxMissingNamed"/>
/// named).</summary>
public sealed record ExpectedSourceColumn(string Name, int Files, bool InEveryFile, bool? InTarget, IReadOnlyList<string> MissingFrom);

/// <summary>
/// What the files at a file flow's declared source carry, file by file: the flow, its source as the flow writes it, how
/// many files its selection matches and how many were read (the newest), each read file's columns, every column with the
/// files carrying it, the table it loads into, the columns some file carries that the table does not have yet
/// (<c>NewColumns</c>), the table's columns no read file carries (<c>NotInFiles</c>, leaving out the ones a load adds
/// itself), the columns asked about, and the columns the flow generates (<c>Generated</c>).
/// </summary>
public sealed record SourceColumnsReport(
    string Flow,
    string SourceType,
    string? Location,
    int FilesMatched,
    int FilesRead,
    string? NoFilesReason,
    IReadOnlyList<ScannedSourceFile> Files,
    IReadOnlyList<SourceColumnPresence> Columns,
    SourceScanTarget? Target,
    IReadOnlyList<string> NewColumns,
    IReadOnlyList<string> NotInFiles,
    IReadOnlyList<ExpectedSourceColumn> Expected,
    IReadOnlyList<string> Generated)
{
    /// <summary>The most files an expected column names as missing from.</summary>
    public const int MaxMissingNamed = 20;

    /// <summary>The suffix SQLFlow's own columns carry (<c>FileName_DW</c>, <c>InsertedDate_DW</c>): a load adds them, so a
    /// file never does.</summary>
    private const string PlatformColumnSuffix = "_DW";

    /// <summary>
    /// The answer from what a scan read: every column in the order the newest files first carry it, with the files that
    /// carry it; the columns new to the target and the target's columns no file carries; and each expected column, matched
    /// by name ignoring case against the columns as a load names them or as the file writes them.
    /// </summary>
    public static SourceColumnsReport Describe(
        string flow, string sourceType, string? location, SourceFileScan scan, SourceScanTarget? target,
        IReadOnlyList<string> expected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flow);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(expected);

        var read = scan.Files.Where(f => f.Error is null).ToList();
        var presence = new Dictionary<string, (string Name, int Files, string? Type, DateTimeOffset? First, DateTimeOffset? Last)>(
            StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var file in read)
        {
            foreach (var column in file.Columns.DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (presence.TryGetValue(column.Name, out var seen))
                {
                    presence[column.Name] = (seen.Name, seen.Files + 1, seen.Type ?? column.SqlType, Earlier(seen.First, file.Modified), Later(seen.Last, file.Modified));
                }
                else
                {
                    order.Add(column.Name);
                    presence[column.Name] = (column.Name, 1, column.SqlType, file.Modified, file.Modified);
                }
            }
        }

        var targetColumns = target?.Columns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var generated = scan.Generated.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columns = order
            .Select(name => presence[name])
            .Select(p => new SourceColumnPresence(p.Name, p.Files, p.Type, p.First, p.Last, targetColumns?.Contains(p.Name)))
            .ToList();
        var newColumns = targetColumns is null ? [] : order.Where(name => !targetColumns.Contains(name)).ToList();
        var notInFiles = target is null || read.Count == 0
            ? []
            : target.Columns
                .Where(name => !presence.ContainsKey(name) && !generated.Contains(name)
                    && !name.EndsWith(PlatformColumnSuffix, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var asked = expected
            .Select(name =>
            {
                var missing = read.Where(file => !Carries(file, name)).ToList();
                return new ExpectedSourceColumn(
                    name,
                    read.Count - missing.Count,
                    read.Count > 0 && missing.Count == 0,
                    targetColumns?.Contains(name),
                    missing.Take(MaxMissingNamed).Select(file => file.Name ?? "(the source)").ToList());
            })
            .ToList();

        var files = scan.Files
            .Select(f => new ScannedSourceFile(f.Name, f.Path, f.Modified, f.SizeBytes, f.Columns.Select(c => c.Name).ToList(), f.Error))
            .ToList();
        return new SourceColumnsReport(
            flow, sourceType, location, scan.FilesMatched, read.Count, scan.NoFilesReason, files, columns, target,
            newColumns, notInFiles, asked, scan.Generated);

        static bool Carries(SourceFileColumns file, string name)
            => file.Columns.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                || file.HeaderNames.Any(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));

        static DateTimeOffset? Earlier(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a < b ? a : b;

        static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;
    }
}
