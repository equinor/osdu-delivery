// The YAML shapes of the workflows a delivery flow runs (osdu/docs/reference/flow/enrich-workflow.md,
// osdu/docs/reference/flow/routes.md, workflow): each workflow's stages, inputs, outputs and results, and the
// Airflow instance behind the Workflow service.
// As in YamlModels.cs, every property must be listed: unknown keys are rejected.
#pragma warning disable CA1812, CA2227, CA1002, CA1034, CA1720

namespace SqlFlow.Delivery.Documents;

/// <summary>The workflow route's declaration (<c>interfaces.&lt;name&gt;.workflow</c>, or <c>target.workflow</c> in the single form).</summary>
internal sealed class WorkflowYaml
{
    /// <summary>How the record is written first: dataset (registered with its files) or storage.</summary>
    public string? Anchor { get; set; }

    public List<WorkflowStageYaml>? Stages { get; set; }

    /// <summary>The payload sets registered as the workflow's inputs, by name, in document order.</summary>
    public OrderedDictionary<string, WorkflowInputYaml?>? Inputs { get; set; }

    public WorkflowResultsYaml? Results { get; set; }

    /// <summary>changed (the default), created or requested.</summary>
    public string? RunWhen { get; set; }

    /// <summary>The secrets a context names with {secret:name}, as references.</summary>
    public Dictionary<string, string>? Secrets { get; set; }

    /// <summary>The tag key the route writes on the record with its anchor tag.</summary>
    public string? AnchorTag { get; set; }
}

internal sealed class WorkflowStageYaml
{
    public string? Workflow { get; set; }

    public string? Contract { get; set; }

    /// <summary>The execution context: any YAML, with placeholders in its strings.</summary>
    public Dictionary<string, object?>? Context { get; set; }

    public int? TimeoutMinutes { get; set; }

    public int? PollSeconds { get; set; }

    public OrderedDictionary<string, WorkflowOutputYaml?>? Outputs { get; set; }
}

internal sealed class WorkflowInputYaml
{
    public string? Root { get; set; }

    public string? LocationColumn { get; set; }

    public string? Pattern { get; set; }

    public string? HashColumn { get; set; }

    public string? ChunkCountColumn { get; set; }

    public string? DatasetKind { get; set; }

    public bool? Optional { get; set; }
}

internal sealed class WorkflowOutputYaml
{
    public string? Value { get; set; }

    public WorkflowXComYaml? Xcom { get; set; }
}

internal sealed class WorkflowXComYaml
{
    public string? Task { get; set; }

    public string? Key { get; set; }

    public string? Match { get; set; }
}

internal sealed class WorkflowResultsYaml
{
    /// <summary>True when the run writes the record itself, which is read back.</summary>
    public bool? Anchor { get; set; }

    public string? Ids { get; set; }

    public WorkflowArtefactYaml? Artefact { get; set; }

    public WorkflowSearchYaml? Search { get; set; }

    public string? Manifest { get; set; }

    public WorkflowXComYaml? Xcom { get; set; }

    public int? Minimum { get; set; }

    public int? WaitSeconds { get; set; }

    public int? Keep { get; set; }

    public bool? Remove { get; set; }
}

internal sealed class WorkflowArtefactYaml
{
    public string? Role { get; set; }

    public string? Kind { get; set; }
}

internal sealed class WorkflowSearchYaml
{
    public string? Kind { get; set; }

    public string? Query { get; set; }
}

/// <summary>The Airflow instance behind the target's Workflow service (<c>target.airflow</c>).</summary>
internal sealed class AirflowYaml
{
    public string? Endpoint { get; set; }

    public TargetAuthYaml? Auth { get; set; }

    public Dictionary<string, string>? Headers { get; set; }

    public string? ApiVersion { get; set; }
}
