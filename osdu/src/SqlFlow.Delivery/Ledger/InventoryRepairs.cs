namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// Repairing what an inventory found missing (osdu/docs/reference/flow/inventory-removals.md, Repairing missing ids): records a
/// ledger delivered that OSDU no longer holds are delivered again through their own flows, under the id they claimed (a
/// soft-deleted record brought back, a purged one written again) or, when the operator asks, under a new one.
/// </summary>
public static class InventoryRepairs
{
    /// <summary>The finding whose ids a repair brings back: a record a ledger delivered that storage does not hold.</summary>
    public const string Finding = InventoryFindings.Missing;

    /// <summary>The activity a repair is audited under, on the inventory flow's ledger, beside the redelivery each delivery ledger records.</summary>
    public const string ActivityKind = "inventory-repair";

    /// <summary>The most ids a repair names; a larger repair takes every missing id of the inventory.</summary>
    public const int MaxIds = Model.InventoryRemovalRequest.MaxIds;

    /// <summary>The ids a skipped reason lists, after which it says only how many.</summary>
    public const int SampleIds = 5;
}
