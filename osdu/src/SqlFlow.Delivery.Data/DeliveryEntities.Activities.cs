namespace SqlFlow.Delivery.Data;

// The audit trail (osdu/docs/reference/concepts/ledger.md, osdu.Activity and osdu.ActivityRecord): every intervention
// and run with the records it changed, and the retrievals of retrieval flows.

/// <summary>The audit trail of runs and interventions: who did what, when, with which inputs, and the outcome.</summary>
public sealed class DeliveryActivity
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long ActivityId { get; set; }

    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Actor { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public string Outcome { get; set; } = "running";

    public string? ParametersJson { get; set; }

    public Guid? SubmissionId { get; set; }

    public Guid? DeliveryKey { get; set; }

    /// <summary>
    /// The platform run the activity ran in, when it ran in one: every operation of a run, an intervention a run's payload
    /// asked for, an inventory removal. Null for an intervention through the control plane, the GUI or the command line. A
    /// plan run records no activity.
    /// </summary>
    public Guid? RunId { get; set; }

    public string? Summary { get; set; }

    public string? Log { get; set; }

    /// <summary>
    /// A run that completed having changed nothing: it planned, held, blocked and sent no record, and left none waiting.
    /// The schedule fired and found nothing new. The audit trail leaves these out unless asked for them, and says how many
    /// it left out; an intervention is never idle.
    /// </summary>
    public bool Idle { get; set; }
}

/// <summary>
/// One record an intervention changed, written in the statement that changed it. An intervention made for one record names
/// it on its <see cref="DeliveryActivity"/>; one made for many (a release of a whole flow, or of every record a problem
/// keeps blocked) names each record here, so a record's history holds every request that reached it, with who asked and
/// when, however many records the request reached.
/// </summary>
public sealed class DeliveryActivityRecord
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public Guid FlowId { get; set; }

    public Guid DeliveryKey { get; set; }

    /// <summary>The intervention, as the audit trail numbers it.</summary>
    public long ActivityId { get; set; }
}

/// <summary>One retrieval run: the window it covered, where its files went, and its outcome.</summary>
public sealed class DeliveryRetrieval
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long RetrievalId { get; set; }

    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    public Guid? RunId { get; set; }

    public string Actor { get; set; } = string.Empty;

    /// <summary>The kinds the run covered, comma separated.</summary>
    public string Kinds { get; set; } = string.Empty;

    /// <summary>The query as it ran, window included.</summary>
    public string? Query { get; set; }

    public string? WindowField { get; set; }

    public DateTime? WindowFrom { get; set; }

    public DateTime? WindowTo { get; set; }

    public string Location { get; set; } = string.Empty;

    public string? ManifestLocation { get; set; }

    public string Status { get; set; } = "running";

    public long Records { get; set; }

    public int Files { get; set; }

    /// <summary>Uncompressed bytes written.</summary>
    public long Bytes { get; set; }

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public string? Error { get; set; }
}
