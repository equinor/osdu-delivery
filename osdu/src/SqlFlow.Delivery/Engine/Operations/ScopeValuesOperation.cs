using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Engine.Operations;

/// <summary>
/// <c>delivery-scope-values</c>: the values the parameters of a flow can take, for the pages that ask for a parameter's
/// value (a run form, a flow's Preview tab, a mapping's value check) to offer them rather than have them typed. Without a
/// <see cref="ParameterArgument"/> it lists each parameter of the scope predicate (<see cref="IIngestionSource.ScopeValuesAsync"/>);
/// with one it lists that parameter's values alone, those containing <see cref="SearchArgument"/> when it is given
/// (<see cref="IIngestionSource.ColumnValuesAsync"/>). A parameter's values are the distinct values the column it is
/// looked up in holds in the flow's own record table: the column <c>source.record.scope</c> binds it to, or the one its
/// <c>lookup</c> names (osdu/docs/reference/flow/parameters.md, Lookups), read with the flow's own connection reference,
/// so no value list is kept anywhere to go stale. Nothing is written.
/// </summary>
public sealed class ScopeValuesOperation : DeliveryOperation
{
    public const string OperationName = "delivery-scope-values";

    /// <summary>The argument naming the one parameter whose values are listed.</summary>
    public const string ParameterArgument = "parameter";

    /// <summary>The argument holding the text the listed values contain, compared ignoring case.</summary>
    public const string SearchArgument = "search";

    /// <summary>The most values listed for one parameter; a column holding more says so.</summary>
    public const int MaxValues = 500;

    public ScopeValuesOperation(EngineContext context)
        : base(context)
    {
    }

    public override string Name => OperationName;

    protected override async Task<object> RunAsync(EngineContext context, FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct)
    {
        // The listing reads no row of a scope, so it needs no parameter value: it is what a value is picked from.
        var source = context.Sources.Open(flow, new Dictionary<string, string>(StringComparer.Ordinal), context.Loggers);
        IReadOnlyList<ScopeParameterValues> parameters;
        if (payload.Argument(ParameterArgument) is { } parameter && !string.IsNullOrWhiteSpace(parameter))
        {
            var search = payload.Argument(SearchArgument) is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;
            parameters = [await source.ColumnValuesAsync(parameter, LookedUpIn(flow, parameter), search, MaxValues, ct).ConfigureAwait(false)];
        }
        else
        {
            parameters = await source.ScopeValuesAsync(MaxValues, ct).ConfigureAwait(false);
        }

        return new ScopeValuesAnswer(flow.Label, flow.Interface, flow.Source.Record.Object, parameters, context.Time.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// The column of this interface's record table <paramref name="parameter"/>'s values are looked up in: the one its
    /// <c>lookup</c> names, else the one this interface's scope binds it to.
    /// </summary>
    /// <exception cref="SqlFlowException">The flow declares no such parameter, or this interface looks none of its values up.</exception>
    public static string LookedUpIn(FlowDefinition flow, string parameter)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameter);
        if (!flow.Parameters.TryGetValue(parameter, out var declared))
        {
            throw new SqlFlowException($"Flow '{flow.Label}' declares no parameter '{parameter}'.");
        }

        if (declared.Lookup is { } lookup && (lookup.Interface is null || string.Equals(lookup.Interface, flow.Interface, StringComparison.Ordinal)))
        {
            return lookup.Column;
        }

        foreach (var (column, bound) in flow.Source.Record.Scope)
        {
            if (string.Equals(bound, parameter, StringComparison.Ordinal))
            {
                return column;
            }
        }

        throw new SqlFlowException(
            $"Flow '{flow.Label}' looks up no values for parameter '{parameter}': its record table's scope does not bind it, and it names no lookup here.");
    }
}

/// <summary>What <see cref="ScopeValuesOperation"/> answers: the flow, its record table, and the values of each parameter listed.</summary>
public sealed record ScopeValuesAnswer(string Flow, string? Interface, string RecordObject, IReadOnlyList<ScopeParameterValues> Parameters, DateTime ReadUtc);
