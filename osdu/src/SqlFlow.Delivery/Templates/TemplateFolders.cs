using System.Collections.Concurrent;
using SqlFlow.Core;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Templates;

/// <summary>What a scan of a repository found: its template folders, and what it saw that is almost one.</summary>
/// <param name="Folders">Every template folder under the repository, in ordinal path order.</param>
/// <param name="Warnings">A folder named like a template folder but not exactly (<c>Templates</c>, or <c>template</c> holding schema files), or one reached through a link, which no document reads.</param>
public sealed record TemplateFolderScan(IReadOnlyList<TemplateFolder> Folders, IReadOnlyList<string> Warnings)
{
    /// <summary>Every file of every folder, in folder order and then path order.</summary>
    public IEnumerable<TemplateFolderFile> Files => Folders.SelectMany(f => f.Files);
}

/// <summary>
/// The template folders of a repository on disk (osdu/docs/reference/concepts/templates.md, Template folders): every
/// directory named exactly <c>templates</c>, holding the bundled schemas of the templates the documents beside it and below
/// it fill. A file is a template when it is a bundled OSDU record schema whose <c>x-osdu-schema-source</c> names its kind;
/// its version is the hash of its canonical form, so a copy anywhere is the same version. What a document reads is the
/// chain of folders on its path (<see cref="ChainFor"/>); what a repository holds is the scan of all of them
/// (<see cref="Scan"/>).
/// </summary>
public static class TemplateFolders
{
    /// <summary>The name a template folder has, exactly: a folder named otherwise (<c>Templates</c>) is not read on any system.</summary>
    public const string FolderName = "templates";

    /// <summary>The singular a template folder is easily given, which the scan warns of when the folder holds schema files.</summary>
    private const string SingularName = "template";

    /// <summary>The largest file read as a template. The largest published OSDU schema, bundled, is well under a megabyte.</summary>
    public const int MaxFileBytes = 8 * 1024 * 1024;

    /// <summary>The most files one folder is read for; a folder past it reports the rest as unread.</summary>
    public const int MaxFilesPerFolder = 1000;

    /// <summary>How far up a document's path is walked, as far as the nearest <c>mappings</c> folder is looked for.</summary>
    public const int MaxAscent = 16;

    /// <summary>How deep a scan of a repository descends.</summary>
    private const int MaxDepth = 32;

    /// <summary>The most parsed files kept; the cache starts over past it.</summary>
    private const int MaxCached = 512;

    /// <summary>Folders no template is read from, as the repository sync skips them.</summary>
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "runs",
    };

    private static readonly EnumerationOptions OneLevel = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        MatchCasing = MatchCasing.CaseSensitive,
        MatchType = MatchType.Simple,
    };

    private static readonly ConcurrentDictionary<(string Path, string Hash), FileRead> Parsed = new();

    /// <summary>A template file as read, with its schema when it holds one.</summary>
    private sealed record FileRead(string? Kind, string? Version, string? Problem, SchemaSnapshot? Schema);

    /// <summary>
    /// The template folders the document at <paramref name="documentPath"/> reads: the folder named <c>templates</c> beside
    /// it and in every folder above it, nearest first, up to <paramref name="root"/> when the caller knows the repository's
    /// top (a sync, lineage), and otherwise up to the first folder holding <c>.git</c>, or <see cref="MaxAscent"/> folders
    /// up when there is none. Paths are named relative to that top wherever one is known.
    /// </summary>
    public static TemplateChain ChainFor(string documentPath, string? root = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        var full = Path.GetFullPath(documentPath);
        var start = Path.GetDirectoryName(full) ?? full;
        var top = root is null ? RepositoryTop(start) : Normalized(Path.GetFullPath(root));
        if (top is not null && !IsWithin(start, top))
        {
            // A document outside the repository it is read for reads no folder of it.
            return new TemplateChain(full, [], []);
        }

        string Shown(string path) => top is null ? path : Relative(top, path);
        var looked = new List<string>();
        var folders = new List<TemplateFolder>();
        var current = start;
        for (var i = 0; i < MaxAscent && current is not null; i++)
        {
            var candidate = Path.Combine(current, FolderName);
            looked.Add(Shown(candidate));
            if (HasFolder(current))
            {
                folders.Add(Read(candidate, Shown));
            }

            if (top is not null && Same(current, top))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }

        Func<IReadOnlyList<TemplateFolderFile>>? elsewhere = top is null ? null : () => [.. Scan(top, CancellationToken.None).Files];
        return new TemplateChain(Shown(full), looked, folders, elsewhere);
    }

    /// <summary>
    /// Every template folder under <paramref name="root"/>, each read: what the repository sync records and what a repository
    /// holds. Folders inside <c>.git</c>, any other folder whose name starts with a dot, <c>bin</c>, <c>obj</c>,
    /// <c>node_modules</c> and <c>runs</c> are not entered, nor any link, so a scan never leaves the repository or circles.
    /// </summary>
    public static TemplateFolderScan Scan(string root, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var top = Normalized(Path.GetFullPath(root));
        if (!Directory.Exists(top))
        {
            throw new DeliveryException($"The repository folder '{top}' does not exist, so its template folders cannot be read.");
        }

        string Shown(string path) => Relative(top, path);
        var folders = new List<TemplateFolder>();
        var warnings = new List<string>();
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((top, 0));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            foreach (var child in ChildDirectories(directory, warnings, Shown))
            {
                var name = Path.GetFileName(child);
                if (string.Equals(name, FolderName, StringComparison.Ordinal))
                {
                    // A folder named templates inside it is a template folder of its own, which a document below it reads,
                    // so the scan goes on looking inside it as a document's walk would.
                    folders.Add(Read(child, Shown));
                }
                else if (string.Equals(name, FolderName, StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(name, SingularName, StringComparison.OrdinalIgnoreCase) && HoldsJson(child)))
                {
                    warnings.Add($"{Shown(child)} is named '{name}', and a template folder is named '{FolderName}' exactly, so no document reads the templates in it.");
                }

                if (name.StartsWith('.') || SkippedDirectories.Contains(name) || depth + 1 >= MaxDepth)
                {
                    continue;
                }

                pending.Push((child, depth + 1));
            }
        }

        folders.Sort((a, b) => string.CompareOrdinal(a.Shown, b.Shown));
        return new TemplateFolderScan(folders, warnings);
    }

    /// <summary>
    /// The template folder at <paramref name="directory"/>: every <c>.json</c> file in it and in the folders under it, except
    /// a folder named <c>templates</c>, which is a template folder of its own, and the folders a scan skips. Files are in
    /// ordinal path order, each with the template it holds or why it holds none.
    /// </summary>
    public static TemplateFolder Read(string directory, Func<string, string> shown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(shown);
        var files = new List<string>();
        var problems = new List<TemplateFolderFile>();
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((directory, 0));
        while (pending.Count > 0)
        {
            var (current, depth) = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(current, "*", OneLevel))
                {
                    if (string.Equals(Path.GetExtension(file), ".json", StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(file);
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(current, "*", OneLevel))
                {
                    var name = Path.GetFileName(child);
                    if (!string.Equals(name, FolderName, StringComparison.Ordinal) && !name.StartsWith('.')
                        && !SkippedDirectories.Contains(name) && depth + 1 < MaxDepth)
                    {
                        pending.Push((child, depth + 1));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(new TemplateFolderFile(current, shown(current), null, null, $"the folder could not be read ({ex.Message})"));
            }
        }

        files.Sort(StringComparer.Ordinal);
        var read = new List<TemplateFolderFile>(Math.Min(files.Count, MaxFilesPerFolder) + problems.Count + 1);
        foreach (var file in files.Take(MaxFilesPerFolder))
        {
            var found = ReadFile(file, shown(file));
            read.Add(new TemplateFolderFile(file, shown(file), found.Kind, found.Version, found.Problem) { Parent = found.Schema?.Parent?.Kind, ParentProblem = found.Schema?.ParentProblem });
        }

        if (files.Count > MaxFilesPerFolder)
        {
            read.Add(new TemplateFolderFile(directory, shown(directory), null, null,
                $"the folder holds {files.Count} .json files, and only the first {MaxFilesPerFolder} in path order are read as templates"));
        }

        read.AddRange(problems);
        return new TemplateFolder(directory, shown(directory), read);
    }

    /// <summary>
    /// The schema of <paramref name="file"/>, read again from disk and checked to be the template it was listed as, so a
    /// file changed since its folder was read is never taken for the version it held.
    /// </summary>
    /// <exception cref="DeliveryException">The file holds no template now, or holds another version.</exception>
    public static SchemaSnapshot Load(TemplateFolderFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var reference = file.Reference;
        var read = ReadFile(file.Path, file.Shown);
        if (read.Schema is null)
        {
            throw new DeliveryException($"{file.Shown} held template {reference} and no longer reads: {read.Problem}");
        }

        if (!string.Equals(read.Kind, reference.Kind, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(read.Version, reference.Version, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeliveryException($"{file.Shown} held template {reference} and now holds {read.Kind} version {read.Version}; read it again.");
        }

        return read.Schema;
    }

    /// <summary>The nearest folder at or above <paramref name="directory"/> holding <c>.git</c> (a folder, or the file a worktree has), or null.</summary>
    public static string? RepositoryTop(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var current = Normalized(Path.GetFullPath(directory));
        for (var i = 0; i < 64 && current is not null; i++)
        {
            var marker = Path.Combine(current, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    /// <summary><paramref name="path"/> relative to <paramref name="top"/>, with forward slashes, as a repository names it.</summary>
    public static string Relative(string top, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(top);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var relative = Path.GetRelativePath(top, path).Replace('\\', '/');
        return relative == "." ? string.Empty : relative;
    }

    /// <summary>
    /// Whether <paramref name="directory"/> holds a <c>.json</c> file of its own: a folder named <c>template</c> is warned of
    /// only then, so a folder of that name kept for something else is left alone.
    /// </summary>
    private static bool HoldsJson(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.json", OneLevel).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Whether <paramref name="directory"/> holds a folder named exactly <see cref="FolderName"/>, not a link to one.</summary>
    private static bool HasFolder(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory, FolderName, OneLevel).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> ChildDirectories(string directory, List<string> warnings, Func<string, string> shown)
    {
        List<string> children;
        try
        {
            children = Directory.EnumerateDirectories(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"{shown(directory)} could not be read for template folders ({ex.Message}).");
            return [];
        }

        children.Sort(StringComparer.Ordinal);
        var real = new List<string>(children.Count);
        foreach (var child in children)
        {
            var link = IsLink(child);
            if (link && string.Equals(Path.GetFileName(child), FolderName, StringComparison.Ordinal))
            {
                warnings.Add($"{shown(child)} is a link, and template folders are read only where they are, so no document reads the templates behind it.");
            }

            if (!link)
            {
                real.Add(child);
            }
        }

        return real;
    }

    private static bool IsLink(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Reads one file as a template. A parse is kept by the file's path and the hash of its bytes, so an unchanged file is
    /// parsed once per process however many documents read it, and a file rewritten with other content is parsed again
    /// however quickly it was rewritten; a file that cannot be opened is not kept, since that can pass.
    /// </summary>
    private static FileRead ReadFile(string path, string shown)
    {
        byte[] bytes;
        DateTime written;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new FileRead(null, null, "the file no longer exists", null);
            }

            if (info.Length > MaxFileBytes)
            {
                return new FileRead(null, null, $"the file is {info.Length} bytes, and a template file holds one bundled schema of at most {MaxFileBytes} bytes", null);
            }

            written = info.LastWriteTimeUtc;
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileRead(null, null, $"the file could not be read ({ex.Message})", null);
        }

        if (bytes.Length > MaxFileBytes)
        {
            // Written past the limit between the look and the read.
            return new FileRead(null, null, $"the file is {bytes.Length} bytes, and a template file holds one bundled schema of at most {MaxFileBytes} bytes", null);
        }

        var key = (path, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
        if (Parsed.TryGetValue(key, out var known))
        {
            return known;
        }

        try
        {
            var root = TemplateSources.ParseObject(Text(bytes), shown);
            var kind = TemplateSources.KindDeclaredBy(root, shown);
            var schema = TemplateSources.Validated(kind, root, new DateTimeOffset(written, TimeSpan.Zero), shown);
            return Keep(key, new FileRead(schema.Kind, schema.Version, null, schema));
        }
        catch (Exception ex) when (ex is SqlFlowException)
        {
            return Keep(key, new FileRead(null, null, Unprefixed(ex.Message, shown), null));
        }
    }

    /// <summary>The text of a file's bytes, as a reader of text files takes it: a byte order mark says the encoding, and UTF-8 otherwise.</summary>
    private static string Text(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static FileRead Keep((string Path, string Hash) key, FileRead read)
    {
        if (Parsed.Count >= MaxCached)
        {
            Parsed.Clear();
        }

        Parsed[key] = read;
        return read;
    }

    /// <summary>A message about a file without the file's name in front, since a folder listing names the file itself.</summary>
    private static string Unprefixed(string message, string shown)
        => message.StartsWith(shown + ": ", StringComparison.Ordinal) ? message[(shown.Length + 2)..] : message;

    private static string Normalized(string path) => Path.TrimEndingDirectorySeparator(path);

    private static bool Same(string a, string b)
        => string.Equals(Normalized(a), Normalized(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsWithin(string path, string top)
    {
        var relative = Path.GetRelativePath(top, path);
        var climbs = relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        return relative == "." || (!climbs && !Path.IsPathRooted(relative));
    }
}
