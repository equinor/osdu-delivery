using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Templates;

// The draft of a loaded mapping, for opening it in the builder: each entry, node, assertion, rule on the rows,
// findBy line and modifier the mapping says, as the draft holds it.
public static partial class MappingBuilder
{
    /// <summary>
    /// The draft of a loaded mapping, for opening it in the builder: everything the document says, so writing the draft back
    /// gives the same mapping. Its lookups, the nodes reading them, the rows a <c>$findAll</c> reads, the items of a list of
    /// values, and the object items of a list of objects with their properties all have their place in the draft.
    /// </summary>
    public static MappingDraft FromDefinition(MappingDefinition mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        // The builder lays out a whole record: its key, its id, its access list and legal block. An enrichment mapping has none of
        // them, and its $base nodes read the record it renders into, which no draft input stands for. What a mapping adds to its
        // records (enrich:, and the $link nodes linking what it adds) has no draft input either, and a draft saved without them
        // would drop them.
        if (mapping.Fragment)
        {
            throw new FlowValidationException(
                $"Mapping {mapping.Reference} is an enrichment mapping (enrichment: true), what an enrichment of another mapping renders for its records, and the mapping builder lays out whole records; edit its file.");
        }

        if (mapping.Enrich.Count > 0 || mapping.LinkNames().Count > 0)
        {
            throw new FlowValidationException(
                $"Mapping {mapping.Reference} declares enrichments (enrich:), which the mapping builder does not lay out, so a draft saved from it would drop them; edit the mapping's file.");
        }

        return new MappingDraft
        {
            Name = mapping.Name,
            Version = mapping.Version,
            TemplateKind = mapping.Template.Kind,
            TemplateVersion = mapping.Template.Version,
            Description = mapping.Description,
            System = mapping.Dataset.System,
            Key = mapping.Dataset.Key,
            Label = mapping.Dataset.Label,
            Identity = mapping.Dataset.Identity,
            IdFrom = mapping.Dataset.IdFrom == MappingIdSource.Key ? MappingMapper.IdFromKey : null,
            Rules = mapping.Dataset.Assertions.Select(DraftRule).ToList(),
            Parameters = mapping.Parameters.Select(kv => new MappingDraftParameter(kv.Key, kv.Value.Required, kv.Value.Default, kv.Value.Description)).ToList(),
            RestrictedAccess = mapping.RestrictedAccess,
            Searches = mapping.Searches.Values
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .Select(s => new MappingDraftSearch(s.Name, s.Kind, s.Schema.Kind, s.Schema.Version, s.Description))
                .ToList(),
            Lookups = mapping.Lookups.Values
                .OrderBy(l => l.Name, StringComparer.Ordinal)
                .Select(l => new MappingDraftLookup
                {
                    Name = l.Name,
                    CacheType = l.CacheType,
                    FindBy = l.FindBy.Select(DraftFind).ToList(),
                    Modifiers = l.Modifiers.Select(DraftModifier).ToList(),
                    IgnoreSeparators = l.IgnoreSeparators,
                    Description = l.Description,
                })
                .ToList(),
            Entries = mapping.Entries.Select(Draft).ToList(),
        };
    }

    private static MappingDraftEntry Draft(MappingEntry entry)
    {
        if (entry.IsCoalesce)
        {
            // The node's settings stay with the entry, and each alternative is drafted as a value of its own without them.
            return new MappingDraftEntry
            {
                Target = entry.Target.Text,
                Input = MappingDraftInput.Coalesce,
                Alternatives = entry.ValueNodes
                    .Select(node => DraftNode(node with { Alternatives = [], AppliesWhen = null, Required = true, RestrictWhenMissing = false, Description = null, Assertions = [] }))
                    .ToList(),
                When = entry.AppliesWhen?.Text,
                Required = entry.Required,
                RestrictWhenMissing = entry.RestrictWhenMissing,
                Description = entry.Description,
                Assertions = entry.Assertions.Select(DraftAssertion).ToList(),
            };
        }

        return DraftNode(entry);
    }

    private static MappingDraftEntry DraftNode(MappingEntry entry)
    {
        if (entry.IsList)
        {
            // A list is its items alone: each a literal, a node with its own condition, required flag and description, or an
            // object whose properties are entries of their own.
            return new MappingDraftEntry
            {
                Target = entry.Target.Text,
                Input = MappingDraftInput.List,
                Items = entry.Parts.Select(DraftNode).ToList(),
            };
        }

        if (entry.IsObject)
        {
            return new MappingDraftEntry
            {
                Target = entry.Target.Text,
                Input = MappingDraftInput.Group,
                Properties = entry.Properties.Select(Draft).ToList(),
            };
        }

        var source = entry.Source;
        if (source?.Lookup is { } lookup)
        {
            // The node carries its lookup's findBy lines and modifiers as the loader resolved them; the draft keeps them on the lookup.
            return new MappingDraftEntry
            {
                Target = entry.Target.Text,
                Input = MappingDraftInput.Lookup,
                Lookup = lookup,
                CacheField = source.CacheField,
                When = entry.AppliesWhen?.Text,
                Required = entry.Required,
                RestrictWhenMissing = entry.RestrictWhenMissing,
                Description = entry.Description,
                Assertions = entry.Assertions.Select(DraftAssertion).ToList(),
            };
        }

        return new MappingDraftEntry
        {
            Target = entry.Target.Text,
            Input = source is null
                ? MappingDraftInput.Static
                : source.Kind switch
                {
                    MappingSourceKind.DatasetColumn => MappingDraftInput.Dataset,
                    MappingSourceKind.DatasetRows => MappingDraftInput.Repeat,
                    MappingSourceKind.Search => MappingDraftInput.Search,
                    MappingSourceKind.Expression => MappingDraftInput.Expression,
                    MappingSourceKind.Parent => MappingDraftInput.Parent,
                    MappingSourceKind.Child => MappingDraftInput.Child,
                    _ => MappingDraftInput.Cache,
                },
            Expression = source?.Expression?.Text,
            ParentPath = source?.ParentPath,
            ChildKind = source?.ChildKind,
            Column = source?.Column is { } column ? ColumnText(column) : null,
            Child = source?.Child,
            CacheType = source?.CacheType,
            CacheField = source?.CacheField,
            FindBy = entry.FindBy.Select(DraftFind).ToList(),
            FindAll = entry.FindAll is { } all
                ? new MappingDraftFindAll
                {
                    Field = all.Field,
                    Column = all.Operand.Column is { } operand ? ColumnText(operand) : null,
                    Literal = all.Operand.Literal,
                    Lookup = all.Operand.Lookup is { } read ? $"{read.Name}.{all.Operand.LookupPath}" : null,
                    Empty = all.Empty,
                    Contains = all.Contains.Select(line => new MappingDraftFindAllContains { Field = line.Field, Part = line.Part }).ToList(),
                }
                : null,
            Modifiers = entry.Modifiers.Select(DraftModifier).ToList(),
            When = entry.AppliesWhen?.Text,
            Where = entry.RowFilter?.Text,
            Required = entry.Required,
            RestrictWhenMissing = entry.RestrictWhenMissing,
            IgnoreSeparators = entry.IgnoreSeparators,
            Unverified = entry.Unverified,
            Static = entry.Static?.ToJsonString(),
            Description = entry.Description,
            Assertions = entry.Assertions.Select(DraftAssertion).ToList(),
        };
    }

    /// <summary>An assertion as the draft holds it: each condition written back as the document writes it.</summary>
    private static MappingDraftAssertion DraftAssertion(NodeAssertion assertion) => new()
    {
        Operator = AssertionText.Of(assertion.Condition.Operator),
        Operand = ConditionReader.Written(assertion.Condition),
        Stage = AssertionWords.Of(assertion.Stage),
        OnFail = AssertionWords.Of(assertion.OnFail),
        AnyValue = assertion.AnyValue,
        IgnoreCase = assertion.Condition.IgnoreCase,
        Tolerance = assertion.Condition.Tolerance,
        Where = assertion.Where
            .Select(filter => new MappingDraftAssertionFilter(
                filter.Field is null ? MappingMapper.ColumnSetting : MappingMapper.FieldSetting,
                filter.Field ?? ColumnText(filter.Column!),
                AssertionText.Of(filter.Condition.Operator),
                ConditionReader.Written(filter.Condition),
                filter.Condition.IgnoreCase,
                filter.Condition.Tolerance))
            .ToList(),
        Name = assertion.Named ? assertion.Label : null,
        Description = assertion.Description,
    };

    /// <summary>A rule on the rows as the draft holds it: its condition and settings written back as the document writes them.</summary>
    private static MappingDraftRowRule DraftRule(DatasetAssertion rule) => new()
    {
        Kind = rule.Kind switch
        {
            DatasetAssertionKind.Count => MappingDraftRowRule.CountKind,
            DatasetAssertionKind.Unique => MappingDraftRowRule.UniqueKind,
            _ => MappingDraftRowRule.ValueKind,
        },
        Per = rule.Per == DatasetAssertionPer.Run ? MappingMapper.PerRun : MappingMapper.PerRecord,
        Rows = rule.Rows,
        Column = rule.Column,
        Operator = rule.Condition is { } condition ? AssertionText.Of(condition.Operator) : "equals",
        Operand = rule.Condition is { } written ? ConditionReader.Written(written) : string.Empty,
        Count = rule.Count is { } count ? CountText(count) : null,
        Unique = rule.Columns,
        AnyRow = rule.AnyRow,
        IgnoreCase = rule.Condition?.IgnoreCase ?? false,
        Tolerance = rule.Condition?.Tolerance,
        OnFail = AssertionWords.Of(rule.OnFail),
        Where = rule.Where
            .Select(filter => new MappingDraftAssertionFilter(
                MappingMapper.ColumnSetting,
                ColumnText(filter.Column!),
                AssertionText.Of(filter.Condition.Operator),
                ConditionReader.Written(filter.Condition),
                filter.Condition.IgnoreCase,
                filter.Condition.Tolerance))
            .ToList(),
        Name = rule.Named ? rule.Label : null,
        Description = rule.Description,
    };

    /// <summary>A count's comparison as <c>count:</c> writes it: a number meaning equals, or a map of its terms (<c>{ atLeast: 2, atMost: 5 }</c>).</summary>
    private static string CountText(Comparison count)
    {
        if (count.Terms is [{ Operator: ComparisonOperator.EqualTo } only])
        {
            return only.Value.Text;
        }

        return "{ " + string.Join(", ", count.Terms.Select(term => term.Operator == ComparisonOperator.Between
            ? $"between: [{term.Value.Text}, {term.Upper!.Text}]"
            : $"{AssertionText.Of(term.Operator)}: {term.Value.Text}")) + " }";
    }

    private static MappingDraftFind DraftFind(FindBy find) => new(
        find.Field,
        find.Column is null ? null : ColumnText(find.Column),
        find.Literal,
        find.IsComposite ? find.And.Select(DraftFind).ToList() : null);

    private static MappingDraftModifier DraftModifier(Modifier modifier) => new(
        modifier.Kind.ToString().ToLowerInvariant(),
        modifier.Separator,
        modifier.Part,
        modifier.Replacements.Count == 0 ? null : modifier.Replacements.Select(kv => new MappingDraftReplacement(kv.Key, kv.Value)).ToList(),
        modifier.Kind switch
        {
            ModifierKind.Id => modifier.Id!.Text,
            ModifierKind.Ref => modifier.EntityType,
            _ => modifier.Text,
        },
        modifier.DecimalSeparator,
        modifier.GroupSeparator,
        modifier.Kind == ModifierKind.Replace ? FallbackKind(modifier.Otherwise) : null,
        modifier.Kind == ModifierKind.Replace && modifier.Otherwise.Kind == ReplaceFallbackKind.Text ? modifier.Otherwise.Text : null,
        modifier.Table?.CacheType,
        modifier.Table?.Match,
        modifier.Table?.Field);

    private static string ColumnText(DatasetColumn column) => column.Child is null ? column.Column : column.Child + "." + column.Column;
}
