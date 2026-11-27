namespace SqlFlow.Core.Model;

/// <summary>
/// What a scan of a source's files found (<see cref="Abstractions.ISourceReader.ScanFileColumnsAsync"/>): how many files
/// its selection matched, the newest of them with the columns each one's header carries, the columns the flow adds to
/// every row itself, and why no file matched when none did. Nothing of a row is read.
/// </summary>
/// <param name="FilesMatched">How many files the source's selection matched (its location, file pattern, path mask and
/// date window, as a run selects them).</param>
/// <param name="Files">The newest files the scan read, newest first.</param>
/// <param name="Generated">The columns the flow adds to every row itself (its provenance and key columns), which no file
/// carries.</param>
/// <param name="NoFilesReason">Why the selection matched no file (an empty location, or every file outside the date
/// window), or null when it matched some.</param>
public sealed record SourceFileScan(
    int FilesMatched,
    IReadOnlyList<SourceFileColumns> Files,
    IReadOnlyList<string> Generated,
    string? NoFilesReason);

/// <summary>
/// One file a scan read: its name and path, when it was last modified, its size, the columns its header carries in
/// order (named as a load names them), the header's names as the file writes them, and the error that kept the scan from
/// reading it, if one did. A file whose header cannot be read is one a run would fail on.
/// </summary>
/// <param name="Name">The file's name, or null for a source that is not a set of files.</param>
/// <param name="Path">Its full path or URI, or null for a source that is not a set of files.</param>
/// <param name="Modified">When it was last modified, as its store reports it.</param>
/// <param name="SizeBytes">Its size in bytes, as its store reports it.</param>
/// <param name="Columns">Its columns in order, named as a load names them.</param>
/// <param name="HeaderNames">Its columns as the file itself names them, before a load cleans the names; the same length
/// and order as <paramref name="Columns"/>.</param>
/// <param name="Error">Why its header could not be read, or null when it was.</param>
public sealed record SourceFileColumns(
    string? Name,
    string? Path,
    DateTimeOffset? Modified,
    long? SizeBytes,
    IReadOnlyList<SourceColumn> Columns,
    IReadOnlyList<string> HeaderNames,
    string? Error);
