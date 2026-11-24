using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// An operator's action on a flow's records from the command line: the runtime it runs in, recorded on the audit trail as
/// <c>cli:&lt;user&gt;@&lt;machine&gt;</c>'s (<see cref="RunActors.LocalAccount"/>), and how a release or an adoption the
/// runtime refused (<see cref="InterventionRefusedException"/>) is answered here.
/// </summary>
internal static class CliInterventions
{
    /// <summary>The runtime an action on <paramref name="flow"/>'s records runs in (<see cref="FlowRuntime.ForTarget"/>), as this machine's account.</summary>
    public static FlowRuntime Runtime(EngineContext engine, FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(flow);
        var runtime = FlowRuntime.ForTarget(flow.Interface is null ? engine : engine.ForInterface(flow.Interface), flow);
        runtime.Actor = RunActors.LocalAccount();
        return runtime;
    }

    /// <summary>
    /// A refusal answered the command line's way: a partition not typed back, or typed wrong, is a usage error that names
    /// <c>--confirm</c>; any other fails the command saying what to look at.
    /// </summary>
    /// <exception cref="FlowValidationException">The refusal is not about the confirmation.</exception>
    public static int Refused(InterventionRefusedException refused, Func<string, int> usageError)
    {
        ArgumentNullException.ThrowIfNull(refused);
        ArgumentNullException.ThrowIfNull(usageError);
        return refused.Refusal switch
        {
            InterventionRefusal.Unconfirmed => usageError($"{refused.Message} Type the partition back with --confirm {refused.Partition}."),
            InterventionRefusal.ConfirmationDiffers => usageError(refused.Message),
            InterventionRefusal.UnknownIssue => throw new FlowValidationException($"{refused.Message} 'records issues' lists the issues it has.", refused),
            _ => throw new FlowValidationException(refused.Message, refused),
        };
    }
}
