using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Removals;

/// <summary>
/// Finds the delivery flow a removal names (osdu/docs/reference/flow/removal.md, The delivery flow), in the checkout the removal
/// flow runs from, and binds it to the run's partition and the removal's interface: the delivery flow whose ledger the removal
/// compares and writes to, and whose route, credentials and removal path take the records out. It is found as every flow that
/// names a delivery flow finds it (<see cref="DeliveryFlowDocuments"/>).
/// </summary>
public static class RemovalTargets
{
    /// <summary>The delivery flow a removal names, bound to <paramref name="partition"/> and the removal's interface, and the file it was read from.</summary>
    /// <exception cref="DeliveryException">The flow cannot be found, is found twice, or does not serve the partition or name the interface.</exception>
    public static Task<(FlowDefinition Flow, string File)> ResolveAsync(
        EngineContext context, RemovalFlowDefinition removal, RemovalEntry entry, string partition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(removal);
        ArgumentNullException.ThrowIfNull(entry);
        return DeliveryFlowDocuments.ResolveAsync(
            context,
            new DeliveryFlowReference
            {
                Flow = entry.Flow,
                Interface = entry.Interface,
                Origin = removal.SourcePath,
                Owner = $"Removal flow '{removal.Name}'",
                At = entry.Path + ".flow",
                InterfaceAt = entry.Path + ".interface",
                Namer = "a removal",
                NamerFlow = "removal flow",
            },
            partition,
            ct);
    }
}
