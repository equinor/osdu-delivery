using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The part of the ledger (<see cref="ILedger"/>) that holds records whose access is restricted in OSDU: asked, settled
/// and lifted. A caller that uses only this part takes it rather than the whole ledger; <see cref="OsduLedger"/>
/// implements every part.
/// </summary>
public interface ILedgerRestrictions
{
    /// <summary>The access restriction registered for <paramref name="partition"/> under <paramref name="name"/>, whatever its case; null when there is none.</summary>
    Task<AccessRestrictionState?> FindAccessRestrictionAsync(string partition, string name, CancellationToken ct = default);

    /// <summary>The registered access restrictions of <paramref name="ids"/>, by id; an id no restriction has is left out.</summary>
    Task<IReadOnlyDictionary<int, AccessRestrictionState>> AccessRestrictionsAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default);

    /// <summary>How many records of the ledger are restricted, or asked to be, whatever their status.</summary>
    Task<long> CountRestrictedAsync(Guid flowId, CancellationToken ct = default);

    /// <summary>
    /// Marks each record of <paramref name="asked"/> restricted under <paramref name="restriction"/> before its ACL in OSDU is
    /// replaced (osdu/docs/reference/concepts/access-restriction.md, How a restriction runs), keeping the ACL OSDU held, and
    /// drops the work queued for it, so a restriction whose answer is lost still keeps the record out of every run. A record
    /// is written only while OSDU holds it for the flow, no lease but <paramref name="holder"/> holds it, and it carries no
    /// restriction or this one unanswered; the others are named as moved.
    /// </summary>
    Task<RestrictionWrite> AskRestrictionAsync(
        Guid flowId, AccessRestrictionState restriction, IReadOnlyList<RestrictionAsked> asked, string? holder, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Settles what OSDU answered for records <see cref="AskRestrictionAsync"/> marked: <paramref name="confirmed"/> are
    /// restricted as of now, each with an attempt (outcome <see cref="AttemptOutcome.Restricted"/>); <paramref name="withdrawn"/>,
    /// whose ACL OSDU kept, are no longer restricted and are asked to be planned again. Only a record this restriction still
    /// asks of is written.
    /// </summary>
    Task<RestrictionWrite> SettleRestrictionAsync(
        Guid flowId, AccessRestrictionState restriction, IReadOnlyList<RestrictionConfirmed> confirmed, IReadOnlyList<DeliveryKey> withdrawn,
        string worker, Guid? runId, DateTime nowUtc, string? correlationId = null, CancellationToken ct = default);

    /// <summary>
    /// Lifts the restriction of each record of <paramref name="lifted"/> once OSDU holds the ACL it had before, or holds no
    /// record of it: an attempt (outcome <see cref="AttemptOutcome.Unrestricted"/>) each, and a request to plan it again, so it
    /// follows its source from here. A record OSDU no longer held is sent again by the next run. Only a record still restricted
    /// and held by no lease but <paramref name="holder"/> is written.
    /// </summary>
    Task<RestrictionWrite> LiftRestrictionAsync(
        Guid flowId, IReadOnlyList<RestrictionLifted> lifted, IReadOnlyDictionary<int, AccessRestrictionState> restrictions, string? holder,
        string worker, Guid? runId, DateTime nowUtc, string? correlationId = null, CancellationToken ct = default);
}
