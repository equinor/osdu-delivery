using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>The rows a delivery flow stages, opened for an assertion to account for: the flow, the mapping its ids are made by, and its record table.</summary>
/// <param name="Flow">The delivery flow, bound to the run's partition and the assertion's interface.</param>
/// <param name="Mapping">The mapping the flow renders with: the source system its delivery keys are derived under and how its OSDU ids are made.</param>
/// <param name="Source">The flow's record table, read under the assertion's values, as its delivery reads it.</param>
public sealed record StagedDelivery(FlowDefinition Flow, MappingDefinition Mapping, IIngestionSource Source);

/// <summary>Opens the rows a staged delivered assertion accounts for (osdu/docs/reference/flow/staged-accounting.md).</summary>
public interface IStagedDeliveries
{
    /// <exception cref="DeliveryException">The flow cannot be found, bound or read under the assertion's values.</exception>
    Task<StagedDelivery> OpenAsync(DeliveredAssertion assertion, string at, CancellationToken ct);
}

/// <summary>
/// Opens a delivery flow's staged rows as a run of an assertion flow reads them: the flow found beside the assertion flow, as a
/// removal finds the flow it names (<see cref="DeliveryFlowDocuments"/>), bound to the partition the run tests and the
/// assertion's interface; its parameters given the assertion's values (each <c>{name}</c> token filled from this flow's values
/// and <c>{partition}</c> from the run's), then its defaults; and its record table opened through its own connection.
/// </summary>
public sealed class EngineStagedDeliveries : IStagedDeliveries
{
    private readonly EngineContext _context;
    private readonly AssertionFlowDefinition _flow;
    private readonly IReadOnlyDictionary<string, string> _values;
    private readonly string _partition;

    /// <param name="context">The run's engine context.</param>
    /// <param name="flow">The assertion flow, whose file the delivery flow is found beside.</param>
    /// <param name="values">The assertion flow's values, with the partition the run tests.</param>
    /// <param name="partition">The partition the run tests.</param>
    public EngineStagedDeliveries(EngineContext context, AssertionFlowDefinition flow, IReadOnlyDictionary<string, string> values, string partition)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        _context = context;
        _flow = flow;
        _values = values;
        _partition = partition;
    }

    public async Task<StagedDelivery> OpenAsync(DeliveredAssertion assertion, string at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        ArgumentException.ThrowIfNullOrWhiteSpace(at);
        var (delivery, _) = await DeliveryFlowDocuments.ResolveAsync(
            _context,
            new DeliveryFlowReference
            {
                Flow = assertion.Flow,
                Interface = assertion.Interface,
                Origin = _flow.SourcePath,
                Owner = $"Assertion flow '{_flow.Name}'",
                At = at + ".delivered",
                InterfaceAt = at + ".interface",
                Namer = "a test",
                NamerFlow = "assertion flow",
            },
            _partition,
            ct).ConfigureAwait(false);

        var values = DeliveryValues(assertion, delivery, at);
        foreach (var (column, parameter) in delivery.Source.Record.Scope)
        {
            if (!values.ContainsKey(parameter))
            {
                throw new DeliveryException(
                    $"{at}.values gives delivery flow '{delivery.Label}' no value for '{parameter}', which its record scope binds column '{column}' to; the staged rows are read under one scope, so name it.");
            }
        }

        MappingDefinition mapping;
        try
        {
            mapping = new MappingCatalog(DeliveryLayout.Resolve(delivery).MappingsDirectory, _context.Documents).Load(delivery.Render.Mapping);
        }
        catch (FlowValidationException ex)
        {
            throw new DeliveryException($"{at}: the mapping {delivery.Render.Mapping} delivery flow '{delivery.Label}' renders with cannot be read, so the delivery keys of its rows are unknown: {ex.Message}", ex);
        }

        return new StagedDelivery(delivery, mapping, _context.Sources.Open(delivery, values, _context.Loggers));
    }

    /// <summary>The delivery flow's parameter values the rows are read under: the assertion's, tokens filled, then the flow's defaults.</summary>
    private IReadOnlyDictionary<string, string> DeliveryValues(DeliveredAssertion assertion, FlowDefinition delivery, string at)
    {
        var supplied = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in assertion.Values ?? new Dictionary<string, string>())
        {
            if (!delivery.Parameters.ContainsKey(name))
            {
                throw new DeliveryException(
                    $"{at}.values.{name} names no parameter of delivery flow '{delivery.Label}'; it declares {(delivery.Parameters.Count == 0 ? "none" : string.Join(", ", delivery.Parameters.Keys))}.");
            }

            supplied[name] = RunValueList.IsList(value)
                ? RunValueList.Of(RunValueList.Items(value).Select(item => FlowParameters.Substitute(item, _values)))
                : FlowParameters.Substitute(value, _values);
        }

        try
        {
            return FlowParameters.Resolve(delivery, supplied);
        }
        catch (FlowValidationException ex)
        {
            throw new DeliveryException($"{at}.values: {ex.Message}", ex);
        }
    }
}
