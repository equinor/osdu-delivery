// The YAML shapes of a mapping document (osdu/docs/reference/flow/mapping.md): its header, template, parameters,
// searches, datasets and enrichments.
// As in YamlModels.cs, every property must be listed: unknown keys are rejected.
#pragma warning disable CA1812, CA2227, CA1002, CA1034, CA1720

namespace SqlFlow.Delivery.Documents;

/// <summary>One enrichment of a mapping (osdu/docs/reference/flow/enrich.md): its rows, its mapping and what its rows' files become.</summary>
internal sealed class MappingEnrichYaml
{
    public string? Description { get; set; }

    /// <summary>How many rows of the child dataset named after it one record takes: <c>one</c> or <c>many</c>; none when left out.</summary>
    public string? Rows { get; set; }

    public string? Mapping { get; set; }

    /// <summary>The columns of a row that tell its dataset from the enrichment's other rows'.</summary>
    public List<string>? Key { get; set; }

    public List<string>? After { get; set; }

    /// <summary>What its hold does: <c>record</c> (the default) holds the record, <c>step</c> sets the enrichment aside.</summary>
    public string? OnHold { get; set; }

    /// <summary>True when the files its one row names are the record's DDMS bulk data.</summary>
    public bool? Bulk { get; set; }
}

internal sealed class MappingYaml
{
    public string? DocumentType { get; set; }

    public string? Name { get; set; }

    public string? Version { get; set; }

    public MappingTemplateYaml? Template { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// True for an enrichment mapping (osdu/docs/reference/flow/enrich.md): what an enrichment of another mapping renders for
    /// each of its records, never a record keyed of its own, so it declares no <c>dataset</c> and no access or legal block.
    /// </summary>
    public bool? Enrichment { get; set; }

    /// <summary>What the header said before <c>enrichment: true</c>: read only to refuse it with the word to write.</summary>
    public object? Fragment { get; set; }

    public MappingDatasetYaml? Dataset { get; set; }

    /// <summary>What the mapping adds to every record it renders, by the enrichment's name, in document order; null when none.</summary>
    public OrderedDictionary<string, MappingEnrichYaml?>? Enrich { get; set; }

    public Dictionary<string, MappingParameterYaml>? Parameters { get; set; }

    /// <summary>What each <c>search: &lt;name&gt;</c> node searches, by the name nodes write it as.</summary>
    public Dictionary<string, MappingSearchYaml>? Searches { get; set; }

    /// <summary>
    /// The records a row is matched to once and read wherever the record needs them, by the name <c>$lookup</c> reads them
    /// as. Read as the author wrote it and parsed node by node, like the record tree.
    /// </summary>
    public Dictionary<string, object?>? Lookups { get; set; }

    /// <summary>
    /// The access restriction a record goes out under while a value a node marks <c>$restrictWhenMissing</c> is missing
    /// (osdu/docs/reference/flow/mapping-restricted-access.md): its name as the partition registers it, written as text that
    /// may read a parameter (<c>{$param.restriction}</c>).
    /// </summary>
    public string? RestrictedAccess { get; set; }

    /// <summary>
    /// The record the mapping renders, laid out as the record is: <c>acl</c>, <c>legal</c>, <c>tags</c>, <c>data</c> and
    /// the properties below them, each a literal, a value node or a <c>forEach</c> array
    /// (osdu/docs/reference/flow/mapping.md). Read as the author wrote it and parsed node by node, since its keys are
    /// the template's property names.
    /// </summary>
    public Dictionary<string, object?>? Record { get; set; }
}

/// <summary>
/// One record set a mapping resolves against by searching the platform, rather than out of the partition's cache.
/// A cache is for a closed vocabulary a capture can hold whole; this is for the records a delivery refers to, which
/// are business data and grow without bound.
/// </summary>
internal sealed class MappingSearchYaml
{
    /// <summary>The OSDU kind searched, as a flow writes a kind (<c>osdu:wks:master-data--Wellbore:*</c>).</summary>
    public string? Kind { get; set; }

    /// <summary>The template whose schema says how the kind's properties are indexed, named like the mapping's own.</summary>
    public MappingTemplateYaml? Schema { get; set; }

    /// <summary>What this set is, shown wherever the mapping is listed.</summary>
    public string? Description { get; set; }
}

internal sealed class MappingTemplateYaml
{
    public string? Kind { get; set; }

    public string? Version { get; set; }
}

internal sealed class MappingParameterYaml
{
    public bool Required { get; set; }

    public string? Default { get; set; }

    public string? Description { get; set; }
}

internal sealed class MappingDatasetYaml
{
    public string? System { get; set; }

    public List<string>? Key { get; set; }

    public string? Label { get; set; }

    public List<string>? Identity { get; set; }

    /// <summary>What the OSDU id's unique segment is made from: <c>deliveryKey</c> (the default) or <c>key</c>.</summary>
    public string? IdFrom { get; set; }

    /// <summary>The rules on the rows the mapping renders from (<c>dataset.assert</c>), each a map read by the mapper.</summary>
    public List<object>? Assert { get; set; }
}
