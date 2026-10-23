using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Catalog;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Templates;
using SqlFlow.Sources;

namespace SqlFlow.Delivery.Tests;

/// <summary>A controllable clock for lease, backoff and cache tests.</summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now;
    private long _timestamp;

    public TestClock(DateTimeOffset? start = null)
    {
        _now = start ?? new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
        _timestamp = 0;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by)
    {
        _now += by;
        _timestamp += by.Ticks;
    }
}

/// <summary>
/// One version of one partition's cache held in memory, as a render reads it. The engine suites share the sample cache
/// through it, so no suite reads a database another suite is writing; the store itself is covered by its own suite.
/// </summary>
public sealed class FixedCacheStore : ICacheStore
{
    private readonly string _scope;
    private readonly string _flowName;
    private readonly ReferenceSnapshot _snapshot;
    private readonly CacheDeclaration _declaration;

    /// <param name="scope">The partition whose cache the store holds.</param>
    /// <param name="flowName">The cache flow that wrote the one version.</param>
    /// <param name="snapshot">The one version.</param>
    /// <param name="declaration">What the partition's cache flows declare; none when null.</param>
    public FixedCacheStore(string scope, string flowName, ReferenceSnapshot snapshot, CacheDeclaration? declaration = null)
    {
        _scope = scope;
        _flowName = flowName;
        _snapshot = snapshot;
        _declaration = declaration ?? CacheDeclaration.None(scope);
    }

    public Task<string?> CurrentVersionAsync(string scope, CancellationToken ct = default)
        => Task.FromResult(scope == _scope ? _snapshot.Version : null);

    public Task<IReadOnlyList<CacheVersionInfo>> CurrentVersionsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CacheVersionInfo>>([Info()]);

    public Task<ReferenceSnapshot?> LoadAsync(string scope, string version, CancellationToken ct = default)
        => Task.FromResult(scope == _scope && version == _snapshot.Version ? _snapshot : null);

    public Task<IReadOnlyList<CacheVersionInfo>> ListVersionsAsync(string scope, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CacheVersionInfo>>(scope == _scope ? [Info()] : []);

    public Task<CacheVersionInfo?> VersionAsync(string scope, string? version, CancellationToken ct = default)
        => Task.FromResult(scope == _scope && (version is null || version == _snapshot.Version) ? Info() : null);

    private CacheVersionInfo Info()
        => new(
            _scope, _snapshot.Version, 1, _snapshot.CapturedUtc.UtcDateTime, true, null, null, "tests", "sample files", _flowName,
            _snapshot.Types.Sum(t => (long)t.Items.Count), _snapshot.Types.Select(t => new CacheVersionType(t.Name, t.EntityType, t.Items.Count)).ToList(),
            _snapshot.SystemProperties);

    public Task<CacheDeclaration> DeclarationAsync(string scope, CancellationToken ct = default)
        => Task.FromResult(scope == _scope ? _declaration : CacheDeclaration.None(scope));

    public Task<IReadOnlySet<string>> HeldByDeliveriesAsync(string scope, string typeName, IReadOnlyCollection<string> ids, CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));

    public Task<CacheWrite> MergeAsync(
        string scope,
        string flowName,
        IReadOnlyList<ReferenceType> captured,
        CacheCapture capture,
        DateTimeOffset capturedUtc,
        IReadOnlyList<SystemPropertyReading>? readings = null,
        CancellationToken ct = default)
        => throw new InvalidOperationException("The fixed sample cache is read-only; write versions through the module's cache store.");

    /// <summary>The one version is the current one, which a retention always keeps, so there is never anything to prune.</summary>
    public Task<CacheRetentionOutcome> ApplyRetentionAsync(CacheRetentionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var declaration = request.Scope == _scope ? _declaration : CacheDeclaration.None(request.Scope);
        var days = request.FlowName is { } flow ? declaration.RetentionFor(flow, request.Days) : request.Days;
        return Task.FromResult(new CacheRetentionOutcome(days, request.Scope == _scope ? 1 : 0, [], 0, DryRun: request.DryRun));
    }
}

/// <summary>
/// Records every delivery and replays configured outcomes. Safe to call from many workers at once: the recordings are
/// written under a lock, and the store keeps how many deliveries were in flight at once, overall and per node, where a
/// node is whatever <see cref="CurrentNode"/> names on the calling flow of control (a fan-out member, say).
/// </summary>
public sealed class FakeProtocol : IDeliveryProtocol
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _inFlightByNode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _maxInFlightByNode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _deliveriesByNode = new(StringComparer.Ordinal);
    private long _version = 1000;
    private int _inFlight;
    private int _maxInFlight;

    /// <summary>The node a delivery made on this flow of control is counted under; unset is <c>local</c>.</summary>
    public static AsyncLocal<string?> CurrentNode { get; } = new();

    public List<DeliveryWork> Deliveries { get; } = [];

    /// <summary>The most deliveries that were in flight at the same moment.</summary>
    public int MaxInFlight
    {
        get
        {
            lock (_gate)
            {
                return _maxInFlight;
            }
        }
    }

    /// <summary>Per node, the most deliveries it had in flight at the same moment.</summary>
    public IReadOnlyDictionary<string, int> MaxInFlightByNode
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, int>(_maxInFlightByNode, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Per node, how many deliveries it made.</summary>
    public IReadOnlyDictionary<string, int> DeliveriesByNode
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, int>(_deliveriesByNode, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>The correlation id in effect when each delivery was made, in the order of <see cref="Deliveries"/>.</summary>
    public List<string?> Correlations { get; } = [];

    public List<(string TargetId, long? Expected)> Verifies { get; } = [];

    public Func<DeliveryWork, Exception?>? FailWith { get; set; }

    public Func<string, VerifyResult>? VerifyWith { get; set; }

    /// <summary>Runs before each delivery; lets a test hold a delivery open (for example until it is cancelled).</summary>
    public Func<DeliveryWork, CancellationToken, Task>? Before { get; set; }

    public DeliveryProtocol Kind => DeliveryProtocol.Ddms;

    public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var node = CurrentNode.Value ?? "local";
        lock (_gate)
        {
            Deliveries.Add(work);
            Correlations.Add(OsduCorrelation.Current);
            _deliveriesByNode[node] = _deliveriesByNode.GetValueOrDefault(node) + 1;
            _maxInFlight = Math.Max(_maxInFlight, ++_inFlight);
            var here = _inFlightByNode[node] = _inFlightByNode.GetValueOrDefault(node) + 1;
            _maxInFlightByNode[node] = Math.Max(_maxInFlightByNode.GetValueOrDefault(node), here);
        }

        try
        {
            return await DeliverInFlightAsync(work, ct);
        }
        finally
        {
            lock (_gate)
            {
                _inFlight--;
                _inFlightByNode[node]--;
            }
        }
    }

    private async Task<DeliveryOutcome> DeliverInFlightAsync(DeliveryWork work, CancellationToken ct)
    {
        if (Before is { } before)
        {
            await before(work, ct);
        }

        if (FailWith?.Invoke(work) is { } failure)
        {
            throw failure;
        }

        var chunks = 0;
        if (work.DeliverPayload && work.Payload is not null)
        {
            foreach (var file in await work.Payload.ListChunksAsync(ct))
            {
                await using var stream = await work.Payload.OpenAsync(file, ct);
                using var sink = new MemoryStream();
                await stream.CopyToAsync(sink, ct);
                chunks++;
            }
        }

        var version = work.DeliverMetadata ? Interlocked.Increment(ref _version) : work.ExistingVersion;
        var returned = new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = work.TargetId };
        if (version is { } v)
        {
            returned["version"] = v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (work.DeliverPayload)
        {
            returned["chunks"] = chunks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var now = DateTime.UtcNow;
        return new DeliveryOutcome
        {
            MetadataDelivered = work.DeliverMetadata,
            PayloadDelivered = work.DeliverPayload,
            TargetVersion = version,
            ChunksSent = chunks,
            Returned = returned,
            Steps = [new DeliveryStep("fake", now, now, 200, returned)],
        };
    }

    public Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Verifies.Add((targetId, expectedVersion));
        }

        return Task.FromResult(VerifyWith?.Invoke(targetId) ?? new VerifyResult(VerifyOutcome.Match, expectedVersion, null));
    }

    public List<(string TargetId, RemovalScope Scope)> Deletes { get; } = [];

    /// <summary>Target ids the fake target no longer holds, so a removal of them reports them already gone.</summary>
    public HashSet<string> Gone { get; } = new(StringComparer.Ordinal);

    /// <summary>Target ids whose removal the fake target refuses.</summary>
    public HashSet<string> Refuse { get; } = new(StringComparer.Ordinal);

    /// <summary>Runs before each removal call; lets a test act while a removal is under way.</summary>
    public Func<string, Task>? BeforeDelete { get; set; }

    public async Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
    {
        if (BeforeDelete is { } before)
        {
            await before(targetId).ConfigureAwait(false);
        }

        lock (_gate)
        {
            Deletes.Add((targetId, scope));
        }

        if (Refuse.Contains(targetId))
        {
            throw new DeliveryException($"HTTP 403 Forbidden from DELETE {targetId}: the fake target refuses this removal");
        }

        return Gone.Contains(targetId)
            ? new DeleteOutcome(false, true, "record not found in OSDU")
            : new DeleteOutcome(true, false, scope.ToString().ToLowerInvariant());
    }

    /// <summary>The records the fake target holds, by target id, for read-backs.</summary>
    public Dictionary<string, JsonObject> Held { get; } = new(StringComparer.Ordinal);

    public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default)
        => Task.FromResult(Held.TryGetValue(targetId, out var record) ? (JsonObject?)record.DeepClone().AsObject() : null);

    public bool Reachable { get; set; } = true;

    public Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default)
        => Task.FromResult(new ProbeOutcome(Reachable, Reachable ? 200 : 503, Reachable ? "the service answered" : "service unavailable", "/about"));
}

/// <summary>A listener that keeps every event it is given, for tests that follow what a worker reported.</summary>
public sealed class RecordingListener : IDeliveryListener
{
    private readonly object _gate = new();
    private readonly List<DeliveryEvent> _events = [];

    public IReadOnlyList<DeliveryEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    public ValueTask OnEventAsync(DeliveryEvent evt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _events.Add(evt);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Hands the engine a ready-made protocol instead of building one over HTTP.</summary>
public sealed class FakeProtocolFactory : IProtocolFactory
{
    private readonly IDeliveryProtocol _protocol;

    public FakeProtocolFactory(IDeliveryProtocol protocol)
    {
        _protocol = protocol;
    }

    /// <summary>Stands in for a protocol that cannot be built at all: an unresolved secret, an identity out of reach.</summary>
    public Func<Exception?>? FailWith { get; set; }

    public Task<IDeliveryProtocol> CreateAsync(FlowDefinition flow, HttpRuntime http, ILoggerFactory loggers, CancellationToken ct = default)
        => FailWith?.Invoke() is { } failure ? Task.FromException<IDeliveryProtocol>(failure) : Task.FromResult(_protocol);
}

/// <summary>
/// Builds the real protocols over one fake OSDU (<paramref name="handler"/>), the way the node's factory builds them over
/// the network, each over an HTTP runtime of its own that the factory disposes.
/// </summary>
public sealed class FakeOsduProtocols(HttpMessageHandler handler) : IProtocolFactory, IDisposable
{
    private readonly List<HttpRuntime> _runtimes = [];
    private readonly object _gate = new();

    public Task<IDeliveryProtocol> CreateAsync(FlowDefinition flow, HttpRuntime http, ILoggerFactory loggers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var runtime = new HttpRuntime(flow.Reliability, new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true, observer: http.Observer);
        lock (_gate)
        {
            _runtimes.Add(runtime);
        }

        return ProtocolFactory.CreateAsync(flow, runtime, new SecretResolver([new EnvSecretProvider()]), loggers, ct);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var runtime in _runtimes)
            {
                runtime.Dispose();
            }

            _runtimes.Clear();
        }
    }
}

/// <summary>A scripted HTTP handler: matches requests by method and path, records bodies, returns canned responses.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    public sealed record Request(HttpMethod Method, Uri Uri, string? Body, string? ContentType, IReadOnlyDictionary<string, string> Headers);

    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<int, HttpResponseMessage> Respond)> _rules = [];
    private readonly Dictionary<string, int> _hits = new(StringComparer.Ordinal);

    public List<Request> Calls { get; } = [];

    public FakeHttpHandler On(HttpMethod method, string pathSuffix, Func<int, HttpResponseMessage> respond)
    {
        _rules.Add((r => r.Method == method && r.RequestUri!.AbsolutePath.EndsWith(pathSuffix, StringComparison.Ordinal), respond));
        return this;
    }

    public FakeHttpHandler On(HttpMethod method, string pathSuffix, HttpStatusCode status, string? json = null)
        => On(method, pathSuffix, _ => Json(status, json));

    /// <summary>A rule over the whole request, for URLs the test cannot know in advance (a run id the protocol chose).</summary>
    public FakeHttpHandler OnMatch(Func<HttpRequestMessage, bool> match, Func<int, HttpResponseMessage> respond)
    {
        _rules.Add((match, respond));
        return this;
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string? json)
    {
        var response = new HttpResponseMessage(status);
        if (json is not null)
        {
            response.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? body = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Calls.Add(new Request(request.Method, request.RequestUri!, body, request.Content?.Headers.ContentType?.MediaType, headers));
        foreach (var (match, respond) in _rules)
        {
            if (match(request))
            {
                var key = request.Method + " " + request.RequestUri!.AbsolutePath;
                var hit = _hits.GetValueOrDefault(key);
                _hits[key] = hit + 1;
                return respond(hit);
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no rule for " + request.RequestUri) };
    }
}

/// <summary>Paths to the sample documents linked into the test output, and a ready-made engine context over them.</summary>
public static class Samples
{
    /// <summary>
    /// The sample estate declares where it delivers the way a real estate does, as ${env:...} references a node holds
    /// (osdu/docs/reference/concepts/environment-variables.md), so the suites supply them exactly as a node would. The
    /// values are the ones the records the suites keep were rendered with (<see cref="RenderedRecord.Folder"/>), which
    /// is what keeps a rendered document byte-identical to the kept record. Set for the whole assembly before any test
    /// reads a sample document, and never overwriting a value the process was started with, so a run against a real
    /// estate keeps its own.
    /// </summary>
    [ModuleInitializer]
    internal static void UseSampleEstateReferences()
    {
        Reference("OSDU_DATA_PARTITION", "dev");
        Reference("OSDU_ACL_OWNER", "data.welldb.owners@dev.dataservices.energy");
        Reference("OSDU_ACL_VIEWER", "data.welldb.viewers@dev.dataservices.energy");
        Reference("OSDU_LEGAL_TAG", "dev-osdu-default-legal");

        static void Reference(string name, string value)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    public const string WellLogKind = "osdu:wks:work-product-component--WellLog:1.4.0";

    public const string WellboreKind = "osdu:wks:master-data--Wellbore:1.3.0";

    /// <summary>The kind the trajectory fixture mapping renders.</summary>
    public const string WellboreTrajectoryKind = "osdu:wks:work-product-component--WellboreTrajectory:1.3.0";

    /// <summary>The connection reference the suites give a flow whose source is the in-memory ingestion tables.</summary>
    public const string MemoryConnection = "mem://ingestion";

    /// <summary>The samples directory as the repository lays it out: a folder per source, and beside them the sample cache records.</summary>
    public static string Root => Path.Combine(AppContext.BaseDirectory, "samples");

    /// <summary>
    /// The welldb source: synthetic well logs of a made-up well database, with the flows that land, key and deliver them,
    /// the mapping they pin, the cache they resolve against and the drop-off folder the pre-ingestion flows read. A
    /// repository is laid out per source, so this is what a sync sees as one project.
    /// </summary>
    public static string Source => Path.Combine(Root, "welldb");

    public static string Mappings => Path.Combine(Source, "mappings");

    /// <summary>
    /// The welldb source's template folder: the bundled OSDU schemas its mappings fill and its flows read, which a run reads
    /// from here (osdu/docs/reference/concepts/templates.md, Template folders) and the suites' in-memory templates load.
    /// </summary>
    public static string TemplateFiles => Path.Combine(Source, "templates");

    /// <summary>
    /// The documents of the kinds the sample estate does not deliver (wellbores, trajectories, documents, several interfaces
    /// in one flow, a retrieval), which the suites that exercise those routes and features read. Test inputs only: they
    /// render against the same partition cache as the sample estate, and none of them is an example.
    /// </summary>
    public static string FixtureDocuments => Path.Combine(AppContext.BaseDirectory, "Fixtures", "documents");

    /// <summary>The mappings of <see cref="FixtureDocuments"/>: Wellbore, WellboreTrajectory and Document.</summary>
    public static string FixtureMappings => Path.Combine(FixtureDocuments, "mappings");

    /// <summary>
    /// The template folder above the fixture documents: the schemas of the kinds the sample does not carry that the fixture
    /// mappings fill, and the ones the schema-version suites compare against. The build links the sample's own templates
    /// into the fixture documents' template folder, nearer to them.
    /// </summary>
    public static string FixtureTemplateFiles => Path.Combine(AppContext.BaseDirectory, "Fixtures", "templates");

    public static string Flow => Path.Combine(Source, "flows", "welldb-welllog-03-header-delivery.yaml");

    /// <summary>The wellbore master-data fixture flow: the storage route, with no payload.</summary>
    public static string WellboreFlowFile => Path.Combine(FixtureDocuments, "flows", "wells-wellbore-03-header-delivery.yaml");

    /// <summary>The fixture flow that delivers several interfaces (wellbores, well logs, documents) from one source.</summary>
    public static string InterfacesFlowFile => Path.Combine(FixtureDocuments, "flows", "wells-source-03-interfaces-delivery.yaml");

    /// <summary>The fixture retrieval flow.</summary>
    public static string RetrievalFlowFile => Path.Combine(FixtureDocuments, "flows", "wells-osdu-04-metadata-retrieval.yaml");

    /// <summary>The sample lookups cache flow: the lookup tables the sample mapping translates source values through.</summary>
    public static string LookupsCacheFlow => Path.Combine(Source, "cache", "welldb-lookups-00-cache.yaml");

    /// <summary>The sample reference cache flow: the OSDU reference data the ids the sample mapping builds are checked against.</summary>
    public static string ReferenceCacheFlow => Path.Combine(Source, "cache", "osdu-reference-00-cache.yaml");

    /// <summary>
    /// The records of the types the sample reference cache flow captures, as a refresh of it would find them: the sample
    /// estate's stand-in for its partition's reference data (<c>osdu/samples/cache-records</c>).
    /// </summary>
    public static string SampleCacheRecords => Path.Combine(Root, "cache-records");

    /// <summary>The fixture cache flow: the reference data only the fixture mappings resolve against.</summary>
    public static string FixtureCacheFlow => Path.Combine(FixtureDocuments, "cache", "fixtures-osdu-00-reference-cache.yaml");

    /// <summary>The cache records of the types only the fixture mappings resolve against.</summary>
    public static string FixtureCacheRecords => Path.Combine(AppContext.BaseDirectory, "Fixtures", "cache-records");

    /// <summary>The source's drop-off folder: the files the pre-ingestion flows read and the payloads the delivery streams.</summary>
    public static string Data => Path.Combine(Source, "data");

    /// <summary>
    /// The partition the sample flows search and deliver to, whose cache the sample delivery flow reads. A cache is keyed
    /// by the partition a flow actually reaches, so this is the resolved partition even though the estate's documents name
    /// it as a reference: capture and read both resolve, so both agree.
    /// </summary>
    public const string SampleCacheScope = SamplePartition;

    /// <summary>
    /// What that reference resolves to on a node, and so what the ids the sample estate mints carry: the render
    /// parameter a mapping composes an id from is resolved before the document is rendered, while the cache a flow
    /// reads is scoped by the partition as the document writes it.
    /// </summary>
    public const string SamplePartition = "dev";

    /// <summary>The name of the sample lookups cache flow, which fills the cache of <see cref="SampleCacheScope"/> with the lookup tables the sample mapping reads.</summary>
    public const string SampleLookupsFlowName = "welldb-lookups-00-cache";

    /// <summary>The name of the sample reference cache flow, which fills the same cache with the reference data the sample mapping's ids name.</summary>
    public const string SampleReferenceFlowName = "osdu-reference-00-cache";

    /// <summary>The name of the fixture cache flow, which adds the fixture mappings' reference data to the same cache.</summary>
    public const string FixtureCacheFlowName = "fixtures-osdu-00-reference-cache";

    /// <summary>When the sample cache records were captured: the version label the sample cache is imported under.</summary>
    public static readonly DateTimeOffset SampleCacheCaptured = new(2026, 9, 8, 21, 27, 27, TimeSpan.Zero);

    // The sample cache, imported once for the whole run through the real store: a cache version is immutable, and after the
    // warm-up a render never reaches the database behind it, so the import holds the test database only while it runs. The
    // SQL Server collection warms it before its first test, so a test inside it never finds its database written by the
    // import; a parallel test that asks first takes the database for the import, waiting while another test process holds it.
    private static readonly Lazy<ICacheStore> SampleStores = new(() =>
    {
        using var hold = OsduTestDatabaseLock.Acquire();
        using var database = new OsduTestDatabase();
        var version = ImportSampleCacheAsync(database.Caches()).GetAwaiter().GetResult();
        return new FixedCacheStore(SampleCacheScope, FixtureCacheFlowName, version, SampleCacheDeclaration());
    });

    /// <summary>The sample templates, read from their files once for the whole run.</summary>
    private static readonly Lazy<MemoryTemplates> SampleTemplateFiles = new(() => new MemoryTemplates(TemplateKinds().Select(SampleTemplate)));

    /// <summary>Imports the sample cache now, when nothing has asked for it yet.</summary>
    internal static void WarmSampleStores() => _ = SampleStores.Value;

    /// <summary>
    /// What the sample cache flows declare for their partition, as the module's database holds it after a sync: the
    /// reference data the sample reference flow captures and the fixture mappings resolve against, and the lookup tables of
    /// the lookups flow with the key and fields a sync reads out of each dictionary.
    /// </summary>
    public static CacheDeclaration SampleCacheDeclaration()
    {
        var loader = new DeliveryDocumentLoader();
        var lookups = loader.LoadCache(LookupsCacheFlow);
        var fixtures = loader.LoadCache(FixtureCacheFlow);
        var reference = loader.LoadCache(ReferenceCacheFlow);
        var declared = fixtures.Types.Select(t => new CacheTypeDeclaration(fixtures.Name, t.Name, t.EntityType, t.Kind, t.Query, t.Fields, t.OnChange))
            .Concat(reference.Types.Select(t => new CacheTypeDeclaration(reference.Name, t.Name, t.EntityType, t.Kind, t.Query, t.Fields, t.OnChange)))
            .ToList();
        foreach (var type in lookups.Types)
        {
            var (key, fields) = type.Origin == CacheOrigin.Dictionary
                ? (SampleDictionary(type.Dictionary!).Key, SampleDictionary(type.Dictionary!).FieldSpecs())
                : (type.Key, type.Fields);
            declared.Add(new CacheTypeDeclaration(lookups.Name, type.Name, type.EntityType, null, "*", fields, type.OnChange, type.Origin, key));
        }

        return new CacheDeclaration(SampleCacheScope, declared);
    }

    /// <summary>A dictionary of the sample estate, read from its file under the estate's dictionaries folder.</summary>
    public static DictionaryDefinition SampleDictionary(string name)
    {
        var path = Path.Combine(Source, DictionaryCatalog.DirectoryName, name + ".yaml");
        return new DeliveryDocumentLoader().LoadDictionary(path, $"{DictionaryCatalog.DirectoryName}/{name}.yaml");
    }

    /// <summary>
    /// The folder under <c>cache/data</c> holding the file each lookup table of the sample estate is loaded from, by the
    /// table's name: the location of the pre-ingestion flow that lands it.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> LookupDataFolders = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["UnitAlias"] = "unit-alias",
        ["DepthUnitAlias"] = "depth-unit-alias",
        ["CurveDictionary"] = "curve-dictionary",
    };

    /// <summary>
    /// The lookup tables the sample lookups cache flow captures, built from the sample estate's own files as a refresh
    /// builds them: each dictionary type from its dictionary, and each table type from the file in <c>cache/data</c> its
    /// pre-ingestion and ingestion flows load into that table, keyed and trimmed as a capture of the table keys and trims it.
    /// </summary>
    public static IReadOnlyList<ReferenceType> SampleLookups()
    {
        var flow = new DeliveryDocumentLoader().LoadCache(LookupsCacheFlow);
        var types = new List<ReferenceType>();
        foreach (var type in flow.Types)
        {
            if (type.Origin == CacheOrigin.Dictionary)
            {
                types.Add(SampleDictionary(type.Dictionary!).ToLookup(type.Name));
                continue;
            }

            var folder = Path.Combine(Source, "cache", "data", LookupDataFolders[type.Name]);
            var file = Directory.GetFiles(folder, "*.csv").Single();
            var lines = File.ReadAllLines(file).Where(line => line.Trim().Length > 0).ToList();
            var header = lines[0].Split(',').Select(column => column.Trim()).ToList();
            var rows = new List<ReferenceItem>();
            foreach (var line in lines.Skip(1))
            {
                // The sample files quote nothing; a row whose cells do not line up with the header would be read wrongly.
                var cells = line.Split(',');
                if (cells.Length != header.Count)
                {
                    throw new InvalidOperationException($"{file}: '{line}' has {cells.Length} cells and the header {header.Count}; the sample lookup files quote nothing.");
                }
                var key = cells[header.IndexOf(type.Key!)].Trim();
                var fields = new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase) { [type.Key!] = ReferenceValue.Of(key) };
                foreach (var field in type.Fields)
                {
                    var value = cells[header.IndexOf(field.Path)];
                    if (value.Length > 0)
                    {
                        fields[field.Name] = ReferenceValue.Of(value);
                    }
                }

                rows.Add(new ReferenceItem(key, fields));
            }

            types.Add(new ReferenceType(type.Name, type.EntityType, rows.OrderBy(r => r.Id, StringComparer.Ordinal), type.Key));
        }

        return types;
    }

    /// <summary>
    /// The sample templates, read from the sample estate's template files and held in memory, shared by the engine tests that
    /// read a template whatever a document's path.
    /// </summary>
    public static MemoryTemplates SampleTemplates => SampleTemplateFiles.Value;

    /// <summary>The sample cache at its one version, shared by the engine tests.</summary>
    public static ICacheStore SampleCache => SampleStores.Value;

    /// <summary>
    /// Imports the sample cache records into <paramref name="store"/> as a version of the sample partition's cache: the
    /// sample lookup tables written a minute before as the lookups flow's refresh writes them, the sample reference data
    /// half a minute later as the reference flow's refresh would capture it, and the fixture mappings' reference data last;
    /// returns the version as loaded back: the current one, labelled <see cref="SampleCacheCaptured"/>, which holds all
    /// three.
    /// </summary>
    public static async Task<ReferenceSnapshot> ImportSampleCacheAsync(ICacheStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        // The estate names its partition as a reference; a cache is keyed by what that resolves to, as a capture and a
        // render both key it.
        var lookups = new SnapshotBuilder(
            store, SampleCacheScope, SampleLookupsFlowName, new TestClock(SampleCacheCaptured.AddMinutes(-1)), Logger<SnapshotBuilder>());
        await lookups.WriteAsync(SampleLookups(), new CacheCapture(null, "tests", "sample dictionaries and curve dictionary"), []);
        var reference = new DeliveryDocumentLoader().LoadCache(ReferenceCacheFlow);
        var referenceBuilder = new SnapshotBuilder(
            store, SampleCacheScope, reference.Name, new TestClock(SampleCacheCaptured.AddSeconds(-30)), Logger<SnapshotBuilder>());
        await referenceBuilder.ImportDirectoryAsync(SampleCacheRecords, reference.Types, new CacheCapture(null, "tests", "sample reference files"));
        var fixtures = new DeliveryDocumentLoader().LoadCache(FixtureCacheFlow);
        var fixtureBuilder = new SnapshotBuilder(
            store, SampleCacheScope, fixtures.Name, new TestClock(SampleCacheCaptured), Logger<SnapshotBuilder>());
        var write = await fixtureBuilder.ImportDirectoryAsync(FixtureCacheRecords, fixtures.Types, new CacheCapture(null, "tests", "fixture files"));
        return (await store.LoadAsync(SampleCacheScope, write.Snapshot.Version))!;
    }

    /// <summary>
    /// Renders <paramref name="record"/> to the end, as the plan does: whatever a render asks of the renderer's search is
    /// answered between renders until one finishes.
    /// </summary>
    public static async Task<RenderResult> RenderSettledAsync(MappingRenderer renderer, SourceRecord record)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        for (var round = 0; round < 10; round++)
        {
            var result = renderer.Render(record);
            if (!result.IsIncomplete)
            {
                return result;
            }

            await renderer.Search.AnswerAsync(result.Unanswered);
        }

        throw new InvalidOperationException("The render still asked the search something after ten rounds.");
    }

    /// <summary>
    /// The kind of every bundled schema the sample estate and the fixtures keep, from its file name
    /// (<c>osdu_wks_master-data--Wellbore_1.3.0.json</c> is <c>osdu:wks:master-data--Wellbore:1.3.0</c>), in kind order: what a
    /// real estate saves for the mappings it pins.
    /// </summary>
    public static IReadOnlyList<string> TemplateKinds()
        => [.. new[] { TemplateFiles, FixtureTemplateFiles }
            .SelectMany(folder => Directory.GetFiles(folder, "*.json"))
            .Select(file => Path.GetFileNameWithoutExtension(file))
            .Select(name =>
            {
                var first = name.IndexOf('_', StringComparison.Ordinal);
                var second = name.IndexOf('_', first + 1);
                var last = name.LastIndexOf('_');
                return $"{name[..first]}:{name[(first + 1)..second]}:{name[(second + 1)..last]}:{name[(last + 1)..]}";
            })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>The sample schema of a kind, read from its bundled file, or from the fixture schemas for a kind the estate does not deliver.</summary>
    public static SchemaSnapshot SampleTemplate(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var name = kind.Replace(':', '_') + ".json";
        var file = Path.Combine(TemplateFiles, name);
        if (!File.Exists(file))
        {
            file = Path.Combine(FixtureTemplateFiles, name);
        }

        return TemplateSources.FromBundledJson(File.ReadAllText(file), kind, new DateTimeOffset(2026, 9, 7, 22, 37, 2, TimeSpan.Zero), file);
    }

    public static string NewTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "osdu-delivery-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The platform file stores plus the delivery writers, exactly as the hosts register them.</summary>
    public static FileStoreRegistry Stores() => new([new LocalFileStore()], [new LocalFileWriter()], [new LocalFileReader()]);

    /// <summary>The payload files over the local stores, as a node lists and opens them.</summary>
    public static IPayloadFiles Payloads() => new StoragePayloadFiles(Stores());

    /// <summary>
    /// The engine as a host composes it, over the in-memory ingestion tables: everything a run needs except a target,
    /// which the suites supply as a fake protocol. A host reads a document's templates from the template folders on its
    /// path; the suites that test what renders read them from <paramref name="templates"/> (the sample templates by
    /// default) whatever a document's path, and a suite of the folders themselves passes
    /// <paramref name="documentTemplates"/> (<see cref="FolderTemplates.Default"/>, as every host composes it). A suite
    /// that reads a synced repository's templates passes the <paramref name="repositories"/> it is synced into.
    /// </summary>
    public static EngineContext Engine(
        ILedger? ledger,
        TimeProvider? time = null,
        IProtocolFactory? protocols = null,
        ITemplateSource? templates = null,
        ICacheStore? cache = null,
        IIngestionSourceFactory? sources = null,
        IPayloadFiles? payloads = null,
        IRecordSearchFactory? searches = null,
        IPartitionRegistry? partitions = null,
        ITemplateSource? documentTemplates = null,
        IRepositoryFolders? repositories = null)
    {
        var held = templates ?? SampleTemplates;
        var stores = Stores();
        var loader = new DeliveryDocumentLoader();
        return new EngineContext(
            loader,
            sources ?? new MemoryIngestionTables(time),
            payloads ?? new StoragePayloadFiles(stores),
            stores,
            new SecretResolver([new EnvSecretProvider()]),
            ledger,
            time ?? TimeProvider.System,
            NullLoggerFactory.Instance,
            protocols ?? new DefaultProtocolFactory(),
            CompositeDeliveryListener.Empty,
            Cache: cache ?? SampleCache,
            Searches: searches ?? FixedRecordSearchFactory.SampleWellbores(),
            Partitions: partitions ?? FixedPartitionRegistry.Empty)
        {
            DocumentTemplates = documentTemplates ?? held,
            RepositoryFolders = repositories ?? RepositoryFolders.None,
        };
    }

    /// <summary>
    /// The sample well log flow with the network target replaced by a local placeholder (tests never call OSDU), its
    /// source pointed at the in-memory ingestion tables, and its work and payload locations under
    /// <paramref name="root"/>, a directory the test owns.
    /// </summary>
    public static FlowDefinition LocalFlow(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var flow = new DeliveryDocumentLoader().LoadFlow(Flow);
        return Localize(flow, root);
    }

    /// <summary>
    /// <paramref name="flow"/> as if its document sat in <paramref name="root"/>, still rendering with the sample mappings. A
    /// run through the executor writes its history next to the flow's document, and the sample estate's folder is shared
    /// by every test of the process (the lineage tests copy it whole while others run), so a test that runs the executor
    /// runs a flow whose document is in a folder of its own.
    /// </summary>
    public static FlowDefinition InFolder(FlowDefinition flow, string root)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var folder = Path.Combine(root, "flows");
        Directory.CreateDirectory(folder);
        return flow with
        {
            SourcePath = Path.Combine(folder, Path.GetFileName(flow.SourcePath ?? Flow)),
            Render = flow.Render with { MappingsDirectory = Mappings },
        };
    }

    /// <summary>The sample wellbore flow, localized the same way; it streams no payload.</summary>
    public static FlowDefinition LocalWellboreFlow(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var flow = new DeliveryDocumentLoader().LoadFlow(WellboreFlowFile);
        return Localize(flow, root);
    }

    private static FlowDefinition Localize(FlowDefinition flow, string root)
    {
        // The sample flow names its partition; a test works in it, as a run bound to it does. The partition keeps the
        // ledger the flow kept before it named its partitions, so every identity the suites derive stays what it was.
        flow = flow.DeclaresPartitions ? flow.ForPartition(SamplePartition) : flow;
        var payloads = flow.Source.Payloads.ToDictionary(
            p => p.Key,
            p => p.Value with { Root = Path.Combine(root, "curves") },
            StringComparer.Ordinal);
        return flow with
        {
            Source = flow.Source with
            {
                Connection = MemoryConnection,
                Work = Path.Combine(root, "work"),
                Payloads = payloads,
            },
            Target = flow.Target with
            {
                Endpoint = "http://localhost:9/facade-api",
                Auth = new TargetAuth { Type = TargetAuthType.None },
                // The partition stays: it is what names the cache the render reads.
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [CacheScope.PartitionHeader] = SampleCacheScope },
            },
            // One record at a time and one renderer, so a suite's outcome never depends on how the server interleaves claims.
            Reliability = flow.Reliability with { Retry = flow.Reliability.Retry with { Attempts = 3, RecordBaseDelayMinutes = 1 } },
            Parallelism = flow.Parallelism with { Concurrency = 1, Renderers = 1 },

            // The sample's cache steps read the ingestion database and the platform the memory tables and the placeholder
            // target stand in for; a suite of the cache steps names the steps it runs.
            Cache = new FlowCacheSteps(),
        };
    }

    public static ILogger<T> Logger<T>() => NullLogger<T>.Instance;

    /// <summary>A flow read for its target alone: its source and render name tables and a mapping no test opens.</summary>
    public static FlowDefinition Targeting(FlowTarget target, string? interfaceName = null) => new()
    {
        Name = "targeting",
        Interface = interfaceName,
        Source = new FlowSource
        {
            Connection = MemoryConnection,
            Record = new FlowSourceTable { Object = "ing.Record", Key = ["record_id"] },
            Work = "work",
        },
        Render = new FlowRender { Mapping = "Targeting@1.0.0" },
        Target = target,
    };
}

/// <summary>A compact OSDU-shaped schema for unit tests: allOf, a definitions ref, an array of objects, tags.</summary>
public static class TestSchema
{
    public const string Kind = "test:wks:work-product-component--Thing:1.0.0";

    /// <summary>The envelope every test mapping's record carries, at the record's own level.</summary>
    public const string Envelope = """
        acl:
          owners: [owners@x]
          viewers: [viewers@x]
        legal:
          legaltags: [tag]
          otherRelevantDataCountries: [US]
        """;

    /// <summary>The data properties every test mapping starts from: the key's own property and the one property the schema requires.</summary>
    public const string BaseData = """
        Name: { $from: name }
        Depth: { $from: depth }
        """;

    public static SchemaSnapshot Build() => SchemaSnapshot.Parse(Kind, """
        {
          "$id": "https://example.org/Thing.1.0.0.json",
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "kind": { "type": "string" },
            "acl": { "$ref": "#/definitions/AbstractAccessControlList.1.0.0" },
            "legal": { "type": "object", "properties": { "legaltags": { "type": "array", "items": { "type": "string" } }, "otherRelevantDataCountries": { "type": "array", "items": { "type": "string" } } }, "required": ["legaltags", "otherRelevantDataCountries"] },
            "tags": { "type": "object", "additionalProperties": { "type": "string" } },
            "data": {
              "allOf": [
                { "$ref": "#/definitions/AbstractCommon.1.0.0" },
                {
                  "type": "object",
                  "properties": {
                    "WellboreID": { "type": "string", "pattern": "^[\\w\\-\\.]+:master-data\\-\\-Wellbore:[\\w\\-\\.\\:\\%]+:[0-9]*$", "x-osdu-relationship": [ { "GroupType": "master-data", "EntityType": "Wellbore" } ] },
                    "Depth": { "type": "number" },
                    "Count": { "type": "integer" },
                    "IsRegular": { "type": "boolean" },
                    "Unit": { "type": "string", "x-osdu-relationship": [ { "GroupType": "reference-data", "EntityType": "UnitOfMeasure" } ] },
                    "When": { "type": "string", "format": "date-time" },
                    "Day": { "type": "string", "format": "date" },
                    "Clock": { "type": "string", "format": "time" },
                    "Weight": { "type": "number" },
                    "Small": { "type": "integer", "format": "int32" },
                    "Big": { "type": "integer", "format": "int64" },
                    "Days": { "type": "array", "items": { "type": "string", "format": "date" } },
                    "Curves": { "type": "array", "items": { "type": "object", "properties": { "CurveID": { "type": "string" }, "TopDepth": { "type": "number" } } } },
                    "Nested": { "type": "object", "properties": { "Inner": { "type": "string" } } },
                    "Aliases": { "type": "array", "items": { "type": "string" } },
                    "Symbol": { "type": "string" }
                  },
                  "required": ["Depth"]
                }
              ]
            }
          },
          "required": ["kind", "acl", "legal"],
          "definitions": {
            "AbstractAccessControlList.1.0.0": { "type": "object", "properties": { "owners": { "type": "array", "items": { "type": "string" } }, "viewers": { "type": "array", "items": { "type": "string" } } }, "required": ["owners", "viewers"] },
            "AbstractCommon.1.0.0": { "type": "object", "properties": { "Name": { "type": "string" }, "Description": { "type": "string" } } }
          }
        }
        """, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>The template version of <see cref="Build"/>, which every test mapping pins.</summary>
    public static TemplateReference Template => new(Kind, Build().Version);

    public static ReferenceSnapshot References() => new("refs-1", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    [
        new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:m", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Code"] = "m", ["Name"] = "metre" }),
            ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:ft", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Code"] = "ft", ["Name"] = "foot" }),
        ]),
        new ReferenceType("Wellbore", "master-data--Wellbore",
        [
            ReferenceItem.FromText("dev:master-data--Wellbore:abc", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["FacilityName"] = "WB 1/1-A" }),
        ]),
    ]);

    public static RenderContext Context(string mapping = "Thing@1.0.0") => new()
    {
        MappingReference = mapping,
        CacheScope = "dev",
        CacheVersion = "refs-1",
        SchemaSnapshotVersion = Build().Version,
        Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = "dev" },
    };

    /// <summary>
    /// A mapping document over the test template: the header, then the record with <see cref="Envelope"/>, the data
    /// properties <paramref name="baseData"/> followed by <paramref name="data"/>, and the record-level properties
    /// <paramref name="record"/> (a tags block, or a property the record writes itself). A fragment is written at any
    /// indentation: it is laid under its parent as it stands.
    /// </summary>
    public static string MappingDocument(string data = "", string baseData = BaseData, string record = "")
    {
        var body = Indented(Envelope, 2) + Indented(record, 2);
        var properties = Indented(baseData, 4) + Indented(data, 4);
        if (properties.Length > 0)
        {
            body += "  data:\n" + properties;
        }

        return $"""
            documentType: mapping
            name: Thing
            version: 1.0.0
            template:
              kind: {Kind}
              version: {Build().Version}
            dataset:
              system: test
              key: [name]
            parameters:
              dataPartition: {"{"} required: true {"}"}
            record:

            """ + body + "\n";
    }

    /// <summary>A valid mapping over the test template, with <paramref name="data"/> after the base data properties.</summary>
    public static MappingDefinition Mapping(string data = "", string baseData = BaseData, string record = "")
        => new DeliveryDocumentLoader().ParseMapping(MappingDocument(data, baseData, record), "thing.yaml");

    /// <summary>A mapping document with a cache node and a repeated array, for the loader tests.</summary>
    public static string MappingYaml => MappingDocument("""
        Unit:
          $cache: UnitOfMeasure.id
          $findBy: Code = unit
        Curves:
          $forEach: curves
          $item:
            CurveID: { $from: curve_id }
            TopDepth: { $from: top }
        """);

    /// <summary>
    /// A YAML fragment laid under a parent at <paramref name="indent"/> spaces: its lines keep their indentation relative
    /// to the least indented one, which moves to <paramref name="indent"/>. Blank lines are dropped.
    /// </summary>
    public static string Indented(string fragment, int indent)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        var lines = fragment.ReplaceLineEndings("\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var least = lines.Min(l => l.Length - l.TrimStart(' ').Length);
        var pad = new string(' ', indent);
        return string.Concat(lines.Select(l => pad + l[least..] + "\n"));
    }

    public static JsonObject Doc(string json) => (JsonObject)JsonNode.Parse(json)!;
}

/// <summary>
/// A search that answers from records it was given instead of a platform, keeping to the protocol a platform search keeps:
/// a question is unknown until it has been asked, and anything it was not given is found nowhere. The parents it was given
/// are read the way storage hands a record over, trimmed to the sections a mapping reads, and the children it was given are
/// found the same way, by id.
/// </summary>
public sealed class FixedRecordSearch : IRecordSearch
{
    private readonly IReadOnlyList<(string Field, string Value, string Id)> _known;
    private readonly IReadOnlyDictionary<string, JsonObject> _parents;
    private readonly IReadOnlyDictionary<string, JsonObject> _children;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<SearchQuestion, SearchAnswer> _answers = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ParentQuestion, ParentAnswer> _read = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ChildQuestion, ChildAnswer> _looked = new();

    public FixedRecordSearch(
        IReadOnlyList<(string Field, string Value, string Id)> known,
        IReadOnlyDictionary<string, JsonObject>? parents = null,
        IReadOnlyDictionary<string, JsonObject>? children = null)
    {
        _known = known;
        _parents = parents ?? new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        _children = children ?? new Dictionary<string, JsonObject>(StringComparer.Ordinal);
    }

    /// <summary>Every child looked for of this search, in order.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<ChildQuestion> Looked { get; } = new();

    public bool TryRead(ChildQuestion question, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ChildAnswer? answer)
        => _looked.TryGetValue(question, out answer);

    /// <summary>Every question asked of this search, in order, so a test can count the round trips.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<SearchQuestion> Asked { get; } = new();

    /// <summary>Every parent read of this search, in order.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<ParentQuestion> Read { get; } = new();

    public bool TryAnswer(SearchQuestion question, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SearchAnswer? answer)
        => _answers.TryGetValue(question, out answer);

    public bool TryRead(ParentQuestion question, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ParentAnswer? answer)
        => _read.TryGetValue(question, out answer);

    public Task AnswerAsync(IReadOnlyCollection<PlatformQuestion> questions, CancellationToken ct = default)
    {
        foreach (var parent in questions.OfType<ParentQuestion>().Where(q => !_read.ContainsKey(q)))
        {
            Read.Enqueue(parent);
            _read[parent] = _parents.TryGetValue(parent.Id, out var record)
                ? OsduRecordSearch.Answer(parent, System.Text.Json.JsonDocument.Parse(record.ToJsonString()).RootElement.Clone())
                : ParentAnswer.None;
        }

        foreach (var child in questions.OfType<ChildQuestion>().Where(q => !_looked.ContainsKey(q)))
        {
            Looked.Enqueue(child);
            _looked[child] = _children.TryGetValue(child.Id, out var record)
                ? OsduRecordSearch.Answer(child, System.Text.Json.JsonDocument.Parse(record.ToJsonString()).RootElement.Clone())
                : ChildAnswer.None;
        }

        foreach (var question in questions.OfType<SearchQuestion>().Where(q => !_answers.ContainsKey(q)))
        {
            Asked.Enqueue(question);
            var ids = _known
                .Where(k => string.Equals(k.Field, question.Field, StringComparison.Ordinal) && string.Equals(k.Value, question.Value, StringComparison.Ordinal))
                .Select(k => k.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            _answers[question] = ids.Count switch
            {
                0 => SearchAnswer.None,
                1 => SearchAnswer.Found(ids[0]),
                _ => SearchAnswer.Ambiguous(ids.Count, ids),
            };
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Makes a <see cref="FixedRecordSearch"/> for every runtime, over the same records, and keeps each one it made. An id
/// written with <see cref="Partition"/> in it is a record of the partition the flow delivers to, as a platform search
/// finds only the records of the partition it is asked in.
/// </summary>
public sealed class FixedRecordSearchFactory(params (string Field, string Value, string Id)[] known) : IRecordSearchFactory
{
    /// <summary>Stands for the partition of the flow the search is made for.</summary>
    public const string Partition = "{partition}";

    /// <summary>
    /// The wellbores the sample logs belong to, found by name in whichever partition the flow delivers to, plus the two
    /// fixture wellbores the fixture documents name; <paramref name="parents"/>, the parent records the platform holds; and
    /// <paramref name="children"/>, the child records it holds.
    /// </summary>
    public static FixedRecordSearchFactory SampleWellbores(
        IReadOnlyDictionary<string, JsonObject>? parents = null, IReadOnlyDictionary<string, JsonObject>? children = null) => new(
    [
        .. SampleWellLogs.Logs().Select(l => l.WellboreUwi).Distinct(StringComparer.Ordinal)
            .Select(uwi => ("data.FacilityName", uwi, Partition + ":master-data--Wellbore:" + WellboreId(uwi))),
        ("data.FacilityName", "OSDU-DEV-1-A", Partition + ":master-data--Wellbore:OSDU-DEV-1-A"),
        ("data.FacilityName", "OSDU-DEV-1-B", Partition + ":master-data--Wellbore:OSDU-DEV-1-B"),
    ])
    {
        Parents = parents ?? new Dictionary<string, JsonObject>(StringComparer.Ordinal),
        Children = children ?? new Dictionary<string, JsonObject>(StringComparer.Ordinal),
    };

    /// <summary>The id part the suites give a wellbore of that name: "Wellbore B-2 B" is Wellbore-B-2-B, "A/1" is A-1.</summary>
    public static string WellboreId(string facilityName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(facilityName);
        return facilityName.Replace(' ', '-').Replace('/', '-');
    }

    public System.Collections.Concurrent.ConcurrentQueue<FixedRecordSearch> Created { get; } = new();

    /// <summary>The parent records the platform holds, by OSDU id; an id written with <see cref="Partition"/> is of the flow's partition.</summary>
    public IReadOnlyDictionary<string, JsonObject> Parents { get; init; } = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

    /// <summary>The child records the platform holds, by OSDU id; an id written with <see cref="Partition"/> is of the flow's partition.</summary>
    public IReadOnlyDictionary<string, JsonObject> Children { get; init; } = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

    public IRecordSearch Create(FlowDefinition flow, Func<CancellationToken, Task<OsduHttpClient>> target, ILoggerFactory loggers)
    {
        var declared = flow.Target.Headers.FirstOrDefault(h => h.Key.Equals("data-partition-id", StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty;
        var partition = declared.StartsWith("${env:", StringComparison.Ordinal) && declared.EndsWith('}')
            ? Environment.GetEnvironmentVariable(declared[6..^1]) ?? declared
            : declared;
        Dictionary<string, JsonObject> InPartition(IReadOnlyDictionary<string, JsonObject> records) => records.ToDictionary(
            p => p.Key.Replace(Partition, partition, StringComparison.Ordinal),
            p => (JsonObject)JsonNode.Parse(p.Value.ToJsonString().Replace(Partition, partition, StringComparison.Ordinal))!,
            StringComparer.Ordinal);
        var search = new FixedRecordSearch(
            known.Select(k => (k.Field, k.Value, k.Id.Replace(Partition, partition, StringComparison.Ordinal))).ToList(),
            InPartition(Parents),
            InPartition(Children));
        Created.Enqueue(search);
        return search;
    }
}

/// <summary>A partition registry holding what a test gives it, for engines that run flows without the module's database.</summary>
public sealed class FixedPartitionRegistry(RegisteredPartitions partitions) : IPartitionRegistry
{
    /// <summary>A registry that holds no partition.</summary>
    public static readonly FixedPartitionRegistry Empty = new(RegisteredPartitions.None);

    /// <summary>A registry holding <paramref name="names"/>, with <paramref name="fallback"/> the default when one is named.</summary>
    public static FixedPartitionRegistry Of(string? fallback, params string[] names)
        => new(new RegisteredPartitions(names.Select(name => new RegisteredPartition(name, null, string.Equals(name, fallback, StringComparison.OrdinalIgnoreCase)))));

    public Task<RegisteredPartitions> ReadAsync(CancellationToken ct = default) => Task.FromResult(partitions);
}

/// <summary>
/// Registers ledgers in the ledger's directory for a test that writes to a ledger directly, as a run registers its own
/// before it writes a row of it (osdu/docs/reference/concepts/ledger.md, Ledgers, flows and partitions). A test that
/// runs a flow needs none of this: the run does it.
/// </summary>
public static class TestLedgers
{
    /// <summary>The partition a test's ledgers are kept under unless it names another.</summary>
    public const string Partition = "dev";

    /// <summary>Registers each of <paramref name="flowIds"/> in <paramref name="partition"/>, named after its identity.</summary>
    public static async Task RegisterAsync(this ILedger ledger, string partition, params Guid[] flowIds)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        foreach (var flowId in flowIds)
        {
            await ledger.RegisterLedgerAsync(new LedgerEntry
            {
                FlowId = flowId,
                Partition = partition,
                Kind = LedgerKinds.Delivery,
                FlowName = $"test-{flowId:N}",
                LedgerName = $"test-{flowId:N}",
            });
        }
    }

    /// <summary>Registers each of <paramref name="flowIds"/> in <see cref="Partition"/>.</summary>
    public static Task RegisterAsync(this ILedger ledger, params Guid[] flowIds) => ledger.RegisterAsync(Partition, flowIds);

    /// <summary>
    /// Registers <paramref name="flowId"/> in <paramref name="partition"/> under the names a run of <paramref name="flowName"/>
    /// registers it with, for a test whose flow is read back by name (the Partitions page lists flows by it).
    /// </summary>
    public static Task RegisterAsync(this ILedger ledger, Guid flowId, string partition, string flowName, string? ledgerName = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        return ledger.RegisterLedgerAsync(new LedgerEntry
        {
            FlowId = flowId,
            Partition = partition,
            Kind = LedgerKinds.Delivery,
            FlowName = flowName,
            LedgerName = ledgerName ?? flowName,
        });
    }

    /// <summary>
    /// Registers <paramref name="flow"/>'s ledger exactly as a run of it would (<see cref="LedgerRegistration"/>), for a test
    /// that drives the intake or the worker itself rather than through the runtime's operations.
    /// </summary>
    public static Task RegisterAsync(this ILedger ledger, FlowDefinition flow)
        => LedgerRegistration.RegisterAsync(ledger, flow, new SecretResolver([new EnvSecretProvider()]), keptWhenUnresolved: false);

    /// <summary>Registers a retrieval flow's ledger in <paramref name="partition"/>, as its executor does before the runner writes.</summary>
    public static Task RegisterAsync(this ILedger ledger, RetrievalDefinition flow, string partition = Partition)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(flow);
        return ledger.RegisterLedgerAsync(new LedgerEntry
        {
            FlowId = flow.Id,
            Partition = partition,
            Kind = LedgerKinds.Retrieval,
            FlowName = flow.Name,
            LedgerName = flow.Name,
        });
    }
}
