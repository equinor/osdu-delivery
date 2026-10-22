namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The hold of a record whose OSDU id, made from its key, OSDU already holds a record at that no record of a ledger claimed
/// (osdu/docs/reference/concepts/record-lifecycle.md, Adopting a record OSDU already holds): written by another system, or by
/// another instance of the delivery whose ledger this database does not hold. Such a record can be adopted; this is the one
/// place that says which records can.
/// </summary>
public static class OccupiedHolds
{
    /// <summary>
    /// The words the intake's message for such a hold begins with, written the same way by every module version: what tells
    /// the hold from any other for a record held before the hold kept the id (<see cref="RecordState.OccupiedTargetId"/>).
    /// </summary>
    public const string Prefix = "OSDU already holds a record at ";

    /// <summary>
    /// Whether an adoption may be asked of <paramref name="record"/>: held for an id OSDU holds, claiming no id, no delivery
    /// holding it, and no adoption asked of it already.
    /// </summary>
    public static bool Adoptable(RecordState record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record is { Status: RecordStatus.Held, ClaimedTargetId: null, LeaseOwner: null, AdoptRequestedUtc: null }
            && (record.OccupiedTargetId is not null || record.LastError?.StartsWith(Prefix, StringComparison.Ordinal) == true);
    }
}
