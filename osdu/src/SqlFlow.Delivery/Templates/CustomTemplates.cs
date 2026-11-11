using System.Globalization;

namespace SqlFlow.Delivery.Templates;

/// <summary>A kind's version as its schema id writes it, <c>major.minor.patch</c>, ordered as numbers.</summary>
public readonly record struct KindVersion(long Major, long Minor, long Patch) : IComparable<KindVersion>
{
    public int CompareTo(KindVersion other)
        => Major != other.Major ? Major.CompareTo(other.Major)
            : Minor != other.Minor ? Minor.CompareTo(other.Minor)
            : Patch.CompareTo(other.Patch);

    public static bool operator <(KindVersion left, KindVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(KindVersion left, KindVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(KindVersion left, KindVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(KindVersion left, KindVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <summary>The version <paramref name="text"/> writes (<c>1.2.0</c>), or null when it is not three whole numbers.</summary>
    public static KindVersion? Parse(string? text)
    {
        var parts = text?.Split('.');
        if (parts is not { Length: 3 })
        {
            return null;
        }

        var numbers = new long[3];
        for (var i = 0; i < 3; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit)
                || !long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return new KindVersion(numbers[0], numbers[1], numbers[2]);
    }
}

/// <summary>
/// One kind of one's own that a repository's template folders hold (osdu/docs/reference/cli/template-register.md): a kind
/// whose authority is not <c>osdu</c>, which the OSDU data definitions never publish, so a partition's Schema service holds
/// it only once someone registers it. Every file holding the kind is listed, since a partition holds one schema for a kind
/// and two files that hold different schemas of it are for a person to settle.
/// </summary>
public sealed record CustomTemplate
{
    /// <summary>The kind, <c>authority:source:entityType:major.minor.patch</c>, as its first file in path order names it.</summary>
    public required string Kind { get; init; }

    /// <summary>The kind without its version, <c>authority:source:entityType</c>: the versions of one family are one schema's history.</summary>
    public required string Family { get; init; }

    /// <summary>The kind's version.</summary>
    public required KindVersion Version { get; init; }

    /// <summary>Every file of the repository holding the kind, in path order.</summary>
    public required IReadOnlyList<TemplateFolderFile> Files { get; init; }

    /// <summary>Whether this is the highest version of its family the repository holds.</summary>
    public required bool Latest { get; init; }

    /// <summary>The template versions the files hold, each once, in ordinal order: one when every copy holds the same schema.</summary>
    public IReadOnlyList<string> TemplateVersions
        => Files.Select(f => f.Version!).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();

    /// <summary>The spellings of the kind the files use, each once: one unless two files write its letters in different cases.</summary>
    public IReadOnlyList<string> Spellings => Files.Select(f => f.Kind!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Why the repository does not say which schema the kind is, or null when it does: two files holding different schemas of
    /// it, or naming it in different cases, which OSDU reads as different kinds.
    /// </summary>
    public string? Conflict
    {
        get
        {
            if (Spellings.Count > 1)
            {
                return $"the repository names the kind {Listed(Spellings)}, spelled in different cases, which OSDU reads as different kinds: "
                    + $"{Listed(Files.Select(f => $"{f.Shown} ({f.Kind})"))}. Write it one way in every file.";
            }

            return TemplateVersions.Count > 1
                ? $"the repository holds {TemplateVersions.Count} different schemas of {Kind}: "
                    + $"{Listed(Files.Select(f => $"{f.Shown} (template version {f.Version})"))}. A partition holds one schema for a kind: "
                    + "keep one of them, or give the other a version of its own."
                : null;
        }
    }

    /// <summary>The file whose schema is the kind's: the first in path order, every other holding the same schema; null when the files conflict.</summary>
    public TemplateFolderFile? File => Conflict is null ? Files[0] : null;

    private static string Listed(IEnumerable<string> items) => string.Join(", ", items);
}

/// <summary>
/// The kinds of one's own a repository's template folders hold: every template whose kind's authority is not <c>osdu</c>.
/// The OSDU data definitions publish <c>osdu</c> kinds alone, so a kind under any other authority is one a partition's
/// Schema service holds only when it is registered there, which is what <c>sqlflow template register</c> does.
/// </summary>
public static class CustomTemplates
{
    /// <summary>The authority of the kinds the OSDU data definitions publish, which the platform holds in its shared schemas.</summary>
    public const string OsduAuthority = "osdu";

    /// <summary>Whether <paramref name="kind"/> is of one's own: a record kind whose authority is not <see cref="OsduAuthority"/>.</summary>
    public static bool IsCustom(string kind)
        => Parse(kind) is { } parts && !string.Equals(parts.Authority, OsduAuthority, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The family (<c>authority:source:entityType</c>) and version of <paramref name="kind"/>, or null when it is not
    /// <c>authority:source:entityType:major.minor.patch</c>.
    /// </summary>
    public static (string Authority, string Family, KindVersion Version)? Parse(string? kind)
    {
        var parts = kind?.Trim().Split(':');
        if (parts is not { Length: 4 } || parts.Take(3).Any(string.IsNullOrWhiteSpace) || KindVersion.Parse(parts[3]) is not { } version)
        {
            return null;
        }

        return (parts[0], $"{parts[0]}:{parts[1]}:{parts[2]}", version);
    }

    /// <summary>
    /// Every kind of one's own <paramref name="scan"/> holds, one entry per kind whatever the files holding it, ordered by
    /// family and then by version; the highest version of each family is marked <see cref="CustomTemplate.Latest"/>. A kind
    /// is told apart regardless of case, so two files naming it in different cases are one entry that says so.
    /// </summary>
    public static IReadOnlyList<CustomTemplate> Of(TemplateFolderScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var kinds = scan.Files
            .Where(file => file.IsTemplate && IsCustom(file.Kind!))
            .GroupBy(file => file.Kind!, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var files = group.OrderBy(f => f.Shown, StringComparer.Ordinal).ToList();
                var (_, family, version) = Parse(files[0].Kind)!.Value;
                return (Kind: files[0].Kind!, Family: family, Version: version, Files: files);
            })
            .ToList();

        var latest = kinds
            .GroupBy(k => k.Family, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Max(k => k.Version), StringComparer.OrdinalIgnoreCase);

        return kinds
            .OrderBy(k => k.Family, StringComparer.OrdinalIgnoreCase)
            .ThenBy(k => k.Version)
            .Select(k => new CustomTemplate
            {
                Kind = k.Kind,
                Family = k.Family,
                Version = k.Version,
                Files = k.Files,
                Latest = latest[k.Family] == k.Version,
            })
            .ToList();
    }
}
