using System.Globalization;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// What a run of each of the module's flow kinds may be given, as its document declares it (<see cref="FlowRunInputs"/>,
/// osdu/docs/reference/flow/parameters.md): the parameters, with the values a delivery flow looks up for them; the
/// <c>partition</c> run value of a flow that works in partitions; and a delivery flow's run setting, the first records of
/// its scope. A run form, a schedule's preview and the kind's own resolution of a run all read the parameters the same way
/// (<see cref="FlowParameters"/>).
/// </summary>
public static class KindRunInputs
{
    /// <summary>
    /// The operations of a delivery run that read the ingestion tables (DeliveryExecutor.ReadsSource): the ones that resolve
    /// the flow's parameters, which scope the rows they read, and read the first records of their scope.
    /// </summary>
    public static IReadOnlyList<string> ReadingOperations { get; } =
        [DeliveryOperations.Deliver, DeliveryOperations.Plan, DeliveryOperations.Intake, DeliveryOperations.Replan];

    /// <summary>
    /// A delivery flow's: its parameters, those its scopes bind taking several values and looked up in the column they bind,
    /// one naming a <c>lookup</c> looked up there, resolved by the operations that read the ingestion tables; the partition
    /// when it works in partitions; and <c>maxRecords</c>.
    /// </summary>
    public static FlowRunInputs Delivery(SourceDefinition source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var scoped = source.Interfaces.SelectMany(i => i.Source.Record.Scope.Values).Distinct(StringComparer.Ordinal).ToList();
        var inputs = FlowParameters.RunInputs(source.Parameters, scoped, (name, parameter) => DeliveryLookup(source, name, parameter));
        return inputs with
        {
            ParameterOperations = ReadingOperations,
            KindValues = source.Partitioned ? [Partition("delivers in")] : [],
            Settings = [FirstRecords(source)],
        };
    }

    /// <summary>
    /// The inputs of a flow of any other kind: its parameters, each taking one value, and the partition when the flow works
    /// in partitions, which a run <paramref name="acts"/> ("tests", "builds in"). <paramref name="every"/> says what naming
    /// <c>*</c> does, for a kind whose run may act in every partition; null when it acts in one.
    /// </summary>
    public static FlowRunInputs Of(IReadOnlyDictionary<string, FlowParameter> parameters, bool partitioned, string acts, string? every = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(acts);
        return FlowParameters.RunInputs(parameters) with { KindValues = partitioned ? [Partition(acts, every)] : [] };
    }

    /// <summary>Where a delivery flow's run form looks a parameter's values up, in words; null when it looks none up.</summary>
    private static string? DeliveryLookup(SourceDefinition source, string name, FlowParameter parameter)
    {
        if (LookupInterface(source, name) is not { } flow)
        {
            return null;
        }

        var column = ScopeValuesOperation.LookedUpIn(flow, name);
        return parameter.Lookup is null
            ? $"the values of column {column} of {flow.Source.Record.Object}, which {KeyPaths.Of(flow).Record}.scope binds it to"
            : $"the values of column {column} of {flow.Source.Record.Object}";
    }

    /// <summary>
    /// The interface whose record table a delivery flow's run form reads a parameter's values from: the one its <c>lookup</c>
    /// names, or the first; for a parameter without one, the first whose scope binds it. Null for a parameter the flow does
    /// not declare, or whose values are not looked up. The column is <see cref="ScopeValuesOperation.LookedUpIn"/>'s.
    /// </summary>
    public static FlowDefinition? LookupInterface(SourceDefinition source, string name)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!source.Parameters.TryGetValue(name, out var parameter))
        {
            return null;
        }

        if (parameter.Lookup is { } lookup)
        {
            return lookup.Interface is { } named ? source.Interface(named) : source.First;
        }

        return source.Interfaces.FirstOrDefault(i => i.Source.Record.Scope.Values.Contains(name, StringComparer.Ordinal));
    }

    /// <summary>The <c>partition</c> run value, for a flow whose runs <paramref name="acts"/> in one.</summary>
    private static KindRunValue Partition(string acts, string? every = null)
        => new(
            PartitionNames.RunValue,
            $"The partition the run {acts}: one the flow names under partitions, or, when it names none, one registered with the catalog. "
            + $"A run that names none {acts} the registry's default partition, or the flow's only one."
            + (every is null ? string.Empty : $" {every}"));

    /// <summary>
    /// The first records of a delivery's scope a run reads (<c>maxRecords</c>), with the value the document gives it: the
    /// one every interface writes, or each interface's when they differ.
    /// </summary>
    private static RunSetting FirstRecords(SourceDefinition source)
    {
        var limits = source.Interfaces.Select(i => i.Source.Record.MaxRecords).ToList();
        string? documentKey;
        string? documentValue;
        if (!source.DeclaresInterfaces)
        {
            documentKey = "source.record.maxRecords";
            documentValue = Shown(limits[0]);
        }
        else if (limits.Distinct().Count() == 1)
        {
            documentKey = "record.maxRecords of every interface";
            documentValue = Shown(limits[0]);
        }
        else
        {
            documentKey = "record.maxRecords of each interface";
            documentValue = string.Join(", ", source.Interfaces.Select(i => $"{i.Interface} {Shown(i.Source.Record.MaxRecords) ?? "none"}"));
        }

        return new RunSetting
        {
            Key = DeliveryRunPayload.MaxRecordsProperty,
            Label = "Records at most",
            Input = RunSettingInput.WholeNumber,
            Help = string.Create(
                CultureInfo.InvariantCulture,
                $"How many of the first records of the scope the run reads, in the order a read pages in: 1 to {FlowSourceTable.MaxRecordsCeiling:N0}, or 0 to read the scope whole."),
            DocumentKey = documentKey,
            DocumentValue = documentValue,
            WhenUnset = "the run reads the scope whole",
            Minimum = 0,
            Maximum = FlowSourceTable.MaxRecordsCeiling,
            Operations = ReadingOperations,
        };

        static string? Shown(int? limit) => limit?.ToString(CultureInfo.InvariantCulture);
    }
}
