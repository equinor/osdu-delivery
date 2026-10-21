using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Templates;

/// <summary>A release of the OSDU data definitions: a version tag of the repository, and the commit it names.</summary>
public sealed record DataDefinitionsRelease(string Name, string Commit, DateTimeOffset? PublishedUtc);

/// <summary>
/// One record schema a release publishes: its kind, entity type and version, the status the release gives it (PUBLISHED,
/// DEVELOPMENT or OBSOLETE; null when the index gives none), and its file under <see cref="OsduDataDefinitions.TreeRoot"/>.
/// </summary>
public sealed record DataDefinitionsSchema(string Kind, string EntityType, string Version, string? Status, string Path);

/// <summary>Every record schema one release publishes, by entity type and newest version first.</summary>
public sealed record DataDefinitionsIndex(DataDefinitionsRelease Release, IReadOnlyList<DataDefinitionsSchema> Schemas);

/// <summary>
/// A kind's schema bundled from one release, the file it was bundled from, and every file the bundle read (the kind's own
/// file first, then the shared schemas it refers to), each a path under <see cref="OsduDataDefinitions.TreeRoot"/>.
/// </summary>
public sealed record DataDefinitionsSchemaFile(DataDefinitionsRelease Release, string Path, SchemaSnapshot Schema, IReadOnlyList<string> Files)
{
    /// <summary>Where a template saved from it came from, as the template's origin records it: the release, its commit and the file.</summary>
    public string Origin
        => $"OSDU data definitions {Release.Name} ({OsduDataDefinitions.ShortCommit(Release)}) {OsduDataDefinitions.TreeRoot}/{Path}";
}

/// <summary>
/// A schema brought in from outside the data definitions (an uploaded file, pasted JSON), as a template is saved from it.
/// <see cref="Release"/> is the release its references were read from, and <see cref="Files"/> the files read, each a path
/// under <see cref="OsduDataDefinitions.TreeRoot"/>; null and empty for a schema that arrived bundled.
/// </summary>
public sealed record ImportedSchema(SchemaSnapshot Schema, DataDefinitionsRelease? Release, IReadOnlyList<string> Files)
{
    /// <summary>
    /// Where a template saved from it came from: <paramref name="source"/> (such as <c>file WellboreTest.1.5.1.json</c>), and
    /// the release and commit its references were read from when they were.
    /// </summary>
    public string Origin(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return Release is null
            ? source
            : $"{source}, references from OSDU data definitions {Release.Name} ({OsduDataDefinitions.ShortCommit(Release)})";
    }
}

/// <summary>A kind's schema file exactly as a release publishes it, unbundled, with the status the release gives it.</summary>
public sealed record DataDefinitionsPublishedFile(DataDefinitionsRelease Release, string Path, string? Status, string Text);

/// <summary>What a sync did: the releases as the repository lists them now, when that list was read, and the releases it downloaded.</summary>
public sealed record DataDefinitionsSync(IReadOnlyList<DataDefinitionsRelease> Releases, DateTimeOffset SyncedUtc, IReadOnlyList<string> Downloaded);

/// <summary>
/// The OSDU data definitions could not answer. <see cref="NotFound"/> when what was asked for is not in them (a release, or
/// a kind a release does not publish); otherwise the repository could not be reached, refused, sent something other than
/// what it serves, or the local copy could not be written or read.
/// </summary>
public sealed class DataDefinitionsException : DeliveryException
{
    public DataDefinitionsException(string message, bool notFound)
        : base(message)
    {
        NotFound = notFound;
    }

    public DataDefinitionsException(string message, bool notFound, Exception innerException)
        : base(message, innerException)
    {
        NotFound = notFound;
    }

    public bool NotFound { get; }
}

/// <summary>
/// The OSDU data definitions: the Open Group's public repository of the OSDU schemas (<see cref="DefaultWebUrl"/>), the
/// canonical source of every <c>osdu:wks</c> kind, kept as a local copy. A release is a version tag; the first time a
/// release is read, its whole <c>Generated</c> folder is downloaded as one archive through the repository's GitLab API and
/// unpacked under <see cref="CacheDirectory"/>, and from then on every file of it is read from disk, across restarts. The
/// release list is kept on disk beside it, read again from the repository when it is older than the freshness given, and
/// on demand by <see cref="SyncAsync"/>. A release's <c>Generated/SchemaStatus.json</c> lists every kind it publishes, and
/// a kind's schema is the file under <c>Generated</c> that declares the kind, bundled with every file it refers to exactly
/// as a local checkout bundles (<see cref="TemplateSources.FromDirectoryAsync"/>).
/// <para>
/// A release is on disk only while every file its download unpacked is there (<see cref="ManifestFile"/>). The local copy
/// lives under the temp folder by default, where a cleanup can delete the files and leave the folders standing, and the
/// files carry their commit's date, so a cleanup that goes by age takes them first. A release found short of a file is
/// downloaded again and put back whole, by the next read that needs it or by a sync.
/// </para>
/// </summary>
public sealed class OsduDataDefinitions : IOfficialExamples
{
    /// <summary>The named HTTP client a host registers for the repository.</summary>
    public const string HttpClientName = "osdu-data-definitions";

    /// <summary>The folder of the repository that holds the schemas, laid out by group.</summary>
    public const string TreeRoot = "Generated";

    /// <summary>The release's index: every kind it publishes, with its status.</summary>
    public const string IndexFile = "SchemaStatus.json";

    /// <summary>The folder of the repository that holds an example record of each kind version, laid out by group.</summary>
    public const string ExamplesRoot = "Examples";

    /// <summary>
    /// The file a release's folder in the local copy holds beside its <see cref="TreeRoot"/> folder once every file of the
    /// release's archive is in place: each file's path under <see cref="TreeRoot"/> and its length. It is written last.
    /// </summary>
    public const string ManifestFile = "release-files.json";

    /// <summary>The largest page of the tag list read.</summary>
    private const int MaxListBytes = 4 * 1024 * 1024;

    /// <summary>The largest file of a release kept. The release index, the largest file, is a few hundred kilobytes.</summary>
    private const long MaxFileBytes = 16L * 1024 * 1024;

    /// <summary>The most a release's Generated folder may unpack to; a release unpacks to a few tens of megabytes.</summary>
    private const long MaxReleaseBytes = 1024L * 1024 * 1024;

    private const int TagPageSize = 100;

    private const int MaxTagPages = 50;

    private const int MaxCachedIndexes = 16;

    private const int MaxCachedFiles = 8192;

    private const int MaxCachedExamples = 1024;

    /// <summary>How long reading one example record may take: it is read while a person waits for a check.</summary>
    private static readonly TimeSpan ExampleTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long one page of the tag list may take.</summary>
    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How old the release list must be before a release it does not name makes it read the list again.</summary>
    private static readonly TimeSpan ReleasesRereadAfter = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions StoreJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Func<HttpClient> _client;
    private readonly Uri _api;
    private readonly string _releasesFile;
    private readonly TimeSpan _freshness;
    private readonly TimeSpan _downloadTimeout;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, ReleaseSchemas> _indexes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LocalRelease> _local = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<LocalRelease>>> _downloads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OfficialExample?> _examples = new(StringComparer.Ordinal);
    private ReleaseList? _releases;

    /// <param name="client">The HTTP client for a request; the caller owns its lifetime.</param>
    /// <param name="apiUrl">The GitLab API URL of the data definitions project, such as <see cref="DefaultApiUrl"/>.</param>
    /// <param name="webUrl">The project's web page, such as <see cref="DefaultWebUrl"/>, which links point into.</param>
    /// <param name="cacheDirectory">Where the local copy lives: one folder per release commit, and the release list.</param>
    /// <param name="freshness">How old the release list may be before it is read again; <see cref="Timeout.InfiniteTimeSpan"/> keeps it until a sync.</param>
    /// <param name="downloadTimeout">How long downloading one release may take.</param>
    /// <param name="time">The clock the release list and the captured schemas are stamped with.</param>
    public OsduDataDefinitions(
        Func<HttpClient> client, Uri apiUrl, Uri webUrl, string cacheDirectory, TimeSpan freshness, TimeSpan downloadTimeout, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(webUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentNullException.ThrowIfNull(time);
        if (!apiUrl.IsAbsoluteUri || !webUrl.IsAbsoluteUri)
        {
            throw new ArgumentException("The data definitions' API and web URLs must be absolute.");
        }

        if (freshness < TimeSpan.Zero && freshness != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(freshness), freshness, "The freshness is zero or more, or Timeout.InfiniteTimeSpan.");
        }

        if (downloadTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(downloadTimeout), downloadTimeout, "The download timeout must be positive.");
        }

        _client = client;
        _api = WithTrailingSlash(apiUrl);
        WebUrl = WithTrailingSlash(webUrl);
        CacheDirectory = Path.GetFullPath(cacheDirectory);
        var source = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_api.AbsoluteUri)))[..16];
        _releasesFile = Path.Combine(CacheDirectory, $"releases-{source}.json");
        _freshness = freshness;
        _downloadTimeout = downloadTimeout;
        _time = time;
    }

    /// <summary>The GitLab API URL of the Open Group's data definitions project.</summary>
    public static Uri DefaultApiUrl { get; } = new("https://community.opengroup.org/api/v4/projects/osdu%2Fdata%2Fdata-definitions/");

    /// <summary>The web page of the Open Group's data definitions project.</summary>
    public static Uri DefaultWebUrl { get; } = new("https://community.opengroup.org/osdu/data/data-definitions/");

    /// <summary>The local copy's default place: <c>sqlflow/osdu-data-definitions</c> under the temp folder.</summary>
    public static string DefaultCacheDirectory { get; } = Path.Combine(Path.GetTempPath(), "sqlflow", "osdu-data-definitions");

    /// <summary>How old the release list may be before it is read again: a day, since releases come a few times a year and a sync reads it at once.</summary>
    public static TimeSpan DefaultFreshness { get; } = TimeSpan.FromDays(1);

    /// <summary>How long downloading one release may take.</summary>
    public static TimeSpan DefaultDownloadTimeout { get; } = TimeSpan.FromMinutes(15);

    /// <summary>The project's web page.</summary>
    public Uri WebUrl { get; }

    /// <summary>Where the local copy lives.</summary>
    public string CacheDirectory { get; }

    /// <summary>When the release list was last read from the repository, or null before it has been read or loaded.</summary>
    public DateTimeOffset? SyncedUtc => _releases?.ReadUtc;

    /// <summary>
    /// Whether the release is in the local copy with every file of it, so reading it needs no download. A release this
    /// instance found whole stays whole to it until a read finds a file gone; <see cref="SyncAsync"/> looks at the disk again.
    /// </summary>
    public bool IsLocal(DataDefinitionsRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return _local.ContainsKey(release.Commit) || CheckLocal(release) is not null;
    }

    /// <summary>The release's schema folder on the project's web page.</summary>
    public Uri ReleaseWebUrl(DataDefinitionsRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return new Uri(WebUrl, $"-/tree/{Uri.EscapeDataString(release.Name)}/{TreeRoot}");
    }

    /// <summary>A schema file of the release on the project's web page.</summary>
    public Uri FileWebUrl(DataDefinitionsRelease release, string path)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new Uri(WebUrl, $"-/blob/{Uri.EscapeDataString(release.Name)}/{TreeRoot}/{path}");
    }

    /// <summary>The releases, newest first: the tags named <c>v</c> and a dotted version.</summary>
    public Task<IReadOnlyList<DataDefinitionsRelease>> ReleasesAsync(CancellationToken ct = default) => ReleasesAsync(reread: false, ct);

    /// <summary>The release named <paramref name="name"/>, or the newest when no name is given.</summary>
    public async Task<DataDefinitionsRelease> ReleaseAsync(string? name, CancellationToken ct = default)
    {
        var releases = await ReleasesAsync(reread: false, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(name))
        {
            return releases[0];
        }

        var wanted = name.Trim();
        var found = releases.FirstOrDefault(r => string.Equals(r.Name, wanted, StringComparison.Ordinal));
        if (found is null && _releases is { } list && _time.GetUtcNow() - list.ReadUtc >= ReleasesRereadAfter)
        {
            // A release tagged since the list was read is found on a fresh read.
            releases = await ReleasesAsync(reread: true, ct).ConfigureAwait(false);
            found = releases.FirstOrDefault(r => string.Equals(r.Name, wanted, StringComparison.Ordinal));
        }

        return found ?? throw new DataDefinitionsException($"The OSDU data definitions have no release '{wanted}'; the latest is {releases[0].Name}.", notFound: true);
    }

    /// <summary>
    /// Reads the release list again from the repository and makes sure the release named <paramref name="release"/> (the
    /// newest when none is named) is in the local copy with every file of it, downloading it when it is not there or is
    /// short of a file. It looks at the disk, not at what this instance found before.
    /// </summary>
    public async Task<DataDefinitionsSync> SyncAsync(string? release, CancellationToken ct = default)
    {
        var releases = await ReleasesAsync(reread: true, ct).ConfigureAwait(false);
        var syncedUtc = _releases?.ReadUtc ?? _time.GetUtcNow();
        var wanted = string.IsNullOrWhiteSpace(release)
            ? releases[0]
            : releases.FirstOrDefault(r => string.Equals(r.Name, release.Trim(), StringComparison.Ordinal))
                ?? throw new DataDefinitionsException($"The OSDU data definitions have no release '{release.Trim()}'; the latest is {releases[0].Name}.", notFound: true);

        var downloaded = new List<string>();
        _local.TryRemove(wanted.Commit, out _);
        if (!IsLocal(wanted))
        {
            await EnsureLocalAsync(wanted, ct).ConfigureAwait(false);
            downloaded.Add(wanted.Name);
        }

        return new DataDefinitionsSync(releases, syncedUtc, downloaded);
    }

    /// <summary>Every record schema the release publishes (the newest release when none is named).</summary>
    public async Task<DataDefinitionsIndex> IndexAsync(string? release, CancellationToken ct = default)
        => (await ReadReleaseAsync(release, ct).ConfigureAwait(false)).Index;

    /// <summary>
    /// The release read whole and kept: every kind it holds a schema file for, and the record schemas among them, which is
    /// what the index is. A template is laid out from a record schema, so a kind the release publishes but holds no record
    /// schema for (the abstract building blocks, the manifest, and the content schemas, which describe what sits inside a
    /// record rather than a record) is not in the index: it could never be browsed, generated from, or compared.
    /// </summary>
    private async Task<ReleaseSchemas> ReadReleaseAsync(string? release, CancellationToken ct)
    {
        var chosen = await ReleaseAsync(release, ct).ConfigureAwait(false);
        if (_indexes.TryGetValue(chosen.Commit, out var cached))
        {
            return cached;
        }

        var what = $"{TreeRoot}/{IndexFile} at {chosen.Name}";
        if (Parse(await ReadFileAsync(chosen, IndexFile, ct).ConfigureAwait(false), what) is not JsonObject statuses)
        {
            throw new DataDefinitionsException($"{what} is not the object of kinds and their statuses a release index is.", notFound: false);
        }

        var files = await ReadLocalAsync(chosen, local => ScanAsync(chosen, local, ct), ct).ConfigureAwait(false);
        var schemas = new List<DataDefinitionsSchema>();
        foreach (var (kind, status) in statuses)
        {
            if (!FlowMapper.IsRecordKind(kind) || !files.TryGetValue(kind, out var file) || !file.IsRecord)
            {
                continue;
            }

            var parts = kind.Split(':');
            var text = status is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
            schemas.Add(new DataDefinitionsSchema(kind, parts[2], parts[3], text, file.Path));
        }

        schemas.Sort((a, b) =>
        {
            var byType = string.CompareOrdinal(a.EntityType, b.EntityType);
            return byType != 0 ? byType : CompareVersions(b.Version, a.Version);
        });

        var read = new ReleaseSchemas(new DataDefinitionsIndex(chosen, schemas), files);
        if (_indexes.Count >= MaxCachedIndexes)
        {
            _indexes.Clear();
        }

        _indexes[chosen.Commit] = read;
        return read;
    }

    /// <summary>
    /// A kind's schema from the release (the newest when none is named), bundled with every file it refers to and checked
    /// as a template is saved: it describes the kind, refers to nothing outside itself, and declares <c>data</c>.
    /// </summary>
    public async Task<DataDefinitionsSchemaFile> FetchAsync(string? release, string kind, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var wanted = kind.Trim();
        TemplateSources.RequireKind(wanted);
        var read = await ReadReleaseAsync(release, ct).ConfigureAwait(false);
        var chosen = read.Index.Release;
        var path = PathOf(read, wanted);
        var files = new List<string>();
        var bundled = await SchemaBundler.BundleTreeAsync(path, (file, token) => ReadSchemaFileAsync(chosen, file, files, token), ct).ConfigureAwait(false);

        var schema = TemplateSources.Validated(wanted, bundled, _time.GetUtcNow(), $"{TreeRoot}/{path} at {chosen.Name}");
        return new DataDefinitionsSchemaFile(chosen, path, schema, files);
    }

    /// <summary>
    /// A schema brought in from outside the data definitions, checked as a template is saved from it. A schema whose every
    /// reference is already in its definitions is taken as it is, and reads nothing. A schema that refers to the shared
    /// schemas of the data definitions, as every file a release publishes does, is bundled with those files from the release
    /// named (the newest when none is), resolved as though it were the kind's own file in that release. <paramref name="where"/>
    /// names the schema in errors.
    /// </summary>
    public async Task<ImportedSchema> ImportAsync(string json, string kind, string? release, string where, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(where);
        var wanted = kind.Trim();
        TemplateSources.RequireKind(wanted);
        // A schema of one's own that names no kind is given the one it is imported as, so the template it saves, and the file
        // a template folder keeps of it, say which kind they hold.
        var root = TemplateSources.WithKind(TemplateSources.ParseObject(json, where), wanted);
        if (!TemplateSources.RefersOutside(root))
        {
            return new ImportedSchema(TemplateSources.Validated(wanted, root, _time.GetUtcNow(), where), null, []);
        }

        var read = await ReadReleaseAsync(release, ct).ConfigureAwait(false);
        var chosen = read.Index.Release;
        var path = PathOf(read, wanted);
        var files = new List<string>();
        var bundled = await SchemaBundler.BundleTreeAsync(path, root, (file, token) => ReadSchemaFileAsync(chosen, file, files, token), ct).ConfigureAwait(false);
        var schema = TemplateSources.Validated(wanted, bundled, _time.GetUtcNow(), $"{where} with its references from {chosen.Name}");
        return new ImportedSchema(schema, chosen, files);
    }

    /// <summary>The first twelve characters of the release's commit, as an origin names it.</summary>
    public static string ShortCommit(DataDefinitionsRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return release.Commit.Length > 12 ? release.Commit[..12] : release.Commit;
    }

    /// <summary>
    /// A file of the release's schema tree exactly as published, by its path under <see cref="TreeRoot"/>, such as one of
    /// the files a bundle read (<see cref="DataDefinitionsSchemaFile.Files"/>).
    /// </summary>
    public Task<string> FileTextAsync(DataDefinitionsRelease release, string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!IsTreePath(path))
        {
            throw new ArgumentException($"'{path}' is not a path inside the data definitions' {TreeRoot} folder.", nameof(path));
        }

        return ReadFileAsync(release, path, ct);
    }

    /// <summary>
    /// A kind's schema file as the release (the newest when none is named) publishes it, before any reference is resolved,
    /// and the status the release's index gives it (null when the index does not list the kind).
    /// </summary>
    public async Task<DataDefinitionsPublishedFile> PublishedFileAsync(string? release, string kind, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var wanted = kind.Trim();
        TemplateSources.RequireKind(wanted);
        var read = await ReadReleaseAsync(release, ct).ConfigureAwait(false);
        var path = PathOf(read, wanted);
        var text = await ReadFileAsync(read.Index.Release, path, ct).ConfigureAwait(false);
        var status = read.Index.Schemas.FirstOrDefault(s => string.Equals(s.Kind, wanted, StringComparison.Ordinal))?.Status;
        return new DataDefinitionsPublishedFile(read.Index.Release, path, status, text);
    }

    /// <summary>
    /// The example record the newest release publishes for <paramref name="kind"/> (<see cref="ExamplesRoot"/>, as
    /// <c>Examples/work-product-component/WellLog.1.4.0.json</c>), or null when it publishes none or the kind is not one
    /// of OSDU's. Each example is read from the repository once and kept beside the release in the local copy, and a
    /// release that publishes none for a kind is remembered there too. A repository that cannot be reached throws.
    /// </summary>
    public async Task<OfficialExample?> ExampleAsync(string kind, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var wanted = kind.Trim();
        if (ExamplePath(wanted) is not { } path)
        {
            return null;
        }

        var release = await ReleaseAsync(null, ct).ConfigureAwait(false);
        var key = release.Commit + ":" + path;
        if (_examples.TryGetValue(key, out var known))
        {
            return known;
        }

        var example = await ReadExampleAsync(release, wanted, path, ct).ConfigureAwait(false);
        if (_examples.Count >= MaxCachedExamples)
        {
            _examples.Clear();
        }

        _examples[key] = example;
        return example;
    }

    /// <summary>
    /// Where the data definitions keep the example record of an <c>osdu</c> kind: <c>Examples/{group}/{entity}.{version}.json</c>,
    /// the group and entity read from the kind's entity type (<c>work-product-component--WellLog</c>). Null for a kind of
    /// another authority, or one whose parts could not name a file of the repository.
    /// </summary>
    public static string? ExamplePath(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var parts = kind.Split(':');
        if (parts.Length != 4 || !string.Equals(parts[0], "osdu", StringComparison.Ordinal))
        {
            return null;
        }

        var type = parts[2];
        var dash = type.IndexOf("--", StringComparison.Ordinal);
        if (dash <= 0 || dash + 2 >= type.Length)
        {
            return null;
        }

        var group = type[..dash];
        var entity = type[(dash + 2)..];
        static bool Plain(string part) => part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
        if (!Plain(group) || !Plain(entity) || VersionNumbers(parts[3]) is null)
        {
            return null;
        }

        var path = $"{ExamplesRoot}/{group}/{entity}.{parts[3]}.json";
        return IsTreePath(path) ? path : null;
    }

    private async Task<OfficialExample?> ReadExampleAsync(DataDefinitionsRelease release, string kind, string path, CancellationToken ct)
    {
        var what = $"{path} at {release.Name}";
        var file = Path.Combine(ReleaseDirectory(release), path.Replace('/', Path.DirectorySeparatorChar));
        var none = file + ".none";
        if (File.Exists(none))
        {
            return null;
        }

        string text;
        if (File.Exists(file))
        {
            try
            {
                if (new FileInfo(file).Length > MaxListBytes)
                {
                    throw ListTooLarge(what);
                }

                text = (await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)).TrimStart('\uFEFF');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new DataDefinitionsException($"{what} could not be read from the local copy at {CacheDirectory}: {ex.Message}", notFound: false, ex);
            }
        }
        else
        {
            try
            {
                (text, _) = await GetListPageAsync(
                    $"repository/files/{Uri.EscapeDataString(path)}/raw?ref={Uri.EscapeDataString(release.Commit)}", what, ct, ExampleTimeout).ConfigureAwait(false);
            }
            catch (DataDefinitionsException ex) when (ex.NotFound)
            {
                await KeepAsync(none, string.Empty, ct).ConfigureAwait(false);
                return null;
            }

            await KeepAsync(file, text, ct).ConfigureAwait(false);
        }

        return Parse(text, what) is JsonObject record
            ? new OfficialExample(kind, release.Name, path, new Uri(WebUrl, $"-/blob/{Uri.EscapeDataString(release.Name)}/{path}"), record)
            : throw new DataDefinitionsException($"{what} is not a JSON object.", notFound: false);
    }

    /// <summary>
    /// Writes a file of the local copy whole or not at all. A copy that cannot be written is read from the repository again
    /// next time, which is all a failed write costs, so it is not an error of the read that asked for it.
    /// </summary>
    private static async Task KeepAsync(string file, string text, CancellationToken ct)
    {
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(temporary, text, ct).ConfigureAwait(false);
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not kept: the next read asks the repository again (see the summary).
        }
        finally
        {
            DeleteQuietly(temporary);
        }
    }

    private async Task<IReadOnlyList<DataDefinitionsRelease>> ReleasesAsync(bool reread, CancellationToken ct)
    {
        var known = _releases ?? await LoadStoredReleasesAsync(ct).ConfigureAwait(false);
        if (!reread && known is not null && Fresh(known.ReadUtc))
        {
            _releases = known;
            return known.Releases;
        }

        IReadOnlyList<DataDefinitionsRelease> read;
        try
        {
            read = await ReadTagsAsync(ct).ConfigureAwait(false);
        }
        catch (DataDefinitionsException ex) when (!reread && !ex.NotFound && known is not null)
        {
            // The repository is out of reach and a list read before is on disk: keep serving it, with the time it was read,
            // which the page shows. A sync asks for a fresh list and reports the failure instead.
            _releases = known;
            return known.Releases;
        }

        var list = new ReleaseList(read, _time.GetUtcNow());
        await StoreReleasesAsync(list, ct).ConfigureAwait(false);
        _releases = list;
        return read;
    }

    private async Task<IReadOnlyList<DataDefinitionsRelease>> ReadTagsAsync(CancellationToken ct)
    {
        const string what = "the list of release tags";
        var releases = new List<DataDefinitionsRelease>();
        string? page = "1";
        for (var pages = 0; page is not null; pages++)
        {
            if (pages == MaxTagPages)
            {
                throw new DataDefinitionsException(
                    string.Create(CultureInfo.InvariantCulture, $"The OSDU data definitions at {WebUrl} list more than {MaxTagPages * TagPageSize} tags, more than a release list holds."),
                    notFound: false);
            }

            var (text, next) = await GetListPageAsync(
                string.Create(CultureInfo.InvariantCulture, $"repository/tags?per_page={TagPageSize}&page={page}"), what, ct).ConfigureAwait(false);
            if (Parse(text, what) is not JsonArray tags)
            {
                throw new DataDefinitionsException($"The OSDU data definitions answered {what} with something other than a list.", notFound: false);
            }

            foreach (var tag in tags.OfType<JsonObject>())
            {
                var name = Text(tag, "name");
                var commit = tag["commit"] as JsonObject;
                var id = commit is null ? null : Text(commit, "id");
                if (name is null || id is null || name.Length < 2 || name[0] != 'v' || VersionNumbers(name[1..]) is null || !IsCommitId(id))
                {
                    continue;
                }

                DateTimeOffset? published = commit is not null && Text(commit, "committed_date") is { } date
                    && DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                        ? parsed.ToUniversalTime()
                        : null;
                releases.Add(new DataDefinitionsRelease(name, id, published));
            }

            page = string.IsNullOrWhiteSpace(next) ? null : next.Trim();
            if (page is not null && !page.All(char.IsAsciiDigit))
            {
                throw new DataDefinitionsException($"The OSDU data definitions answered {what} with a next page '{page}' that is not a page number.", notFound: false);
            }
        }

        if (releases.Count == 0)
        {
            throw new DataDefinitionsException($"The OSDU data definitions at {WebUrl} have no release tag (v and a dotted version).", notFound: true);
        }

        releases.Sort((a, b) => CompareVersions(b.Name[1..], a.Name[1..]));
        return releases;
    }

    private async Task<ReleaseList?> LoadStoredReleasesAsync(CancellationToken ct)
    {
        if (!File.Exists(_releasesFile))
        {
            return null;
        }

        try
        {
            var stream = File.OpenRead(_releasesFile);
            await using (stream.ConfigureAwait(false))
            {
                var stored = await JsonSerializer.DeserializeAsync<StoredReleases>(stream, StoreJson, ct).ConfigureAwait(false);
                return stored is { Releases.Count: > 0 } && stored.Releases.All(r => IsCommitId(r.Commit))
                    ? new ReleaseList(stored.Releases, stored.ReadUtc)
                    : null;
            }
        }
        catch (JsonException)
        {
            // A damaged list is not an answer: the list is read again from the repository, which rewrites the file.
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DataDefinitionsException($"The release list of the local copy at {CacheDirectory} could not be read: {ex.Message}", notFound: false, ex);
        }
    }

    private async Task StoreReleasesAsync(ReleaseList list, CancellationToken ct)
    {
        var temporary = _releasesFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var stream = File.Create(temporary);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, new StoredReleases(list.ReadUtc, [.. list.Releases]), StoreJson, ct).ConfigureAwait(false);
            }

            File.Move(temporary, _releasesFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DataDefinitionsException($"The release list could not be written to the local copy at {CacheDirectory}: {ex.Message}", notFound: false, ex);
        }
        finally
        {
            DeleteQuietly(temporary);
        }
    }

    private async Task<string> ReadFileAsync(DataDefinitionsRelease release, string path, CancellationToken ct)
    {
        var key = release.Commit + ":" + path;
        if (_files.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var what = $"{TreeRoot}/{path} at {release.Name}";
        var file = Path.Combine(ReleaseDirectory(release), TreeRoot, path.Replace('/', Path.DirectorySeparatorChar));
        var text = await ReadLocalAsync(release, async local =>
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists && local.Holds(path))
                {
                    throw new LocalFileMissingException(path);
                }

                if (!info.Exists)
                {
                    throw new DataDefinitionsException($"The OSDU data definitions have no {what}.", notFound: true);
                }

                if (info.Length > MaxFileBytes)
                {
                    throw new DataDefinitionsException(string.Create(CultureInfo.InvariantCulture, $"{what} holds more than {MaxFileBytes / 1024 / 1024} MB, more than any schema file holds."), notFound: false);
                }

                return (await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)).TrimStart('﻿');
            }
            catch (Exception ex) when ((ex is FileNotFoundException or DirectoryNotFoundException) && local.Holds(path))
            {
                // Gone between the look and the read.
                throw new LocalFileMissingException(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new DataDefinitionsException($"{what} could not be read from the local copy at {CacheDirectory}: {ex.Message}", notFound: false, ex);
            }
        }, ct).ConfigureAwait(false);

        if (_files.Count >= MaxCachedFiles)
        {
            _files.Clear();
        }

        _files[key] = text;
        return text;
    }

    /// <summary>A schema file of the release as a bundle reads it, noted in <paramref name="files"/> in the order it is read.</summary>
    private async Task<JsonObject> ReadSchemaFileAsync(DataDefinitionsRelease release, string file, List<string> files, CancellationToken ct)
    {
        files.Add(file);
        var what = $"{TreeRoot}/{file} at {release.Name}";
        return Parse(await ReadFileAsync(release, file, ct).ConfigureAwait(false), what) as JsonObject
            ?? throw new DataDefinitionsException($"{what} is not a JSON object.", notFound: false);
    }

    /// <summary>
    /// Where the release keeps a kind's schema file, under <see cref="TreeRoot"/>. A kind the release holds no file for
    /// keeps the path its entity group names, so the file that was looked for is the file the error names.
    /// </summary>
    private static string PathOf(ReleaseSchemas read, string kind)
        => read.Files.TryGetValue(kind, out var file) ? file.Path : SchemaBundler.KindPath(kind);

    /// <summary>
    /// Every kind the release holds a schema file for: where the file sits under <see cref="TreeRoot"/>, and whether it is
    /// a record schema a template can be laid out from. The tree, not the kind, says where a schema is: a release files the
    /// generic kinds (<c>osdu:wks:dataset--GenericDataset:1.0.0</c> and its four siblings) under <c>manifest/</c>, where
    /// their entity group would have them under <c>dataset/</c> and the rest. A file that is not valid JSON, is not an
    /// object, or declares no kind of the shape a record carries is no kind's schema here and is left out, and so is one
    /// larger than a schema file may be; asking for such a kind by name still reads its file and reports what is wrong
    /// with it. The files read are the ones the release's download unpacked (<paramref name="local"/>), so a file the
    /// local copy lost is noticed rather than leaving its kind out.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, TreeFile>> ScanAsync(DataDefinitionsRelease release, LocalRelease local, CancellationToken ct)
    {
        var root = Path.Combine(ReleaseDirectory(release), TreeRoot);
        var what = $"{TreeRoot} at {release.Name}";

        // Ordinal order of the paths, so which file a kind is read from never depends on how a file system lists a folder.
        var paths = local.Files
            .Where(f => f.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && f.Value <= MaxFileBytes)
            .Select(f => f.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        var found = new Dictionary<string, TreeFile>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var file = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            string text;
            try
            {
                text = (await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)).TrimStart('﻿');
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new LocalFileMissingException(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new DataDefinitionsException($"{what} could not be read from the local copy at {CacheDirectory}: {ex.Message}", notFound: false, ex);
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                continue;
            }

            if (node is not JsonObject schema || Text(schema, "x-osdu-schema-source") is not { } kind || !FlowMapper.IsRecordKind(kind))
            {
                continue;
            }

            // Two files declaring one kind: the one the kind's own entity group names wins, and otherwise the first read.
            if (!found.ContainsKey(kind) || string.Equals(path, SchemaBundler.KindPath(kind), StringComparison.Ordinal))
            {
                found[kind] = new TreeFile(path, TemplateSources.DeclaresData(schema));
            }
        }

        return found;
    }

    /// <summary>
    /// The release as the local copy holds it, every file of it there: a release not on disk, or short of a file, is
    /// downloaded, and readers of a release being downloaded wait for the one download.
    /// </summary>
    private async Task<LocalRelease> EnsureLocalAsync(DataDefinitionsRelease release, CancellationToken ct)
    {
        if (_local.TryGetValue(release.Commit, out var known))
        {
            return known;
        }

        if (CheckLocal(release) is { } found)
        {
            return found;
        }

        var download = _downloads.GetOrAdd(
            release.Commit, _ => new Lazy<Task<LocalRelease>>(() => DownloadAsync(release), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            // The download is shared by every reader of the release, so one reader giving up does not cancel it.
            return Remember(release, await download.Value.WaitAsync(ct).ConfigureAwait(false));
        }
        finally
        {
            // A finished download leaves the table, and so does a failed one, so the next reader tries again.
            if (download.Value.IsCompleted)
            {
                _downloads.TryRemove(new KeyValuePair<string, Lazy<Task<LocalRelease>>>(release.Commit, download));
            }
        }
    }

    /// <summary>
    /// Runs a read of the release's local copy. When a file the copy holds has gone since it was found whole (a cleanup of
    /// the temp folder), the copy is put back whole from the repository and the read runs once more.
    /// </summary>
    private async Task<T> ReadLocalAsync<T>(DataDefinitionsRelease release, Func<LocalRelease, Task<T>> read, CancellationToken ct)
    {
        var local = await EnsureLocalAsync(release, ct).ConfigureAwait(false);
        try
        {
            return await read(local).ConfigureAwait(false);
        }
        catch (LocalFileMissingException)
        {
            // The copy lost files after it was found whole: what this instance knew of it no longer holds.
            _local.TryRemove(release.Commit, out _);
        }

        local = await EnsureLocalAsync(release, ct).ConfigureAwait(false);
        try
        {
            return await read(local).ConfigureAwait(false);
        }
        catch (LocalFileMissingException ex)
        {
            throw new DataDefinitionsException(
                $"{TreeRoot}/{ex.TreePath} at {release.Name} went missing from the local copy at {CacheDirectory} again just after the release was put back whole; something is deleting files there.",
                notFound: false,
                ex);
        }
    }

    /// <summary>
    /// The release as its folder in the local copy holds it, or null when the folder is not there or is short of a file its
    /// download unpacked (<see cref="ManifestFile"/>). A folder unpacked before that list was kept has none, and is
    /// downloaded once more to write it; the files it holds whole stay as they are.
    /// </summary>
    private LocalRelease? CheckLocal(DataDefinitionsRelease release)
    {
        var folder = ReleaseDirectory(release);
        var manifest = Path.Combine(folder, ManifestFile);
        try
        {
            var info = new FileInfo(manifest);
            if (!info.Exists || info.Length > MaxFileBytes)
            {
                return null;
            }

            var stored = JsonSerializer.Deserialize<StoredManifest>(File.ReadAllText(manifest), StoreJson);
            if (stored is not { Files.Count: > 0 } || !string.Equals(stored.Commit, release.Commit, StringComparison.Ordinal))
            {
                return null;
            }

            var root = Path.Combine(folder, TreeRoot);
            foreach (var (path, length) in stored.Files)
            {
                if (!IsTreePath(path) || !HasLength(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)), length))
                {
                    return null;
                }
            }

            return Remember(release, new LocalRelease(stored.Files));
        }
        catch (JsonException)
        {
            // A damaged list: the release is downloaded again, which writes the list anew.
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder cannot be looked at, so it is not taken as whole: the download that follows writes to it, and
            // reports the local copy's problem in its own error when it has one.
            return null;
        }
    }

    private LocalRelease Remember(DataDefinitionsRelease release, LocalRelease local)
    {
        if (_local.Count >= MaxCachedIndexes && !_local.ContainsKey(release.Commit))
        {
            _local.Clear();
        }

        _local[release.Commit] = local;
        return local;
    }

    private static bool HasLength(string file, long length)
    {
        var info = new FileInfo(file);
        return info.Exists && info.Length == length;
    }

    /// <summary>The list of a release's files says the same as <paramref name="other"/>, byte for byte.</summary>
    private static bool SameBytes(string file, string other)
        => File.Exists(other) && File.ReadAllBytes(file).AsSpan().SequenceEqual(File.ReadAllBytes(other));

    /// <summary>
    /// Puts a file of a download into a release folder that is already there, unless <paramref name="inPlace"/> says the
    /// same file is there whole. A file there whole is left alone, so a reader of it in another process is not disturbed;
    /// another process putting the same file there first is as good.
    /// </summary>
    private static void Place(string from, string to, Func<bool> inPlace)
    {
        if (inPlace())
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        try
        {
            File.Move(from, to, overwrite: true);
        }
        catch (IOException) when (inPlace())
        {
            // Another process put the same file of the same commit there first.
        }
    }

    /// <summary>
    /// Downloads the release's Generated folder as one archive and unpacks it into a staging folder with the list of its
    /// files (<see cref="ManifestFile"/>), which is then renamed into place. A release folder already there, short of files
    /// or unpacked before the list was kept, is filled in instead: each file not there whole is put back, and the list last.
    /// </summary>
    private async Task<LocalRelease> DownloadAsync(DataDefinitionsRelease release)
    {
        var target = ReleaseDirectory(release);
        var what = $"the {TreeRoot} folder of release {release.Name}";
        var staging = Path.Combine(CacheDirectory, ".download-" + Guid.NewGuid().ToString("N"));
        var stagingRoot = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        using var timeout = new CancellationTokenSource(_downloadTimeout);
        try
        {
            Directory.CreateDirectory(staging);
            var files = new Dictionary<string, long>(StringComparer.Ordinal);
            var url = new Uri(_api, $"repository/archive.tar.gz?sha={Uri.EscapeDataString(release.Commit)}&path={TreeRoot}");
            using var response = await SendAsync(url, what, timeout.Token, CancellationToken.None).ConfigureAwait(false);
            var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                var gzip = new GZipStream(body, CompressionMode.Decompress);
                await using (gzip.ConfigureAwait(false))
                {
                    var tar = new TarReader(gzip);
                    await using (tar.ConfigureAwait(false))
                    {
                        long unpacked = 0;
                        while (await tar.GetNextEntryAsync(copyData: false, timeout.Token).ConfigureAwait(false) is { } entry)
                        {
                            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                            {
                                continue;
                            }

                            // The archive holds one top folder named for the project, release and commit; the tree is under it.
                            var slash = entry.Name.IndexOf('/', StringComparison.Ordinal);
                            var relative = slash < 0 ? null : entry.Name[(slash + 1)..];
                            if (relative is null || !relative.StartsWith(TreeRoot + "/", StringComparison.Ordinal))
                            {
                                continue;
                            }

                            var file = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
                            if (!IsTreePath(relative[(TreeRoot.Length + 1)..]) || !file.StartsWith(stagingRoot, StringComparison.Ordinal))
                            {
                                throw new DataDefinitionsException($"{what}: the archive entry '{entry.Name}' leads outside the release folder.", notFound: false);
                            }

                            unpacked += entry.Length;
                            if (entry.Length > MaxFileBytes || unpacked > MaxReleaseBytes)
                            {
                                throw new DataDefinitionsException(
                                    string.Create(CultureInfo.InvariantCulture, $"{what} unpacks to more than a release holds (the entry '{entry.Name}' holds {entry.Length} bytes)."),
                                    notFound: false);
                            }

                            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                            await entry.ExtractToFileAsync(file, overwrite: false, timeout.Token).ConfigureAwait(false);
                            files[relative[(TreeRoot.Length + 1)..]] = entry.Length;
                        }
                    }
                }
            }

            if (!Directory.Exists(Path.Combine(staging, TreeRoot)))
            {
                throw new DataDefinitionsException($"The OSDU data definitions have no {TreeRoot} folder at release {release.Name}.", notFound: true);
            }

            var manifest = Path.Combine(staging, ManifestFile);
            var stream = File.Create(manifest);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, new StoredManifest(release.Commit, files), StoreJson, timeout.Token).ConfigureAwait(false);
            }

            var local = new LocalRelease(files);
            if (!Directory.Exists(target))
            {
                try
                {
                    Directory.Move(staging, target);
                    return local;
                }
                catch (IOException) when (Directory.Exists(target))
                {
                    // Another process published the release folder first: whatever it lacks is filled in below.
                }
            }

            // The release folder is there and was not found whole (a cleanup of the temp folder took files from it, or it
            // was unpacked before the list of its files was kept): put back each file not there whole, and the list last,
            // so the folder is taken as whole only once it is.
            var root = Path.Combine(target, TreeRoot);
            foreach (var (path, length) in files)
            {
                var relative = path.Replace('/', Path.DirectorySeparatorChar);
                var to = Path.Combine(root, relative);
                Place(Path.Combine(staging, TreeRoot, relative), to, () => HasLength(to, length));
            }

            var kept = Path.Combine(target, ManifestFile);
            Place(manifest, kept, () => SameBytes(manifest, kept));
            return local;
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
        {
            throw new DataDefinitionsException(
                string.Create(CultureInfo.InvariantCulture, $"{what} did not finish downloading from {WebUrl} within {_downloadTimeout.TotalMinutes:0.#} minutes."), notFound: false, ex);
        }
        catch (HttpIOException ex)
        {
            throw new DataDefinitionsException($"The download of {what} from {WebUrl} broke off: {ex.Message}", notFound: false, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new DataDefinitionsException($"The download of {what} from {WebUrl} failed: {ex.Message}", notFound: false, ex);
        }
        catch (InvalidDataException ex)
        {
            throw new DataDefinitionsException($"{what} arrived as an archive that could not be read: {ex.Message}", notFound: false, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DataDefinitionsException($"{what} could not be written to the local copy at {CacheDirectory}: {ex.Message}", notFound: false, ex);
        }
        finally
        {
            DeleteQuietly(staging);
        }
    }

    private async Task<(string Text, string? NextPage)> GetListPageAsync(string relative, string what, CancellationToken ct, TimeSpan? limit = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit ?? ListTimeout);
        using var response = await SendAsync(new Uri(_api, relative), what, timeout.Token, ct).ConfigureAwait(false);
        var next = response.Headers.TryGetValues("X-Next-Page", out var values) ? values.FirstOrDefault() : null;
        if (response.Content.Headers.ContentLength is > MaxListBytes)
        {
            throw ListTooLarge(what);
        }

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxListBytes)
                    {
                        throw ListTooLarge(what);
                    }

                    buffer.Write(chunk, 0, read);
                }

                return (Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('﻿'), next);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new DataDefinitionsException($"The OSDU data definitions at {WebUrl} did not finish sending {what} in time.", notFound: false, ex);
        }
        catch (HttpIOException ex)
        {
            throw new DataDefinitionsException($"{what} from {WebUrl} broke off: {ex.Message}", notFound: false, ex);
        }
    }

    /// <summary>Sends a GET and returns a successful response; <paramref name="caller"/> tells a caller's cancellation from a timeout.</summary>
    private async Task<HttpResponseMessage> SendAsync(Uri url, string what, CancellationToken token, CancellationToken caller)
    {
        HttpResponseMessage response;
        try
        {
            response = await _client().GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new DataDefinitionsException($"The OSDU data definitions at {WebUrl} could not be reached for {what}: {ex.Message}", notFound: false, ex);
        }
        catch (OperationCanceledException ex) when (!caller.IsCancellationRequested)
        {
            throw new DataDefinitionsException($"The OSDU data definitions at {WebUrl} did not answer in time for {what}.", notFound: false, ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new DataDefinitionsException($"The OSDU data definitions have no {what}.", notFound: true);
            }

            throw new DataDefinitionsException(
                string.Create(CultureInfo.InvariantCulture, $"The OSDU data definitions at {WebUrl} answered {(int)response.StatusCode} {response.ReasonPhrase} for {what}."),
                notFound: false);
        }
    }

    private string ReleaseDirectory(DataDefinitionsRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return IsCommitId(release.Commit)
            ? Path.Combine(CacheDirectory, release.Commit)
            : throw new DataDefinitionsException($"Release {release.Name} names '{release.Commit}', which is not a commit id.", notFound: false);
    }

    private bool Fresh(DateTimeOffset readUtc)
        => _freshness == Timeout.InfiniteTimeSpan || _time.GetUtcNow() - readUtc < _freshness;

    private static bool IsCommitId(string value) => value.Length is >= 7 and <= 64 && value.All(char.IsAsciiHexDigit);

    /// <summary>A forward-slash path inside the tree: no root, no empty, current or parent segment, no backslash.</summary>
    private static bool IsTreePath(string path)
        => !path.StartsWith('/') && !path.Contains('\\') && path.Split('/').All(segment => segment is not ("" or "." or ".."));

    private static DataDefinitionsException ListTooLarge(string what)
        => new(string.Create(CultureInfo.InvariantCulture, $"The OSDU data definitions answered {what} with more than {MaxListBytes / 1024 / 1024} MB, more than it should hold."), notFound: false);

    /// <summary>Removes a staging folder or a temporary file this instance made; one it cannot remove yet is left for the operating system's temp cleanup.</summary>
    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A file still held open (an antivirus scan, say) keeps the leftover; it never holds a published release.
        }
        catch (UnauthorizedAccessException)
        {
            // As above: the leftover sits under a staging name no reader ever opens.
        }
    }

    private static JsonNode? Parse(string text, string what)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new DataDefinitionsException($"{what} is not valid JSON ({ex.Message}).", notFound: false, ex);
        }
    }

    private static string? Text(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    /// <summary>Orders dotted versions by their numbers, a version with a further part after the one without it; text that is not a dotted version orders as text.</summary>
    private static int CompareVersions(string left, string right)
    {
        var a = VersionNumbers(left);
        var b = VersionNumbers(right);
        if (a is null || b is null)
        {
            return string.CompareOrdinal(left, right);
        }

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : -1;
            var y = i < b.Length ? b[i] : -1;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    }

    private static int[]? VersionNumbers(string text)
    {
        var parts = text.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit)
                || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return numbers;
    }

    private static Uri WithTrailingSlash(Uri url)
        => url.AbsoluteUri.EndsWith('/') ? url : new Uri(url.AbsoluteUri + "/");

    /// <summary>
    /// A release as it has been read and kept: the record schemas it publishes, which is its index, and where every kind it
    /// holds a schema file for keeps that file.
    /// </summary>
    private sealed record ReleaseSchemas(DataDefinitionsIndex Index, IReadOnlyDictionary<string, TreeFile> Files);

    /// <summary>One kind's schema file in a release: its path under <see cref="TreeRoot"/>, and whether it is a record schema.</summary>
    private sealed record TreeFile(string Path, bool IsRecord);

    private sealed record ReleaseList(IReadOnlyList<DataDefinitionsRelease> Releases, DateTimeOffset ReadUtc);

    /// <summary>A release found whole in the local copy: every file its download unpacked, by path under <see cref="TreeRoot"/>, with its length.</summary>
    private sealed record LocalRelease(IReadOnlyDictionary<string, long> Files)
    {
        public bool Holds(string path) => Files.ContainsKey(path);
    }

    /// <summary>The list of a release's files as its folder in the local copy keeps it (<see cref="ManifestFile"/>).</summary>
    private sealed record StoredManifest(string Commit, Dictionary<string, long> Files);

    /// <summary>A file the local copy holds for a release is not on disk: the copy lost it after it was found whole.</summary>
    private sealed class LocalFileMissingException(string path) : DeliveryException($"{TreeRoot}/{path} is not in the local copy.")
    {
        public string TreePath { get; } = path;
    }

    /// <summary>The release list as the local copy keeps it on disk.</summary>
    private sealed record StoredReleases(DateTimeOffset ReadUtc, List<DataDefinitionsRelease> Releases);
}
