using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Engine;

/// <summary>The operations a run of the delivery, cache, retrieval, assertion, dimension, inventory and removal kinds performs, by the names runs carry.</summary>
public static class DeliveryOperations
{
    /// <summary>Plan the source and deliver what changed: the delivery kind's default.</summary>
    public const string Deliver = "deliver";

    /// <summary>Plan and report, change nothing.</summary>
    public const string Plan = "plan";

    /// <summary>Plan into work batches without delivering: a fan-out member's share, or a planning run alone.</summary>
    public const string Intake = "intake";

    /// <summary>Deliver the work batches already planned.</summary>
    public const string Drain = "drain";

    /// <summary>Compare what OSDU holds with what the ledger recorded.</summary>
    public const string Verify = "verify";

    /// <summary>Read every row of the scope again and deliver what renders differently now.</summary>
    public const string Replan = "replan";

    /// <summary>
    /// Read the rows of the ledger's records from the ingestion tables and consolidate the ledger with them: record what
    /// it lacks of a row, ask for the records whose rows changed unseen to be planned again, and report the rows that are
    /// gone. Nothing is rendered and nothing reaches OSDU.
    /// </summary>
    public const string Sync = "sync";

    /// <summary>
    /// Put OSDU back as it was before one run or one submission (osdu/docs/reference/concepts/removal-and-reversal.md,
    /// Reversing a run or a submission): what it created is removed again and what it updated is given back the version
    /// OSDU held before, record by record, every step written to the ledger. The payload names the source:
    /// <c>submissionId</c>, or <c>runId</c>.
    /// </summary>
    public const string Reverse = "reverse";

    /// <summary>
    /// Delete the ledger (osdu/docs/reference/concepts/removal-and-reversal.md, Deleting the ledger): remove from OSDU,
    /// reversibly, every record the ledger holds there, then delete everything the ledger keeps, so the flow's next run
    /// reads every row and delivers each as a new record. The payload names the partition the run acts in
    /// (<c>confirm</c>), as the operator typed it.
    /// </summary>
    public const string DeleteLedger = "delete-ledger";

    /// <summary>
    /// Undo what unfinished deliveries left in OSDU (osdu/docs/reference/concepts/removal-and-reversal.md, Unfinished
    /// deliveries and their undo): every artifact due, every one whose undo failed and is past its backoff, and what a
    /// unit its record no longer carries created. A deliver run and a flow-wide drain end with the same sweep; this
    /// runs it alone. With <c>force</c> it also takes artifacts whose undo failed as many times as the sweep tries.
    /// </summary>
    public const string Undo = "undo";

    /// <summary>Capture a cache flow's types into its partition's cache: the cache kind's default.</summary>
    public const string Refresh = "refresh";

    /// <summary>Retrieve records of OSDU kinds into files: the retrieval kind's default.</summary>
    public const string Retrieve = "retrieve";

    /// <summary>Run an assertion flow's tests against what OSDU holds and record their results: the assertion kind's default.</summary>
    public const string Test = "test";

    /// <summary>
    /// Read every distinct value of a dimension flow's dimensions and keep them with their clean values: the dimension kind's
    /// default. An inventory flow's default too: read every id its inventories' kinds hold, keep them, and reconcile them with
    /// the ledgers of the partition.
    /// </summary>
    public const string Build = "build";

    /// <summary>Compare an inventory flow's inventories, as their last builds left them, with the ledgers as they stand now, reading OSDU only for the ids a ledger expects.</summary>
    public const string Reconcile = "reconcile";

    /// <summary>
    /// Remove from OSDU the ids of one finding an inventory found, as an operator asked
    /// (osdu/docs/reference/flow/inventory.md, What a removal does): an inventory flow's operation, run only for a flow
    /// whose document allows it. A removal flow's default too: compare the keys its sources hold with the ledgers of the
    /// delivery flows it names, and take out of OSDU, into those ledgers, the records whose keys the source no longer holds
    /// (osdu/docs/reference/flow/removal.md, A run). A delivery flow's operation as well: remove the records of one ledger
    /// the payload names (<c>recordKeys</c>, or the listing <c>filter</c> every match of which goes) to the extent its
    /// <c>scope</c> asks, and with <c>purgeLedger</c> delete each one OSDU answered for from the ledger too
    /// (osdu/docs/reference/concepts/removal-and-reversal.md, Removing records from OSDU).
    /// </summary>
    public const string Remove = "remove";

    /// <summary>
    /// Find which ids of an inventory hold what an operator's conditions ask in OSDU (osdu/docs/reference/flow/inventory.md,
    /// Selecting ids by what OSDU holds), and keep them as the selection the payload names: an inventory flow's operation,
    /// which reads OSDU's search and changes nothing there.
    /// </summary>
    public const string Match = "match";

    /// <summary>The operation a run of a delivery flow performs: the one it names, or <see cref="Deliver"/>.</summary>
    public static string Of(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters.Operation ?? Deliver;
    }

    /// <summary>
    /// Refuses the per-run overrides SQLFlow's own flow kinds take (a full load, a backfill window, a file pattern, a source
    /// filter, an assertions-only or reprocess run): a flow of a registered kind reads its source its own way, and quietly
    /// ignoring one of them would run something other than what the caller asked for.
    /// </summary>
    public static void RefuseBuiltInOverrides(RunParameters parameters, string flowType, string instead)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var named = new List<string>();
        if (parameters.FullLoad)
        {
            named.Add("fullLoad");
        }

        if (parameters.BackfillFrom is not null || parameters.BackfillTo is not null)
        {
            named.Add("a backfill window");
        }

        if (parameters.FilePattern is not null)
        {
            named.Add("filePattern");
        }

        if (parameters.SourceFilter is not null)
        {
            named.Add("sourceFilter");
        }

        if (parameters.AssertionsOnly)
        {
            named.Add("assertionsOnly");
        }

        if (parameters.ReprocessFromSourceMin)
        {
            named.Add("reprocessFromSourceMin");
        }

        if (named.Count > 0)
        {
            throw new SqlFlowException($"{string.Join(", ", named)} do(es) not apply to '{flowType}' flows; {instead}");
        }
    }

    /// <summary>
    /// Refuses a run value given several values (a <see cref="RunValueList"/>) that the kind cannot apply: the partition for
    /// every kind, since a run acts in one partition, and every parameter of a kind whose parameters each fill a query or a
    /// location. A delivery flow's parameters that its record scope binds take several (<paramref name="scopeTakesSeveral"/>);
    /// which those are is the flow's to say, so its run settles the rest (Documents.FlowParameters.Resolve).
    /// </summary>
    public static void RefuseSeveralValues(RunParameters parameters, string flowType, bool scopeTakesSeveral)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        foreach (var (name, value) in parameters.Values)
        {
            if (!RunValueList.IsList(value))
            {
                continue;
            }

            var count = RunValueList.Items(value).Count;
            if (string.Equals(name, PartitionNames.RunValue, StringComparison.Ordinal))
            {
                throw new SqlFlowException($"{PartitionNames.RunValue} is given {count} values; a run acts in one partition.");
            }

            if (!scopeTakesSeveral)
            {
                throw new SqlFlowException(
                    $"Parameter '{name}' is given {count} values; a parameter of '{flowType}' flows takes one, since it fills a query or a location.");
            }
        }
    }
}

/// <summary>
/// What a deliver run sends again, by the names a run's payload and the API carry
/// (osdu/docs/reference/concepts/removal-and-reversal.md, Redelivering records): everything, or one part of the record,
/// named by what it is on the record's route. The part that is not named keeps what OSDU holds: the record keeps its
/// dataset references and its DDMS bulk link, and a bulk resend writes a new bulk version of the same record.
/// </summary>
public static class RedeliverScopes
{
    public const string All = "all";

    /// <summary>The record document alone.</summary>
    public const string Record = "record";

    /// <summary>The files a record carries (the file, dataset, manifest and composed routes, and a workflow route's own): uploaded and registered again, and the record rewritten with them.</summary>
    public const string Files = "files";

    /// <summary>The bulk data a DDMS stores for the record (the ddms route): written again as a new version of the record's bulk data.</summary>
    public const string Bulk = "bulk";

    /// <summary>The record document, by the name the single form has always used for it.</summary>
    public const string Metadata = "metadata";

    /// <summary>The files or the bulk data, whichever the route sends, by the name the single form has always used for them.</summary>
    public const string Payload = "payload";

    /// <summary>The workflow run of the workflow route: triggered again, without sending anything else again.</summary>
    public const string Workflow = PayloadParts.Workflow;

    public static IReadOnlyList<string> Names { get; } = [All, Record, Files, Bulk, Workflow, Metadata, Payload];

    /// <summary>The parts a record <paramref name="flow"/> delivers can be sent again by.</summary>
    public static IReadOnlyList<string> For(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (PayloadParts.Of(flow) is not null)
        {
            return [All, Record, .. PayloadParts.Roles(flow), Metadata, Payload];
        }

        return flow.Target.Protocol switch
        {
            DeliveryProtocol.File or DeliveryProtocol.Manifest or DeliveryProtocol.Dataset => [All, Record, Files, Metadata, Payload],
            DeliveryProtocol.Ddms => [All, Record, Bulk, Metadata, Payload],
            _ => [All, Record, Metadata],
        };
    }

    /// <summary>
    /// What <paramref name="name"/> sends again of a record <paramref name="flow"/> delivers: everything when it names
    /// nothing. A route that sends its payload in parts sends only the part named (files, bulk, workflow). Throws
    /// <see cref="DeliveryException"/> for a name that is not a part, or a part the flow's route does not send (files on
    /// a ddms route, bulk data on a file route, anything but the record on the storage route).
    /// </summary>
    public static RedeliverSelection Of(string? name, FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var part = string.IsNullOrWhiteSpace(name) ? All : name.Trim().ToLowerInvariant();
        if (!Names.Contains(part, StringComparer.Ordinal))
        {
            throw new DeliveryException($"redeliver '{name}' is not one of {string.Join(", ", Names)}.");
        }

        var parts = For(flow);
        if (!parts.Contains(part, StringComparer.Ordinal))
        {
            throw new DeliveryException(
                $"'{flow.Label}' is delivered by the {DeliveryProtocols.Name(flow.Target.Protocol)} route, which sends {Sends(flow)}, so a redelivery of '{part}' has nothing to send; name one of {string.Join(", ", parts)}.");
        }

        return part switch
        {
            All => new RedeliverSelection(RedeliverScope.All, []),
            Record or Metadata => new RedeliverSelection(RedeliverScope.Metadata, []),
            Payload => new RedeliverSelection(RedeliverScope.Payload, []),
            _ when PayloadParts.Of(flow) is not null => new RedeliverSelection(RedeliverScope.Payload, [part]),
            _ => new RedeliverSelection(RedeliverScope.Payload, []),
        };
    }

    private static string Sends(FlowDefinition flow)
    {
        if (PayloadParts.Of(flow) is not null)
        {
            var roles = PayloadParts.Roles(flow);
            return "the record" + (roles.Count == 0 ? string.Empty : ", " + string.Join(", ", roles.Select(r => r switch
            {
                PayloadParts.Files => "its files",
                PayloadParts.Bulk => "its bulk data",
                _ => "its workflow run",
            })));
        }

        return flow.Target.Protocol switch
        {
            DeliveryProtocol.File or DeliveryProtocol.Manifest or DeliveryProtocol.Dataset => "the record and its files",
            DeliveryProtocol.Ddms => "the record and its bulk data",
            DeliveryProtocol.Etp => "the object alone, its XML and arrays rendered into its document",
            _ => "the record alone",
        };
    }
}

/// <summary>
/// The kind-owned arguments of a run of this module's kinds (<see cref="RunParameters.Payload"/>): for a delivery run,
/// whether it forces a re-plan, the submission it works on, the records it is scoped to and what of them it redelivers or
/// removes, and the key slices a fan-out member plans; the tests, dimensions and inventories a run of the other kinds selects; and the
/// central configuration the control plane supplies to every one of them. Parsed strictly: an unknown property, a wrong
/// type or a value out of range is refused with a message naming it, at every trust boundary the platform validates a run
/// at, and each kind refuses every property it does not take (<see cref="RefuseOtherThan"/>).
/// </summary>
public sealed record DeliveryRunPayload
{
    /// <summary>The most records one run can be scoped to.</summary>
    public const int MaxRecordKeys = 1000;

    public const string ForceProperty = "force";

    public const string SubmissionIdProperty = "submissionId";

    /// <summary>The run a reverse run reverses (osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a run or a submission).</summary>
    public const string RunIdProperty = "runId";

    public const string RecordKeysProperty = "recordKeys";

    public const string RedeliverProperty = "redeliver";

    public const string RerenderProperty = "rerender";

    public const string SlicesProperty = "slices";

    /// <summary>
    /// How many runs of a fan-out share the flow's <c>reliability.rateLimitRps</c>: the coordinating run and its members
    /// (osdu/docs/reference/concepts/submissions.md, Many nodes).
    /// </summary>
    public const string RateShareProperty = "rateShare";

    public const string InterfaceProperty = "interface";

    public const string InterfacesProperty = "interfaces";

    /// <summary>The central configuration the control plane supplied with this run.</summary>
    public const string ReferencesProperty = "references";

    /// <summary>The central configuration set for one partition, by partition, which a run bound to it resolves with first.</summary>
    public const string PartitionReferencesProperty = "partitionReferences";

    /// <summary>The partition a run deleting the ledger, an inventory flow's remove run, or a removal flow's approved run acts in, named by whoever asked for it as confirmation.</summary>
    public const string ConfirmProperty = "confirm";

    /// <summary>How much of each record a delivery flow's remove run takes: record, previous, history or everything.</summary>
    public const string ScopeProperty = "scope";

    /// <summary>The listing a delivery flow's remove run empties: every record of the ledger it matches, resolved when the run removes them.</summary>
    public const string FilterProperty = "filter";

    /// <summary>Whether a delivery flow's remove run also deletes from the ledger each record OSDU answered for.</summary>
    public const string PurgeLedgerProperty = "purgeLedger";

    /// <summary>The tests of an assertion flow a run runs, by name.</summary>
    public const string TestsProperty = "tests";

    /// <summary>The tags of an assertion flow's tests a run runs: every test carrying one of them.</summary>
    public const string TagsProperty = "tags";

    /// <summary>The dimensions of a dimension flow a run builds, by name.</summary>
    public const string DimensionsProperty = "dimensions";

    /// <summary>The inventories of an inventory flow a run builds or reconciles, by name.</summary>
    public const string InventoriesProperty = "inventories";

    /// <summary>The types of a cache flow a run refreshes or plans, by name.</summary>
    public const string TypesProperty = "types";

    /// <summary>The removals of a removal flow a run carries out, by name.</summary>
    public const string EntriesProperty = "entries";

    /// <summary>
    /// How many removed keys a removal flow's guard held for approval an operator approved, as the operator was shown them
    /// (osdu/docs/reference/flow/removal.md, The guard).
    /// </summary>
    public const string ApproveProperty = "approve";

    /// <summary>What an inventory flow's remove run removes from OSDU (osdu/docs/reference/flow/inventory.md, Operations).</summary>
    public const string RemovalProperty = "removal";

    /// <summary>
    /// The selection an inventory flow's match run fills (osdu/docs/reference/flow/inventory.md, Selecting ids by what OSDU
    /// holds), by its number: the control plane opens it with the operator's conditions and queues the run.
    /// </summary>
    public const string SelectionProperty = "selection";

    /// <summary>The most test names, and the most tags, one run selects.</summary>
    public const int MaxSelected = AssertionFlowDefinition.MaxTests;

    /// <summary>The fewest runs a fan-out shares a rate between: its coordinating run and one member.</summary>
    public const int MinRateShare = 2;

    /// <summary>The most runs a fan-out shares a rate between: its coordinating run and the most members a flow declares.</summary>
    public const int MaxRateShare = FlowParallelism.MaxFanOut + 1;

    private static readonly string[] Properties =
        [ForceProperty, SubmissionIdProperty, RunIdProperty, RecordKeysProperty, RedeliverProperty, RerenderProperty, SlicesProperty, RateShareProperty, InterfaceProperty, InterfacesProperty, ReferencesProperty,
            PartitionReferencesProperty, ConfirmProperty, ScopeProperty, FilterProperty, PurgeLedgerProperty, TestsProperty, TagsProperty, DimensionsProperty, InventoriesProperty, RemovalProperty,
            SelectionProperty, TypesProperty, EntriesProperty, ApproveProperty];

    /// <summary>
    /// For every property a run asks something by (all but the central configuration, which every kind takes), the runs that
    /// take it: what the message refusing it on any other kind says.
    /// </summary>
    private static readonly Dictionary<string, string> TakenBy = new(StringComparer.Ordinal)
    {
        [ForceProperty] = "only delivery and retrieval runs force",
        [SubmissionIdProperty] = "only a delivery flow's runs name a submission",
        [RunIdProperty] = "only a delivery flow's reverse run names the run it reverses",
        [RecordKeysProperty] = "only a delivery flow's runs are scoped to records",
        [RedeliverProperty] = "only a delivery flow's deliver runs send records again",
        [RerenderProperty] = "only a delivery flow's runs bring records up to date",
        [SlicesProperty] = "only a delivery flow's intake members plan slices",
        [RateShareProperty] = "only a delivery flow's fan-out members share its rate limit",
        [InterfaceProperty] = "only a delivery flow's runs name an interface",
        [InterfacesProperty] = "only a delivery flow's runs select interfaces",
        [ConfirmProperty] = "only a delivery flow's run deleting the ledger, an inventory flow's remove run and a removal flow's approved run name the partition they act in",
        [ScopeProperty] = "only a delivery flow's remove run names how much of each record it removes",
        [FilterProperty] = "only a delivery flow's remove run names the listing it empties",
        [PurgeLedgerProperty] = "only a delivery flow's remove run deletes what it removes from the ledger",
        [TestsProperty] = "only an assertion flow's runs select tests",
        [TagsProperty] = "only an assertion flow's runs select tests by tag",
        [DimensionsProperty] = "only a dimension flow's runs select dimensions",
        [InventoriesProperty] = "only an inventory flow's runs select inventories",
        [RemovalProperty] = "only an inventory flow's remove run names what it removes",
        [SelectionProperty] = "only an inventory flow's match run names the selection it fills",
        [TypesProperty] = "only a cache flow's runs select types",
        [EntriesProperty] = "only a removal flow's runs select removals",
        [ApproveProperty] = "only a removal flow's remove run carries an approval",
    };

    /// <summary>The properties a delivery flow's runs take, besides the central configuration; which operation takes which is <see cref="Validate"/>'s.</summary>
    private static readonly string[] DeliveryProperties =
        [ForceProperty, SubmissionIdProperty, RunIdProperty, RecordKeysProperty, RedeliverProperty, RerenderProperty, SlicesProperty, RateShareProperty, InterfaceProperty, InterfacesProperty, ConfirmProperty,
            ScopeProperty, FilterProperty, PurgeLedgerProperty];

    private static readonly string[] RemovalProperties = ["finding", "scope", "expected", "ids", "selection"];

    private static readonly string[] ApprovalProperties = ["expected"];

    public static DeliveryRunPayload None { get; } = new();

    /// <summary>
    /// Look at every record past the whole-run gates: tier 0 and an already completed submission. It sends nothing that
    /// has not changed, since each record's own hashes still decide; <see cref="Redeliver"/> sends records again.
    /// </summary>
    public bool Force { get; init; }

    /// <summary>The submission the run works on: a re-run, a fan-out member's share, or the submission a reverse run reverses.</summary>
    public Guid? SubmissionId { get; init; }

    /// <summary>
    /// The run a reverse run reverses: what was delivered under the submissions it coordinated, and what it delivered itself
    /// (osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a run or a submission). Only a reverse run
    /// names it.
    /// </summary>
    public Guid? RunId { get; init; }

    /// <summary>The delivery keys the run is scoped to.</summary>
    public IReadOnlyList<Guid> RecordKeys { get; init; } = [];

    /// <summary>
    /// What a deliver run sends again whatever the hashes say: all, the record, or its payload or a part of it. It applies to
    /// the records <see cref="RecordKeys"/> names, or without keys to every record the flow has delivered. Null sends again
    /// everything of named records, and nothing of a run without keys.
    /// </summary>
    public string? Redeliver { get; init; }

    /// <summary>
    /// Brings records up to date: renders them again under the rules of now and lets each record's hashes decide what is
    /// sent, so only a part that renders differently goes, where <see cref="Redeliver"/> sends whatever the hashes say. A
    /// deliver run asks it of the records <see cref="RecordKeys"/> names, or without keys of every record the flow has
    /// delivered, as a request the ledger keeps until a run plans each record. A plan run renders every record it reads
    /// (those keys, or every row of the scope) without passing an unchanged one, and so says what a delivery would send.
    /// </summary>
    public bool Rerender { get; init; }

    /// <summary>The key slices of <see cref="SubmissionId"/> an intake member plans.</summary>
    public IReadOnlyList<int> Slices { get; init; } = [];

    /// <summary>
    /// How many runs share the flow's rate limit with a fan-out member: its coordinating run and every member of the fan-out
    /// it belongs to. The member sends under that share of <c>reliability.rateLimitRps</c>, so the fan-out as a whole keeps
    /// to the rate the flow declares. Null for every other run, which has the rate to itself.
    /// </summary>
    public int? RateShare { get; init; }

    /// <summary>
    /// The one interface of the source the run works on: what a fan-out member, a record-scoped run and a run on a
    /// submission name when the flow declares several. Null for a flow in the single form, and for a run of the whole source.
    /// </summary>
    public string? Interface { get; init; }

    /// <summary>The interfaces a run of a source runs, in any order; empty runs every interface.</summary>
    public IReadOnlyList<string> Interfaces { get; init; } = [];

    /// <summary>
    /// The partition a run deleting the ledger, or an inventory flow's remove run, acts in, as whoever asked for it named it:
    /// the run refuses to remove or delete anything unless it is the partition the ledger is kept in or the inventory reads, so a
    /// deletion is never one click away from a trigger dialog.
    /// </summary>
    public string? Confirm { get; init; }

    /// <summary>
    /// How much of each record a remove run takes (osdu/docs/reference/concepts/removal-and-reversal.md, Removing records
    /// from OSDU): the record reversibly, the latest version given back its previous one, the earlier versions, or the record
    /// and every version. Only a remove run names it, and it has to.
    /// </summary>
    public RemovalChoice? Scope { get; init; }

    /// <summary>
    /// The listing a remove run empties instead of naming its records by <see cref="RecordKeys"/>: every record of the ledger
    /// it matches, resolved against the ledger when the run removes them, so "every record this submission delivered" travels
    /// as a filter rather than as thousands of keys. Only a remove run names it.
    /// </summary>
    public RecordQuery? Filter { get; init; }

    /// <summary>
    /// Whether a remove run that takes its records out of OSDU (<see cref="Scope"/> record or everything) also deletes from the
    /// ledger each record OSDU answered for, keeping one line of it. Only a remove run asks it.
    /// </summary>
    public bool PurgeLedger { get; init; }

    /// <summary>
    /// The central configuration the control plane supplied with this run: the values a flow's ${env:NAME} references
    /// resolve to, ahead of the node's own environment. Empty when the control plane holds none, which leaves every
    /// reference to the node. A value here is a non-secret value or a reference the node resolves, never a secret.
    /// </summary>
    public IReadOnlyDictionary<string, string> References { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>
    /// The central configuration set for single partitions (osdu/docs/reference/concepts/control-plane.md, The central
    /// configuration on every run), by partition name: what a run bound to one of them resolves with ahead of
    /// <see cref="References"/>. Every partition the control plane holds values for travels, so a run that refreshes
    /// several partitions in turn resolves each with its own.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> PartitionReferences { get; init; }
        = ReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>.Empty;

    /// <summary>
    /// The tests of an assertion flow the run runs, by name (osdu/docs/reference/flow/assertion.md, Operations and the
    /// run payload): with <see cref="Tags"/>, a test runs when it is named here or carries one of the tags; with
    /// neither, every test runs.
    /// </summary>
    public IReadOnlyList<string> Tests { get; init; } = [];

    /// <summary>The tags of an assertion flow's tests the run runs: every test carrying one of them.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>True when the payload selects tests of an assertion flow, by name or by tag.</summary>
    public bool SelectsTests => Tests.Count > 0 || Tags.Count > 0;

    /// <summary>The dimensions of a dimension flow the run builds, by name; with none, every dimension is built.</summary>
    public IReadOnlyList<string> Dimensions { get; init; } = [];

    /// <summary>True when the payload selects dimensions of a dimension flow.</summary>
    public bool SelectsDimensions => Dimensions.Count > 0;

    /// <summary>The inventories of an inventory flow the run builds or reconciles, by name; with none, every inventory.</summary>
    public IReadOnlyList<string> Inventories { get; init; } = [];

    /// <summary>
    /// What an inventory flow's remove run removes: the ids of one finding of the inventory <see cref="Inventories"/> names, with
    /// <see cref="Confirm"/> naming the partition it acts in. Only that run carries it.
    /// </summary>
    public InventoryRemovalRequest? Removal { get; init; }

    /// <summary>
    /// The selection an inventory flow's match run fills, by its number, for the inventory <see cref="Inventories"/> names.
    /// Only that run carries it.
    /// </summary>
    public long? SelectionId { get; init; }

    /// <summary>True when the payload selects inventories of an inventory flow, names what one removes, or the selection one fills.</summary>
    public bool SelectsInventories => Inventories.Count > 0 || Removal is not null || SelectionId is not null;

    /// <summary>
    /// The types of a cache flow the run refreshes or plans, by name (osdu/docs/reference/flow/cache.md, Operations and the
    /// run payload); with none, every type of the flow.
    /// </summary>
    public IReadOnlyList<string> Types { get; init; } = [];

    /// <summary>The removals of a removal flow the run carries out, by name; with none, every removal of the flow.</summary>
    public IReadOnlyList<string> Entries { get; init; } = [];

    /// <summary>
    /// The approval a removal flow's remove run carries: how many removed keys of the removal <see cref="Entries"/> names wait for
    /// approval, as the operator was shown them, with <see cref="Confirm"/> naming the partition. Only that run carries it.
    /// </summary>
    public RemovalApproval? Approve { get; init; }

    /// <summary>True when the payload carries nothing.</summary>
    public bool IsEmpty => CarriesOnlyConfiguration && References.Count == 0 && PartitionReferences.Count == 0;

    /// <summary>
    /// True when the payload carries nothing but the central configuration the control plane supplied: what the payload of
    /// a cache run, which names nothing, may hold.
    /// </summary>
    public bool CarriesOnlyConfiguration => Named().Count == 0;

    /// <summary>
    /// The properties this payload asks something by, in the order a payload lists them: every one but the central
    /// configuration, which every kind of the module takes. A property counts when it asks for something (force set, a
    /// list that names something), as each kind reads it; one written as false or empty asks for nothing.
    /// </summary>
    public IReadOnlyList<string> Named()
    {
        var named = new List<string>();
        Add(Force, ForceProperty);
        Add(SubmissionId is not null, SubmissionIdProperty);
        Add(RunId is not null, RunIdProperty);
        Add(RecordKeys.Count > 0, RecordKeysProperty);
        Add(Redeliver is not null, RedeliverProperty);
        Add(Rerender, RerenderProperty);
        Add(Slices.Count > 0, SlicesProperty);
        Add(RateShare is not null, RateShareProperty);
        Add(Interface is not null, InterfaceProperty);
        Add(Interfaces.Count > 0, InterfacesProperty);
        Add(Confirm is not null, ConfirmProperty);
        Add(Scope is not null, ScopeProperty);
        Add(Filter is not null, FilterProperty);
        Add(PurgeLedger, PurgeLedgerProperty);
        Add(Tests.Count > 0, TestsProperty);
        Add(Tags.Count > 0, TagsProperty);
        Add(Dimensions.Count > 0, DimensionsProperty);
        Add(Inventories.Count > 0, InventoriesProperty);
        Add(Removal is not null, RemovalProperty);
        Add(SelectionId is not null, SelectionProperty);
        Add(Types.Count > 0, TypesProperty);
        Add(Entries.Count > 0, EntriesProperty);
        Add(Approve is not null, ApproveProperty);
        return named;

        void Add(bool asks, string property)
        {
            if (asks)
            {
                named.Add(property);
            }
        }
    }

    /// <summary>
    /// Refuses the first property this payload asks something by that a run of <paramref name="flowType"/> does not take,
    /// naming it, the runs that take it, and what the kind's payload names, in one message shape for every kind:
    /// <c>payload P does not apply to a K flow: only ...; a K flow's payload names only ...</c>. The central configuration
    /// the control plane supplies is taken by every kind and never refused here.
    /// </summary>
    /// <param name="flowType">The kind the run is of, as its documents' <c>flowType</c> names it.</param>
    /// <param name="takes">The properties the kind takes, besides the central configuration; empty for a kind that takes none.</param>
    /// <exception cref="SqlFlowException">The payload names a property the kind does not take.</exception>
    public void RefuseOtherThan(string flowType, IReadOnlyCollection<string> takes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowType);
        ArgumentNullException.ThrowIfNull(takes);
        foreach (var property in Named())
        {
            if (takes.Contains(property, StringComparer.Ordinal))
            {
                continue;
            }

            var kind = $"{(StartsWithVowel(flowType) ? "an" : "a")} {flowType} flow";
            var names = takes.Count switch
            {
                0 => "carries only the central configuration the control plane supplies",
                1 => $"names only {takes.First()}",
                _ => $"names only {string.Join(", ", takes.Take(takes.Count - 1))} and {takes.Last()}",
            };
            throw new SqlFlowException($"payload {property} does not apply to {kind}: {TakenBy[property]}; {kind}'s payload {names}.");
        }
    }

    private static bool StartsWithVowel(string word) => "aeiouAEIOU".Contains(word[0], StringComparison.Ordinal);

    /// <summary>
    /// The configuration a run resolves its references with when it acts on <paramref name="partition"/>: the partition's
    /// own values over the ones set for no partition. A value set for a partition always wins, so an estate-wide endpoint
    /// never sends a run of one partition to another's platform. With no partition, the values set for none.
    /// </summary>
    public IReadOnlyDictionary<string, string> ReferencesFor(string? partition)
    {
        if (partition is null)
        {
            return References;
        }

        var own = PartitionReferences.FirstOrDefault(p => string.Equals(p.Key, partition, StringComparison.OrdinalIgnoreCase)).Value;
        if (own is null || own.Count == 0)
        {
            return References;
        }

        var merged = new Dictionary<string, string>(References, StringComparer.Ordinal);
        foreach (var (name, value) in own)
        {
            merged[name] = value;
        }

        return merged;
    }

    /// <summary>The payload of a run's parameters; none when it carries none.</summary>
    public static DeliveryRunPayload Parse(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return Parse(parameters.Payload);
    }

    /// <summary>Parses a payload's JSON text strictly; null or blank is no payload.</summary>
    public static DeliveryRunPayload Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return None;
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw new SqlFlowException("payload must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new SqlFlowException($"payload is not valid JSON: {ex.Message}", ex);
        }

        foreach (var (name, _) in root)
        {
            if (!Properties.Contains(name, StringComparer.Ordinal))
            {
                throw new SqlFlowException($"payload property '{name}' is not one of {string.Join(", ", Properties)}.");
            }
        }

        return new DeliveryRunPayload
        {
            Force = Boolean(root, ForceProperty),
            SubmissionId = root[SubmissionIdProperty] is null ? null : Id(root[SubmissionIdProperty], SubmissionIdProperty),
            RunId = root[RunIdProperty] is null ? null : Id(root[RunIdProperty], RunIdProperty),
            RecordKeys = Keys(root[RecordKeysProperty]),
            Redeliver = root[RedeliverProperty] is null ? null : Text(root[RedeliverProperty], RedeliverProperty),
            Rerender = Boolean(root, RerenderProperty),
            Slices = SliceList(root[SlicesProperty]),
            RateShare = root[RateShareProperty] is null ? null : Share(root[RateShareProperty]),
            Interface = root[InterfaceProperty] is null ? null : Text(root[InterfaceProperty], InterfaceProperty),
            Interfaces = Names(root[InterfacesProperty]),
            Confirm = root[ConfirmProperty] is null ? null : Text(root[ConfirmProperty], ConfirmProperty),
            Scope = root[ScopeProperty] is null ? null : RemovalScopeOf(root[ScopeProperty]),
            Filter = root[FilterProperty] is null ? null : ListingFilter(root[FilterProperty]),
            PurgeLedger = Boolean(root, PurgeLedgerProperty),
            References = ReferenceMap(root[ReferencesProperty], ReferencesProperty),
            PartitionReferences = PartitionReferenceMap(root[PartitionReferencesProperty]),
            Tests = Selection(root[TestsProperty], TestsProperty, "test names"),
            Tags = Selection(root[TagsProperty], TagsProperty, "tags"),
            Dimensions = Selection(root[DimensionsProperty], DimensionsProperty, "dimension names"),
            Inventories = Selection(root[InventoriesProperty], InventoriesProperty, "inventory names"),
            Removal = root[RemovalProperty] is null ? null : RemovalRequest(root[RemovalProperty]),
            SelectionId = root[SelectionProperty] is null ? null : Number(root[SelectionProperty], SelectionProperty),
            Types = Selection(root[TypesProperty], TypesProperty, "type names"),
            Entries = RemovalEntries(root[EntriesProperty]),
            Approve = root[ApproveProperty] is null ? null : ApprovalRequest(root[ApproveProperty]),
        };
    }

    /// <summary>
    /// Checks the payload against the operation it travels with, throwing <see cref="SqlFlowException"/> naming the property:
    /// what each operation takes, and the ranges every property keeps to.
    /// </summary>
    public void Validate(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        // A removal takes as many records as one removal may; every other run is scoped to a run's worth of them.
        if (operation == DeliveryOperations.Remove && RecordKeys.Count > RemovalLimits.MaxSelection)
        {
            throw new SqlFlowException($"payload recordKeys holds {RecordKeys.Count} keys; one removal takes at most {RemovalLimits.MaxSelection} records.");
        }

        if (operation != DeliveryOperations.Remove && RecordKeys.Count > MaxRecordKeys)
        {
            throw new SqlFlowException($"payload recordKeys holds {RecordKeys.Count} keys; one run is scoped to at most {MaxRecordKeys} records.");
        }

        if (RecordKeys.Any(k => k == Guid.Empty))
        {
            throw new SqlFlowException("payload recordKeys holds an empty UUID; a delivery key is a non-empty UUID.");
        }

        if (RecordKeys.Distinct().Count() != RecordKeys.Count)
        {
            throw new SqlFlowException("payload recordKeys names a record more than once.");
        }

        if (SubmissionId == Guid.Empty)
        {
            throw new SqlFlowException("payload submissionId is an empty UUID.");
        }

        if (RunId == Guid.Empty)
        {
            throw new SqlFlowException("payload runId is an empty UUID.");
        }

        if (RunId is not null && operation != DeliveryOperations.Reverse)
        {
            throw new SqlFlowException($"payload {RunIdProperty} does not apply to the {operation} operation: only a reverse run names the run it reverses.");
        }

        if (Confirm is not null && operation != DeliveryOperations.DeleteLedger)
        {
            throw new SqlFlowException($"payload {ConfirmProperty} does not apply to the {operation} operation: only a run deleting the ledger names the partition it acts in.");
        }

        if (operation != DeliveryOperations.Remove)
        {
            Refuse(Scope is not null, ScopeProperty, operation, "only a remove run names how much of each record it removes");
            Refuse(Filter is not null, FilterProperty, operation, "only a remove run names the listing it empties");
            Refuse(PurgeLedger, PurgeLedgerProperty, operation, "only a remove run deletes what it removes from the ledger");
        }

        if (Redeliver is { } scope && !RedeliverScopes.Names.Contains(scope, StringComparer.OrdinalIgnoreCase))
        {
            throw new SqlFlowException($"payload redeliver '{scope}' is not one of {string.Join(", ", RedeliverScopes.Names)}.");
        }

        if (Slices.Any(s => s < 0 || s >= KeySlices.MaxSlices))
        {
            throw new SqlFlowException($"payload slices holds an index outside 0 to {KeySlices.MaxSlices - 1}.");
        }

        if (Slices.Distinct().Count() != Slices.Count)
        {
            throw new SqlFlowException("payload slices names a slice more than once.");
        }

        if (RateShare is { } share && (share < MinRateShare || share > MaxRateShare))
        {
            throw new SqlFlowException(
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"payload {RateShareProperty} is {share}; a fan-out's runs number {MinRateShare} to {MaxRateShare}, its coordinating run and at most {FlowParallelism.MaxFanOut} members."));
        }

        if (Interface is { } named && !SourceDefinition.IsInterfaceName(named))
        {
            throw new SqlFlowException($"payload interface '{named}' is not an interface name: a letter followed by letters, digits, '_' and '-'.");
        }

        foreach (var name in Interfaces)
        {
            if (!SourceDefinition.IsInterfaceName(name))
            {
                throw new SqlFlowException($"payload interfaces holds '{name}', which is not an interface name: a letter followed by letters, digits, '_' and '-'.");
            }
        }

        if (Interfaces.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Interfaces.Count)
        {
            throw new SqlFlowException("payload interfaces names an interface more than once.");
        }

        if (Interface is not null && Interfaces.Count > 0)
        {
            throw new SqlFlowException("payload names both an interface and interfaces; a run works on one interface or runs a selection of them, not both.");
        }

        if (Interfaces.Count > 0 && (SubmissionId is not null || RecordKeys.Count > 0 || Slices.Count > 0))
        {
            throw new SqlFlowException("payload interfaces selects interfaces for a run of the source; a run on a submission, on records or on slices works on one interface, which 'interface' names.");
        }

        if (SubmissionId is not null && RecordKeys.Count > 0)
        {
            throw new SqlFlowException("payload names both a submissionId and recordKeys; a run works on a submission or is scoped to records, not both.");
        }

        RefuseOtherThan(FlowDefinition.FlowTypeName, DeliveryProperties);

        switch (operation)
        {
            case DeliveryOperations.Deliver:
                Refuse(Slices.Count > 0 && SubmissionId is null, SlicesProperty, operation, "slices are cut from the submission their coordinating run registered, which submissionId names");
                Refuse(Slices.Count > 0 && Slices.Max() - Slices.Min() + 1 != Slices.Count, SlicesProperty, operation, "a slice run delivers a share of neighbouring key slices, as its coordinating run deals them out");
                Refuse(RateShare is not null && Slices.Count == 0, RateShareProperty, operation, "only a slice run of a fan-out shares its coordinating run's rate limit");
                Refuse(Slices.Count > 0 && Redeliver is not null, RedeliverProperty, operation, "a slice run delivers what its share plans; its coordinating run marked what is sent again");
                Refuse(Slices.Count > 0 && Rerender, RerenderProperty, operation, "a slice run delivers what its share plans; its coordinating run asked for records to be rendered again");
                Refuse(Redeliver is not null && SubmissionId is not null, RedeliverProperty, operation, "a run on a submission delivers what that submission planned");
                Refuse(Rerender && SubmissionId is not null, RerenderProperty, operation, "a run on a submission delivers what that submission planned");
                Refuse(Rerender && Redeliver is not null, RerenderProperty, operation,
                    "rerender sends what renders differently and redeliver sends whatever the hashes say; name one");
                break;
            case DeliveryOperations.Plan:
                Refuse(Slices.Count > 0, SlicesProperty, operation, "only a run of a fan-out plans slices");
                Refuse(RateShare is not null, RateShareProperty, operation, "only a run of a fan-out shares its coordinating run's rate limit");
                Refuse(Redeliver is not null, RedeliverProperty, operation, "a plan sends nothing");
                Refuse(Rerender && SubmissionId is not null, RerenderProperty, operation, "a plan of a submission reads the rows that submission recorded");
                break;
            case DeliveryOperations.Intake:
                Refuse(Redeliver is not null, RedeliverProperty, operation, "an intake sends nothing");
                Refuse(Rerender, RerenderProperty, operation, "an intake sends nothing; bring records up to date with a deliver run");
                Refuse(Slices.Count > 0 && SubmissionId is null, SlicesProperty, operation, "slices are cut from the submission their coordinating run registered, which submissionId names");
                Refuse(RateShare is not null && SubmissionId is null, RateShareProperty, operation, "a member shares the rate of the run whose submission it works on, which submissionId names");
                break;
            case DeliveryOperations.Drain:
                Refuse(Force, ForceProperty, operation, "a drain plans nothing to force");
                Refuse(RecordKeys.Count > 0, RecordKeysProperty, operation, "a drain delivers the batches a submission planned");
                Refuse(Redeliver is not null, RedeliverProperty, operation, "a drain delivers what was planned");
                Refuse(Rerender, RerenderProperty, operation, "a drain delivers what was planned");
                Refuse(Slices.Count > 0, SlicesProperty, operation, "only a run of a fan-out plans slices");
                Refuse(RateShare is not null && SubmissionId is null, RateShareProperty, operation, "a member shares the rate of the run whose submission it works on, which submissionId names");
                break;
            case DeliveryOperations.Verify:
                Refuse(RateShare is not null, RateShareProperty, operation, "only a run of a fan-out shares its coordinating run's rate limit");
                Refuse(SubmissionId is not null, SubmissionIdProperty, operation, "a verify reads the ledger's delivered records, not a submission");
                Refuse(Redeliver is not null, RedeliverProperty, operation, "a verify sends nothing");
                Refuse(Rerender, RerenderProperty, operation, "a verify sends nothing");
                Refuse(Slices.Count > 0, SlicesProperty, operation, "only a run of a fan-out plans slices");
                break;
            case DeliveryOperations.Sync:
                Refuse(RateShare is not null, RateShareProperty, operation, "only a run of a fan-out shares its coordinating run's rate limit");
                Refuse(Force, ForceProperty, operation, "a sync passes no gate: it reads every row it is given");
                Refuse(SubmissionId is not null, SubmissionIdProperty, operation, "a sync reads the rows of the ledger's records, not a submission's");
                Refuse(Redeliver is not null, RedeliverProperty, operation, "a sync sends nothing");
                Refuse(Rerender, RerenderProperty, operation, "a sync sends nothing");
                Refuse(Slices.Count > 0, SlicesProperty, operation, "only a run of a fan-out plans slices");
                break;
            case DeliveryOperations.Reverse:
                Refuse(RateShare is not null, RateShareProperty, operation, "only a run of a fan-out shares its coordinating run's rate limit");
                Refuse(SubmissionId is null && RunId is null, SubmissionIdProperty, operation, "a reverse run names what it reverses, a submissionId or a runId");
                Refuse(SubmissionId is not null && RunId is not null, RunIdProperty, operation, "a reverse run reverses one submission or one run, not both");
                Refuse(Force, ForceProperty, operation, "a reversal passes no gate: it takes every record its source delivered");
                Refuse(RecordKeys.Count > 0, RecordKeysProperty, operation, "a reversal takes every record its source delivered");
                Refuse(Redeliver is not null, RedeliverProperty, operation, "a reversal puts back what OSDU held; it sends nothing the flow renders");
                Refuse(Rerender, RerenderProperty, operation, "a reversal puts back what OSDU held; it renders nothing");
                Refuse(Slices.Count > 0, SlicesProperty, operation, "only a run of a fan-out plans slices");
                Refuse(Interfaces.Count > 0, InterfacesProperty, operation, "a reversal works in one interface's ledger, which 'interface' names");
                break;
            case DeliveryOperations.DeleteLedger:
                if (Confirm is null)
                {
                    throw new SqlFlowException(
                        $"payload {ConfirmProperty} is required by the {operation} operation: name the partition the run acts in, which the run checks before it removes or deletes anything.");
                }

                Refuse(Force, ForceProperty, operation, "deleting the ledger passes no gate: it takes every record");
                Refuse(SubmissionId is not null, SubmissionIdProperty, operation, "deleting the ledger takes every record of it, not a submission's");
                Refuse(RecordKeys.Count > 0, RecordKeysProperty, operation, "deleting the ledger takes every record of it; remove records with a removal");
                Refuse(Redeliver is not null, RedeliverProperty, operation, "deleting the ledger sends nothing");
                Refuse(Rerender, RerenderProperty, operation, "deleting the ledger renders nothing");
                Refuse(RateShare is not null, RateShareProperty, operation, "only a run of a fan-out shares its coordinating run's rate limit");
                Refuse(Slices.Count > 0, SlicesProperty, operation, "only a run of a fan-out plans slices");
                break;
            case DeliveryOperations.Remove:
                if (Scope is not { } removing)
                {
                    throw new SqlFlowException(
                        $"payload {ScopeProperty} is required by the {operation} operation: name how much of each record goes, record, previous, history or everything.");
                }

                if (RecordKeys.Count == 0 && Filter is null)
                {
                    throw new SqlFlowException(
                        $"payload names no records for the {operation} operation: a removal names them by {RecordKeysProperty}, or by {FilterProperty}, the listing every match of which it removes.");
                }

                Refuse(RecordKeys.Count > 0 && Filter is not null, FilterProperty, operation, "a removal names its records by recordKeys or by filter, not both");
                Refuse(PurgeLedger && removing is not (RemovalChoice.Record or RemovalChoice.Everything), PurgeLedgerProperty, operation,
                    $"a record is deleted from the ledger only with a removal that takes it out of OSDU (record or everything), not with {RemovalScopes.Wire(removing)}");
                Refuse(Force, ForceProperty, operation, "a removal passes no gate: it takes the records it names");
                Refuse(SubmissionId is not null, SubmissionIdProperty, operation, "a removal names its records by recordKeys or by a filter, which can name a submission");
                Refuse(Redeliver is not null, RedeliverProperty, operation, "a removal sends nothing the flow renders");
                Refuse(Rerender, RerenderProperty, operation, "a removal renders nothing");
                Refuse(RateShare is not null, RateShareProperty, operation, "only a run of a fan-out shares its coordinating run's rate limit");
                Refuse(Slices.Count > 0, SlicesProperty, operation, "only a run of a fan-out plans slices");
                Refuse(Interfaces.Count > 0, InterfacesProperty, operation, "a removal works in one interface's ledger, which 'interface' names");
                break;
            case DeliveryOperations.Undo:
                Refuse(RateShare is not null, RateShareProperty, operation, "only a run of a fan-out shares its coordinating run's rate limit");
                Refuse(SubmissionId is not null, SubmissionIdProperty, operation, "an undo takes what every unfinished delivery of the ledger left, not a submission's");
                Refuse(RecordKeys.Count > 0, RecordKeysProperty, operation, "an undo takes what every unfinished delivery of the ledger left");
                Refuse(Redeliver is not null, RedeliverProperty, operation, "an undo sends nothing the flow renders");
                Refuse(Rerender, RerenderProperty, operation, "an undo renders nothing");
                Refuse(Slices.Count > 0, SlicesProperty, operation, "only a run of a fan-out plans slices");
                break;
            case DeliveryOperations.Replan:
                Refuse(RateShare is not null, RateShareProperty, operation, "only a run of a fan-out shares its coordinating run's rate limit");
                Refuse(SubmissionId is not null, SubmissionIdProperty, operation, "a replan reads every row of the scope under a submission of its own");
                Refuse(RecordKeys.Count > 0, RecordKeysProperty, operation, "a replan reads every row of the scope; scope a deliver run to records instead");
                Refuse(Redeliver is not null, RedeliverProperty, operation, "a replan decides by each record's hashes");
                Refuse(Rerender, RerenderProperty, operation, "a replan reads the rows of the scope; bring delivered records up to date with a deliver run");
                Refuse(Slices.Count > 0, SlicesProperty, operation, "only a run of a fan-out plans slices");
                break;
            default:
                throw new SqlFlowException($"'{operation}' is not an operation of a delivery flow.");
        }
    }

    /// <summary>The compact JSON text a run carries, or null when the payload carries nothing.</summary>
    public string? ToJson()
    {
        if (IsEmpty)
        {
            return null;
        }

        var root = new JsonObject();
        if (Force)
        {
            root[ForceProperty] = true;
        }

        if (SubmissionId is { } submission)
        {
            root[SubmissionIdProperty] = submission.ToString("D");
        }

        if (RunId is { } run)
        {
            root[RunIdProperty] = run.ToString("D");
        }

        if (RecordKeys.Count > 0)
        {
            root[RecordKeysProperty] = new JsonArray(RecordKeys.Select(k => (JsonNode?)JsonValue.Create(k.ToString("D"))).ToArray());
        }

        if (Redeliver is { } redeliver)
        {
            root[RedeliverProperty] = redeliver;
        }

        if (Rerender)
        {
            root[RerenderProperty] = true;
        }

        if (Slices.Count > 0)
        {
            root[SlicesProperty] = new JsonArray(Slices.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        }

        if (RateShare is { } share)
        {
            root[RateShareProperty] = share;
        }

        if (Interface is { } named)
        {
            root[InterfaceProperty] = named;
        }

        if (Interfaces.Count > 0)
        {
            root[InterfacesProperty] = new JsonArray(Interfaces.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        }

        if (Confirm is { } confirm)
        {
            root[ConfirmProperty] = confirm;
        }

        if (Scope is { } removalScope)
        {
            root[ScopeProperty] = RemovalScopes.Wire(removalScope);
        }

        if (Filter is { } listing)
        {
            root[FilterProperty] = JsonNode.Parse(RemovalFilter.ToJson(listing));
        }

        if (PurgeLedger)
        {
            root[PurgeLedgerProperty] = true;
        }

        if (References.Count > 0)
        {
            // Ordered by name so the same configuration writes the same payload, which keeps a run row comparable.
            var references = new JsonObject();
            foreach (var (name, value) in References.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                references[name] = value;
            }

            root[ReferencesProperty] = references;
        }

        if (PartitionReferences.Count > 0)
        {
            var partitions = new JsonObject();
            foreach (var (partition, values) in PartitionReferences.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var map = new JsonObject();
                foreach (var (name, value) in values.OrderBy(r => r.Key, StringComparer.Ordinal))
                {
                    map[name] = value;
                }

                partitions[partition] = map;
            }

            root[PartitionReferencesProperty] = partitions;
        }

        if (Tests.Count > 0)
        {
            root[TestsProperty] = new JsonArray(Tests.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
        }

        if (Tags.Count > 0)
        {
            root[TagsProperty] = new JsonArray(Tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
        }

        if (Dimensions.Count > 0)
        {
            root[DimensionsProperty] = new JsonArray(Dimensions.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray());
        }

        if (Inventories.Count > 0)
        {
            root[InventoriesProperty] = new JsonArray(Inventories.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        }

        if (Types.Count > 0)
        {
            root[TypesProperty] = new JsonArray(Types.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
        }

        if (Removal is { } removal)
        {
            var written = new JsonObject
            {
                ["finding"] = removal.Finding,
                ["scope"] = removal.Scope,
                ["expected"] = removal.Expected,
            };
            if (removal.NamesIds)
            {
                written["ids"] = new JsonArray(removal.Ids.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
            }

            if (removal.Selection is { } taken)
            {
                written["selection"] = taken;
            }

            root[RemovalProperty] = written;
        }

        if (SelectionId is { } selection)
        {
            root[SelectionProperty] = selection;
        }

        if (Entries.Count > 0)
        {
            root[EntriesProperty] = new JsonArray(Entries.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
        }

        if (Approve is { } approve)
        {
            root[ApproveProperty] = new JsonObject { ["expected"] = approve.Expected };
        }

        return root.ToJsonString();
    }

    private static void Refuse(bool refused, string property, string operation, string why)
    {
        if (refused)
        {
            throw new SqlFlowException($"payload {property} does not apply to the {operation} operation: {why}.");
        }
    }

    private static bool Boolean(JsonObject root, string property)
    {
        var node = root[property];
        if (node is null)
        {
            return false;
        }

        return node is JsonValue value && value.TryGetValue<bool>(out var flag)
            ? flag
            : throw new SqlFlowException($"payload {property} must be true or false.");
    }

    private static string Text(JsonNode? node, string property)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : throw new SqlFlowException($"payload {property} must be a non-empty string.");

    private static Guid Id(JsonNode? node, string property)
        => Guid.TryParse(Text(node, property), out var id)
            ? id
            : throw new SqlFlowException($"payload {property} must be a UUID.");

    /// <summary>A rate share as the payload writes it: a whole number; <see cref="Validate"/> checks its range.</summary>
    private static int Share(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<int>(out var share)
            ? share
            : throw new SqlFlowException($"payload {RateShareProperty} must be a whole number: how many runs of the fan-out share the rate limit.");

    /// <summary>A number a payload names something by, such as a selection: a whole number, at least 1.</summary>
    private static long Number(JsonNode? node, string property)
        => node is JsonValue value && value.TryGetValue<long>(out var number) && number >= 1
            ? number
            : throw new SqlFlowException($"payload {property} must be the number of what it names, a whole number of at least 1.");

    /// <summary>
    /// The delivery keys a payload names, at most as many as one removal takes; how many the operation it travels with takes is
    /// <see cref="Validate"/>'s to say.
    /// </summary>
    private static IReadOnlyList<Guid> Keys(JsonNode? node)
    {
        if (node is null)
        {
            return [];
        }

        if (node is not JsonArray array)
        {
            throw new SqlFlowException($"payload {RecordKeysProperty} must be an array of delivery key UUIDs.");
        }

        if (array.Count > RemovalLimits.MaxSelection)
        {
            throw new SqlFlowException($"payload {RecordKeysProperty} holds {array.Count} keys; a run names at most {RemovalLimits.MaxSelection} records.");
        }

        return array.Select(item => Id(item, RecordKeysProperty)).ToList();
    }

    /// <summary>How much of each record a remove run takes, by its name on the wire, which has to be one of the four exactly.</summary>
    private static RemovalChoice RemovalScopeOf(JsonNode? node)
        => RemovalScopes.TryParse(Text(node, ScopeProperty), out var scope)
            ? scope
            : throw new SqlFlowException($"payload {ScopeProperty} must be one of record, previous, history or everything.");

    /// <summary>The listing a remove run empties, read as strictly as the payload around it: every part of it, and nothing else.</summary>
    private static RecordQuery ListingFilter(JsonNode? node)
        => node is JsonObject filter
            ? RemovalFilter.FromJson(filter.ToJsonString())
            : throw new SqlFlowException($"payload {FilterProperty} must be a JSON object: the listing filter every match of which the run removes.");

    /// <summary>
    /// The central configuration a payload carries: a JSON object of reference name to value, refused property by
    /// property so a bad one names itself. Absent is none, which leaves every reference to the node.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReferenceMap(JsonNode? node, string property)
    {
        if (node is null)
        {
            return ReadOnlyDictionary<string, string>.Empty;
        }

        if (node is not JsonObject obj)
        {
            throw new SqlFlowException($"payload {property} must be a JSON object of reference name to value.");
        }

        if (obj.Count > DeliveryConfigNames.MaxPerRun)
        {
            throw new SqlFlowException($"payload {property} holds {obj.Count} properties; one run carries at most {DeliveryConfigNames.MaxPerRun}.");
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in obj)
        {
            if (!DeliveryConfigNames.IsName(name))
            {
                throw new SqlFlowException($"payload {property} property '{name}' does not name a reference: a letter or underscore followed by letters, digits and underscores, at most {DeliveryConfigNames.MaxNameLength} characters.");
            }

            if (value is not JsonValue text || !text.TryGetValue<string>(out var supplied) || !DeliveryConfigNames.IsValue(supplied))
            {
                throw new SqlFlowException($"payload {property} property '{name}' must be a non-empty string of at most {DeliveryConfigNames.MaxValueLength} characters without control characters.");
            }

            map[name] = supplied;
        }

        return map;
    }

    /// <summary>
    /// The central configuration set for single partitions a payload carries: a JSON object of partition name to the
    /// partition's reference map, each refused as a flow refuses a partition name and as <see cref="References"/> refuses a map.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> PartitionReferenceMap(JsonNode? node)
    {
        if (node is null)
        {
            return ReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>.Empty;
        }

        if (node is not JsonObject obj)
        {
            throw new SqlFlowException($"payload {PartitionReferencesProperty} must be a JSON object of partition name to reference map.");
        }

        if (obj.Count > PartitionNames.MaxPerFlow)
        {
            throw new SqlFlowException($"payload {PartitionReferencesProperty} holds {obj.Count} partitions; one run carries the values of at most {PartitionNames.MaxPerFlow}.");
        }

        var map = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (partition, values) in obj)
        {
            if (!CacheScope.IsPartitionId(partition))
            {
                throw new SqlFlowException($"payload {PartitionReferencesProperty} names '{partition}', which is not a data-partition-id.");
            }

            if (!map.TryAdd(partition, ReferenceMap(values, $"{PartitionReferencesProperty}.{partition}")))
            {
                throw new SqlFlowException($"payload {PartitionReferencesProperty} names partition '{partition}' more than once (ignoring case).");
            }
        }

        return map;
    }

    /// <summary>
    /// The removal a payload names, parsed strictly: a removable finding, a scope (record or everything), the count the operator
    /// was shown, and at most <see cref="InventoryRemovalRequest.MaxIds"/> distinct ids, each an OSDU id, whose number is that count.
    /// </summary>
    /// <summary>
    /// An approval of the removed keys a removal flow's guard held: how many wait for approval (<c>expected</c>), which the run
    /// holds the ledger to before it makes them pending.
    /// </summary>
    private static RemovalApproval ApprovalRequest(JsonNode? node)
    {
        if (node is not JsonObject approval)
        {
            throw new SqlFlowException($"payload {ApproveProperty} must be an object naming expected: how many removed keys the operator was shown waiting for approval.");
        }

        foreach (var (name, _) in approval)
        {
            if (!ApprovalProperties.Contains(name, StringComparer.Ordinal))
            {
                throw new SqlFlowException($"payload {ApproveProperty}.{name} is not one of {string.Join(", ", ApprovalProperties)}.");
            }
        }

        if (approval["expected"] is not JsonValue expectedValue || !expectedValue.TryGetValue<long>(out var expected) || expected < 1)
        {
            throw new SqlFlowException($"payload {ApproveProperty}.expected must be the number of keys the operator was shown, at least 1.");
        }

        return new RemovalApproval { Expected = expected };
    }

    private static InventoryRemovalRequest RemovalRequest(JsonNode? node)
    {
        if (node is not JsonObject removal)
        {
            throw new SqlFlowException($"payload {RemovalProperty} must be an object naming finding, scope, expected and optionally ids or a selection.");
        }

        foreach (var (name, _) in removal)
        {
            if (!RemovalProperties.Contains(name, StringComparer.Ordinal))
            {
                throw new SqlFlowException($"payload {RemovalProperty}.{name} is not one of {string.Join(", ", RemovalProperties)}.");
            }
        }

        var finding = Text(removal["finding"], $"{RemovalProperty}.finding");
        if (!InventoryRemovals.IsAskable(finding))
        {
            throw new SqlFlowException(
                $"payload {RemovalProperty}.finding is '{finding}'; an inventory removes the ids of {string.Join(", ", InventoryRemovals.Askable)}.");
        }

        var scope = Text(removal["scope"], $"{RemovalProperty}.scope");
        if (!InventoryRemovals.IsScope(scope))
        {
            throw new SqlFlowException(
                $"payload {RemovalProperty}.scope is '{scope}'; it is {InventoryRemovals.SoftDelete} (a soft delete, reversible), {InventoryRemovals.Purge} (a purge, every version destroyed) or {InventoryRemovals.InventoryOnly} (a gone id's row in the inventory, nothing asked of OSDU).");
        }

        if (InventoryRemovals.Mismatch(finding, scope) is { } mismatch)
        {
            throw new SqlFlowException($"payload {RemovalProperty} asks for {finding} ids with scope {scope}: {mismatch}");
        }

        if (removal["expected"] is not JsonValue expectedValue || !expectedValue.TryGetValue<long>(out var expected) || expected < 1)
        {
            throw new SqlFlowException($"payload {RemovalProperty}.expected must be the number of ids the operator was shown, at least 1.");
        }

        IReadOnlyList<string> ids = [];
        if (removal["ids"] is { } listed)
        {
            if (listed is not JsonArray array || array.Count == 0 || array.Count > InventoryRemovalRequest.MaxIds)
            {
                throw new SqlFlowException(
                    $"payload {RemovalProperty}.ids must be an array of 1 to {InventoryRemovalRequest.MaxIds} OSDU ids; leave it out to remove every id of the finding.");
            }

            var named = new List<string>(array.Count);
            foreach (var item in array)
            {
                var id = Text(item, $"{RemovalProperty}.ids").Trim();
                if (id.Length == 0 || id.Length > DeliveryModel.MaxTargetIdLength || id.Any(char.IsWhiteSpace))
                {
                    throw new SqlFlowException($"payload {RemovalProperty}.ids holds '{id}', which is not an OSDU id.");
                }

                if (named.Contains(id, StringComparer.Ordinal))
                {
                    throw new SqlFlowException($"payload {RemovalProperty}.ids names '{id}' more than once.");
                }

                named.Add(id);
            }

            if (named.Count != expected)
            {
                throw new SqlFlowException(string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"payload {RemovalProperty}.ids names {named.Count} ids and expected says {expected}; a removal that names its ids expects as many."));
            }

            ids = named;
        }

        long? selection = null;
        if (removal["selection"] is { } taken)
        {
            selection = Number(taken, $"{RemovalProperty}.selection");
            if (ids.Count > 0)
            {
                throw new SqlFlowException($"payload {RemovalProperty} names both ids and a selection; a removal takes the ids it names or those of a selection, not both.");
            }

            if (!InventoryRemovals.IsRemovable(finding))
            {
                throw new SqlFlowException(
                    $"payload {RemovalProperty} takes {finding} ids from a selection; a selection holds ids OSDU serves, so it is taken only for {string.Join(", ", InventoryRemovals.Removable)} ids, which go from OSDU.");
            }
        }

        return new InventoryRemovalRequest { Finding = finding, Scope = scope, Expected = expected, Ids = ids, Selection = selection };
    }

    /// <summary>
    /// A selection of an assertion flow's tests or tags, a dimension flow's dimensions or an inventory flow's inventories: an
    /// array of names, each a name as a flow writes one (<see cref="SelectableNames.IsName"/>), none twice. Whether each names
    /// something of the flow is the run's to check, since a payload is validated before the flow document is read.
    /// </summary>
    /// <summary>
    /// The removals a removal flow's run names (<see cref="EntriesProperty"/>): at most as many as a flow declares, each a
    /// removal's name as the flow keeps it (<see cref="RemovalNames"/>), which may carry a delivery flow's interface after a
    /// slash, so the rule test, tag and dimension names keep to is not theirs.
    /// </summary>
    private static IReadOnlyList<string> RemovalEntries(JsonNode? node)
    {
        if (node is null)
        {
            return [];
        }

        if (node is not JsonArray array || array.Count > RemovalFlowDefinition.MaxEntries)
        {
            throw new SqlFlowException($"payload {EntriesProperty} must be an array of at most {RemovalFlowDefinition.MaxEntries} removal names.");
        }

        var names = new List<string>(array.Count);
        foreach (var item in array)
        {
            var name = Text(item, EntriesProperty);
            if (!RemovalNames.IsName(name))
            {
                throw new SqlFlowException($"payload {EntriesProperty} holds '{name}', which is not a removal's name: {RemovalNames.Rule}.");
            }

            if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new SqlFlowException($"payload {EntriesProperty} names '{name}' more than once.");
            }

            names.Add(name);
        }

        return names;
    }

    private static IReadOnlyList<string> Selection(JsonNode? node, string property, string what)
    {
        if (node is null)
        {
            return [];
        }

        if (node is not JsonArray array || array.Count > MaxSelected)
        {
            throw new SqlFlowException($"payload {property} must be an array of at most {MaxSelected} {what}.");
        }

        var names = new List<string>(array.Count);
        foreach (var item in array)
        {
            var name = Text(item, property);
            if (!SelectableNames.IsName(name))
            {
                throw new SqlFlowException($"payload {property} holds '{name}', which is not a name: {SelectableNames.Rule}.");
            }

            if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new SqlFlowException($"payload {property} names '{name}' more than once.");
            }

            names.Add(name);
        }

        return names;
    }

    private static IReadOnlyList<string> Names(JsonNode? node)
    {
        if (node is null)
        {
            return [];
        }

        if (node is not JsonArray array || array.Count > SourceDefinition.MaxInterfaces)
        {
            throw new SqlFlowException($"payload {InterfacesProperty} must be an array of at most {SourceDefinition.MaxInterfaces} interface names.");
        }

        return array.Select(item => Text(item, InterfacesProperty)).ToList();
    }

    private static IReadOnlyList<int> SliceList(JsonNode? node)
    {
        if (node is null)
        {
            return [];
        }

        if (node is not JsonArray array || array.Count > KeySlices.MaxSlices)
        {
            throw new SqlFlowException($"payload {SlicesProperty} must be an array of at most {KeySlices.MaxSlices} slice indexes.");
        }

        return array
            .Select(item => item is JsonValue value && value.TryGetValue<int>(out var slice)
                ? slice
                : throw new SqlFlowException($"payload {SlicesProperty} must hold whole numbers."))
            .ToList();
    }
}
