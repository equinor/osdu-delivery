using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Model;
using SqlFlow.Core.Secrets;
using SqlFlow.Execution;
using SqlFlow.Yaml;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One file a source scan read: its name, path, last-modified time and size, its columns in order as a load names
/// them, and why its header could not be read, if it could not.</summary>
public sealed record SourceFileColumnsDto(
    string? Name, string? Path, DateTimeOffset? Modified, long? SizeBytes, IReadOnlyList<string> Columns, string? Error);

/// <summary>One column the scanned files carry: in how many of them, the type a self-describing format declares (null for
/// text formats), the oldest and newest of those files by their modified time, and whether the table the flow loads into
/// has it (null when the catalog holds no columns of that table).</summary>
public sealed record SourceColumnPresenceDto(
    string Name, int Files, string? Type, DateTimeOffset? FirstSeen, DateTimeOffset? LastSeen, bool? InTarget);

/// <summary>The table a file flow loads into as the catalog last read it: its catalog key, database, schema, name and
/// columns in order.</summary>
public sealed record SourceTargetDto(string ObjectKey, string? Database, string Schema, string Table, IReadOnlyList<string> Columns);

/// <summary>One column the caller asked about: in how many scanned files it is, whether every one of them carries it,
/// whether the target table has it, and the files it is missing from (at most 20 named).</summary>
public sealed record ExpectedColumnDto(string Name, int Files, bool InEveryFile, bool? InTarget, IReadOnlyList<string> MissingFrom);

/// <summary>
/// What the files at a file flow's declared source carry, file by file: the flow, its source as the flow writes it, how
/// many files its selection matches and how many were read (the newest), each read file's columns, every column with the
/// files carrying it, the table it loads into, the columns some file carries that the table does not have yet
/// (<c>NewColumns</c>), the table's columns no read file carries (<c>NotInFiles</c>, leaving out the ones a load adds
/// itself), the columns asked about, and the columns the flow generates (<c>Generated</c>).
/// </summary>
public sealed record SourceColumnsDto(
    Guid PipelineId,
    string Flow,
    string SourceType,
    string? Location,
    int FilesMatched,
    int FilesRead,
    string? NoFilesReason,
    IReadOnlyList<SourceFileColumnsDto> Files,
    IReadOnlyList<SourceColumnPresenceDto> Columns,
    SourceTargetDto? Target,
    IReadOnlyList<string> NewColumns,
    IReadOnlyList<string> NotInFiles,
    IReadOnlyList<ExpectedColumnDto> Expected,
    IReadOnlyList<string> Generated);

/// <summary>
/// The columns the files at a file flow's source carry (<c>GET /pipelines/{id}/source-columns</c>): the flow's document as
/// the last sync stored it, its relative location resolved against its own folder in the repository's checkout and a
/// referenced one through the control plane's resolver, as a run resolves them, then each of the newest files' headers
/// read by the reader of its format (<see cref="ISourceReader.ScanFileColumnsAsync"/>). The caller names the flow, never
/// a location, so nothing but what a flow already declares is read, and nothing of a row.
/// </summary>
public static class PipelineSourceColumns
{
    /// <summary>How many of the newest files are read when the caller does not say.</summary>
    public const int DefaultFiles = 20;

    /// <summary>The most files one scan reads.</summary>
    public const int MaxFiles = 200;

    /// <summary>The most columns one request asks about.</summary>
    public const int MaxExpected = 200;

    /// <summary>The most files an expected column names as missing from.</summary>
    private const int MaxMissingNamed = 20;

    /// <summary>The suffix SQLFlow's own columns carry (<c>FileName_DW</c>, <c>InsertedDate_DW</c>): a load adds them, so a
    /// file never does.</summary>
    private const string PlatformColumnSuffix = "_DW";

    internal static async Task<Results<Ok<SourceColumnsDto>, ProblemHttpResult>> GetAsync(
        Guid id, int? files, string? expect, CatalogDbContext db, YamlDocumentLoader documents, IEnumerable<ISourceReader> readers,
        ISecretResolver secrets, CancellationToken ct)
    {
        var count = files ?? DefaultFiles;
        if (count is < 1 or > MaxFiles)
        {
            return Invalid($"files must be between 1 and {MaxFiles}.");
        }

        var expected = (expect ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (expected.Count > MaxExpected || expected.Any(name => name.Length > 128))
        {
            return Invalid($"expect names at most {MaxExpected} columns, each at most 128 characters.");
        }

        var pipeline = await db.Pipelines.AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new { p.Name, p.Kind, p.Yaml, p.RelativePath, p.RepoId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return TypedResults.Problem(detail: $"No pipeline with id '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        var root = await db.Repos.AsNoTracking().Where(r => r.Id == pipeline.RepoId).Select(r => r.RootPath)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var flowFile = string.IsNullOrWhiteSpace(root)
            ? pipeline.RelativePath
            : Path.GetFullPath(Path.Combine(root, pipeline.RelativePath));

        FlowDefinition flow;
        try
        {
            if (documents.Parse(pipeline.Yaml, flowFile) is not FileFlowDocument document)
            {
                return Invalid($"Flow '{pipeline.Name}' is a '{pipeline.Kind}' flow. Only a file flow reads files, so only its source can be scanned.");
            }

            flow = document.Flow;
        }
        catch (SqlFlowException ex)
        {
            return Invalid($"Flow '{pipeline.Name}' could not be read as the last sync stored it: {SecretHygiene.RedactedMessage(ex)}");
        }

        var written = flow.Source.Location;
        if (string.IsNullOrWhiteSpace(root) && IsRelativeFile(written))
        {
            return Invalid($"Flow '{pipeline.Name}' reads '{written}', relative to its own folder, and the control plane holds no checkout of its repository to resolve it against. Sync the repository.");
        }

        SourceFileScan scan;
        try
        {
            flow = await SourceLocations.ResolveAsync(DocumentLoader.ResolveRelativeLocation(flow, flowFile), secrets, ct).ConfigureAwait(false);
            var reader = readers.FirstOrDefault(r => r.CanHandle(flow.Source.Type))
                ?? throw new SqlFlowException($"No reader handles source type '{flow.Source.Type}'.");
            scan = await reader.ScanFileColumnsAsync(flow.Source, count, ct).ConfigureAwait(false);
        }
        catch (SqlFlowException ex)
        {
            return TypedResults.Problem(
                detail: $"The source of flow '{pipeline.Name}' could not be read: {SecretHygiene.RedactedMessage(ex)}",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Source not read");
        }

        var target = await TargetAsync(db, id, flow.Target, ct).ConfigureAwait(false);
        return TypedResults.Ok(Describe(id, pipeline.Name, flow.Source.Type, written, scan, target, expected));
    }

    /// <summary>
    /// The answer from what the scan read: every column in the order the newest files first carry it, with the files that
    /// carry it; the columns new to the target and the target's columns no file carries; and each expected column, matched
    /// by name ignoring case against the columns as a load names them or as the file writes them.
    /// </summary>
    internal static SourceColumnsDto Describe(
        Guid id, string flow, string sourceType, string? location, SourceFileScan scan, SourceTargetDto? target,
        IReadOnlyList<string> expected)
    {
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
            .Select(p => new SourceColumnPresenceDto(p.Name, p.Files, p.Type, p.First, p.Last, targetColumns?.Contains(p.Name)))
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
                return new ExpectedColumnDto(
                    name,
                    read.Count - missing.Count,
                    read.Count > 0 && missing.Count == 0,
                    targetColumns?.Contains(name),
                    missing.Take(MaxMissingNamed).Select(file => file.Name ?? "(the source)").ToList());
            })
            .ToList();

        var files = scan.Files
            .Select(f => new SourceFileColumnsDto(f.Name, f.Path, f.Modified, f.SizeBytes, f.Columns.Select(c => c.Name).ToList(), f.Error))
            .ToList();
        return new SourceColumnsDto(
            id, flow, sourceType, location, scan.FilesMatched, read.Count, scan.NoFilesReason, files, columns, target,
            newColumns, notInFiles, asked, scan.Generated);

        static bool Carries(SourceFileColumns file, string name)
            => file.Columns.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                || file.HeaderNames.Any(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));

        static DateTimeOffset? Earlier(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a < b ? a : b;

        static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;
    }

    /// <summary>
    /// The table the flow loads into, as the catalog holds it: the object the flow's lineage writes (or creates) whose schema
    /// and name are the target's, with its columns in order. Null when lineage names no such object or the catalog holds no
    /// columns of it.
    /// </summary>
    private static async Task<SourceTargetDto?> TargetAsync(CatalogDbContext db, Guid pipelineId, TargetSpec target, CancellationToken ct)
    {
        var writes = nameof(LineageRelation.Writes);
        var creates = nameof(LineageRelation.Creates);
        var keys = await db.LineageEdges.AsNoTracking()
            .Where(e => e.PipelineId == pipelineId && (e.Relation == writes || e.Relation == creates))
            .Select(e => e.ObjectKey)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        if (keys.Count == 0)
        {
            return null;
        }

        var objects = await db.Objects.AsNoTracking()
            .Where(o => keys.Contains(o.Key) && o.Schema == target.Schema && o.Name == target.Table)
            .Select(o => new { o.Key, o.Database, o.Schema, o.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var item in objects.OrderByDescending(o => o.Database is not null).ThenBy(o => o.Key, StringComparer.Ordinal))
        {
            var columns = await db.ObjectColumns.AsNoTracking()
                .Where(c => c.ObjectKey == item.Key)
                .OrderBy(c => c.Ordinal)
                .Select(c => c.Name)
                .ToListAsync(ct).ConfigureAwait(false);
            if (columns.Count > 0)
            {
                return new SourceTargetDto(item.Key, item.Database, item.Schema ?? target.Schema, item.Name, columns);
            }
        }

        return null;
    }

    /// <summary>A location that is a path on disk relative to the flow's folder: not a URI, not a reference, not rooted.</summary>
    private static bool IsRelativeFile(string? location)
        => !string.IsNullOrWhiteSpace(location)
            && !location.Contains("://", StringComparison.Ordinal)
            && !SourceLocations.NamesReference(location)
            && !Path.IsPathRooted(location);

    private static ProblemHttpResult Invalid(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
}
