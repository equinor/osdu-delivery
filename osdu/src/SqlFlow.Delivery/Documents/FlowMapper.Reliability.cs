using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

// A flow's reliability, parallelism and failWhen blocks (osdu/docs/reference/flow/delivery.md, reliability,
// parallelism and failWhen): a delivery flow's, an interface's laid over its source's, and those of the other kinds,
// which run no interfaces; and the refusals of the settings that moved from reliability to parallelism or took
// another name there.
internal static partial class FlowMapper
{
    private static int MapParallelInterfaces(FlowParallelismYaml? parallelism, string source)
    {
        var parallel = parallelism?.InterfacesAtOnce ?? SourceDefinition.DefaultParallelInterfaces;
        return parallel is < 1 or > SourceDefinition.MaxParallelInterfaces
            ? throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: parallelism.interfacesAtOnce must be between 1 and {SourceDefinition.MaxParallelInterfaces}."))
            : parallel;
    }

    private static FlowFailWhen MapFailWhen(FailWhenYaml? declared, string source, KeyPaths paths)
    {
        var defaults = new FlowFailWhen();
        if (declared is null)
        {
            return defaults;
        }

        var failWhen = new FlowFailWhen
        {
            FailedPercent = declared.FailedPercent,
            MinRecords = declared.MinRecords ?? defaults.MinRecords,
            ConsecutiveFailures = declared.ConsecutiveFailures,
            OutageFailures = declared.OutageFailures ?? defaults.OutageFailures,
        };

        if (failWhen.FailedPercent is { } percent && (double.IsNaN(percent) || percent <= 0 || percent > 100))
        {
            throw new FlowValidationException($"{source}: {paths.Shared("failWhen.failedPercent")} must be above 0 and at most 100.");
        }

        if (failWhen.MinRecords < 1)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("failWhen.minRecords")} must be at least 1.");
        }

        if (failWhen.ConsecutiveFailures is < 1)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("failWhen.consecutiveFailures")} must be at least 1.");
        }

        if (failWhen.OutageFailures < 0)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("failWhen.outageFailures")} must not be negative (0 turns the outage rule off).");
        }

        return failWhen;
    }

    /// <summary>
    /// Where a setting a delivery flow's <c>reliability</c> block still declares is written now, by the name <c>reliability</c>
    /// knew it by; null for the one that is gone (<c>batchSize</c>: retries are claimed <c>recordsAtOnce</c> at a time).
    /// </summary>
    private static string? DeliveryRename(string was) => was switch
    {
        "concurrency" => "recordsAtOnce",
        "fanOut" => "nodes",
        "fanOutMinRecords" => "minRowsPerNode",
        "renderParallelism" => "renderThreads",
        "batchRecords" => "recordsPerBatch",
        "parallelInterfaces" => "interfacesAtOnce",
        _ => null,
    };

    /// <summary>The settings that moved from <c>reliability</c> to <c>parallelism</c>, by the name <c>reliability</c> knew each by, and whether a block sets it.</summary>
    private static readonly (string Was, Func<FlowReliabilityYaml, bool> Set)[] MovedToParallelism =
    [
        ("concurrency", r => r.Concurrency is not null),
        ("fanOut", r => r.FanOut is not null),
        ("fanOutMinRecords", r => r.FanOutMinRecords is not null),
        ("renderParallelism", r => r.RenderParallelism is not null),
        ("batchRecords", r => r.BatchRecords is not null),
        ("batchSize", r => r.BatchSize is not null),
        ("parallelInterfaces", r => r.ParallelInterfaces is not null),
    ];

    /// <summary>
    /// Refuses a setting a <c>reliability</c> block still declares that moved to <c>parallelism</c>
    /// (osdu/docs/reference/flow/delivery.md, parallelism), naming where it is written now: <paramref name="at"/> is where
    /// the block sits and <paramref name="parallelismAt"/> its parallelism beside it. <paramref name="writtenAs"/> gives the
    /// key the flow writes it as now, or null when the flow takes it no more, which <paramref name="unused"/> then explains;
    /// <c>batchSize</c> is gone from every kind.
    /// </summary>
    private static void RefuseMoved(
        FlowReliabilityYaml? r, string at, string parallelismAt, Func<string, string?> writtenAs, Func<string, string> unused, string source)
    {
        if (r is null)
        {
            return;
        }

        foreach (var (was, set) in MovedToParallelism)
        {
            if (!set(r))
            {
                continue;
            }

            if (was == "batchSize")
            {
                throw new FlowValidationException(
                    $"{source}: {at}.batchSize is not a setting any more: a worker claims the records due for a retry {parallelismAt}.recordsAtOnce at a time, at least {FlowParallelism.RetryClaimFloor}. Remove it.");
            }

            throw new FlowValidationException(writtenAs(was) is { } now
                ? $"{source}: {at}.{was} is now {parallelismAt}.{now}: what runs at once, and across the fleet, is the flow's parallelism, and reliability keeps what makes a request hold. Move it."
                : $"{source}: {at}.{was} is not a setting here any more: {unused(was)}");
        }
    }

    /// <summary>The keys a delivery flow's <c>parallelism</c> block knew before each said what it is, and what each is now.</summary>
    private static readonly (string Was, string? Now, string What, Func<FlowParallelismYaml, bool> Set)[] RenamedInParallelism =
    [
        ("fanOut", "nodes", "the most nodes a delivery is spread over, each part on a node of its own; it counts every part, where fanOut counted the parts beside the first run", p => p.FanOut is not null),
        ("fanOutMinRecords", "minRowsPerNode", "the fewest rows a part takes", p => p.FanOutMinRecords is not null),
        ("concurrency", "recordsAtOnce", "the records each part sends to OSDU at the same time", p => p.Concurrency is not null),
        ("renderers", "renderThreads", "the threads that render records on one node", p => p.Renderers is not null),
        ("batchRecords", "recordsPerBatch", "the records one batch file holds", p => p.BatchRecords is not null),
        ("batchSize", null, $"a worker claims the records due for a retry recordsAtOnce at a time, at least {FlowParallelism.RetryClaimFloor}", p => p.BatchSize is not null),
        ("interfaces", "interfacesAtOnce", "how many interfaces of a source deliver at the same time", p => p.Interfaces is not null),
    ];

    /// <summary>
    /// Refuses a key a delivery flow's <c>parallelism</c> block at <paramref name="at"/> still writes by the name it had
    /// before each key said what it is (osdu/docs/reference/flow/delivery.md, parallelism), naming the key it is now.
    /// </summary>
    private static void RefuseRenamed(FlowParallelismYaml? p, string at, string source)
    {
        if (p is null)
        {
            return;
        }

        foreach (var (was, now, what, set) in RenamedInParallelism)
        {
            if (set(p))
            {
                throw new FlowValidationException(now is null
                    ? $"{source}: {at}.{was} is not a setting any more: {what}. Remove it."
                    : $"{source}: {at}.{was} is now {at}.{now}: {what}. Rename it.");
            }
        }
    }

    /// <summary>
    /// The reliability block of a flow of another kind, which runs no interfaces and fans nothing out. <paramref name="kind"/>
    /// names the kind in a message ("an assertion flow"); <paramref name="takesRecordsAtOnce"/> says whether the kind reads
    /// <c>parallelism.recordsAtOnce</c>, which a cache flow does not.
    /// </summary>
    internal static FlowReliability MapReliability(FlowReliabilityYaml? r, string kind, bool takesRecordsAtOnce, string source)
    {
        RefuseMoved(
            r, "reliability", "parallelism", was => takesRecordsAtOnce && was == "concurrency" ? "recordsAtOnce" : null,
            was => $"a delivery flow writes it as parallelism.{DeliveryRename(was)}, and {kind} has no use for it. Remove it.", source);
        return MapReliability(r, source, KeyPaths.Single);
    }

    /// <summary>
    /// The parallelism block of an assertion, inventory, download or retrieval flow: what one run works on at once, written
    /// <c>recordsAtOnce</c> as on a delivery flow (osdu/docs/reference/flow/delivery.md, parallelism), so a flow's settings
    /// read alike whatever its kind. <paramref name="what"/> says what the kind runs at once, for the message refusing the
    /// old name. A dimension flow's block also spreads a build over nodes (<see cref="DimensionMapper"/>).
    /// </summary>
    internal static RunParallelism MapRunParallelism(RunParallelismYaml? p, string what, string source)
    {
        if (p?.Concurrency is not null)
        {
            throw new FlowValidationException(
                $"{source}: parallelism.concurrency is now parallelism.recordsAtOnce, as on a delivery flow: {what}. Rename it.");
        }

        var parallelism = new RunParallelism { RecordsAtOnce = p?.RecordsAtOnce ?? new RunParallelism().RecordsAtOnce };
        return parallelism.RecordsAtOnce < 1
            ? throw new FlowValidationException($"{source}: parallelism.recordsAtOnce must be at least 1.")
            : parallelism;
    }

    /// <summary>A delivery flow's parallelism block, or an interface's laid over its source's; <see cref="Validate"/> checks the ranges.</summary>
    private static FlowParallelism MapParallelism(FlowParallelismYaml? p)
    {
        var defaults = new FlowParallelism();
        return p is null ? defaults : new FlowParallelism
        {
            Nodes = p.Nodes ?? defaults.Nodes,
            MinRowsPerNode = p.MinRowsPerNode ?? defaults.MinRowsPerNode,
            RecordsAtOnce = p.RecordsAtOnce ?? defaults.RecordsAtOnce,
            RenderThreads = p.RenderThreads ?? defaults.RenderThreads,
            RecordsPerBatch = p.RecordsPerBatch ?? defaults.RecordsPerBatch,
        };
    }

    private static FlowReliability MapReliability(FlowReliabilityYaml? r, string source, KeyPaths paths)
    {
        var defaults = new FlowReliability();
        if (r is null)
        {
            return defaults;
        }

        var retryDefaults = new FlowRetry();
        return new FlowReliability
        {
            Retry = r.Retry is null ? retryDefaults : new FlowRetry
            {
                Attempts = r.Retry.Attempts ?? retryDefaults.Attempts,
                Backoff = ParseEnum(r.Retry.Backoff, BackoffKind.Exponential, paths.Shared("reliability.retry.backoff"), source),
                BaseDelayMs = r.Retry.BaseDelayMs ?? retryDefaults.BaseDelayMs,
                MaxDelayMs = r.Retry.MaxDelayMs ?? retryDefaults.MaxDelayMs,
                HonorRetryAfter = r.Retry.HonorRetryAfter ?? retryDefaults.HonorRetryAfter,
                RecordBaseDelayMinutes = r.Retry.RecordBaseDelayMinutes ?? retryDefaults.RecordBaseDelayMinutes,
                RecordMaxDelayMinutes = r.Retry.RecordMaxDelayMinutes ?? retryDefaults.RecordMaxDelayMinutes,
            },
            SkipStatusCodes = r.SkipStatusCodes ?? [],
            TimeoutSeconds = r.TimeoutSeconds ?? defaults.TimeoutSeconds,
            RateLimitRps = r.RateLimitRps ?? defaults.RateLimitRps,
            VerifyTls = r.VerifyTls ?? defaults.VerifyTls,
            UrlAllowlist = r.UrlAllowlist ?? [],
            MaxResponseBytes = r.MaxResponseBytes ?? defaults.MaxResponseBytes,
            MaxRequestBodyBytes = r.MaxRequestBodyBytes ?? defaults.MaxRequestBodyBytes,
            LeaseSeconds = r.LeaseSeconds ?? defaults.LeaseSeconds,
        };
    }
}
