using System.Text.Json;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Runs a slice run of a fan-out as a node runs it (osdu/docs/reference/concepts/submissions.md, Many nodes): the flow's
/// deliver operation on a runtime of its own over the engine it is given, filtered to the key slices its payload names, and
/// what the node journals as the run's result. The flow it is given is the one the node runs, so a suite can give each
/// node a copy of the repository, and with it a work folder, of its own.
/// </summary>
internal static class FanOutMembers
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Runs <paramref name="member"/> to its end and returns what its node journals as its result.</summary>
    public static async Task<(string Operation, DeliveryRunPayload Payload, string ResultJson)> RunAsync(
        EngineContext engine, FlowDefinition flow, RunParameters member, Guid runId, CancellationToken ct)
    {
        var (operation, payload, runtime) = await OpenAsync(engine, flow, member, runId, ct);
        using (runtime)
        {
            var run = await runtime.RunAsync(DeliveryExecutor.ForcesReplan(payload, reRunningSubmission: true), ct);
            var outcome = DeliverOutcome.OfShare(run, operation, flow.Source.Record.Object, runtime.Selection.Describe(), payload.Slices);
            return (operation, payload, JsonSerializer.Serialize(outcome, Json));
        }
    }

    /// <summary>
    /// Plans the share <paramref name="member"/> was dealt into its node's work folder and sends none of it, as a slice run
    /// whose node stopped after planning leaves it.
    /// </summary>
    public static async Task<DeliveryRunPayload> PlanOnlyAsync(EngineContext engine, FlowDefinition flow, RunParameters member, Guid runId, CancellationToken ct)
    {
        var (_, payload, runtime) = await OpenAsync(engine, flow, member, runId, ct);
        using (runtime)
        {
            await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: true, ct);
            return payload;
        }
    }

    private static async Task<(string Operation, DeliveryRunPayload Payload, FlowRuntime Runtime)> OpenAsync(
        EngineContext engine, FlowDefinition flow, RunParameters member, Guid runId, CancellationToken ct)
    {
        var operation = DeliveryOperations.Of(member);
        var payload = DeliveryRunPayload.Parse(member);
        payload.Validate(operation);
        Assert.Equal(DeliveryOperations.Deliver, operation);
        Assert.NotEmpty(payload.Slices);

        // A run reaches the executor, which takes the partition it names off its values and binds the flow to it: a slice run
        // of a run bound to a partition names that partition, and is never a parameter of the flow.
        var (partition, values) = PartitionNames.SplitRunValues(member.Values, keptAsParameter: false);
        Assert.Equal(flow.Partition, partition);
        var runtime = await FlowRuntime.CreateAsync(engine, flow, values, ct);
        runtime.RunId = runId;
        runtime.Actor = "slice run";
        runtime.SubmissionId = payload.SubmissionId;
        runtime.Slices = payload.Slices;
        if (payload.RateShare is { } share)
        {
            runtime.ShareRateLimit(share);
        }

        return (operation, payload, runtime);
    }
}
