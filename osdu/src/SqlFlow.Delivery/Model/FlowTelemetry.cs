namespace SqlFlow.Delivery.Model;

/// <summary>
/// The operation codes a run's correlation id says what ran with (osdu/docs/reference/concepts/request-correlation.md): the
/// flow's own, <c>telemetry.operation</c>, or else its kind's.
/// </summary>
public static class FlowTelemetry
{
    public const string Delivery = "dlv";

    public const string Cache = "cache";

    public const string Retrieval = "ret";

    public const string Assertion = "ast";

    public const string Dimension = "dim";

    public const string Inventory = "inv";

    public const string Removal = "rmv";

    public const string Download = "dwn";

    /// <summary>Each flow kind's code, by its <c>flowType</c>.</summary>
    public static IReadOnlyDictionary<string, string> KindOperations { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [FlowDefinition.FlowTypeName] = Delivery,
        [CacheDefinition.FlowTypeName] = Cache,
        [RetrievalDefinition.FlowTypeName] = Retrieval,
        [AssertionFlowDefinition.FlowTypeName] = Assertion,
        [DimensionFlowDefinition.FlowTypeName] = Dimension,
        [InventoryFlowDefinition.FlowTypeName] = Inventory,
        [RemovalFlowDefinition.FlowTypeName] = Removal,
        [DownloadFlowDefinition.FlowTypeName] = Download,
    };

    /// <summary>The code of the kind <paramref name="flowType"/> names.</summary>
    public static string KindOperation(string flowType)
        => KindOperations.TryGetValue(flowType, out var code)
            ? code
            : throw new ArgumentException($"'{flowType}' is not a flow kind of the module: {string.Join(", ", KindOperations.Keys)}.", nameof(flowType));
}
