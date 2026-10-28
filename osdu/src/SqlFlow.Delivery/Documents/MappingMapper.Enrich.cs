using System.Globalization;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Documents;

// The enrich: block of a mapping (osdu/docs/reference/flow/enrich.md): what the mapping adds to every record it renders,
// each enrichment from rows of its own, and the $link nodes that write the ids of the records an enrichment registers.
// Everything that needs no other document is checked here; what needs the enrichment's own mapping or the flow's bindings
// is checked when a run binds the flow to its mapping (EnrichBinding).
internal static partial class MappingMapper
{
    /// <summary>The words <c>enrich.&lt;name&gt;.rows</c> takes.</summary>
    internal const string RowsOne = "one";

    /// <inheritdoc cref="RowsOne"/>
    internal const string RowsMany = "many";

    /// <summary>The words <c>enrich.&lt;name&gt;.onHold</c> takes.</summary>
    internal const string HoldRecord = "record";

    /// <inheritdoc cref="HoldRecord"/>
    internal const string HoldStep = "step";

    /// <summary>What an enrichment is named by: letters, digits and <c>_</c>, not starting with a digit.</summary>
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnrichNamePattern();

    /// <summary>
    /// The mapping's enrichments (<c>enrich:</c>), in the order they render: the order the document lists them in, each moved
    /// after every one its <c>after:</c> names. Empty for a mapping that declares none. Names, rows, keys, holds, references
    /// and cycles are refused here.
    /// </summary>
    private static IReadOnlyList<MappingEnrichment> MapEnrich(OrderedDictionary<string, MappingEnrichYaml?>? declared, string source)
    {
        if (declared is null)
        {
            return [];
        }

        if (declared.Count == 0)
        {
            throw new FlowValidationException($"{source}: enrich lists no enrichment. Declare what is added to each record under it, or remove it.");
        }

        if (declared.Count > EnrichStep.MaxSteps)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: enrich lists {declared.Count} enrichments; a mapping declares at most {EnrichStep.MaxSteps}. Fold enrichments that read the same rows into one."));
        }

        var enrichments = new List<MappingEnrichment>(declared.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, written) in declared)
        {
            var name = key?.Trim() ?? string.Empty;
            var at = $"enrich.{name}";
            if (!EnrichNamePattern().IsMatch(name) || name.Length > EnrichStep.MaxNameLength || name.Equals(SourceDatasets.Record, StringComparison.OrdinalIgnoreCase))
            {
                throw new FlowValidationException(
                    string.Create(CultureInfo.InvariantCulture, $"{source}: enrich names an enrichment '{name}'; an enrichment is named by letters, digits and '_', not starting with a digit, at most {EnrichStep.MaxNameLength} characters, since its rows are the child dataset of that name, and '{SourceDatasets.Record}' names the record table itself."));
            }

            if (!names.Add(name))
            {
                throw new FlowValidationException($"{source}: enrich declares '{name}' more than once (names are compared ignoring case, as the child datasets named after them are).");
            }

            var y = written ?? throw new FlowValidationException(
                $"{source}: {at} is empty; name the enrichment mapping it renders under {at}.mapping, or declare bulk: true for one whose row names the record's bulk data.");

            var mapping = string.IsNullOrWhiteSpace(y.Mapping) ? null : y.Mapping.Trim();
            if (mapping is not null && !mapping.Contains('@', StringComparison.Ordinal))
            {
                throw new FlowValidationException($"{source}: {at}.mapping '{mapping}' must be pinned as 'Name@version'; floating references are not allowed.");
            }

            var bulk = y.Bulk ?? false;
            if (mapping is null && !bulk)
            {
                throw new FlowValidationException(
                    $"{source}: {at} names no mapping and no bulk data, so it adds nothing to the record; name the enrichment mapping it renders under {at}.mapping, or declare bulk: true for one whose row names the record's bulk data.");
            }

            EnrichRows? rows = y.Rows?.Trim() switch
            {
                null => null,
                RowsOne => EnrichRows.One,
                RowsMany => EnrichRows.Many,
                var other => throw new FlowValidationException(
                    $"{source}: {at}.rows is '{other}'; write {RowsOne} for an enrichment that takes at most one row of the child dataset {name} per record, read as its mapping's own row, or {RowsMany} for one whose mapping reads them with $forEach: {name}. Leave it out for an enrichment that reads no rows."),
            };

            if (bulk && rows != EnrichRows.One)
            {
                throw new FlowValidationException(rows is null
                    ? $"{source}: {at}.bulk names the record's bulk data by the enrichment's row, and it reads no rows; declare rows: {RowsOne}, and bind the child dataset {name} with its files under source.datasets in the flow."
                    : $"{source}: {at}.bulk is the record's bulk data, of which a record has one, and the enrichment takes many rows (rows: {RowsMany}); one supplying bulk data takes one row per record.");
            }

            var keyColumns = (y.Key ?? []).Select((column, i) => KeyColumn(column, $"{at}.key[{i}]", source)).ToList();
            if (keyColumns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keyColumns.Count)
            {
                throw new FlowValidationException($"{source}: {at}.key names a column more than once.");
            }

            if (keyColumns.Count > 0 && rows != EnrichRows.Many)
            {
                throw new FlowValidationException(
                    $"{source}: {at}.key tells the datasets of an enrichment's rows apart, and it takes {(rows is null ? "no rows" : $"one row per record (rows: {RowsOne}), whose one dataset its name names")}; remove {at}.key, or declare rows: {RowsMany}.");
            }

            var onHold = y.OnHold?.Trim() switch
            {
                null => EnrichHold.Record,
                HoldRecord => EnrichHold.Record,
                HoldStep => EnrichHold.Step,
                var other => throw new FlowValidationException(
                    $"{source}: {at}.onHold is '{other}'; write {HoldRecord} for an enrichment whose hold holds the record (the default), or {HoldStep} for one set aside while the record goes ahead with what it added to its last delivery."),
            };

            enrichments.Add(new MappingEnrichment
            {
                Name = name,
                Description = string.IsNullOrWhiteSpace(y.Description) ? null : y.Description.Trim(),
                Rows = rows,
                Mapping = mapping,
                Key = keyColumns,
                After = (y.After ?? []).Select(a => a?.Trim() ?? string.Empty).ToList(),
                OnHold = onHold,
                Bulk = bulk,
            });
        }

        return OrderEnrichments(enrichments, source);
    }

    /// <summary>
    /// The enrichments in the order they render: the order the document lists them in, each moved after every one its
    /// <c>after:</c> names. A name no enrichment has, an enrichment naming itself or one twice, and enrichments whose
    /// <c>after:</c> wait for each other are refused.
    /// </summary>
    private static List<MappingEnrichment> OrderEnrichments(List<MappingEnrichment> enrichments, string source)
    {
        var byName = enrichments.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var enrichment in enrichments)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in enrichment.After)
            {
                if (!byName.ContainsKey(name))
                {
                    throw new FlowValidationException($"{source}: enrich.{enrichment.Name}.after names '{name}', which is no enrichment of the mapping.");
                }

                if (name.Equals(enrichment.Name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new FlowValidationException($"{source}: enrich.{enrichment.Name}.after names the enrichment itself; an enrichment renders after other enrichments.");
                }

                if (!seen.Add(name))
                {
                    throw new FlowValidationException($"{source}: enrich.{enrichment.Name}.after names '{name}' more than once.");
                }
            }
        }

        // A stable topological order: each pass takes, in document order, the first enrichment whose after: ones are all placed.
        var ordered = new List<MappingEnrichment>(enrichments.Count);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (ordered.Count < enrichments.Count)
        {
            var next = enrichments.FirstOrDefault(e => !placed.Contains(e.Name) && e.After.All(placed.Contains));
            if (next is null)
            {
                var waiting = enrichments.Where(e => !placed.Contains(e.Name)).Select(e => e.Name).ToList();
                throw new FlowValidationException(
                    $"{source}: the enrichments {string.Join(", ", waiting)} wait for each other through after:, so none of them could render first.");
            }

            ordered.Add(next with { After = next.After.Select(name => byName[name].Name).ToList() });
            placed.Add(next.Name);
        }

        return ordered;
    }

    /// <summary>
    /// Every <c>$link</c> names an enrichment of this mapping that renders with a mapping, whose kind (another than the record's)
    /// is checked when a run binds the flow. An enrichment mapping links nothing of its own: the ids it would name are the
    /// keying mapping's enrichments'.
    /// </summary>
    private static void ValidateLinks(IReadOnlyList<MappingEntry> entries, IReadOnlyList<MappingEnrichment> enrich, bool fragment, string source)
    {
        foreach (var node in entries.SelectMany(e => e.ValueNodes).Where(n => n.Source?.Kind == MappingSourceKind.Link))
        {
            var name = node.Source!.LinkName!;
            if (fragment)
            {
                throw new FlowValidationException(
                    $"{source}: {node.Where}: {LinkKey} writes the ids of the records an enrichment of the record's mapping registers, and an enrichment mapping declares no enrichments; write the link in the mapping that keys the record.");
            }

            var enrichment = enrich.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new FlowValidationException(enrich.Count == 0
                    ? $"{source}: {node.Where}: {LinkKey} names '{name}', and the mapping declares no enrichment; declare it under enrich.{name}, with the rows and the mapping of the records it registers."
                    : $"{source}: {node.Where}: {LinkKey} names '{name}', which is no enrichment of the mapping; it declares {string.Join(", ", enrich.Select(e => e.Name))}.");

            if (enrichment.Mapping is null)
            {
                throw new FlowValidationException(
                    $"{source}: {node.Where}: {LinkKey} names enrich.{enrichment.Name}, which names the record's bulk data alone and adds no record: an enrichment adds a record for each of its rows, rendered by its mapping (mapping:) of another kind than the record's.");
            }
        }
    }
}
