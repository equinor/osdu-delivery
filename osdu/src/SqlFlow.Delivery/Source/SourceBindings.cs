using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Source;

/// <summary>
/// Checks a flow's declarations against the ingestion tables it reads and the mapping it renders with
/// (osdu/docs/reference/flow/delivery.md, What a run checks against the tables): the record key is the mapping's
/// dataset key, the scope, business version and payload columns are columns the record table holds, and every child
/// dataset the mapping repeats is one the flow declares. Everything here is decided before a row is read, so a flow
/// that has drifted from its tables fails the plan with one message naming the flow, the declaration and the table,
/// rather than midway through a delivery.
/// </summary>
public static class SourceBindings
{
    /// <summary>Throws a <see cref="FlowValidationException"/> naming the first disagreement, or returns when everything binds.</summary>
    /// <param name="flow">The flow being planned.</param>
    /// <param name="mapping">The mapping the flow pins.</param>
    /// <param name="header">The opened read, which knows what each table holds.</param>
    /// <param name="where">The flow file, for messages.</param>
    public static void Check(FlowDefinition flow, MappingDefinition mapping, SourceHeader header, string where)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentException.ThrowIfNullOrWhiteSpace(where);
        var keys = KeyPaths.Of(flow);

        var record = header.Columns.TryGetValue(SourceDatasets.Record, out var columns)
            ? columns
            : throw new FlowValidationException($"{where}: the opened source does not describe the record table, so the flow cannot be checked against it.");

        CheckKey(flow, mapping, where);

        foreach (var (column, parameter) in flow.Source.Record.Scope)
        {
            Require(record, column, $"{where}: {keys.Name("source.record.scope")} binds column '{column}' to parameter '{parameter}', which the record table {flow.Source.Record.Object} does not hold");
        }

        if (flow.Source.LastModified is { } lastModified)
        {
            Require(record, lastModified, $"{where}: {keys.Name("source.lastModified")} names column '{lastModified}', which the record table {flow.Source.Record.Object} does not hold");
        }

        CheckPayload(flow, header, record, where);

        foreach (var name in flow.Source.Datasets.Keys)
        {
            if (!header.Columns.ContainsKey(name))
            {
                throw new FlowValidationException($"{where}: {keys.Name("source.datasets")} declares '{name}', which the opened source does not describe.");
            }
        }
    }

    private static void CheckKey(FlowDefinition flow, MappingDefinition mapping, string where)
    {
        var keys = KeyPaths.Of(flow);
        var source = flow.Source.Record.Key;
        var dataset = mapping.Dataset.Key;
        var same = source.Count == dataset.Count
            && !source.Where((column, i) => !column.Equals(dataset[i], StringComparison.OrdinalIgnoreCase)).Any();
        if (!same)
        {
            throw new FlowValidationException(
                $"{where}: {keys.Name("source.record.key")} is [{string.Join(", ", source)}] but mapping '{flow.Render.Mapping}' keys its records by [{string.Join(", ", dataset)}]. "
                + "A record's identity is one thing: the two have to name the same columns in the same order.");
        }
    }

    private static void CheckPayload(FlowDefinition flow, SourceHeader header, IReadOnlySet<string> recordColumns, string where)
    {
        var keys = KeyPaths.Of(flow);
        if (PayloadParts.Streamed(flow) is not { } payloadName)
        {
            return;
        }

        if (!flow.Source.Payloads.TryGetValue(payloadName, out var payload))
        {
            throw new FlowValidationException(
                $"{where}: the {flow.Target.Protocol} protocol streams payload '{payloadName}', which {keys.Name("source.payloads")} does not declare.");
        }

        // A payload a step supplies (osdu/docs/reference/flow/enrich-bulk.md) is named by the step's row, so its columns are the
        // step table's; any other is named by the record's row.
        var record = recordColumns;
        var table = $"the record table {flow.Source.Record.Object}";
        if (payload.Step is { } step)
        {
            table = $"the table of step '{step}', {flow.Source.Datasets[step].Object},";
            record = header.Columns.TryGetValue(step, out var stepColumns)
                ? stepColumns
                : throw new FlowValidationException($"{where}: the opened source does not describe the table of step '{step}', which names the record's bulk data.");
        }

        if (payload.LocationColumn is { } location)
        {
            Require(record, location, $"{where}: payload '{payloadName}' takes each record's folder from column '{location}', which {table} does not hold");
        }

        if (payload.HashColumn is { } hash)
        {
            Require(record, hash, $"{where}: payload '{payloadName}' takes its content hash from column '{hash}', which {table} does not hold");
        }
        else if (flow.Change.PayloadDetect != ChangeDetection.LastModified)
        {
            throw new FlowValidationException(
                $"{where}: payload '{payloadName}' declares no hashColumn, and the flow decides payload changes by content hash. "
                + "Name the column holding it, or take the files' modified times instead with change.payloadDetect: lastModified.");
        }

        if (payload.ChunkCountColumn is { } count)
        {
            Require(record, count, $"{where}: payload '{payloadName}' takes its file count from column '{count}', which {table} does not hold");
        }
    }

    private static void Require(IReadOnlySet<string> columns, string column, string message)
    {
        if (!columns.Contains(column))
        {
            throw new FlowValidationException(message + $". Columns: {string.Join(", ", columns.OrderBy(c => c, StringComparer.Ordinal))}.");
        }
    }
}
