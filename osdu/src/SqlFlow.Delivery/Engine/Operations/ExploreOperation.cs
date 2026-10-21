using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Engine.Operations;

/// <summary>
/// <c>delivery-explore</c>: the explorer's reads of what an OSDU partition holds, through the connection (endpoint, credentials
/// and partition) of the flow the control plane picked for that partition, or of a record's own flow for the check a record
/// page makes, and nothing else of the flow: no ledger, mapping or cache is read. The payload's <c>action</c> says which read:
/// <list type="bullet">
/// <item><description><c>types</c>: the kinds of the records a search finds, with their counts (<c>search</c>).</description></item>
/// <item><description><c>search</c>: one page of the records a search finds (<c>search</c>).</description></item>
/// <item><description><c>fields</c>: the properties the records of a <c>kind</c> hold: the record's own, those the kind's schema declares (read from the Schema service), and those its records hold beyond them.</description></item>
/// <item><description><c>read</c>: one record from the storage service by its <c>targetId</c>, at its latest or at a <c>version</c>, with its version list.</description></item>
/// <item><description><c>dimension-keys</c>: the commonest keys of a drafted dimension's path, which the builder's example steps through (<see cref="DimensionSampler.KeysAsync"/>), asked by the <c>keys</c> long argument.</description></item>
/// <item><description><c>dimension-example</c>: one key of a drafted dimension made into its row as a build makes it (<see cref="DimensionSampler.ExampleAsync"/>): the dimension's item as YAML (<c>item</c>), the <c>key</c>, and how the key and each collected path are indexed (<c>fields</c>).</description></item>
/// <item><description><c>validate</c>: one record (<c>targetId</c>, at its latest or a <c>version</c>) checked against the schema of its kind, the Schema service's or a template a synced repository holds (<c>schema</c>, <c>templateVersion</c>), with the records it refers to looked up in storage, and the assertions of the synced <c>mapping</c> named by its id judged on it (<see cref="ExplorerChecks.RecordAsync"/>).</description></item>
/// <item><description><c>validate-list</c>: the records a <c>search</c> finds, up to <c>max</c>, checked the same way and counted by rule and by assertion (<see cref="ExplorerChecks.ListAsync"/>).</description></item>
/// <item><description><c>referenced-by</c>: the types whose schemas name records of a <c>type</c> (<see cref="ExplorerReferences"/>), answered from what the partition's Schema service holds, read once and kept (<see cref="PartitionSchemaIndex"/>); <c>refresh</c> reads it again.</description></item>
/// </list>
/// Every read goes to the platform's own services (search, storage and schema), whatever route the flow delivers by.
/// </summary>
public sealed class ExploreOperation : DeliveryOperation
{
    public const string OperationName = "delivery-explore";

    /// <summary>The task argument naming the read: <see cref="TypesAction"/>, <see cref="SearchAction"/>, <see cref="FieldsAction"/> or <see cref="ReadAction"/>.</summary>
    public const string ActionArgument = "action";

    /// <summary>The task argument carrying the search, as <see cref="ExplorerSearch.ToJson"/> writes it.</summary>
    public const string SearchArgument = "search";

    /// <summary>The task argument naming the kind whose properties are read.</summary>
    public const string KindArgument = "kind";

    /// <summary>The long argument (<see cref="LongArgument"/>) carrying a <see cref="DimensionKeysRequest"/> as JSON.</summary>
    public const string KeysArgument = "keys";

    /// <summary>The long argument carrying a drafted dimension's item, as <see cref="DimensionBuilder.ToYaml"/> writes it.</summary>
    public const string ItemArgument = "item";

    /// <summary>The task argument carrying the key a dimension example is made of.</summary>
    public const string KeyArgument = "key";

    /// <summary>The long argument carrying how a dimension example's key and collected paths are indexed, as <see cref="DimensionExampleFields"/> JSON.</summary>
    public const string FieldsArgument = "fields";

    public const string TypesAction = "types";
    public const string SearchAction = "search";
    public const string FieldsAction = "fields";
    public const string ReadAction = "read";
    public const string DimensionKeysAction = "dimension-keys";
    public const string DimensionExampleAction = "dimension-example";
    public const string ValidateAction = "validate";
    public const string ValidateListAction = "validate-list";
    public const string ReferencedByAction = "referenced-by";

    /// <summary>The task argument naming the type whose referring types are asked for: a type, or a kind naming one.</summary>
    public const string TypeArgument = "type";

    /// <summary>The task argument that, as <c>true</c>, starts a pass reading the partition's schemas again.</summary>
    public const string RefreshArgument = "refresh";

    /// <summary>
    /// How long an ask of the types referring to a type waits on a pass reading the partition's schemas before it answers
    /// with where the pass stands; the pass goes on, and the next ask joins it.
    /// </summary>
    public static readonly TimeSpan ReferencesWait = TimeSpan.FromSeconds(5);

    /// <summary>The task argument naming the schema a check reads: <c>osdu</c> (the Schema service's, the default) or <c>template</c>.</summary>
    public const string SchemaArgument = "schema";

    /// <summary>
    /// The task argument naming the version of the template a check reads, among those the synced repositories hold; the
    /// version the most recently synced repository holds when left out.
    /// </summary>
    public const string TemplateVersionArgument = "templateVersion";

    /// <summary>The task argument naming how many records a check of a search reads at most.</summary>
    public const string MaxArgument = "max";

    /// <summary>
    /// The task argument naming, by its id, the synced mapping whose assertions a check judges on the records it reads
    /// (osdu/docs/reference/flow/mapping-assertions.md); a check that names none judges none.
    /// </summary>
    public const string MappingArgument = "mapping";

    /// <summary>Every read the task can name.</summary>
    public static readonly IReadOnlyList<string> Actions = [TypesAction, SearchAction, FieldsAction, ReadAction, DimensionKeysAction, DimensionExampleAction, ValidateAction, ValidateListAction, ReferencedByAction];

    /// <summary>How the dimension builder's requests and answers are written: the web's conventions, as the API writes them.</summary>
    public static JsonSerializerOptions BuilderJson { get; } = ReadOnly(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private readonly HttpMessageHandler? _transport;
    private readonly bool _allowLoopback;

    public ExploreOperation(EngineContext context)
        : this(context, null, EngineContext.LoopbackAllowed)
    {
    }

    /// <summary>The operation sending through <paramref name="transport"/> instead of the network (the tests).</summary>
    internal ExploreOperation(EngineContext context, HttpMessageHandler? transport, bool allowLoopback)
        : base(context)
    {
        _transport = transport;
        _allowLoopback = allowLoopback;
    }

    public override string Name => OperationName;

    protected override async Task<object> RunAsync(EngineContext context, FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct)
    {
        var action = payload.RequireArgument(ActionArgument);
        if (!Actions.Contains(action, StringComparer.Ordinal))
        {
            throw new SqlFlowException($"'{action}' is not a read the explorer makes: {string.Join(", ", Actions)}.");
        }

        // The connection the engine keeps for the flow's target, so a person browsing is not given a new token and new
        // connections for every page; a transport of the tests' own is a connection of its own.
        using var target = _transport is null && context.Clients is { } clients
            ? await clients.OpenAsync(context, flow, ct).ConfigureAwait(false)
            : TargetClients.OneOff(context, flow, _transport, _allowLoopback);
        var client = await target.ClientAsync(ct).ConfigureAwait(false);
        if (action == ReadAction)
        {
            // The storage service's own read, whatever the flow delivers by: the explorer shows the record OSDU keeps.
            var storage = new OsduRecordProtocol(client, new ProtocolOptions { VerifyPath = ReadPathOf(flow) }, context.Time);
            var targetId = TargetId.WithoutVersion(payload.RequireArgument("targetId").Trim());
            return await ReadRecordOperation.ReadBackAsync(storage, flow.Label, targetId, null, ReadRecordOperation.VersionOf(payload), null, context.Time, ct).ConfigureAwait(false);
        }

        var partition = await PartitionOfAsync(context, flow, ct).ConfigureAwait(false);
        var explorer = new RecordExplorer(client, partition, context.Loggers.CreateLogger<RecordExplorer>());
        using var correlation = OsduCorrelation.Begin();
        object answer = action switch
        {
            ValidateAction => await (await ChecksAsync(context, flow, client, ct).ConfigureAwait(false)).RecordAsync(
                TargetId.WithoutVersion(payload.RequireArgument("targetId").Trim()), ReadRecordOperation.VersionOf(payload), SchemaSourceOf(payload),
                payload.Argument(TemplateVersionArgument), await AssertionsAsync(context, payload, ct).ConfigureAwait(false), ct).ConfigureAwait(false),
            ValidateListAction => await (await ChecksAsync(context, flow, client, ct).ConfigureAwait(false)).ListAsync(
                explorer, ExplorerSearch.Parse(payload.Argument(SearchArgument)), MaxOf(payload), SchemaSourceOf(payload),
                await AssertionsAsync(context, payload, ct).ConfigureAwait(false), ct).ConfigureAwait(false),
            TypesAction => await explorer.TypesAsync(ExplorerSearch.Parse(payload.Argument(SearchArgument)), ct).ConfigureAwait(false),
            SearchAction => await explorer.SearchAsync(ExplorerSearch.Parse(payload.Argument(SearchArgument)), ct).ConfigureAwait(false),
            DimensionKeysAction => await Sampler(client, context).KeysAsync(KeysOf(payload, partition), ct).ConfigureAwait(false),
            DimensionExampleAction => await ExampleAsync(context, Sampler(client, context), payload, partition, ct).ConfigureAwait(false),
            ReferencedByAction => await ReferencedByAsync(context, flow, client, partition, payload, ct).ConfigureAwait(false),
            _ => await explorer.FieldsAsync(payload.RequireArgument(KindArgument), new SchemaServiceReader(client, context.Time), ct).ConfigureAwait(false),
        };

        return new
        {
            connection = flow.Label,
            partition,
            correlationId = correlation.Id,
            answeredUtc = context.Time.GetUtcNow().UtcDateTime,
            answer,
        };
    }

    /// <summary>
    /// The explorer's checks through the connection, reading storage where the explorer reads it, the templates the synced
    /// repositories hold (from their files) where the host keeps the module database, and the data definitions' example
    /// records where the host keeps them.
    /// </summary>
    private static async Task<ExplorerChecks> ChecksAsync(EngineContext context, FlowDefinition flow, OsduHttpClient client, CancellationToken ct)
        => new(
            client,
            new OsduRecordProtocol(client, new ProtocolOptions { VerifyPath = ReadPathOf(flow) }, context.Time),
            flow.Target.ProtocolOptions.VerifyBatchPath ?? OsduRecordProtocol.DefaultVerifyBatchPath,
            context.Ledger is { } ledger ? await Catalog.RepositoryTemplates.ReadAsync(ledger, context.RepositoryFolders, ct).ConfigureAwait(false) : null,
            context.Time,
            examples: context.Examples);

    /// <summary>
    /// The types whose schemas name records of the payload's type, from the index of the partition's schemas the engine
    /// keeps, which a pass fills through a connection of its own to the flow's target. An ask waits on a pass under way for
    /// <see cref="ReferencesWait"/>, then answers with where it stands. A host that keeps no index reads every schema for
    /// the ask, and answers once they are read.
    /// </summary>
    private async Task<ExplorerReferences> ReferencedByAsync(
        EngineContext context, FlowDefinition flow, OsduHttpClient client, string? partition, ComputeTaskPayload payload, CancellationToken ct)
    {
        var asked = payload.RequireArgument(TypeArgument);
        var entityType = ExplorerReferences.EntityTypeOf(asked) ?? throw new SqlFlowException(ExplorerReferences.TypeProblem(asked));
        var refresh = string.Equals(payload.Argument(RefreshArgument)?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        using var own = context.SchemaIndexes is null ? new PartitionSchemaIndexes(context.Time, context.Loggers) : null;
        var index = (context.SchemaIndexes ?? own!).For(client.Endpoint, partition ?? client.Header(CacheScope.PartitionHeader));
        if (index.Ensure(token => SchemaConnectionAsync(context, flow, token), refresh) is { } pass)
        {
            try
            {
                await (own is null ? pass.WaitAsync(ReferencesWait, ct) : pass.WaitAsync(ct)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The pass goes on; the answer says where it stands.
            }
        }

        return ExplorerReferences.Of(entityType, index.View());
    }

    /// <summary>A connection to the flow's target of the pass's own, held until the pass ends: the one the engine keeps for the target, or one of its own.</summary>
    private async Task<SchemaIndexConnection> SchemaConnectionAsync(EngineContext context, FlowDefinition flow, CancellationToken ct)
    {
        var lease = _transport is null && context.Clients is { } clients
            ? await clients.OpenAsync(context, flow, ct).ConfigureAwait(false)
            : TargetClients.OneOff(context, flow, _transport, _allowLoopback);
        try
        {
            return new SchemaIndexConnection(await lease.ClientAsync(ct).ConfigureAwait(false), lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The mapping whose assertions a check judges, as the task names it by its synced id, read from the module database; or
    /// why it cannot be read, which the answer says. Null when the task names none.
    /// </summary>
    internal static async Task<ExplorerAssertions?> AssertionsAsync(EngineContext context, ComputeTaskPayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Argument(MappingArgument)?.Trim() is not { Length: > 0 } text)
        {
            return null;
        }

        if (!Guid.TryParse(text, out var id))
        {
            throw new SqlFlowException($"'{text}' is not the id of a synced mapping, as the Mappings page lists them.");
        }

        var (mapping, problem) = await SyncedMappings.ByIdAsync(context, id, ct).ConfigureAwait(false);
        return new ExplorerAssertions(mapping, problem);
    }

    /// <summary>The schema a check reads, as the task names it: <c>osdu</c> (the default) or <c>template</c>.</summary>
    internal static ExplorerSchemaSource SchemaSourceOf(ComputeTaskPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return payload.Argument(SchemaArgument)?.Trim().ToLowerInvariant() switch
        {
            null or "" or "osdu" => ExplorerSchemaSource.Osdu,
            "template" => ExplorerSchemaSource.Template,
            var other => throw new SqlFlowException($"'{other}' is not a schema a record is checked against: osdu or template."),
        };
    }

    /// <summary>How many records a check of a search reads, as the task names it: 1 to <see cref="ExplorerChecks.MaxRecords"/>, the most when left out.</summary>
    internal static int MaxOf(ComputeTaskPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Argument(MaxArgument) is not { } text)
        {
            return ExplorerChecks.MaxRecords;
        }

        return int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var max) && max is >= 1 and <= ExplorerChecks.MaxRecords
            ? max
            : throw new SqlFlowException($"'{text}' is not how many records a check reads: 1 to {ExplorerChecks.MaxRecords}.");
    }

    private static DimensionSampler Sampler(OsduHttpClient client, EngineContext context)
    {
        var log = context.Loggers.CreateLogger<DimensionSampler>();
        return new DimensionSampler(new OsduSearch(client, RecordExplorer.QueryPath, RetrievalSource.DefaultSearchPath, log), log);
    }

    /// <summary>
    /// The builder's keys request, checked again here as the control plane checked it, its query's <c>{partition}</c> filled
    /// with the partition the connection reads, as a build fills it.
    /// </summary>
    internal static DimensionKeysRequest KeysOf(ComputeTaskPayload payload, string? partition)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var request = Deserialize<DimensionKeysRequest>(LongArgument.Require(payload, KeysArgument), KeysArgument);
        if (ExplorerKinds.Problem(request.Kind) is { } wrong)
        {
            throw new SqlFlowException(wrong);
        }

        if (string.IsNullOrWhiteSpace(request.Path) || !OsduPath.IsPath(request.Path.Trim()))
        {
            throw new SqlFlowException($"'{request.Path}' is not a property path a key is read at, such as data.WellboreID.");
        }

        return request with { Path = request.Path.Trim(), Query = Filled(request.Query, partition) };
    }

    /// <summary>
    /// One key of the drafted dimension the payload carries made into its row: the item read back by the document loader, as a
    /// flow's own would be, so what the builder shows is what a build of that YAML would make.
    /// </summary>
    private static async Task<DimensionExample> ExampleAsync(
        EngineContext context, DimensionSampler sampler, ComputeTaskPayload payload, string? partition, CancellationToken ct)
    {
        var item = LongArgument.Require(payload, ItemArgument);
        var key = payload.RequireArgument(KeyArgument);
        var fields = Deserialize<DimensionExampleFields>(LongArgument.Read(payload, FieldsArgument) ?? "{}", FieldsArgument);
        var dimension = context.Documents.ParseDimension(DimensionBuilder.Document(item), DimensionBuilder.CheckSource).Dimensions[0];
        var collected = new Dictionary<string, OsduField>(StringComparer.Ordinal);
        foreach (var (name, wire) in fields.Collected ?? new Dictionary<string, DimensionFieldWire>(StringComparer.Ordinal))
        {
            if (wire.Field() is { } field)
            {
                collected[name] = field;
            }
        }

        DimensionCleaner cleaner;
        try
        {
            // A map step reads a dictionary of the flow's repository, which a drafted dimension has none of.
            cleaner = DimensionCleaner.Build(dimension.Clean, name => throw new DeliveryException(
                $"The clean step map reads the dictionary {name} of the flow's repository, which the builder cannot read, so the example is not cleaned by it."));
        }
        catch (DeliveryException ex)
        {
            throw new SqlFlowException(ex.Message, ex);
        }

        return await sampler.ExampleAsync(dimension, key, Filled(dimension.Query, partition), fields.Key?.Field(), collected, cleaner, ct).ConfigureAwait(false);
    }

    /// <summary>A query with its <c>{partition}</c> token filled with the partition the connection reads; any other token stays as written.</summary>
    private static string? Filled(string? query, string? partition)
        => query is null || partition is null
            ? query
            : FlowParameters.Substitute(query, new Dictionary<string, string>(StringComparer.Ordinal) { [PartitionNames.RunValue] = partition });

    private static JsonSerializerOptions ReadOnly(JsonSerializerOptions options)
    {
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static T Deserialize<T>(string json, string argument)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, BuilderJson) ?? throw new SqlFlowException($"The '{argument}' argument is empty.");
        }
        catch (JsonException ex)
        {
            throw new SqlFlowException($"The '{argument}' argument is not what the dimension builder sends: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The storage service's record path the explorer reads by: the one a storage flow points its reads at, which follows a
    /// deployment that serves storage elsewhere, and the platform's own for a flow of any other route, whose read path (where
    /// it names one) is its DDMS's.
    /// </summary>
    internal static string ReadPathOf(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.Target.Protocol == DeliveryProtocol.Storage && flow.Target.ProtocolOptions.VerifyPath is { Length: > 0 } path
            ? path
            : OsduRecordProtocol.DefaultVerifyPath;
    }

    /// <summary>
    /// The data partition the flow's connection reads: the one it is bound to, or what its partition header resolves to. A
    /// connection whose partition cannot be told still reads; only an id written without its partition then stays as typed.
    /// </summary>
    private static async Task<string?> PartitionOfAsync(EngineContext context, FlowDefinition flow, CancellationToken ct)
    {
        try
        {
            return await LedgerRegistration.PartitionAsync(flow, context.Secrets, ct).ConfigureAwait(false);
        }
        catch (DeliveryException)
        {
            return null;
        }
    }
}

/// <summary>How a drafted dimension's key and each of its collected paths are indexed, as the control plane settled them from the templates the synced repositories hold.</summary>
public sealed record DimensionExampleFields(DimensionFieldWire? Key, IReadOnlyDictionary<string, DimensionFieldWire>? Collected);
