using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The read ahead of each partition's current cache version (<see cref="CacheWarmupService"/>): what each look does with a
/// version it has not seen, one it holds, one that cannot be read and a partition whose cache is gone, how a host composes it,
/// and the settings a node reads from its environment.
/// </summary>
public class CacheWarmupTests
{
    [Fact]
    public async Task A_partition_s_current_version_is_read_at_the_first_look_and_held_after()
    {
        var store = new CountingCacheStore();
        store.Current("dev", "20261010T233100Z", items: 212_607);
        var logs = new ListLogger();
        var service = new CacheWarmupService(store, new CacheWarmupOptions(), logs);

        var first = await service.PassAsync(store, CancellationToken.None);
        var second = await service.PassAsync(store, CancellationToken.None);

        Assert.Equal([new CacheWarmed("dev", "20261010T233100Z", CacheWarmState.Read, 212_607)], first);
        Assert.Equal([new CacheWarmed("dev", "20261010T233100Z", CacheWarmState.Held, 212_607)], second);
        // The store answers each look from what it keeps; the service says once that the version is in memory.
        var read = Assert.Single(logs.Lines);
        Assert.Contains("Version 20261010T233100Z of the cache of partition 'dev' (212607 records) is in memory", read, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_newer_version_is_read_at_the_first_look_after_it_becomes_current()
    {
        var store = new CountingCacheStore();
        store.Current("dev", "v1");
        var service = new CacheWarmupService(store, new CacheWarmupOptions(), new ListLogger());
        await service.PassAsync(store, CancellationToken.None);

        // A refresh on any host wrote a version; the next look reads it.
        store.Current("dev", "v2");
        var looked = await service.PassAsync(store, CancellationToken.None);

        Assert.Equal([new CacheWarmed("dev", "v2", CacheWarmState.Read, 3)], looked);
        Assert.Equal(1, store.Loads(("dev", "v2")));
    }

    [Fact]
    public async Task A_version_that_cannot_be_read_is_reported_once_and_not_read_again_while_it_is_current()
    {
        var store = new CountingCacheStore();
        store.Current("dev", "v1");
        store.Broken.Add(("dev", "v1"));
        var logs = new ListLogger();
        var service = new CacheWarmupService(store, new CacheWarmupOptions(), logs);

        var first = await service.PassAsync(store, CancellationToken.None);
        var second = await service.PassAsync(store, CancellationToken.None);

        Assert.Equal(CacheWarmState.Refused, Assert.Single(first).State);
        Assert.Equal(CacheWarmState.Refused, Assert.Single(second).State);
        Assert.Equal(1, store.Loads(("dev", "v1")));
        var refused = Assert.Single(logs.Lines);
        Assert.Contains("cannot be read ahead, and is not tried again while it is current", refused, StringComparison.Ordinal);
        Assert.Contains("does not match the content hash it was written with", refused, StringComparison.Ordinal);

        // The version a refresh writes after it is read as any other.
        store.Current("dev", "v2");
        Assert.Equal(CacheWarmState.Read, Assert.Single(await service.PassAsync(store, CancellationToken.None)).State);
    }

    [Fact]
    public async Task Every_partition_is_looked_at_and_one_whose_cache_is_gone_is_read_again_when_it_returns()
    {
        var store = new CountingCacheStore();
        store.Current("dev", "d1");
        store.Current("test", "t1");
        var service = new CacheWarmupService(store, new CacheWarmupOptions(), new ListLogger());

        var both = await service.PassAsync(store, CancellationToken.None);
        Assert.Equal([("dev", CacheWarmState.Read), ("test", CacheWarmState.Read)], both.Select(w => (w.Scope, w.State)));

        store.Drop("test");
        Assert.Equal([("dev", CacheWarmState.Held)], (await service.PassAsync(store, CancellationToken.None)).Select(w => (w.Scope, w.State)));

        // Written again under the same label (a cache rebuilt from scratch starts its labels again), it is read again.
        store.Current("test", "t1");
        Assert.Equal(
            [("dev", CacheWarmState.Held), ("test", CacheWarmState.Read)],
            (await service.PassAsync(store, CancellationToken.None)).Select(w => (w.Scope, w.State)));
    }

    [Fact]
    public async Task A_version_whose_row_went_between_the_two_reads_is_left_to_the_next_look()
    {
        var store = new CountingCacheStore();
        store.Current("dev", "v1");
        store.Gone.Add(("dev", "v1"));
        var service = new CacheWarmupService(store, new CacheWarmupOptions(), new ListLogger());

        Assert.Empty(await service.PassAsync(store, CancellationToken.None));

        store.Gone.Clear();
        Assert.Equal(CacheWarmState.Read, Assert.Single(await service.PassAsync(store, CancellationToken.None)).State);
    }

    [Fact]
    public async Task The_service_looks_when_the_host_starts_and_stops_with_it()
    {
        var store = new CountingCacheStore();
        store.Current("dev", "v1");
        using var service = new CacheWarmupService(store, new CacheWarmupOptions { PollSeconds = CacheWarmupOptions.MaximumPollSeconds }, new ListLogger());

        await service.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (store.Loads(("dev", "v1")) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, store.Loads(("dev", "v1")));
        await service.StopAsync(CancellationToken.None);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task A_host_without_the_module_database_starts_and_reads_nothing_ahead()
    {
        // A node started without SQLFLOW_CATALOG_DB: the ledger source answers no cache, and the node must still start.
        var services = new ServiceCollection()
            .AddSingleton<ILogger<CacheWarmupService>>(new ListLogger())
            .AddDeliveryLedger(_ => null)
            .AddDeliveryCacheWarmup(new CacheWarmupOptions());
        await using var provider = services.BuildServiceProvider();
        var service = Assert.IsType<CacheWarmupService>(Assert.Single(provider.GetServices<IHostedService>()));

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
        await service.StopAsync(CancellationToken.None);

        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
    }

    [Fact]
    public void A_host_composes_the_service_unless_its_options_turn_it_off()
    {
        static IReadOnlyList<IHostedService> Hosted(CacheWarmupOptions options)
        {
            var services = new ServiceCollection()
                .AddSingleton<ILogger<CacheWarmupService>>(new ListLogger())
                .AddDeliveryLedger(_ => null)
                .AddDeliveryCacheWarmup(options);
            using var provider = services.BuildServiceProvider();
            return provider.GetServices<IHostedService>().ToList();
        }

        Assert.IsType<CacheWarmupService>(Assert.Single(Hosted(new CacheWarmupOptions())));
        Assert.Empty(Hosted(new CacheWarmupOptions { Enabled = false }));

        var tooOften = Assert.Throws<InvalidOperationException>(() => Hosted(new CacheWarmupOptions { PollSeconds = 1 }));
        Assert.Contains("Osdu:CacheWarmup:PollSeconds must be between 5 and 3600", tooOften.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_node_reads_its_settings_from_its_environment_and_refuses_a_value_they_cannot_hold()
    {
        static CacheWarmupOptions Read(params (string Name, string Value)[] set)
            => CacheWarmupOptions.FromEnvironment(name => set.FirstOrDefault(v => v.Name == name).Value);

        var defaults = Read();
        Assert.True(defaults.Enabled);
        Assert.Equal(60, defaults.PollSeconds);

        var set = Read(("OSDU_CACHE_WARMUP", " False "), ("OSDU_CACHE_WARMUP_SECONDS", "300"));
        Assert.False(set.Enabled);
        Assert.Equal(300, set.PollSeconds);

        Assert.Contains("OSDU_CACHE_WARMUP is 'maybe'; it is true or false.", Assert.Throws<InvalidOperationException>(() => Read(("OSDU_CACHE_WARMUP", "maybe"))).Message, StringComparison.Ordinal);
        Assert.Contains("OSDU_CACHE_WARMUP_SECONDS is 'soon'; it is a whole number of seconds.", Assert.Throws<InvalidOperationException>(() => Read(("OSDU_CACHE_WARMUP_SECONDS", "soon"))).Message, StringComparison.Ordinal);
        Assert.Contains("PollSeconds must be between 5 and 3600", Assert.Throws<InvalidOperationException>(() => Read(("OSDU_CACHE_WARMUP_SECONDS", "4"))).Message, StringComparison.Ordinal);
    }

    /// <summary>A store of current versions only, counting what is asked of it; the warm-up reads, so a write is a failure of the test.</summary>
    private sealed class CountingCacheStore : ICacheStore
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, (string Version, long Items)> _current = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Scope, string Version), int> _loads = [];

        /// <summary>Versions whose load fails as one whose records no longer match its hash does.</summary>
        public HashSet<(string Scope, string Version)> Broken { get; } = [];

        /// <summary>Versions whose row is gone by the time they are loaded.</summary>
        public HashSet<(string Scope, string Version)> Gone { get; } = [];

        public void Current(string scope, string version, long items = 3)
        {
            lock (_gate)
            {
                _current[scope] = (version, items);
            }
        }

        public void Drop(string scope)
        {
            lock (_gate)
            {
                _current.Remove(scope);
            }
        }

        public int Loads((string Scope, string Version) version)
        {
            lock (_gate)
            {
                return _loads.GetValueOrDefault(version);
            }
        }

        public Task<string?> CurrentVersionAsync(string scope, CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_current.TryGetValue(scope, out var current) ? current.Version : null);
            }
        }

        public Task<IReadOnlyList<CacheVersionInfo>> CurrentVersionsAsync(CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<CacheVersionInfo>>(_current
                    .OrderBy(c => c.Key, StringComparer.Ordinal)
                    .Select(c => Info(c.Key, c.Value.Version, c.Value.Items))
                    .ToList());
            }
        }

        public Task<ReferenceSnapshot?> LoadAsync(string scope, string version, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _loads[(scope, version)] = _loads.GetValueOrDefault((scope, version)) + 1;
            }

            if (Broken.Contains((scope, version)))
            {
                throw new DeliveryException($"Version {version} of the cache of partition '{scope}' does not match the content hash it was written with: its records were altered after the version was written, so nothing renders against it.");
            }

            return Task.FromResult(Gone.Contains((scope, version)) ? null : new ReferenceSnapshot(version, DateTimeOffset.UnixEpoch, []));
        }

        public async Task<IReadOnlyList<CacheVersionInfo>> ListVersionsAsync(string scope, CancellationToken ct = default)
            => (await CurrentVersionsAsync(ct)).Where(v => v.Scope == scope).ToList();

        public async Task<CacheVersionInfo?> VersionAsync(string scope, string? version, CancellationToken ct = default)
            => (await ListVersionsAsync(scope, ct)).FirstOrDefault(v => version is null || v.Version == version);

        public Task<CacheDeclaration> DeclarationAsync(string scope, CancellationToken ct = default) => Task.FromResult(new CacheDeclaration(scope, []));

        public Task<IReadOnlySet<string>> HeldByDeliveriesAsync(string scope, string typeName, IReadOnlyCollection<string> ids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));

        public Task<CacheWrite> MergeAsync(
            string scope, string flowName, IReadOnlyList<ReferenceType> captured, CacheCapture capture, DateTimeOffset capturedUtc,
            IReadOnlyList<SystemPropertyReading>? readings = null, CancellationToken ct = default)
            => throw new InvalidOperationException("The warm-up reads the cache; it never writes a version.");

        public Task<CacheRetentionOutcome> ApplyRetentionAsync(CacheRetentionRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("The warm-up reads the cache; it never prunes it.");

        private static CacheVersionInfo Info(string scope, string version, long items)
            => new(scope, version, 1, DateTime.UnixEpoch, true, null, null, "tests", "tests", "tests-cache", items, [], []);
    }

    /// <summary>Every line the service logs, as it would read in a host's log.</summary>
    private sealed class ListLogger : ILogger<CacheWarmupService>
    {
        private readonly Lock _gate = new();
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                {
                    return [.. _lines];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _lines.Add(formatter(state, exception));
            }
        }
    }
}
