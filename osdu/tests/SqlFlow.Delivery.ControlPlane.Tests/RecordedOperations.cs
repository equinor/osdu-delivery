using System.Collections.Concurrent;
using SqlFlow.Core.Compute;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Execution;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The operations the control plane runs in process (<see cref="DirectOperations"/>), stood in for: each keeps the payload it
/// was given, exactly as the real operation would be given it, and answers with a fixed document naming itself. What an API
/// test asks of the control plane is then read from <see cref="Given"/> without reaching an OSDU or the ingestion tables.
/// </summary>
internal sealed class RecordedOperations
{
    /// <summary>Every name the control plane runs in process.</summary>
    public static readonly string[] Names =
    [
        ProbeTargetOperation.OperationName,
        ReadRecordOperation.OperationName,
        ReadSourceRowOperation.OperationName,
        PreviewRecordOperation.OperationName,
        ScopeValuesOperation.OperationName,
        StagedRowsOperation.OperationName,
        ExploreOperation.OperationName,
    ];

    /// <summary>What each operation was given, in the order it was asked.</summary>
    public ConcurrentQueue<ComputeTaskPayload> Given { get; } = new();

    /// <summary>
    /// What an operation answers a payload with, as its JSON, where a test wants a particular answer (or a failure, thrown):
    /// null leaves it to the fixed document naming the operation.
    /// </summary>
    public Func<ComputeTaskPayload, string?>? Answer { get; set; }

    /// <summary>The registry the control plane runs from, made of stand-ins for every operation it runs.</summary>
    public DirectOperations Registry() => new(Names.Select(name => (IComputeOperation)new Recorder(name, this)));

    /// <summary>The payload the last request was run with, taken from the record so the next is read alone.</summary>
    public ComputeTaskPayload Last()
    {
        var all = new List<ComputeTaskPayload>();
        while (Given.TryDequeue(out var payload))
        {
            all.Add(payload);
        }

        return all.Count == 1
            ? all[0]
            : throw new InvalidOperationException($"Expected one operation to have run since the last read, and {all.Count} did.");
    }

    private sealed class Recorder(string name, RecordedOperations owner) : IComputeOperation
    {
        public string Name { get; } = name;

        public Task<string> ExecuteAsync(ComputeTaskPayload payload, CancellationToken ct)
        {
            owner.Given.Enqueue(payload);
            return Task.FromResult(owner.Answer?.Invoke(payload) ?? $$"""{"ran":"{{Name}}"}""");
        }
    }
}
