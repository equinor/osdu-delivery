using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Engine.Snapshots;

/// <summary>
/// The types of a partition's cache a pipeline's cache steps bring up to date (osdu/docs/reference/flow/delivery.md, cache),
/// resolved against what the partition's synced cache flows declare. An entry names a type of the cache by its name, or an
/// OSDU entity type, which stands for every type of the cache holding records of it, whichever repository's cache flow
/// declares it: the steps work on the OSDU dataset's own terms, not on one repository's names.
/// </summary>
public sealed record CacheStepPlan(IReadOnlyList<CacheStepType> Before, IReadOnlyList<CacheStepType> After)
{
    /// <summary>
    /// True when a type of the after step holds a kind the pipeline's steps of <c>enrich:</c> register: the step reads back
    /// the datasets the changed records link besides the records (osdu/docs/reference/flow/enrich-files.md, The cache).
    /// </summary>
    public bool TakesStepDatasets { get; init; }

    /// <summary>A pipeline that brings nothing up to date.</summary>
    public static CacheStepPlan None { get; } = new([], []);

    /// <summary>True when neither step has a type to work on.</summary>
    public bool IsEmpty => Before.Count == 0 && After.Count == 0;

    /// <summary>
    /// The types <paramref name="steps"/> name in the partition's cache <paramref name="declaration"/>, for a pipeline that
    /// delivers <paramref name="kind"/>, whose chain reads <paramref name="reads"/> and whose steps of <c>enrich:</c> register
    /// <paramref name="stepKinds"/>. Everything a step cannot do is refused here, before the run reads or sends anything, with
    /// every finding at once.
    /// </summary>
    /// <param name="flow">The pipeline, as its messages name it.</param>
    /// <param name="steps">The pipeline's <c>cache:</c> block.</param>
    /// <param name="declaration">What the partition's synced cache flows declare.</param>
    /// <param name="kind">The exact kind the pipeline's mapping writes.</param>
    /// <param name="reads">
    /// The cache types the mapping and every fragment of its steps read by name, and the entity types they check the ids they
    /// build against (<see cref="MappingCacheReads.OfChain"/>).
    /// </param>
    /// <param name="stepKinds">The kinds the pipeline's steps register their files as (<see cref="EnrichFiles.KindsOf"/>); none when left out.</param>
    /// <exception cref="DeliveryException">An entry names nothing the partition's cache holds, or a type a step cannot work on.</exception>
    public static CacheStepPlan Resolve(
        string flow, FlowCacheSteps steps, CacheDeclaration declaration, string kind, MappingCacheReads reads, IReadOnlyList<string>? stepKinds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flow);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(reads);
        if (steps.IsEmpty)
        {
            return None;
        }

        var registered = stepKinds ?? [];
        var problems = new List<string>();
        var before = steps.Before.Auto
            ? AutoBefore(declaration, reads)
            : Named(steps.Before, "cache.before", declaration, problems, type => BeforeProblem(type));
        var after = steps.After.Auto
            ? [.. declaration.Steps().Where(type => (type.Holds(kind) || registered.Any(type.Holds)) && TakesEveryRecord(type))]
            : Named(steps.After, "cache.after", declaration, problems, type => AfterProblem(type, kind, registered));
        if (problems.Count > 0)
        {
            throw new DeliveryException(
                $"Pipeline '{flow}' names in its cache steps what the cache of partition '{declaration.Scope}' cannot bring up to date, so nothing was read or sent: {string.Join("; ", problems)}.");
        }

        return new CacheStepPlan(before, after) { TakesStepDatasets = after.Any(type => registered.Any(type.Holds)) };
    }

    /// <summary>
    /// The types <c>cache.before: auto</c> stands for: every OSDU type of the partition's cache the chain reads (the mapping
    /// and every fragment of its steps), by name or by the entity type it checks the ids it builds against, and every lookup
    /// table from an ingestion table it reads by name. A dictionary or a dimension is its cache flow's to capture, in the order
    /// lineage gives it, and is left out.
    /// </summary>
    private static List<CacheStepType> AutoBefore(CacheDeclaration declaration, MappingCacheReads reads)
    {
        var entityTypes = reads.EntityTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var named = reads.Types.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. declaration.Steps().Where(type =>
            (type.IsOsdu && (named.Contains(type.TypeName) || entityTypes.Contains(type.EntityType)))
            || (type.IsTable && named.Contains(type.TypeName)))];
    }

    private static List<CacheStepType> Named(
        CacheStepEntries entries, string key, CacheDeclaration declaration, List<string> problems, Func<CacheStepType, string?> problemOf)
    {
        var types = new List<CacheStepType>();
        foreach (var entry in entries.Named)
        {
            var found = CacheStepEntries.IsEntityType(entry)
                ? declaration.Steps().Where(type => string.Equals(type.EntityType, entry, StringComparison.OrdinalIgnoreCase)).ToList()
                : declaration.Step(entry) is { } one ? [one] : [];
            if (found.Count == 0)
            {
                var held = declaration.Steps().Select(t => $"{t.TypeName} ({t.EntityType})").ToList();
                problems.Add(held.Count == 0
                    ? $"{key} names '{entry}', and no synced cache flow declares anything for the partition"
                    : $"{key} names '{entry}', which is neither a type of the partition's cache nor an entity type one holds; it holds {string.Join(", ", held)}");
                continue;
            }

            foreach (var type in found)
            {
                if (problemOf(type) is { } problem)
                {
                    problems.Add($"{key} names '{entry}': {problem}");
                }
                else if (!types.Any(t => string.Equals(t.TypeName, type.TypeName, StringComparison.OrdinalIgnoreCase)))
                {
                    types.Add(type);
                }
            }
        }

        return types;
    }

    private static string? BeforeProblem(CacheStepType type)
        => type.IsOsdu || type.IsTable
            ? null
            : $"{type.TypeName} is a lookup table from a dictionary or a dimension, which only its cache flow reads, from the repository or the dimension's build; run that cache flow before the pipeline, as lineage orders it";

    private static string? AfterProblem(CacheStepType type, string kind, IReadOnlyList<string> stepKinds)
    {
        if (!type.IsOsdu)
        {
            return $"{type.TypeName} is a lookup table, and the pipeline delivers OSDU records, which a lookup table does not hold";
        }

        if (!type.Holds(kind) && !stepKinds.Any(type.Holds))
        {
            var registered = stepKinds.Count == 0 ? string.Empty : $", and its steps of enrich: register {string.Join(", ", stepKinds)}";
            return $"{type.TypeName} holds {string.Join(", ", type.Declarations.Select(d => d.Kind).Distinct(StringComparer.Ordinal))}, and the pipeline delivers {kind}{registered}";
        }

        return TakesEveryRecord(type)
            ? null
            : $"every cache flow declaring {type.TypeName} narrows it with a query, so a delivered record the query does not match would stay in the cache; declare it without a query in one cache flow, or leave it out";
    }

    /// <summary>
    /// True when some flow declares the type without narrowing it, so every record of its kind belongs in it: what a delivery
    /// adds is then what a refresh would have found.
    /// </summary>
    private static bool TakesEveryRecord(CacheStepType type)
        => type.Declarations.Any(d => string.IsNullOrWhiteSpace(d.Query) || d.Query.Trim() == "*");
}
