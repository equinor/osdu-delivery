using System.Data;
using SqlFlow.Core.Model;

namespace SqlFlow.Core.Abstractions;

/// <summary>
/// The single extension point for ingesting any source - relational or non-relational. An
/// implementation declares the source <c>type</c> it handles, returns the source's columns, and
/// exposes rows as an <see cref="IDataReader"/>. Non-tabular sources (JSON, XML, documents)
/// flatten/project into the tabular reader within their implementation. The engine never knows
/// about a concrete source type.
/// </summary>
/// <remarks>
/// Raw-layer typing rule: non-relational sources (CSV, JSON, files) expose every column as
/// <see cref="string"/> - the raw layer does NOT infer data types; type assertion is a later
/// concern handled via overrides or a transform layer. Relational sources expose their predefined
/// types (including binary and others); the .NET driver translates those for bulk load.
/// </remarks>
public interface ISourceReader
{
    /// <summary>True if this reader handles the given <see cref="SourceSpec.Type"/> (case-insensitive).</summary>
    bool CanHandle(string sourceType);

    /// <summary>
    /// Returns the source's columns. Implementations should keep this cheap (metadata-only) - e.g. a
    /// relational reader can use <c>SELECT TOP 0</c>; a file reader reads only the header.
    /// </summary>
    Task<IReadOnlyList<SourceColumn>> GetColumnsAsync(SourceSpec source, CancellationToken ct = default);

    /// <summary>
    /// The columns each file of the source carries, newest file first and at most <paramref name="maxFiles"/> of them,
    /// read from each file's header as a run reads it: the same selection, the same names. What a drop zone holds now,
    /// file by file, so a column that arrived in some files and not others is seen. A source that is not a set of files
    /// answers with its columns as one entry naming no file. Reads no row.
    /// </summary>
    async Task<SourceFileScan> ScanFileColumnsAsync(SourceSpec source, int maxFiles, CancellationToken ct = default)
    {
        var columns = await GetColumnsAsync(source, ct).ConfigureAwait(false);
        return new SourceFileScan(
            1,
            [new SourceFileColumns(null, null, null, null, columns, [.. columns.Select(c => c.Name)], null)],
            [],
            null);
    }

    /// <summary>
    /// Opens a forward-only reader over the source rows (shaped to <paramref name="columns"/>) and
    /// returns the manifest of files read - the in-result file log for stateless mode.
    /// </summary>
    Task<SourceReadResult> OpenAsync(SourceSpec source, IReadOnlyList<SourceColumn> columns, CancellationToken ct = default);

    /// <summary>
    /// Post-ingest hook invoked by the engine after a successful load - e.g. for file lifecycle
    /// (copy/zip/delete). Default is a no-op for sources that have nothing to finalize.
    /// </summary>
    Task CompleteAsync(SourceSpec source, CancellationToken ct = default) => Task.CompletedTask;
}
