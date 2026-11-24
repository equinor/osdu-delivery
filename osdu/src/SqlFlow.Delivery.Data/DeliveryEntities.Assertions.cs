namespace SqlFlow.Delivery.Data;

// The runs and results of assertion flows (osdu/docs/reference/flow/assertion.md, The report).

/// <summary>
/// One run of an assertion flow's tests in one partition (osdu/docs/reference/flow/assertion.md, The report): the tests it
/// ran, how they came out, and who asked. The results of each test are rows of <see cref="DeliveryAssertionResult"/>.
/// </summary>
public sealed class DeliveryAssertionRun
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long AssertionRunId { get; set; }

    /// <summary>The ledger identity of the flow in the partition.</summary>
    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    /// <summary>The platform run the tests ran in.</summary>
    public Guid? RunId { get; set; }

    public string Actor { get; set; } = string.Empty;

    /// <summary>The tests the run was asked for, as JSON (an object of tests and tags); null when it ran every test.</summary>
    public string? Selection { get; set; }

    /// <summary>running, passed, failed, errored or cancelled.</summary>
    public string Status { get; set; } = "running";

    /// <summary>The tests the run took up, those skipped included.</summary>
    public int Tests { get; set; }

    public int Passed { get; set; }

    public int Failed { get; set; }

    public int Warned { get; set; }

    public int Errored { get; set; }

    public int Skipped { get; set; }

    /// <summary>A hash of the tests the run ran, as their definitions stood: two runs of the same tests hash the same.</summary>
    public string? DefinitionsHash { get; set; }

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// One test's result in one run of an assertion flow: its outcome, what it read and matched, and every assertion with what
/// it expected, what it found and the records that failed it (<see cref="Detail"/>, JSON).
/// </summary>
public sealed class DeliveryAssertionResult
{
    /// <summary>The partition the row belongs to: the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long ResultId { get; set; }

    public long AssertionRunId { get; set; }

    /// <summary>The ledger identity of the flow in the partition.</summary>
    public Guid FlowId { get; set; }

    public string TestName { get; set; } = string.Empty;

    /// <summary>The OSDU kind the test reads.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>passed, failed, warned, errored or skipped.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>The heaviest severity among the assertions that failed; null when none did.</summary>
    public string? Severity { get; set; }

    /// <summary>The records the test matched (the index's count, or the ids storage holds); null when it did not get as far.</summary>
    public long? Matched { get; set; }

    /// <summary>The records the test read and held its assertions to.</summary>
    public long? Evaluated { get; set; }

    /// <summary>True when more records matched than the test reads, and it evaluated the first ones as a sample.</summary>
    public bool Sampled { get; set; }

    public int Assertions { get; set; }

    public int FailedAssertions { get; set; }

    /// <summary>The hash of the test's definition when it ran, so a result says whether the test has changed since.</summary>
    public string DefinitionHash { get; set; } = string.Empty;

    public long DurationMs { get; set; }

    public string? Error { get; set; }

    /// <summary>The whole result as JSON: every assertion, what it expected and found, and its failing examples.</summary>
    public string Detail { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    public DateTime CompletedUtc { get; set; }
}
