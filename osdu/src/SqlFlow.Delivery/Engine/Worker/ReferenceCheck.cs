using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Engine.Worker;

/// <summary>
/// The check <c>target.verifyReferences: storage</c> asks for before a record is sent
/// (osdu/docs/reference/concepts/preflight.md, References checked in storage): the ids a record refers to are looked up
/// in the ledger, and the ones no record of the ledger holds in OSDU's storage service. A record waits for the records
/// the ledger holds and has not delivered; a record referring to one storage does not hold is not sent: a source that
/// must not write dangling references holds it until the record it names exists. The lookup itself is the one every
/// check of references makes (<see cref="ReferenceResolver"/>), which the gate before a record is sent asks through
/// <see cref="Resolver"/>.
/// </summary>
public sealed class ReferenceCheck
{
    /// <summary>How many of a record's missing references its hold names; the rest are counted.</summary>
    private const int Named = 5;

    private readonly ILedgerRecords _ledger;
    private readonly OsduHttpClient _client;
    private readonly string _batchPath;

    public ReferenceCheck(ILedgerRecords ledger, OsduHttpClient client, string batchPath)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchPath);
        _ledger = ledger;
        _client = client;
        _batchPath = batchPath;
    }

    /// <summary>
    /// The resolver of a flow's references: the ledger of <paramref name="flowId"/>'s partition, then storage. The cache is
    /// given so a verdict can say what it holds, but storage, asked, decides for every id the ledger does not hold.
    /// </summary>
    public ReferenceResolver Resolver(Guid flowId, ReferenceSnapshot? cache = null, string? cacheLabel = null)
        => new(
            cache,
            cacheLabel,
            (ids, ct) => _ledger.HeldIdsAsync(flowId, ids, ct),
            (ids, ct) => StoragePresence.PresentAsync(_client, _batchPath, ids, ct));

    /// <summary>
    /// The references of <paramref name="records"/> that neither the ledger nor storage holds, by record; records whose
    /// references all resolve are left out. Throws when the ledger or storage cannot be read.
    /// </summary>
    public async Task<IReadOnlyDictionary<DeliveryKey, IReadOnlyList<RecordReference>>> MissingAsync(IReadOnlyList<RecordState> records, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        var missing = new Dictionary<DeliveryKey, IReadOnlyList<RecordReference>>();

        // The records are one ledger's, and an id is referred to within its partition: the ledger's partition holds it or none does.
        foreach (var flow in records.Where(r => r.PendingReferences.Count > 0).GroupBy(r => r.FlowId))
        {
            var references = flow.SelectMany(r => r.PendingReferences).Select(r => new FoundReference(r.Id, string.Empty, r.Property, r.Property)).ToList();
            var answers = await Resolver(flow.Key).ResolveAsync(references, ct).ConfigureAwait(false);
            foreach (var record in flow)
            {
                var names = record.PendingReferences
                    .Where(r => answers.GetValueOrDefault(ReferenceResolver.Key(r.Id))?.State == ReferenceState.Missing)
                    .ToList();
                if (names.Count > 0)
                {
                    missing[record.DeliveryKey] = names;
                }
            }
        }

        return missing;
    }

    /// <summary>Why a record with <paramref name="missing"/> references is held.</summary>
    public static string Describe(IReadOnlyList<RecordReference> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);
        var named = string.Join(", ", missing.Take(Named).Select(r => $"{r.Id} ({r.Property})"));
        var more = missing.Count > Named ? $" and {missing.Count - Named} more" : string.Empty;
        return $"refers to {named}{more}, which neither the ledger nor OSDU's storage service holds; target.verifyReferences is storage, "
            + "so the record is not sent with a reference to nothing. Release it once the records it names exist.";
    }
}
