using System.Globalization;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Engine;

/// <summary>What the legal service says of the legal tags a mapping puts on every record.</summary>
/// <param name="Tags">The tags, their parameters filled.</param>
/// <param name="Refused">The tags it refuses, with why; null when the target does not ask it.</param>
public sealed record LegalTagAnswer(IReadOnlyList<string> Tags, IReadOnlyDictionary<string, string>? Refused);

// The checks a run makes before it plans or sends anything, which a check ahead of any delivery makes too
// (osdu/docs/reference/concepts/preflight.md, The run's preflight): the flow's route can deliver its kind, the legal
// service accepts the mapping's legal tags, the restriction the mapping puts records under is registered and the flow
// can lift it, and the records a document refers to exist.
public sealed partial class FlowRuntime
{
    /// <summary>
    /// Checks that the flow's route can deliver <paramref name="kind"/>, the kind its mapping renders
    /// (<see cref="RouteChecks"/>). A flow on the ddms route that names a DDMS by its registration has its protocol built
    /// first, which reads the registration, so the check sees what the DDMS registered. A flow on the dspdm route has its
    /// business object read from DSPDM's metadata, which says whether it exists and how its rows are found again.
    /// </summary>
    public async Task CheckRouteAsync(string kind, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (Flow.Target.Protocol == DeliveryProtocol.Dspdm)
        {
            RouteChecks.Check(Flow, kind);
            if (await ProtocolAsync(ct).ConfigureAwait(false) is OsduDspdmProtocol dspdm)
            {
                await dspdm.CheckKindAsync(kind, ct).ConfigureAwait(false);
            }

            return;
        }
        if (DeliveryProtocols.ReachesDdms(Flow.Target.Protocol) && DdmsDiscovery.Needed(Flow))
        {
            var routing = await ProtocolAsync(ct).ConfigureAwait(false) switch
            {
                OsduDdmsProtocol ddms => ddms.Routing,
                OsduFileAndDdmsProtocol files => files.Routing,
                OsduManifestAndDdmsProtocol manifests => manifests.Routing,
                _ => null,
            };
            if (routing is not null)
            {
                RouteChecks.Check(Flow, kind, routing);
                return;
            }
        }

        RouteChecks.Check(Flow, kind);
    }

    /// <summary>
    /// Asks the legal service about the mapping's legal tags before a run plans or sends anything. Every record the
    /// mapping renders carries the same tags, and storage refuses a record whose tag is unknown or expired, so a run
    /// that starts with a bad tag would fail each record it plans with the same error; refusing the run names the tag
    /// and the service's reason once, before anything reaches the ledger or OSDU. A target that does not ask (see
    /// <see cref="IDeliveryProtocol.InvalidLegalTagsAsync"/>) is logged as not checked, never taken as valid.
    /// </summary>
    public Task CheckLegalTagsAsync(CancellationToken ct = default) => EnsureLegalTagsAsync(ct);

    /// <summary>
    /// What the legal service says of the legal tags the mapping puts on every record, without refusing anything: the tags, and
    /// those it refuses with why; <see cref="LegalTagAnswer.Refused"/> is null when the target does not ask it, and a mapping
    /// that names no tags (DSPDM business object rows, which are no OSDU records) has none to ask about.
    /// </summary>
    public async Task<LegalTagAnswer> AskLegalTagsAsync(CancellationToken ct = default)
    {
        var tags = Mapping.Mapping.Envelope.LegalTags.Select(tag => MappingEntry.ExpandParameters(tag, Mapping.Renderer.ParameterValue)).ToList();
        if (tags.Count == 0)
        {
            return new LegalTagAnswer(tags, new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var protocol = await ProtocolAsync(ct).ConfigureAwait(false);
        return new LegalTagAnswer(tags, await protocol.InvalidLegalTagsAsync(tags, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Whether the records a document refers to exist, asked of the ledger when this host has one, then of the cache the runtime
    /// renders with, then of OSDU's storage service for what neither holds, whatever <c>target.verifyReferences</c> says: what
    /// a check made ahead of any delivery wants to know, where the gate before sending asks storage only when the flow says so
    /// (<see cref="GateAsync"/>). Thousands of records name the same reference data, so what the cache holds is not asked again.
    /// </summary>
    public async Task<ReferenceResolver> StorageReferencesAsync(CancellationToken ct = default)
    {
        var client = await _target.ClientAsync(ct).ConfigureAwait(false);
        var batchPath = Flow.Target.ProtocolOptions.VerifyBatchPath ?? OsduRecordProtocol.DefaultVerifyBatchPath;
        var cache = _mapping?.References;
        var ledger = _context.Ledger;
        return new ReferenceResolver(
            cache,
            _mapping?.Context is { } context ? (context.CacheScope is null ? $"cache version {context.CacheVersion}" : $"version {context.CacheVersion} of the cache of partition '{context.CacheScope}'") : null,
            ledger is null ? null : (ids, token) => ledger.HeldIdsAsync(Flow.Id, ids, token),
            (ids, token) => StoragePresence.PresentAsync(client, batchPath, ids, token),
            cacheFirst: true);
    }

    /// <summary>The legal tag check, asked once per runtime; see <see cref="CheckLegalTagsAsync"/>.</summary>
    private async Task EnsureLegalTagsAsync(CancellationToken ct)
    {
        if (_legalTagsChecked)
        {
            return;
        }

        if (Mapping.Mapping.Envelope.LegalTags.Count == 0)
        {
            // A mapping of DSPDM business object rows puts no legal tags on anything: its rows are no OSDU records.
            _legalTagsChecked = true;
            return;
        }

        var (tags, invalid) = await AskLegalTagsAsync(ct).ConfigureAwait(false);
        if (invalid is null)
        {
            _log.LogInformation(
                "The mapping's legal tags ({Tags}) were not checked with the legal service before the run: the target does not ask it (validateLegalTags is off, or a well log endpoint names neither ddmsRoot nor legalValidatePath).",
                string.Join(", ", tags));
            _legalTagsChecked = true;
            return;
        }

        if (invalid.Count > 0)
        {
            throw new DeliveryException(
                string.Create(CultureInfo.InvariantCulture, $"The legal service refuses {invalid.Count} of the legal tag(s) mapping {Mapping.Mapping.Reference} puts on every record, so nothing was planned or sent: ")
                + string.Join("; ", invalid.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + ": " + kv.Value)));
        }

        _log.LogInformation("The legal service accepts the legal tag(s) the mapping puts on every record ({Tags}).", string.Join(", ", tags));
        _legalTagsChecked = true;
    }

    /// <summary>
    /// Before a run plans anything for a mapping that restricts a record while a value its <c>$restrictWhenMissing</c> nodes should
    /// give is missing (osdu/docs/reference/flow/mapping-restricted-access.md): the restriction the run resolved is still the
    /// one the partition registers under that name, and the flow can give a restricted record its own access back once the
    /// value arrives. Storage lets only an owner write a record again, so the flow's identity has to be a member of one of the
    /// restriction's owner groups, or a data manager, which Entitlements is asked once
    /// (<see cref="Restrictions.RecordRestrictions.CheckAsync"/>, the check a restriction an operator asks makes). Refused
    /// before anything is planned or sent, and asked once a runtime.
    /// </summary>
    private async Task EnsureRestrictedAccessAsync(CancellationToken ct)
    {
        if (_restrictedAccessChecked || Mapping.Context.RestrictedAccess is not { } restricted)
        {
            return;
        }

        var reference = Mapping.Mapping.Reference;
        var partition = Mapping.Context.DataPartition;
        var registered = await RequireLedger().FindAccessRestrictionAsync(partition, restricted.Name, ct).ConfigureAwait(false);
        if (registered is null || registered.RestrictionId != restricted.RestrictionId)
        {
            throw new DeliveryException(
                $"Mapping {reference} restricts a record under access restriction '{restricted.Name}' while a value its $restrictWhenMissing nodes should give is missing, and partition '{partition}' no longer registers it as it did when the run opened, so nothing was planned or sent; register it under Access restrictions, or name one the partition registers.");
        }

        var protocol = await ProtocolAsync(ct).ConfigureAwait(false);
        var check = await Restrictions.RecordRestrictions.CheckAsync(Flow, protocol, [registered], ct, again: "run the flow again").ConfigureAwait(false);
        if ((check.Refusal ?? check.Uses[0].Refusal) is { } reason)
        {
            throw new DeliveryException(
                $"Mapping {reference} restricts a record under access restriction '{restricted.Name}' while a value its $restrictWhenMissing nodes should give is missing, and {reason}. Nothing was planned or sent.");
        }

        _log.LogInformation(
            "A record mapping {Mapping} finds a $restrictWhenMissing value missing in goes out under access restriction {Restriction} of partition {Partition}, which {Identity} can give it its own access back from.",
            reference, restricted.Name, partition, check.Identity ?? "the flow's identity");
        _restrictedAccessChecked = true;
    }
}
