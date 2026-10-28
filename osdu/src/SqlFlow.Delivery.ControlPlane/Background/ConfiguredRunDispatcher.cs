using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>
/// The run dispatcher with the partitions' layer of the central configuration attached. The values set for no partition
/// reach every run of every kind through the control plane's own reference source (<see cref="DeliveryConfigStore"/> as an
/// <see cref="IRunReferenceSource"/>); a run of a flow this module owns is also queued carrying the values set for each
/// partition, and resolves with the ones of the partition it is bound to over them (osdu/docs/reference/cli/config.md,
/// Which runs read it). A node task of the module carries the values of the one partition it names, laid over the values
/// set for none, in the task's own references.
/// </summary>
/// <remarks>
/// <para>
/// This decorates the dispatcher rather than the endpoints because a run reaches the queue by more ways than one: an
/// endpoint, a schedule fire, a fan-out group, the CLI through the API. All of them call
/// <see cref="IRunDispatcher.EnqueueAsync"/> or <see cref="IRunDispatcher.EnqueueGroupAsync"/>, so attaching here is the
/// only way a scheduled delivery and a delivery someone pressed a button for resolve the same references. A node task
/// reaches the queue through <see cref="IRunDispatcher.EnqueueComputeTaskAsync"/>, and is given its configuration here
/// for the same reason. Runs of SQLFlow's own flow kinds take no partition, and pass through to the values every run gets.
/// </para>
/// <para>
/// A run that already carries partition values keeps them, and a task that already carries references keeps them: a
/// caller that composed a payload deliberately is not overruled. Nothing is attached when no partition has a value. A
/// configuration a run cannot carry (too many values, more partitions than a flow serves, or more than its payload holds)
/// refuses the run with a message naming the configuration to put right, rather than queuing a run that would resolve
/// against whatever its node's environment happens to hold.
/// </para>
/// </remarks>
public sealed class ConfiguredRunDispatcher : IRunDispatcher
{
    /// <summary>The task argument naming the repository whose configuration a node task of the module is given, and whose
    /// synced folder the task's flow file is read from.</summary>
    public const string RepoArgument = DeliveryOperation.RepositoryArgument;

    private readonly IRunDispatcher _inner;
    private readonly DeliveryConfigStore _config;

    public ConfiguredRunDispatcher(IRunDispatcher inner, DeliveryConfigStore config)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(config);
        _inner = inner;
        _config = config;
    }

    public async Task<Guid> EnqueueAsync(CatalogDbContext catalog, RunEnqueueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Configures(request.FlowKind))
        {
            return await _inner.EnqueueAsync(catalog, request, ct).ConfigureAwait(false);
        }

        var supplied = await SuppliedAsync(request.RepoId, request.FlowName, ct).ConfigureAwait(false);
        return await _inner.EnqueueAsync(catalog, request with { Parameters = With(request.Parameters, supplied, request.FlowName) }, ct).ConfigureAwait(false);
    }

    public async Task<RunGroupEnqueueResult> EnqueueGroupAsync(
        CatalogDbContext catalog, RunGroupEnqueueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A group's members can be of several kinds: the whole estate's pre, ingestion and delivery flows run as one
        // ordered set. Only the members this module owns are given the partitions' values.
        var owned = request.Members.Where(m => Configures(m.FlowKind)).Select(m => m.FlowName).ToList();
        if (owned.Count == 0)
        {
            return await _inner.EnqueueGroupAsync(catalog, request, ct).ConfigureAwait(false);
        }

        var supplied = await SuppliedAsync(request.RepoId, request.Anchor, ct).ConfigureAwait(false);
        if (supplied.Partitions.Count == 0)
        {
            return await _inner.EnqueueGroupAsync(catalog, request, ct).ConfigureAwait(false);
        }

        var members = new Dictionary<string, RunParameters>(
            request.MemberParameters ?? new Dictionary<string, RunParameters>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var name in owned)
        {
            members[name] = With(members.GetValueOrDefault(name), supplied, name);
        }

        return await _inner.EnqueueGroupAsync(catalog, request with { MemberParameters = members }, ct).ConfigureAwait(false);
    }

    public async Task<Guid> EnqueueComputeTaskAsync(CatalogDbContext catalog, ComputeTaskEnqueueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Configures(request.ProviderKind) || string.IsNullOrWhiteSpace(request.ArgumentsJson))
        {
            return await _inner.EnqueueComputeTaskAsync(catalog, request, ct).ConfigureAwait(false);
        }

        var task = ComputeTaskPayload.FromJson(request.ArgumentsJson, [request.Operation]);
        if (task.References is { Count: > 0 } || !Guid.TryParse(task.Argument(RepoArgument), out var repoId))
        {
            // A task that carries its configuration already, or names no repository to read one for, goes as it is: the
            // control plane's own source gives it the values set for no repository.
            return await _inner.EnqueueComputeTaskAsync(catalog, request, ct).ConfigureAwait(false);
        }

        var supplied = await SuppliedAsync(repoId, request.SourceRef, ct).ConfigureAwait(false);
        var own = supplied.For(task.Argument(DeliveryOperation.PartitionArgument));
        if (own.Count == 0)
        {
            return await _inner.EnqueueComputeTaskAsync(catalog, request, ct).ConfigureAwait(false);
        }

        var configured = WithReferences(task, own);
        configured.Validate([request.Operation]);
        return await _inner.EnqueueComputeTaskAsync(catalog, request with { ArgumentsJson = configured.ToJson() }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="task"/> carrying <paramref name="own"/>, the central configuration of its partition over the values set
    /// for none, as the task's references, which every operation resolves with before the environment of the process it runs
    /// in. One shape for a task queued for a node and for an operation the control plane runs itself, so both resolve a
    /// reference to the same value.
    /// </summary>
    internal static ComputeTaskPayload WithReferences(ComputeTaskPayload task, IReadOnlyDictionary<string, string> own)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(own);
        return own.Count == 0 ? task : task with { References = own };
    }

    // Everything that is not a queueing decision passes straight through: this decorator exists to attach configuration
    // to a run, and nothing else about dispatching changes.
    public Task<CancelOutcome> CancelAsync(CatalogDbContext catalog, Guid runId, CancellationToken ct = default)
        => _inner.CancelAsync(catalog, runId, ct);

    public Task<GroupCancelResult> CancelGroupAsync(CatalogDbContext catalog, Guid groupId, CancellationToken ct = default)
        => _inner.CancelGroupAsync(catalog, groupId, ct);

    public Task<CancelOutcome> CancelComputeTaskAsync(CatalogDbContext catalog, Guid taskId, CancellationToken ct = default)
        => _inner.CancelComputeTaskAsync(catalog, taskId, ct);

    /// <summary>
    /// The flow kinds this module adds, by the <c>flowType</c> their documents carry: the ones its loader reads
    /// (<see cref="DeliveryDocumentLoader.FlowTypes"/>). Every one acts in a partition, so every one is queued carrying the
    /// partitions' values, and a kind added to the module is configured by being added there.
    /// </summary>
    public static IReadOnlyList<string> ConfiguredKinds => DeliveryDocumentLoader.FlowTypes;

    /// <summary>Whether a run of <paramref name="flowKind"/> is one of this module's, and so is given the partitions' values.</summary>
    public static bool Configures(string? flowKind)
        => flowKind is not null && ConfiguredKinds.Contains(flowKind.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The configuration for a repository, refused when a run could not carry it: more values in one scope than a run
    /// carries, or more partitions than a flow serves. A read that fails throws, as the control plane's own source does.
    /// </summary>
    /// <exception cref="SqlFlowException">The configuration is larger than a run carries.</exception>
    private async Task<DeliveryConfiguration> SuppliedAsync(Guid repoId, string flowName, CancellationToken ct)
    {
        var supplied = await _config.ConfigurationAsync(repoId, ct).ConfigureAwait(false);
        var widest = supplied.Partitions.Values.Select(p => p.Count).Append(supplied.Base.Count).Max();
        if (widest > DeliveryConfigNames.MaxPerRun)
        {
            throw new SqlFlowException(
                $"Flow '{flowName}' resolves {widest} central configuration properties in one scope, more than the {DeliveryConfigNames.MaxPerRun} a run carries. Remove properties the estate does not use.");
        }

        if (supplied.Partitions.Count > PartitionNames.MaxPerFlow)
        {
            throw new SqlFlowException(
                $"The central configuration of flow '{flowName}' sets values for {supplied.Partitions.Count} partitions, more than the {PartitionNames.MaxPerFlow} a flow serves. Remove the values of partitions the estate does not deliver to.");
        }

        return supplied;
    }

    /// <summary>
    /// <paramref name="parameters"/> carrying every partition's own values in its payload. A payload that already names
    /// references is left alone, and nothing is written when no partition has a value.
    /// </summary>
    /// <exception cref="SqlFlowException">The partitions' values are more than a run's payload holds.</exception>
    private static RunParameters With(RunParameters? parameters, DeliveryConfiguration supplied, string flowName)
    {
        var current = parameters ?? RunParameters.None;
        if (supplied.Partitions.Count == 0)
        {
            return current;
        }

        var payload = DeliveryRunPayload.Parse(current.Payload);
        if (payload.References.Count > 0 || payload.PartitionReferences.Count > 0)
        {
            return current;
        }

        var carried = (payload with { PartitionReferences = supplied.Partitions }).ToJson();
        if (carried is { Length: > RunParameters.MaxPayloadLength })
        {
            throw new SqlFlowException(
                $"The central configuration of flow '{flowName}' is {carried.Length} characters as a run carries it, more than the {RunParameters.MaxPayloadLength} a run's payload holds. Remove properties the estate does not use, or set fewer for single partitions.");
        }

        return current with { Payload = carried };
    }
}
