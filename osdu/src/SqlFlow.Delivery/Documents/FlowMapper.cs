using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// How a message names a key of the document: in the single form as written (<c>source.record</c>), for an interface
/// under the interface (<c>interfaces.wells.record</c>), and for a key the source declares for every interface and an
/// interface may override, with the interface it applies to.
/// </summary>
internal sealed record KeyPaths(string? Interface)
{
    public static KeyPaths Single { get; } = new((string?)null);

    public static KeyPaths ForInterface(string name) => new(name);

    private string At => $"interfaces.{Interface}";

    public string Record => Interface is null ? "source.record" : At + ".record";

    public string Datasets => Interface is null ? "source.datasets" : At + ".datasets";

    public string Mapping => Interface is null ? "render.mapping" : At + ".mapping";

    /// <summary>Where enrichments were declared before the mapping declared them: <c>enrich</c>, or the interface's own.</summary>
    public string Enrich => Interface is null ? "enrich" : At + ".enrich";

    /// <summary>The workflows run over the records of the mapping's enrichments: <c>workflows</c>, or the interface's own.</summary>
    public string Workflows => Interface is null ? "workflows" : At + ".workflows";

    /// <summary>A payload set: <c>source.payloads.name</c>, or the interface's <c>files</c> or <c>bulk</c>.</summary>
    public string Payload(string name) => Interface is null ? $"source.payloads.{name}" : $"{At}.{name}";

    /// <summary>A key the single form writes as <paramref name="key"/>, and the interface form at the source or the interface.</summary>
    public string Shared(string key) => Interface is null ? key : $"{key} of interface '{Interface}'";

    /// <summary>The key paths of the document a flow definition was read from.</summary>
    public static KeyPaths Of(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.Interface is null ? Single : ForInterface(flow.Interface);
    }

    /// <summary>
    /// The key a message names for what the single form writes as <paramref name="singleFormKey"/>: the record table, the
    /// child tables, the payload sets and the mapping under the interface, and any other key with the interface it applies to.
    /// </summary>
    public string Name(string singleFormKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(singleFormKey);
        if (Interface is null)
        {
            return singleFormKey;
        }

        foreach (var (single, own) in new[] { ("source.record", ".record"), ("source.datasets", ".datasets"), ("render.mapping", ".mapping") })
        {
            if (singleFormKey.Equals(single, StringComparison.Ordinal) || singleFormKey.StartsWith(single + ".", StringComparison.Ordinal))
            {
                return At + own + singleFormKey[single.Length..];
            }
        }

        const string payloads = "source.payloads.";
        return singleFormKey.StartsWith(payloads, StringComparison.Ordinal)
            ? $"{At}.{singleFormKey[payloads.Length..]}"
            : Shared(singleFormKey);
    }

    /// <summary>Where a message about a flow says it comes from: the file, and the interface when the flow is one.</summary>
    public static string Where(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var where = flow.SourcePath ?? flow.Name;
        return flow.Interface is null ? where : $"{where} (interface '{flow.Interface}')";
    }
}

// Maps a delivery flow document (osdu/docs/reference/flow/delivery.md) onto its source and the flows it delivers:
// Map reads the document in the single form or the interface form and MapFlow maps one flow's blocks. What the
// parts of the document share is here too: the headers and telemetry a flow declares, which the other kinds' mappers
// read as well, the payload set names, the cache steps, where a DDMS is under the endpoint, and the words a message
// uses for a key that is required or a value that is not one of its kind's. Each other part of the document has a
// file of its own beside this one.
internal static partial class FlowMapper
{
    /// <summary>The tenant header every OSDU service requires on every request.</summary>
    public const string PartitionHeader = "data-partition-id";

    /// <summary>
    /// The headers a flow declares on its requests, <paramref name="key"/> naming where: any but the correlation id, which every
    /// request carries as its run sets it (osdu/docs/reference/concepts/request-correlation.md).
    /// </summary>
    public static Dictionary<string, string> MapHeaders(Dictionary<string, string>? declared, string key, string source)
    {
        var headers = declared is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(declared, StringComparer.OrdinalIgnoreCase);
        if (headers.ContainsKey(OsduCorrelation.HeaderName))
        {
            throw new FlowValidationException(
                $"{source}: {key} names '{OsduCorrelation.HeaderName}', which every request of a run carries as the run sets it: {OsduCorrelation.Product}, the code of the repository ({RepositoryDefinition.FileName}), the flow's operation code (telemetry.operation) and the run's id. Remove the header.");
        }

        return headers;
    }

    /// <summary>
    /// The flow's operation code (<c>telemetry.operation</c>): the segment of its runs' correlation id that says what ran
    /// (osdu/docs/reference/concepts/request-correlation.md), or the code of its kind, <paramref name="flowType"/>, when it
    /// names none.
    /// </summary>
    public static string MapTelemetry(TelemetryYaml? declared, string flowType, string source)
    {
        var kind = FlowTelemetry.KindOperation(flowType);
        if (declared?.Operation is not { } operation)
        {
            return kind;
        }

        return OsduCorrelation.IsCode(operation)
            ? operation
            : throw new FlowValidationException(
                $"{source}: telemetry.operation '{operation}' is not an operation code: {OsduCorrelation.CodeRule}. Leave it out to take the kind's own, '{kind}'.");
    }

    /// <summary>
    /// True for a kind the storage service accepts on a record (openapi storage v2, Record.kind:
    /// <c>^[\w\-\.]+:[\w\-\.]+:[\w\-\.]+:[0-9]+.[0-9]+.[0-9]+$</c>). A kind that only looks like four colon-separated
    /// parts, with a space in the entity type or a two-part version, would be refused on every record of a run.
    /// </summary>
    public static bool IsRecordKind(string kind) => RecordKindPattern().IsMatch(kind);

    [System.Text.RegularExpressions.GeneratedRegex(@"^[\w\-\.]+:[\w\-\.]+:[\w\-\.]+:[0-9]+\.[0-9]+\.[0-9]+$")]
    private static partial System.Text.RegularExpressions.Regex RecordKindPattern();

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial System.Text.RegularExpressions.Regex DatasetName();

    /// <summary>The names of the parts an interface's record carries, as its route reads them from the source's payload sets.</summary>
    public const string FilesPayload = "files";

    public const string BulkPayload = "bulk";

    public static SourceDefinition Map(FlowYaml y, string source)
    {
        var name = Require(y.Name, "name", source);
        var description = string.IsNullOrWhiteSpace(y.Description) ? null : y.Description!.Trim();
        var batch = string.IsNullOrWhiteSpace(y.Batch) ? null : y.Batch!.Trim();
        var path = source == "<inline>" ? null : source;
        var partitions = MapPartitions(y.Partitions, source);
        var ledgerPartition = MapKeepLedger(y.KeepLedger, partitions, source);
        RefuseMoved(y.Reliability, "reliability", "parallelism", DeliveryRename, _ => string.Empty, source);
        RefuseRenamed(y.Parallelism, "parallelism", source);
        if (y.Interfaces is null)
        {
            if (y.Parallelism?.InterfacesAtOnce is not null)
            {
                throw new FlowValidationException($"{source}: parallelism.interfacesAtOnce says how many interfaces run at once, and the document declares no interfaces.");
            }

            var flow = WithLedgerPartition(MapFlow(y, source, KeyPaths.Single, partitions), ledgerPartition, source) with
            {
                AdoptedLedger = MapLedger(y.Ledger, "ledger", "the flow", source),
            };
            CheckLedgerName(flow.Name, "name", source);
            if (flow.AdoptedLedger is { } adopted)
            {
                CheckLedgerName(adopted, "ledger", source);
            }

            CheckPartitionLedgers([flow], source);
            return CheckParameterLookups(
                new SourceDefinition { SourcePath = path, Name = name, Description = description, Batch = batch, Interfaces = [flow] }, source);
        }

        return CheckParameterLookups(
            new SourceDefinition
            {
                SourcePath = path,
                Name = name,
                Description = description,
                Batch = batch,
                DeclaresInterfaces = true,
                Interfaces = MapInterfaces(y, name, source, partitions).Select(f => WithLedgerPartition(f, ledgerPartition, source)).ToList(),
                ParallelInterfaces = MapParallelInterfaces(y.Parallelism, source),
            },
            source);
    }

    /// <summary>
    /// A parameter's <c>lookup</c> names a column of a record table the source reads: of the interface it names, which the
    /// source declares, or of the first. A parameter <c>source.record.scope</c> binds is looked up in the column it binds, so
    /// it names none (osdu/docs/reference/flow/parameters.md, Lookups).
    /// </summary>
    private static SourceDefinition CheckParameterLookups(SourceDefinition parsed, string source)
    {
        foreach (var (name, parameter) in parsed.Parameters)
        {
            if (parameter.Lookup is not { } lookup)
            {
                continue;
            }

            var at = $"parameters.{name}.lookup";
            CheckColumn(lookup.Column, at + ".column", source);
            if (lookup.Interface is { } named)
            {
                if (!parsed.DeclaresInterfaces)
                {
                    throw new FlowValidationException(
                        $"{source}: {at}.interface names interface '{named}', and the flow declares no interfaces: its one record table is where the column is read, so leave interface out.");
                }

                if (!parsed.Interfaces.Any(i => string.Equals(i.Interface, named, StringComparison.Ordinal)))
                {
                    throw new FlowValidationException(
                        $"{source}: {at}.interface names interface '{named}', which the flow does not declare; it declares {string.Join(", ", parsed.Interfaces.Select(i => i.Interface))}.");
                }
            }

            foreach (var flow in parsed.Interfaces)
            {
                foreach (var (column, bound) in flow.Source.Record.Scope)
                {
                    if (string.Equals(bound, name, StringComparison.Ordinal))
                    {
                        throw new FlowValidationException(
                            $"{source}: {at}: {KeyPaths.Of(flow).Record}.scope binds parameter '{name}' to column '{column}', whose values a run form offers for it; leave lookup out.");
                    }
                }
            }
        }

        return parsed;
    }

    /// <summary>
    /// One flow definition out of a document shaped as the single form: the document itself, or the view of one interface
    /// the interface form builds (<see cref="InterfaceView"/>), whose messages name the interface's keys.
    /// </summary>
    private static FlowDefinition MapFlow(FlowYaml y, string source, KeyPaths paths, IReadOnlyList<DeclaredPartition> partitions)
    {
        var name = Require(y.Name, "name", source);
        var src = y.Source ?? throw Missing("source", source);
        var render = y.Render ?? throw Missing("render", source);
        var target = y.Target ?? throw Missing("target", source);

        var protocol = ParseProtocol(Require(target.Protocol, "target.protocol", source), source);

        // What is added to each record is the mapping's to say (osdu/docs/reference/flow/enrich.md): the flow binds the rows,
        // the files and the workflows.
        if (y.Enrich is not null)
        {
            throw new FlowValidationException(
                $"{source}: {paths.Enrich} declares what is added to each record, which the mapping says now: move each enrichment to the enrich: block of {paths.Mapping}'s mapping, with its rows, mapping, key, after and onHold; bind its rows here under {paths.Datasets}.<name> (their files under {paths.Datasets}.<name>.files), and its workflow under {paths.Workflows}.<name>.");
        }

        var options = MapOptions(target.ProtocolOptions, source);

        var mapping = Require(render.Mapping, paths.Mapping, source);
        if (!mapping.Contains('@', StringComparison.Ordinal))
        {
            throw new FlowValidationException($"{source}: {paths.Mapping} '{mapping}' must be pinned as 'Name@version'; floating references are not allowed.");
        }

        var workflow = MapWorkflow(target.Workflow, protocol, src.Payloads?.ContainsKey(FilesPayload) == true, source, paths);
        var mappedSource = WithInputs(MapSource(src, source, paths), target.Workflow, source, paths);

        var flow = new FlowDefinition
        {
            SourcePath = source == "<inline>" ? null : source,
            Name = name,
            Description = string.IsNullOrWhiteSpace(y.Description) ? null : y.Description!.Trim(),
            Batch = string.IsNullOrWhiteSpace(y.Batch) ? null : y.Batch!.Trim(),
            Interface = paths.Interface,
            Partitions = partitions,

            // Neither a list nor a header: the flow serves every partition registered with the catalog.
            FollowsRegistry = partitions.Count == 0 && !(target.Headers ?? []).Keys.Any(k => k.Equals(PartitionHeader, StringComparison.OrdinalIgnoreCase)),
            Parameters = FlowParameterDeclarations.Map(y.Parameters, source),
            Source = mappedSource,
            Render = new FlowRender
            {
                Mapping = mapping,
                CacheVersion = MapCacheVersion(render, source),
                Parameters = render.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal),
                MappingsDirectory = string.IsNullOrWhiteSpace(render.Mappings) ? null : render.Mappings!.Trim(),
            },
            Change = new FlowChange
            {
                Detect = ParseEnum(y.Change?.Detect, ChangeDetection.RenderedHash, paths.Shared("change.detect"), source),
                PayloadDetect = ParseEnum(y.Change?.PayloadDetect, ChangeDetection.ContentHash, paths.Shared("change.payloadDetect"), source),
                OnUnchanged = ParseEnum(y.Change?.OnUnchanged, UnchangedAction.Skip, paths.Shared("change.onUnchanged"), source),
                UseSourceVersions = y.Change?.UseSourceVersions ?? true,
            },
            TelemetryOperation = MapTelemetry(y.Telemetry, FlowDefinition.FlowTypeName, source),
            Target = new FlowTarget
            {
                Endpoint = Require(target.Endpoint, "target.endpoint", source),
                Auth = MapAuth(target.Auth, source),
                Headers = MapHeaders(target.Headers, "target.headers", source),
                Protocol = protocol,
                ProtocolOptions = options,
                Ddms = MapDdms(target.Ddms, protocol, paths.Interface is not null, options.DdmsRoot, source),
                Workflow = workflow,
                Airflow = MapAirflow(target.Airflow, source),
                Eds = MapEds(target.Eds, source),
                Dspdm = MapDspdm(target.Dspdm, protocol, source),
                Etp = MapEtp(target.Etp, protocol, source),
                VerifyReferences = MapVerifyReferences(target.VerifyReferences, protocol, source),
                Validation = MapValidation(target.Validation, paths, source),
            },
            Reliability = MapReliability(y.Reliability, source, paths),
            Parallelism = MapParallelism(y.Parallelism),
            Verify = new FlowVerify { Reconcile = y.Verify?.Reconcile ?? false },
            FailWhen = MapFailWhen(y.FailWhen, source, paths),
            Cache = MapCacheSteps(y.Cache, source, paths),
            EnrichWorkflows = MapEnrichWorkflows(y.Workflows, source, paths),
        };

        Validate(flow, source, paths);
        return flow;
    }

    /// <summary>
    /// The workflows the flow runs over the records of its mapping's enrichments (<c>workflows:</c>,
    /// osdu/docs/reference/flow/enrich-workflow.md), by the enrichment's name: each declared as <c>target.workflow</c> declares
    /// the workflow route, over the record the flow's route wrote. Which enrichments the names are is the mapping's to say, so a
    /// run checks them when it binds the flow to its mapping.
    /// </summary>
    private static Dictionary<string, WorkflowRoute> MapEnrichWorkflows(OrderedDictionary<string, WorkflowYaml?>? declared, string source, KeyPaths paths)
    {
        var workflows = new Dictionary<string, WorkflowRoute>(StringComparer.Ordinal);
        foreach (var (key, written) in declared ?? [])
        {
            var name = key?.Trim() ?? string.Empty;
            var at = $"{paths.Workflows}.{name}";
            if (!DatasetName().IsMatch(name) || name.Length > EnrichStep.MaxNameLength)
            {
                throw new FlowValidationException(
                    $"{source}: {paths.Workflows} names '{name}', and each workflow there is named by the enrichment of the flow's mapping it runs over: letters, digits and '_', at most {EnrichStep.MaxNameLength.ToString(CultureInfo.InvariantCulture)} characters.");
            }

            if (workflows.Keys.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FlowValidationException($"{source}: {paths.Workflows} names '{name}' more than once (enrichment names are compared ignoring case).");
            }

            workflows[name] = MapStepWorkflow(written ?? throw Missing(at + ".stages", source), at, source);
        }

        return workflows;
    }

    /// <summary>
    /// A pipeline's cache steps (osdu/docs/reference/flow/delivery.md, cache): each <c>auto</c>, or the cache types and
    /// entity types it brings up to date, at least one and at most <see cref="CacheStepEntries.MaxNamed"/>, each once. Which
    /// types an entry stands for is the partition's cache's to say, so a run resolves them against it before it sends
    /// anything.
    /// </summary>
    private static FlowCacheSteps MapCacheSteps(FlowCacheYaml? declared, string source, KeyPaths paths)
        => declared is null
            ? new FlowCacheSteps()
            : new FlowCacheSteps
            {
                Before = MapCacheEntries(declared.Before, paths.Shared("cache.before"), source),
                After = MapCacheEntries(declared.After, paths.Shared("cache.after"), source),
            };

    private static CacheStepEntries MapCacheEntries(object? declared, string where, string source)
    {
        const string Shape = "write auto, or list the types of the partition's cache by name (UnitOfMeasure) or by OSDU entity type (reference-data--UnitOfMeasure), for example [UnitOfMeasure]";
        switch (declared)
        {
            case null:
                return CacheStepEntries.None;

            case string word when word.Trim().Equals(CacheStepEntries.AutoWord, StringComparison.Ordinal):
                return new CacheStepEntries { Auto = true };

            case string word:
                throw new FlowValidationException($"{source}: {where} is '{word.Trim()}': {Shape}.");

            case IList<object?> list:
                if (list.Count == 0)
                {
                    throw new FlowValidationException($"{source}: {where} lists nothing. Leave it out, or {Shape}.");
                }

                if (list.Count > CacheStepEntries.MaxNamed)
                {
                    throw new FlowValidationException($"{source}: {where} lists {list.Count} entries, more than the {CacheStepEntries.MaxNamed} a step names; write auto, or name fewer.");
                }

                var named = new List<string>(list.Count);
                foreach (var item in list)
                {
                    var entry = (item as string)?.Trim();
                    if (string.Equals(entry, CacheStepEntries.AutoWord, StringComparison.Ordinal))
                    {
                        throw new FlowValidationException($"{source}: {where} lists auto among its entries; auto stands alone: write {where.Split(' ')[0]}: auto.");
                    }

                    if (!CacheStepEntries.IsEntry(entry))
                    {
                        throw new FlowValidationException(
                            $"{source}: {where} holds '{item}', which names neither a type of the partition's cache nor an OSDU entity type: a name of at most {CacheStepEntries.MaxNameLength} characters, without spaces, wildcards, ':' or '|'.");
                    }

                    if (named.Contains(entry!, StringComparer.OrdinalIgnoreCase))
                    {
                        throw new FlowValidationException($"{source}: {where} names '{entry}' twice.");
                    }

                    named.Add(entry!);
                }

                return new CacheStepEntries { Named = named };

            default:
                throw new FlowValidationException($"{source}: {where} is a map: {Shape}.");
        }
    }

    /// <summary>
    /// The cache version a flow pins, <c>current</c> when it pins none. A flow reads the cache of the partition it delivers to,
    /// so there is no cache to name: a document still naming one under <c>render.cache</c> is refused, rather than read against
    /// a cache other than the one its author meant.
    /// </summary>
    private static string MapCacheVersion(FlowRenderYaml render, string source)
    {
        if (!string.IsNullOrWhiteSpace(render.Cache))
        {
            throw new FlowValidationException(
                $"{source}: render.cache is not a setting any more: a flow reads the cache of the partition it delivers to (target.headers.data-partition-id), which every cache flow of that partition fills. Remove render.cache.");
        }

        return string.IsNullOrWhiteSpace(render.CacheVersion) ? FlowRender.CurrentCacheVersion : render.CacheVersion!.Trim();
    }

    /// <summary>Where a declared DDMS is under the endpoint: a path starting with '/', without a trailing '/'; null when the endpoint is the DDMS.</summary>
    private static string? MapDdmsRoot(string? declared, string key, string source, string example = "/api/os-wellbore-ddms")
    {
        if (string.IsNullOrWhiteSpace(declared))
        {
            return null;
        }

        var written = declared.Trim();
        var root = written.TrimEnd('/');
        if (root.Length == 0 || root[0] != '/' || root.Contains("://", StringComparison.Ordinal) || root.Any(c => char.IsWhiteSpace(c) || c is '{' or '}' or '?' or '#'))
        {
            throw new FlowValidationException($"{source}: {key} '{written}' must be a path under the endpoint starting with '/', such as {example}.");
        }

        return root;
    }

    internal static IEnumerable<string> Tokens(string text)
        => System.Text.RegularExpressions.Regex.Matches(text, @"\{(?<name>[A-Za-z0-9_]+)\}").Select(m => m.Groups["name"].Value).Distinct(StringComparer.Ordinal);

    internal static string Require(string? value, string key, string source)
        => string.IsNullOrWhiteSpace(value) ? throw Missing(key, source) : value!.Trim();

    internal static FlowValidationException Missing(string key, string source) => new($"{source}: '{key}' is required.");

    internal static T ParseEnum<T>(string value, string key, string source)
        where T : struct, Enum
        => Enum.TryParse<T>(value.Replace("-", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new FlowValidationException($"{source}: '{key}' value '{value}' is not one of {string.Join(", ", Enum.GetNames<T>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]))}.");

    internal static T ParseEnum<T>(string? value, T fallback, string key, string source)
        where T : struct, Enum
        => string.IsNullOrWhiteSpace(value) ? fallback : ParseEnum<T>(value!, key, source);

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
