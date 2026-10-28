using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// The ids a record's <c>$link</c> nodes write (osdu/docs/reference/flow/enrich-files.md): for each enrichment that registers
/// datasets, the id of the dataset each of its rows registers, made from the record's own id, the enrichment's name and, for
/// one taking many rows, the row's key, so the record links them before anything is sent and on every render alike. An
/// enrichment set aside on its hold links what its last delivery registered instead, which the chain hands over as
/// <c>kept</c>, so the record never links a dataset that was not registered.
/// </summary>
public sealed class EnrichLinks
{
    private readonly SourceRecord _record;
    private readonly IReadOnlyList<ResolvedStep> _steps;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _kept;

    /// <param name="record">The record being rendered, whose rows of each enrichment are read as the child dataset of its name.</param>
    /// <param name="steps">The enrichments of the record's mapping, in the order they render.</param>
    /// <param name="kept">For an enrichment set aside on its hold, the ids its last delivery registered, by its name.</param>
    public EnrichLinks(SourceRecord record, IReadOnlyList<ResolvedStep> steps, IReadOnlyDictionary<string, IReadOnlyList<string>> kept)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(kept);
        _record = record;
        _steps = steps;
        _kept = kept;
    }

    /// <summary>
    /// The ids the datasets of <paramref name="enrichment"/> are registered under for the record whose id is
    /// <paramref name="ownerId"/>, each once, in the order of its rows; none for a record without an id, an enrichment
    /// registering no datasets, or rows whose id cannot be made (the enrichment's own render holds the record for those).
    /// </summary>
    public IReadOnlyList<string> Ids(string enrichment, string? ownerId)
    {
        ArgumentNullException.ThrowIfNull(enrichment);
        if (_kept.TryGetValue(enrichment, out var kept))
        {
            return kept;
        }

        if (ownerId is null || _steps.FirstOrDefault(s => s.Step.Name.Equals(enrichment, StringComparison.OrdinalIgnoreCase)) is not { Records: { } records } step)
        {
            return [];
        }

        // An enrichment reading no rows adds one record for the record.
        IReadOnlyList<SourceRow> rows = step.Step.ReadsRows ? _record.ScopeRows(step.Step.Name) : [SourceRow.Empty];
        if (step.Step.Rows == EnrichRows.One && rows.Count != 1)
        {
            // No row registers nothing; two or more hold the record when the enrichment renders.
            return [];
        }

        var ids = new List<string>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            if (Derive(step.Step, records, rows[i], i, ownerId, out _) is { } id && !ids.Contains(id, StringComparer.Ordinal))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// The id of the dataset one row of an enrichment registers: the record's id with the dataset's entity type and a suffix of
    /// <c>enrich.</c>, the enrichment's name and, for one taking many rows, a hash of the row's key. Null, with why in
    /// <paramref name="problem"/>, for a row whose key column is empty or an id OSDU could not take.
    /// </summary>
    internal static string? Derive(EnrichStep step, ResolvedRecords records, SourceRow row, int index, string ownerId, out string? problem)
    {
        // The suffix holds a dot, which neither the dataset a record's own files go in (files) nor a workflow input's name can,
        // so an enrichment's dataset never takes the id another route derives from the record's.
        problem = null;
        var suffix = EnrichRecords.SuffixPrefix + step.Name;
        if (records.Records.Key.Count > 0)
        {
            var values = records.Records.Key.Select(column => row.GetString(column)?.Trim()).ToList();
            if (values.FindIndex(string.IsNullOrEmpty) is var empty and >= 0)
            {
                problem = $"key column '{records.Records.Key[empty]}' is empty in row {index + 1} of the enrichment's dataset, and each row's record is named by its key";
                return null;
            }

            suffix = $"{EnrichRecords.SuffixPrefix}{step.Name}.{DeliveryKey.Derive(step.Name, values).Value:N}";
        }

        try
        {
            return Engine.Workflows.WorkflowValues.DerivedDatasetId(ownerId, records.EntityType, suffix);
        }
        catch (DeliveryException ex)
        {
            problem = ex.Message;
            return null;
        }
    }
}
