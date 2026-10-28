using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Validation;

/// <summary>
/// The preflight gate (osdu/docs/reference/concepts/preflight.md, The preflight gate). Before any render, and with no
/// OSDU call, checks that the mapping, its pinned template, the cache and the flow's source tables agree. If the
/// combination does not validate, nothing renders.
/// </summary>
public static partial class Preflight
{
    /// <summary>
    /// Runs every check and returns the issues. <paramref name="sourceColumns"/> maps a scope name (<c>record</c> or a child
    /// dataset) to the columns that table holds; pass null to check without a source.
    /// </summary>
    /// <param name="mapping">The mapping checked.</param>
    /// <param name="schema">The template it pins.</param>
    /// <param name="references">The cache version it renders against.</param>
    /// <param name="context">The render context, with the flow's parameters.</param>
    /// <param name="sourceColumns">The flow's source tables and their columns, when known.</param>
    /// <param name="searches">
    /// The mapping's searches resolved against the schemas they pin. A mapping that declares searches is checked only
    /// with them, since without them nothing says whether its lookups can be asked at all.
    /// </param>
    public static IReadOnlyList<ValidationIssue> Check(
        MappingDefinition mapping,
        SchemaSnapshot schema,
        ReferenceSnapshot references,
        RenderContext context,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? sourceColumns,
        ResolvedSearches? searches = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(context);

        var issues = new List<ValidationIssue>();
        var where = mapping.SourcePath ?? mapping.Reference;

        // 1. The pinned template version is the one given.
        if (!string.Equals(schema.Kind, mapping.Template.Kind, StringComparison.Ordinal) || !string.Equals(schema.Version, mapping.Template.Version, StringComparison.Ordinal))
        {
            issues.Add(ValidationIssue.Error($"{where}: the mapping pins template {mapping.Template}, but template {schema.Kind} version {schema.Version} was given."));
            return issues;
        }

        // 4b. Every search resolves against the schema it pins, and every property it compares can be asked for.
        if (mapping.Searches.Count > 0)
        {
            if (searches is null)
            {
                issues.Add(ValidationIssue.Error(
                    $"{where}: the mapping searches {string.Join(", ", mapping.Searches.Keys.Order(StringComparer.Ordinal))}, and the schemas those searches pin were not loaded for this check, so nothing says whether its lookups can be asked."));
                return issues;
            }

            issues.AddRange(searches.Problems.Select(problem => ValidationIssue.Error(problem)));
        }

        // 4c. A mapping that reads its records' parent reads it as the parent's template declares it, under an id of its own.
        if (mapping.ReadsParent)
        {
            CheckParent(mapping, schema, searches?.Parent, issues, where);
        }

        // 4d. A mapping that links its records' children links kinds whose templates are linked to its own.
        if (mapping.LinksChildren)
        {
            CheckChildren(mapping, searches?.Children, issues, where);
        }

        MappingRenderer renderer;
        try
        {
            renderer = new MappingRenderer(mapping, schema, references, context, searches);
        }
        catch (FlowValidationException ex)
        {
            issues.Add(ValidationIssue.Error(ex.Message));
            return issues;
        }

        var template = OsduTemplate.From(schema);
        foreach (var entry in mapping.Entries)
        {
            CheckEntry(entry, template, references, renderer, issues, where);
        }

        // 5. Every property the schema requires has an entry that is allowed to be empty only if the schema allows it. The
        //    rule is MappingCoverage's, so the gate and the view of what a mapping covers judge a required property alike. A
        //    fragment adds paths to a record the flow's own mapping renders whole, which is the one held to them.
        if (!mapping.Fragment)
        {
            issues.AddRange(MappingCoverage.RequiredIssues(mapping, schema, where));
        }

        // 6. Every dataset column and child dataset exists in the flow's source tables.
        if (sourceColumns is not null)
        {
            CheckColumns(mapping, sourceColumns, issues, where);
        }

        foreach (var value in FlowParameters.ExplainMapping(mapping, context.Parameters))
        {
            if (value.Origin == Core.Runs.RunValueOrigin.Missing)
            {
                issues.Add(ValidationIssue.Error($"{where}: mapping parameter '{value.Name}' is required and the flow supplies no value."));
            }
            else if (value.Origin == Core.Runs.RunValueOrigin.NotDeclared)
            {
                issues.Add(ValidationIssue.Error($"{where}: the flow supplies parameter '{value.Name}', which the mapping does not declare. Undeclared values would be overrides, and overrides are not allowed (osdu/docs/reference/flow/mapping.md, parameters)."));
            }
        }

        return issues;
    }

    /// <summary>
    /// A mapping that reads its records' parent (<c>$parent</c>, osdu/docs/reference/flow/mapping-parent.md): its template is
    /// linked to one parent, of an entity type other than its own, so a record and its parent never share an id; the folders
    /// on the mapping's path hold the parent's template; every path it reads is a property of that template, of a shape the
    /// variable it fills can take.
    /// </summary>
    private static void CheckParent(MappingDefinition mapping, SchemaSnapshot schema, ResolvedParent? resolved, List<ValidationIssue> issues, string where)
    {
        if (schema.Parent is not { } link)
        {
            issues.Add(ValidationIssue.Error(schema.ParentProblem is { } problem
                ? $"{where}: the mapping reads its records' parent with $parent, and template {mapping.Template} names no parent that can be read: {problem}."
                : $"{where}: the mapping reads its records' parent with $parent, and template {mapping.Template} is linked to no parent; name the canonical kind its records stand beside in the template's {TemplateParent.Key}, as [{{ \"name\": \"WellLog\", \"kind\": \"osdu:wks:work-product-component--WellLog:1.4.0\" }}]."));
            return;
        }

        if (string.Equals(link.EntityType, mapping.EntityType, StringComparison.Ordinal))
        {
            issues.Add(ValidationIssue.Error(
                $"{where}: template {mapping.Template} is linked to {link.Kind}, a {link.EntityType} record as its own records are, so a record and its parent would have the same OSDU id and the record would be written over its parent; a linked template takes an entity type of its own, the namespace its records' ids are minted in."));
            return;
        }

        if (resolved?.Schema is not { } parent)
        {
            issues.Add(ValidationIssue.Error(resolved?.Problem is { } why
                ? $"{where}: the mapping reads its records' parent, of {link.Kind}, and that template cannot be read: {why}"
                : $"{where}: the mapping reads its records' parent, of {link.Kind}, and that template was not loaded for this check, so nothing says whether the paths it reads are there."));
            return;
        }

        foreach (var entry in mapping.Entries)
        {
            // An item of a list is one value of the list it fills, so only a node filling its variable itself is matched to it.
            var whole = !entry.IsList;
            foreach (var node in entry.ValueNodes.Where(n => n.Source is { Kind: MappingSourceKind.Parent }))
            {
                var path = node.Source!.ParentPath!;
                if (ParentProperty(parent, path) is not { } read)
                {
                    issues.Add(ValidationIssue.Error(
                        $"{where}: {node.Where} reads {path} of the record's parent, and template {parent.Kind} version {parent.Version} declares no {path}.", node.Target.Text));
                    continue;
                }

                if (whole && schema.Resolve(node.Target.SchemaPath) is { } filled && Mismatch(read, filled) is { } given)
                {
                    issues.Add(ValidationIssue.Error(
                        $"{where}: {node.Where} takes the parent's {path}, {given}, where template {mapping.Template.Kind} takes {ShapeOf(filled)} at {node.Target.Text}.", node.Target.Text));
                }
            }
        }
    }

    /// <summary>
    /// A mapping that links its records' children (<c>$child</c>, osdu/docs/reference/flow/mapping-child.md): each kind it
    /// links is of an entity type other than its own, so a record and its child never share an id; the folders on the
    /// mapping's path hold the kind's template; and that template is linked to a kind of the mapping's own entity type, so
    /// the records it links are the ones that stand beside its own. Where the link is written is checked node by node, as
    /// any value is (<see cref="CheckChild"/>).
    /// </summary>
    private static void CheckChildren(MappingDefinition mapping, IReadOnlyDictionary<string, ResolvedChild>? resolved, List<ValidationIssue> issues, string where)
    {
        foreach (var entry in mapping.Entries)
        {
            foreach (var node in entry.ValueNodes.Where(n => n.Source is { Kind: MappingSourceKind.Child }))
            {
                var kind = node.Source!.ChildKind!;
                var entityType = TargetId.EntityTypeFromKind(kind);
                if (string.Equals(entityType, mapping.EntityType, StringComparison.Ordinal))
                {
                    issues.Add(ValidationIssue.Error(
                        $"{where}: {node.Where} links the record's child of {kind}, a {entityType} record as the mapping's own records are, so a record and its child would have the same OSDU id; a child is a record of a linked template, which takes an entity type of its own.",
                        node.Target.Text));
                    continue;
                }

                if (resolved is null || !resolved.TryGetValue(kind, out var child))
                {
                    issues.Add(ValidationIssue.Error(
                        $"{where}: {node.Where} links the record's child of {kind}, and that template was not loaded for this check, so nothing says whether it is linked to {mapping.Template.Kind}.",
                        node.Target.Text));
                    continue;
                }

                if (child.Schema is not { } template)
                {
                    issues.Add(ValidationIssue.Error(
                        $"{where}: {node.Where} links the record's child of {kind}, and that template cannot be read: {child.Problem}",
                        node.Target.Text));
                    continue;
                }

                if (template.Parent is not { } link)
                {
                    issues.Add(ValidationIssue.Error(template.ParentProblem is { } problem
                        ? $"{where}: {node.Where} links the record's child of {kind}, and that template names no parent that can be read: {problem}."
                        : $"{where}: {node.Where} links the record's child of {kind}, and that template is linked to no parent, so its records are no record's children; a custom template names the kind its records stand beside in its {TemplateParent.Key}, as [{{ \"name\": \"{mapping.EntityType.Split("--")[^1]}\", \"kind\": \"{mapping.Template.Kind}\" }}].",
                        node.Target.Text));
                    continue;
                }

                if (!string.Equals(link.EntityType, mapping.EntityType, StringComparison.Ordinal))
                {
                    issues.Add(ValidationIssue.Error(
                        $"{where}: {node.Where} links the record's child of {kind}, and that template is linked to {link.Kind}, a {link.EntityType} record, where this mapping renders {mapping.EntityType} records, so the records it links are not this mapping's records' children.",
                        node.Target.Text));
                }
            }
        }
    }

    /// <summary>
    /// The property <paramref name="path"/> names in the parent's template; for a path inside an object that template leaves
    /// open (one declaring no properties and not closed by <c>additionalProperties: false</c>, as OSDU's
    /// <c>ExtensionProperties</c> is, where the wellbore DDMS keeps its <c>wdms</c> references), a value of any shape, since
    /// the parent may hold anything there. Null when the template declares no such property.
    /// </summary>
    private static SchemaProperty? ParentProperty(SchemaSnapshot parent, string path)
    {
        if (parent.Resolve(path) is { } declared)
        {
            return declared;
        }

        var segments = path.Split('.');
        for (var length = segments.Length - 1; length > 0; length--)
        {
            if (parent.Resolve(string.Join('.', segments, 0, length)) is not { } holder)
            {
                continue;
            }

            var open = holder.Type is SchemaType.Object or SchemaType.Any
                && holder.Schema["properties"] is not JsonObject { Count: > 0 }
                && holder.Schema["oneOf"] is null && holder.Schema["anyOf"] is null
                && !(holder.Schema["additionalProperties"] is JsonValue closed && closed.TryGetValue<bool>(out var allowed) && !allowed);
            return open ? new SchemaProperty(segments[^1], SchemaType.Any, new JsonObject(), null, null, false) : null;
        }

        return null;
    }

    /// <summary>
    /// What a section of the parent is, when the variable it fills cannot take it: an object where no object goes, a list
    /// where a single value or an object goes, or a single value where an object goes. A single value fills a list as a list
    /// of one, and a property either template leaves untyped takes anything. Null when the variable can take it.
    /// </summary>
    private static string? Mismatch(SchemaProperty given, SchemaProperty taken)
    {
        if (given.Type == SchemaType.Any || taken.Type == SchemaType.Any)
        {
            return null;
        }

        var takesObjects = taken.Type == SchemaType.Object || (taken.Type == SchemaType.Array && taken.ItemScalarType == SchemaType.Object);
        var takesValues = taken.Type is not (SchemaType.Object or SchemaType.Array) || (taken.Type == SchemaType.Array && taken.ItemScalarType != SchemaType.Object);
        return given.Type switch
        {
            SchemaType.Object => takesObjects ? null : "an object",
            SchemaType.Array => taken.Type == SchemaType.Array ? null : "a list",
            _ => takesValues ? null : "a single value",
        };
    }

    private static string ShapeOf(SchemaProperty property) => property.Type switch
    {
        SchemaType.Array => property.ItemScalarType == SchemaType.Object ? "a list of objects" : "a list",
        SchemaType.Object => "an object",
        _ => "a single value",
    };

    /// <summary>Throws a <see cref="FlowValidationException"/> listing every error when any is present.</summary>
    public static void ThrowIfFailed(IReadOnlyList<ValidationIssue> issues, string where)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var errors = issues.Where(i => i.Severity == IssueSeverity.Error).ToList();
        if (errors.Count == 0)
        {
            return;
        }

        throw new FlowValidationException(
            $"{where}: preflight failed with {errors.Count} error(s):" + Environment.NewLine
            + string.Join(Environment.NewLine, errors.Select(e => "  - " + e.Message)));
    }

    /// <summary>
    /// Everything an entry's checks find concerns the variable the entry fills, so each finding carries that target and a
    /// view of the template can show it on the variable itself.
    /// </summary>
    private static void CheckEntry(MappingEntry entry, OsduTemplate template, ReferenceSnapshot references, MappingRenderer renderer, List<ValidationIssue> issues, string where)
    {
        var found = new List<ValidationIssue>();
        CheckEntryInto(entry, template, references, renderer, found, where);
        issues.AddRange(found.Select(issue => issue with { Target = entry.Target.Text }));
    }

    private static void CheckEntryInto(MappingEntry entry, OsduTemplate template, ReferenceSnapshot references, MappingRenderer renderer, List<ValidationIssue> issues, string where)
    {
        var name = $"{where}: {entry.Where}";

        // 2. The target is a variable of the template, with an agreeing shape; 3. and one a mapping may fill.
        var variable = template.Find(entry.Target);
        if (variable is null)
        {
            issues.Add(ValidationIssue.Error($"{name} fills a variable that template {template.Kind} version {template.Version} does not have."));
            return;
        }

        switch (variable.Role)
        {
            case TemplateVariableRole.Engine:
                issues.Add(ValidationIssue.Error($"{name}: {entry.Target.Text} is written by OSDU Delivery ({(entry.Target.Leaf == "id" ? "from dataset.key" : "from the template")}), not by a mapping."));
                return;
            case TemplateVariableRole.Osdu:
                issues.Add(ValidationIssue.Error($"{name}: {entry.Target.Text} is set by OSDU when the record is stored, not by a mapping."));
                return;
        }

        if (variable.Nested)
        {
            issues.Add(ValidationIssue.Error(
                $"{name}: {entry.Target.Text} is a list of objects inside the items of another array, which a mapping does not fill: neither a $forEach inside a $forEach nor a list inside the item of a list is supported."));
            return;
        }

        if (entry.Parts.Any(part => part.IsObject) && variable.Shape != TemplateVariableShape.GroupList && variable.Inside is null)
        {
            issues.Add(ValidationIssue.Error(
                $"{name} is a list of objects whose properties read values, which fills a list of objects the template breaks into properties, and {entry.Target.Text} is {Describe(variable)}."));
            return;
        }

        // The node's assertions judge a value of this variable, so each has to suit its type, and every field their conditions
        // read has to be a property of the template (osdu/docs/reference/flow/mapping-assertions.md).
        foreach (var assertion in entry.Assertions)
        {
            foreach (var problem in AssertionTemplates.CheckAssertion(variable, assertion, template))
            {
                issues.Add(ValidationIssue.Error($"{where}: {assertion.Location}: {problem}"));
            }
        }

        // A $coalesce node fills its variable with whichever alternative gives a value, and a list of values with what every
        // item gives, so each alternative or item is checked as a node of its own against the variable, and named by where
        // it is written. The value nodes of an object item's properties fill variables inside the list's items instead, so
        // they are left to the properties' own checks below.
        foreach (var node in entry.ValueNodes.Where(node => node.Target.Equals(entry.Target)))
        {
            // The node itself is checked without its alternatives or items, whose expressions it would otherwise count as its own.
            CheckValueNode(ReferenceEquals(node, entry) ? entry with { Alternatives = [], Parts = [] } : node, variable, references, renderer, issues, ReferenceEquals(node, entry) ? name : $"{where}: {node.Where}");
        }

        // Each property of an item of a list of objects is checked as an entry of its own against the variable of the list's
        // items it fills; what it finds concerns the list, whose entry it is part of.
        foreach (var property in entry.Parts.SelectMany(part => part.Properties))
        {
            CheckEntryInto(property, template, references, renderer, issues, where);
        }
    }

    /// <summary>The checks of one value node against the variable it fills: a node of its own, or one alternative of a <c>$coalesce</c>.</summary>
    private static void CheckValueNode(MappingEntry entry, TemplateVariable variable, ReferenceSnapshot references, MappingRenderer renderer, List<ValidationIssue> issues, string name)
    {
        var shapeProblem = ShapeProblem(entry, variable);
        if (shapeProblem is not null)
        {
            issues.Add(ValidationIssue.Error($"{name}: {shapeProblem}"));
            return;
        }

        if (entry.Modifiers.Count > 0 && entry.Modifiers[^1].Kind == ModifierKind.Equals && entry.Source?.ReadsRow == true
            && variable.Type is not ("boolean" or "any"))
        {
            issues.Add(ValidationIssue.Error($"{name}: the last modifier is equals, which gives true or false, but the template takes a {variable.Type} at {entry.Target.Text}."));
        }

        CheckWrittenForm(entry, variable, renderer, issues, name);
        CheckReplaces(entry, references, issues, name);
        CheckId(entry, variable, references, renderer, issues, name);
        CheckExpressionParameters(entry, renderer, issues, name);

        if (entry.IsStatic)
        {
            CheckStatic(entry, variable, references, renderer, issues, name);
        }
        else if (entry.Source!.Kind == MappingSourceKind.Cache)
        {
            CheckCache(entry, variable, references, issues, name);
        }
        else if (entry.Source.Kind == MappingSourceKind.Search)
        {
            CheckSearch(entry, variable, renderer, issues, name);
        }
        else if (entry.Source.Kind == MappingSourceKind.Child)
        {
            CheckChild(entry, variable, renderer, issues, name);
        }
    }

    /// <summary>
    /// A <c>$child</c> node writes the reference to the record's child, <c>{partition}:{entityType}:{unique}:</c>, as text: the
    /// variable takes text without a format, and a reference of the child's entity type passes its relationship and its
    /// pattern, judged on a reference of that shape. A variable without either, a property of an open object such as
    /// <c>ExtensionProperties</c> or a tag, takes any reference, which is where a canonical template, whose schema the estate
    /// does not change, carries the link.
    /// </summary>
    private static void CheckChild(MappingEntry entry, TemplateVariable variable, MappingRenderer renderer, List<ValidationIssue> issues, string name)
    {
        var target = entry.Target.Text;
        var property = renderer.Schema.Resolve(entry.Target.SchemaPath);
        var listed = variable.Shape == TemplateVariableShape.ValueList;
        var written = listed ? variable.ItemType ?? "any" : variable.Type;
        var format = listed ? property?.ItemFormat : variable.Format;
        if (written is not ("string" or "any") || format is not null)
        {
            issues.Add(ValidationIssue.Error(
                $"{name}: a $child node writes the reference to the record's child as text, and the template takes {(format is null ? $"a {written}" : $"a {format} string")} at {target}."));
            return;
        }

        var partition = renderer.ParameterValue(RenderContext.DataPartitionParameter) is { Length: > 0 } given ? given : "partition";
        var sample = TargetId.Reference(partition, TargetId.EntityTypeFromKind(entry.Source!.ChildKind!), new string('0', 32));
        if (IdValues.Problem(sample, property) is { } problem)
        {
            issues.Add(ValidationIssue.Error($"{name}: the link to the record's child of {entry.Source.ChildKind} is written as '{sample}', which {target} does not take: {problem}."));
        }
    }

    /// <summary>
    /// Whether a dataset value's last modifier gives what the variable takes, judged by the variable itself or, for a list
    /// of values, by each item's type and format: a number modifier must fill a number, an integer or unformatted text, and a
    /// date modifier text whose format a date is written as. Text filling a date or date-time without the date modifier is
    /// written as it arrives, which is warned about because whatever reads the record expects the RFC 3339 form.
    /// </summary>
    private static void CheckWrittenForm(MappingEntry entry, TemplateVariable variable, MappingRenderer renderer, List<ValidationIssue> issues, string name)
    {
        if (entry.Source?.ReadsRow != true)
        {
            return;
        }

        var target = entry.Target.Text;
        var listed = variable.Shape == TemplateVariableShape.ValueList;
        var written = listed ? variable.ItemType ?? "any" : variable.Type;
        var format = listed ? renderer.Schema.Resolve(entry.Target.SchemaPath)?.ItemFormat : variable.Format;
        string Described(string textFormat) => listed ? $"a list of {textFormat} strings" : $"a {textFormat} string";
        var last = entry.Modifiers.Count > 0 ? entry.Modifiers[^1].Kind : (ModifierKind?)null;

        if (last == ModifierKind.Number)
        {
            if (written is not ("number" or "integer" or "string" or "any"))
            {
                issues.Add(ValidationIssue.Error($"{name}: the last modifier is number, which gives a number, but the template takes a {written} at {target}."));
            }
            else if (written == "string" && format is { } numberFormat)
            {
                issues.Add(ValidationIssue.Error($"{name}: the last modifier is number, but {target} takes {Described(numberFormat)}, which a number is not written as."));
            }
        }

        if (last == ModifierKind.Date)
        {
            if (written is not ("string" or "any"))
            {
                issues.Add(ValidationIssue.Error($"{name}: the last modifier is date, which gives a date written as text, but the template takes a {written} at {target}."));
            }
            else if (format is { } dateFormat && !Rendering.DateValues.Writes(dateFormat))
            {
                issues.Add(ValidationIssue.Error($"{name}: the last modifier is date, but {target} takes {Described(dateFormat)}, which a date is not written as."));
            }
        }
        else if (variable.Shape is TemplateVariableShape.Value or TemplateVariableShape.ValueList && written == "string"
            && format is Rendering.DateValues.DateFormat or Rendering.DateValues.DateTimeFormat
            && !entry.Modifiers.Any(m => m.Kind == ModifierKind.Date))
        {
            // A non-ISO value is only right when the record's meta describes its format, so this warns rather than refuses.
            issues.Add(ValidationIssue.Warning(
                $"{name}: {target} takes {Described(format)}, and a text value from {entry.Source} is written as it arrives; add the date modifier so every record carries the RFC 3339 form."));
        }
    }

    /// <summary>
    /// Why the entry's value cannot take the variable's shape, or null when it can. Inside an open object nothing gives a
    /// shape, so what the mapping writes there is the shape: a value, a literal, a list or a repeater's items.
    /// </summary>
    private static string? ShapeProblem(MappingEntry entry, TemplateVariable variable)
    {
        var shape = variable.Shape;
        var target = entry.Target.Text;
        if (variable.Inside is not null)
        {
            return null;
        }

        if (entry.IsRepeater)
        {
            return shape == TemplateVariableShape.GroupList
                ? null
                : $"{entry.Source} repeats rows into {target}, but a $forEach fills a list of objects and {target} is {Describe(variable)}.";
        }

        if (entry.Static is JsonObject)
        {
            return shape is TemplateVariableShape.Group or TemplateVariableShape.Whole && variable.Type is "object" or "any"
                ? null
                : $"a literal object cannot be written to {target}, which is {Describe(variable)}.";
        }

        if (entry.Static is JsonArray)
        {
            return shape is TemplateVariableShape.ValueList or TemplateVariableShape.GroupList or TemplateVariableShape.Whole && variable.Type is "array" or "any"
                ? null
                : $"a literal list cannot be written to {target}, which is {Describe(variable)}.";
        }

        if (entry.IsStatic)
        {
            return shape is TemplateVariableShape.Value or TemplateVariableShape.ValueList
                ? null
                : $"a single literal value cannot be written to {target}, which is {Describe(variable)}.";
        }

        if (entry.Source!.ReadsRow)
        {
            return shape is TemplateVariableShape.Value or TemplateVariableShape.ValueList
                ? null
                : $"{entry.Source} is one value, and {target} is {Describe(variable)}; fill the properties inside it instead.";
        }

        if (entry.Source.Kind == MappingSourceKind.Search)
        {
            return shape is TemplateVariableShape.Value or TemplateVariableShape.ValueList
                ? null
                : $"{entry.Source} is the id of the record a search finds, and {target} is {Describe(variable)}.";
        }

        if (entry.Source.Kind == MappingSourceKind.Parent)
        {
            // A section of the parent is written whole, an object or a list of objects as the parent holds it; whether its
            // shape fits the variable is judged against the parent's own template (CheckParent).
            return null;
        }

        if (entry.Source.Kind == MappingSourceKind.Child)
        {
            return shape is TemplateVariableShape.Value or TemplateVariableShape.ValueList || (shape == TemplateVariableShape.Whole && variable.Type == "any")
                ? null
                : $"{entry.Source} is the reference to one of the record's children, one value, and {target} is {Describe(variable)}.";
        }

        if (entry.FindAll is not null)
        {
            return shape is TemplateVariableShape.ValueList || (shape == TemplateVariableShape.Whole && variable.Type is "array" or "any")
                ? null
                : $"{entry.Source} with $findAll gives a list, one value from every row it finds, and {target} is {Describe(variable)}.";
        }

        return shape == TemplateVariableShape.GroupList
            ? $"{target} is {Describe(variable)}, which a $forEach fills from a child dataset, not a cache value."
            : null;
    }

    private static string Describe(TemplateVariable variable) => variable.Shape switch
    {
        TemplateVariableShape.Value => $"one {variable.Type}",
        TemplateVariableShape.ValueList => $"a list of {variable.ItemType}",
        TemplateVariableShape.Group => "an object with properties of its own",
        TemplateVariableShape.GroupList => "a list of objects",
        _ => variable.Type == "array" ? "a list the schema does not break into properties" : "an object the schema does not break into properties",
    };

    /// <summary>
    /// Every parameter an expression of the entry reads has a value in the flow: one without reads as no value, so a
    /// condition on it would never hold and a value computed from it would be missing, on every record.
    /// </summary>
    private static void CheckExpressionParameters(MappingEntry entry, MappingRenderer renderer, List<ValidationIssue> issues, string name)
    {
        foreach (var expression in entry.Expressions)
        {
            foreach (var parameter in expression.Parameters.Where(p => string.IsNullOrWhiteSpace(renderer.ParameterValue(p))))
            {
                issues.Add(ValidationIssue.Error($"{name} reads $param.{parameter} in '{expression}', which the flow supplies no value for."));
            }
        }
    }

    private static void CheckStatic(MappingEntry entry, TemplateVariable variable, ReferenceSnapshot references, MappingRenderer renderer, List<ValidationIssue> issues, string name)
    {
        var texts = MappingMapper.StaticTexts(entry.Static).ToList();
        foreach (var text in texts)
        {
            foreach (var parameter in MappingEntry.ParameterNames(text))
            {
                if (renderer.ParameterValue(parameter) is null)
                {
                    issues.Add(ValidationIssue.Error($"{name} uses {{$param.{parameter}}}, which the flow supplies no value for."));
                }
            }
        }

        // 9. A static id on a relationship exists in the cache, when the cache holds that entity type.
        if (variable.Relationships.Count == 0 || entry.Static is JsonObject)
        {
            return;
        }

        foreach (var text in texts)
        {
            var value = MappingEntry.ExpandParameters(text, renderer.ParameterValue).Trim();
            var match = RecordId().Match(value);
            if (!match.Success)
            {
                issues.Add(ValidationIssue.Error($"{name}: '{value}' is not an OSDU record id, and {entry.Target.Text} points to {string.Join(" or ", variable.Relationships)}."));
                continue;
            }

            var entityType = match.Groups["entity"].Value;
            if (!Points(variable.Relationships, entityType))
            {
                issues.Add(ValidationIssue.Error($"{name}: '{value}' is a {entityType} record, and {entry.Target.Text} points to {string.Join(" or ", variable.Relationships)}."));
                continue;
            }

            // Only an entity type the cache holds can be checked. Many fixed ids point at reference data nobody caches
            // (alias name types, say), and a finding on every plan for a value that cannot be checked is noise.
            var cached = CachedReferences.Holding(references, entityType);
            if (cached.Count > 0 && CachedReferences.Find(cached, new CachedReference(match.Groups["id"].Value, entityType)) is null)
            {
                issues.Add(ValidationIssue.Error($"{name}: '{value}' is not in cache version '{references.Version}', which caches {entityType} as {string.Join(", ", cached.Select(t => t.Name))}."));
            }
        }
    }

    /// <summary>
    /// 8, for a search: the id a search finds is of the entity type it searches, which has to be one the template points
    /// the target to. Whether each property its findBy lines compare can be asked for at all is the searches' resolution
    /// against their schemas, reported with the mapping's other problems.
    /// </summary>
    private static void CheckSearch(MappingEntry entry, TemplateVariable variable, MappingRenderer renderer, List<ValidationIssue> issues, string name)
    {
        var searchName = entry.Source!.CacheType!;
        if (!renderer.Mapping.Searches.TryGetValue(searchName, out var search))
        {
            return;
        }

        var entityType = search.Kind.Split(':')[2];
        if (variable.Relationships.Count > 0)
        {
            if (!Points(variable.Relationships, entityType))
            {
                issues.Add(ValidationIssue.Error(
                    $"{name} writes the id of a {entityType} record search '{searchName}' finds, but the template points {entry.Target.Text} to {string.Join(" or ", variable.Relationships)}."));
            }
        }
        else if (variable.Pattern is null)
        {
            issues.Add(ValidationIssue.Warning($"{name} writes an OSDU reference, but the template does not mark {entry.Target.Text} as a relationship."));
        }
    }

    private static void CheckCache(MappingEntry entry, TemplateVariable variable, ReferenceSnapshot references, List<ValidationIssue> issues, string name)
    {
        var source = entry.Source!;
        // 7. The cache type exists and holds the fields findBy compares and the field the source reads.
        if (references.Type(source.CacheType!) is not { } cached)
        {
            var available = references.Types.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            issues.Add(ValidationIssue.Error(
                $"{name} reads {source.CacheType} from the cache, and cache version '{references.Version}' does not hold it. Cached: {(available.Count == 0 ? "nothing" : string.Join(", ", available))}."));
            return;
        }

        var cachedFields = string.Join(", ", cached.FieldNames.Prepend("id"));
        if (entry.FindAll is { } findAll)
        {
            // A $findAll node compares its key and its empty fields, not findBy lines.
            CheckFindAll(entry, findAll, cached, references, issues, name);
        }
        else
        {
            var fields = entry.FindBy.Select(f => f.Field).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var known = fields.Where(f => cached.HasField(f) || cached.MeansRecordId(f)).ToList();
            if (known.Count == 0)
            {
                issues.Add(ValidationIssue.Error($"{name} finds {cached.Name} by {string.Join(" or ", fields)}, and cache version '{references.Version}' caches none of those. Cached: {cachedFields}."));
            }
            else
            {
                foreach (var field in fields.Except(known, StringComparer.OrdinalIgnoreCase))
                {
                    issues.Add(ValidationIssue.Warning($"{name} finds {cached.Name} by '{field}', which cache version '{references.Version}' does not cache. Cached: {cachedFields}."));
                }
            }
        }

        if (source.ReadsRecordId && cached.IsLookup)
        {
            issues.Add(ValidationIssue.Error(
                $"{name} reads the id of a {cached.Name} row, and {cached.Name} is a lookup table whose rows are not OSDU records, so it has no id to write. Read one of its fields: {string.Join(", ", cached.FieldNames)}."));
            return;
        }

        if (!source.ReadsRecordId && !cached.MeansRecordId(source.CacheField!) && cached.Items.All(item => cached.Value(item, source.CacheField!) is null))
        {
            issues.Add(ValidationIssue.Error($"{name} reads '{source.CacheField}' out of {cached.Name}, which cache version '{references.Version}' does not cache. Cached: {cachedFields}."));
        }

        // 8. A cached id resolves to the entity type the schema expects for the target; so does every id a cached field
        //    written to a relationship holds.
        if (!source.ReadsRecordId)
        {
            if (variable.Relationships.Count > 0)
            {
                CheckCachedReferences(entry, variable, cached, references, issues, name);
            }

            return;
        }

        if (variable.Relationships.Count > 0)
        {
            if (!Points(variable.Relationships, cached.EntityType))
            {
                issues.Add(ValidationIssue.Error(
                    $"{name} writes the id of a cached {cached.Name} ({cached.EntityType}), but the template points {entry.Target.Text} to {string.Join(" or ", variable.Relationships)}."));
            }
        }
        else if (variable.Pattern is null)
        {
            issues.Add(ValidationIssue.Warning($"{name} writes an OSDU reference, but the template does not mark {entry.Target.Text} as a relationship."));
        }
    }

    /// <summary>
    /// 7d. A <c>$findAll</c>: the cache holds the field its key compares, and every row it reads is judged by fields the
    /// cache captures. A field no row of a type holds reads as empty on every row, so asking for it to be empty would let
    /// every row the key finds through (the field groups of a country, taken for its country groups) where the likelier
    /// truth is that the cache flow does not capture it; that is refused. A lookup operand reads a type the cache holds, by
    /// fields it caches, at a path its records hold.
    /// </summary>
    private static void CheckFindAll(MappingEntry entry, FindAllQuery findAll, ReferenceType cached, ReferenceSnapshot references, List<ValidationIssue> issues, string name)
    {
        var cachedFields = string.Join(", ", cached.FieldNames.Prepend("id"));
        if (!cached.HasField(findAll.Field) && !cached.MeansRecordId(findAll.Field))
        {
            issues.Add(ValidationIssue.Error(
                $"{name} reads every {cached.Name} row whose {findAll.Field} holds {findAll.Operand}, and cache version '{references.Version}' caches no {findAll.Field}. Cached: {cachedFields}."));
        }

        foreach (var field in findAll.Empty)
        {
            if (cached.Items.Count > 0 && cached.Items.All(item => cached.HoldsNothing(item, field)))
            {
                issues.Add(ValidationIssue.Error(
                    $"{name} reads only the {cached.Name} rows whose {field} is empty, and no row of cache version '{references.Version}' holds a {field}, so every row would pass. "
                    + $"Capture data.{ReferenceField.Normalize(field)} in the cache flow that fills {cached.Name}. Cached: {cachedFields}."));
            }
        }

        if (findAll.Operand.Lookup is not { } lookup)
        {
            return;
        }

        if (references.Type(lookup.CacheType) is not { } found)
        {
            issues.Add(ValidationIssue.Error(
                $"{name} reads {findAll.Operand} from lookup '{lookup.Name}', which finds a {lookup.CacheType}, and cache version '{references.Version}' does not hold {lookup.CacheType}."));
            return;
        }

        var foundFields = string.Join(", ", found.FieldNames.Prepend("id"));
        var findFields = lookup.FindBy.Select(f => f.Field).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!findFields.Any(f => found.HasField(f) || found.MeansRecordId(f)))
        {
            issues.Add(ValidationIssue.Error(
                $"{name}: lookup '{lookup.Name}' finds {found.Name} by {string.Join(" or ", findFields)}, and cache version '{references.Version}' caches none of those. Cached: {foundFields}."));
        }

        var path = findAll.Operand.LookupPath!;
        if (!found.MeansRecordId(path) && found.Items.All(item => found.Value(item, path) is null))
        {
            issues.Add(ValidationIssue.Error(
                $"{name} reads {findAll.Operand}, and no {found.Name} record of cache version '{references.Version}' holds '{path}'. Cached: {foundFields}."));
        }
    }

    /// <summary>
    /// 7c. An id or a ref modifier: a ref's entity type is one the template settles; the variable takes text; each cache
    /// token reads a lookup table the cache version holds, at a field its rows hold; each parameter has a value; and the id
    /// the template builds, with a stand-in for what a row gives, is one the variable takes: of an entity type its
    /// relationship allows, and matching its pattern.
    /// </summary>
    private static void CheckId(MappingEntry entry, TemplateVariable variable, ReferenceSnapshot references, MappingRenderer renderer, List<ValidationIssue> issues, string name)
    {
        if (entry.Modifiers.LastOrDefault(m => m.BuildsId) is not { } builder)
        {
            return;
        }

        IdTemplate? template;
        if (builder.Kind == ModifierKind.Ref)
        {
            template = renderer.ReferenceTemplate(entry, out var problem);
            if (template is null)
            {
                issues.Add(ValidationIssue.Error($"{name}: {problem}."));
                return;
            }
        }
        else
        {
            template = builder.Id!;
        }

        var target = entry.Target.Text;
        var property = renderer.Schema.Resolve(entry.Target.SchemaPath);
        var listed = variable.Shape == TemplateVariableShape.ValueList;
        var written = listed ? variable.ItemType ?? "any" : variable.Type;
        var format = listed ? property?.ItemFormat : variable.Format;
        if (written is not ("string" or "any") || format is not null)
        {
            issues.Add(ValidationIssue.Error(
                $"{name}: the id modifier gives an OSDU id, written as text, but the template takes {(format is null ? $"a {written}" : $"a {format} string")} at {target}."));
            return;
        }

        foreach (var token in template.Tokens.Where(t => t.Kind == IdTokenKind.Cache).DistinctBy(t => t.Text, StringComparer.Ordinal))
        {
            CheckIdCacheToken(token, references, issues, name);
        }

        foreach (var parameter in template.Parameters.Where(p => string.IsNullOrWhiteSpace(renderer.ParameterValue(p))))
        {
            issues.Add(ValidationIssue.Error($"{name}: the id {template} reads {{$param.{parameter}}}, which the flow supplies no value for."));
        }

        if (template.EntityType is not { } entityType)
        {
            if (variable.Relationships.Count > 0 || variable.Pattern is not null)
            {
                issues.Add(ValidationIssue.Warning(
                    $"{name}: a token writes the entity type of the id {template}, so whether each id is one {target} takes is checked record by record, as it is built."));
            }

            return;
        }

        if (variable.Relationships.Count > 0 && !Points(variable.Relationships, entityType))
        {
            issues.Add(ValidationIssue.Error(
                $"{name}: the id {template} names a {entityType} record, but the template points {target} to {string.Join(" or ", variable.Relationships)}."));
            return;
        }

        if (variable.Relationships.Count == 0 && variable.Pattern is null)
        {
            issues.Add(ValidationIssue.Warning($"{name} writes an OSDU id, but the template does not mark {target} as a relationship."));
        }

        if (property is null || IdValues.PatternText(property) is not { } patternText)
        {
            return;
        }

        if (IdValues.Pattern(patternText) is null)
        {
            issues.Add(ValidationIssue.Warning(
                $"{name}: the pattern the template gives {target}, {patternText}, cannot be read as a regular expression, so each id is checked for OSDU's id shape and entity type only."));
        }
        else if (IdValues.Sample(template, renderer.ParameterValue) is { } sample && IdValues.Problem(sample, property) is { } problem)
        {
            issues.Add(ValidationIssue.Error(
                $"{name}: the id {template} gives ids such as '{sample}', which {target} does not take: {problem}. A reference ends with ':' and, when it pins one, the version."));
        }
    }

    /// <summary>A cache token of an id: a lookup table the cache version holds, looked up by its key, at a field its rows hold.</summary>
    private static void CheckIdCacheToken(IdToken token, ReferenceSnapshot references, List<ValidationIssue> issues, string name)
    {
        if (references.Type(token.CacheType!) is not { } type)
        {
            var available = references.Types.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            issues.Add(ValidationIssue.Error(
                $"{name}: the id reads {token}, and cache version '{references.Version}' holds no {token.CacheType}. Cached: {(available.Count == 0 ? "nothing" : string.Join(", ", available))}."));
            return;
        }

        if (type.Key is not { } key)
        {
            issues.Add(ValidationIssue.Error(
                $"{name}: the id reads {token}, and {type.Name} holds OSDU records ({type.EntityType}), which have no key to look the value up by; a cache token reads a lookup table, "
                + $"keyed by the value. Turn the value into the field an id needs with a replace before the id, such as replace: $cache.{type.Name}, match: Code, field: Code."));
            return;
        }

        var field = ReferenceField.Normalize(token.CacheField!);
        if (field.Equals(ReferenceField.Normalize(key), StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(ValidationIssue.Warning($"{name}: the id reads {token}, the key {type.Name} is looked up by, which is the value itself; write {{$value}}."));
            return;
        }

        if (type.Items.Count == 0)
        {
            issues.Add(ValidationIssue.Warning(
                $"{name}: the id reads {token}, and {type.Name} holds no rows in cache version '{references.Version}', so no record gets an id from it."));
            return;
        }

        if (type.Items.All(item => type.Value(item, field) is null))
        {
            issues.Add(ValidationIssue.Error(
                $"{name}: the id reads {token}, and no {type.Name} row holds '{field}' in cache version '{references.Version}'. Cached: {string.Join(", ", type.FieldNames)}."));
            return;
        }

        var several = type.Items.Where(item => type.Value(item, field) is { } value && value.Node is not JsonArray { Count: 0 } && ReplaceTables.SingleText(value) is null)
            .Select(item => item.Id).ToList();
        if (several.Count > 0)
        {
            issues.Add(ValidationIssue.Warning(
                $"{name}: {several.Count} {type.Name} row(s) hold several values or an object at '{field}' in cache version '{references.Version}' ({Listed(several)}); a record whose value is one of them is held, since {token} writes one value."));
        }
    }

    /// <summary>
    /// 7b. Every replace reading a cached table reads one the cache version holds, on fields it can settle and holds. And
    /// where the replaced value is then found in the cache, as a findBy value, every value a replace can give (inline or
    /// cached) is looked up in the type found, and the ones that find nothing are listed: each is a record a render holds,
    /// or leaves without its reference, and the listing names them before any row arrives.
    /// </summary>
    private static void CheckReplaces(MappingEntry entry, ReferenceSnapshot references, List<ValidationIssue> issues, string name)
    {
        for (var i = 0; i < entry.Modifiers.Count; i++)
        {
            var modifier = entry.Modifiers[i];
            if (modifier.Kind != ModifierKind.Replace)
            {
                continue;
            }

            IReadOnlyCollection<string> gives;
            if (modifier.Table is { } table)
            {
                if (CachedReplaceValues(table, references, issues, name) is not { } produced)
                {
                    continue;
                }

                gives = produced;
            }
            else
            {
                gives = modifier.Replacements.Values.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            }

            if (modifier.Otherwise.Kind == ReplaceFallbackKind.Text)
            {
                gives = gives.Append(modifier.Otherwise.Text!).Distinct(StringComparer.Ordinal).ToList();
            }

            CheckReplacedValuesResolve(entry, i, gives, references, issues, name);
        }
    }

    /// <summary>
    /// The values a replace reading <paramref name="table"/> can give, each once, or null (with the error) when the cache
    /// version does not hold the table or it cannot be read as the replace names it.
    /// </summary>
    private static List<string>? CachedReplaceValues(CachedReplaceTable table, ReferenceSnapshot references, List<ValidationIssue> issues, string name)
    {
        if (references.Type(table.CacheType) is not { } type)
        {
            var available = references.Types.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            issues.Add(ValidationIssue.Error(
                $"{name}: replace reads $cache.{table.CacheType}, which cache version '{references.Version}' does not hold. Cached: {(available.Count == 0 ? "nothing" : string.Join(", ", available))}."));
            return null;
        }

        if (ReplaceTables.Fields(table, type, out var problem) is not { } fields)
        {
            issues.Add(ValidationIssue.Error($"{name}: {problem}, in cache version '{references.Version}'."));
            return null;
        }

        var cachedFields = string.Join(", ", type.FieldNames.Prepend("id"));
        if (type.Items.Count == 0)
        {
            issues.Add(ValidationIssue.Warning(
                $"{name}: replace reads $cache.{type.Name}, which holds no rows in cache version '{references.Version}', so every value becomes what its otherwise says."));
            return [];
        }

        if (!type.HasField(fields.Match) && !type.MeansRecordId(fields.Match))
        {
            issues.Add(ValidationIssue.Error(
                $"{name}: replace matches the value on '{fields.Match}' of {type.Name}, which cache version '{references.Version}' does not cache. Cached: {cachedFields}."));
            return null;
        }

        // A field the replace names has to be one some row holds. One it settles is held by definition, or is the value of a
        // dictionary whose every entry gives no value, which is exactly what it says.
        if (table.Field is not null && !type.MeansRecordId(fields.Field) && type.Items.All(item => type.Value(item, fields.Field) is null))
        {
            issues.Add(ValidationIssue.Error(
                $"{name}: replace replaces the value by '{fields.Field}' of {type.Name}, which cache version '{references.Version}' does not cache. Cached: {cachedFields}."));
            return null;
        }

        if (type.IsAmbiguous(fields.Match))
        {
            issues.Add(ValidationIssue.Warning(
                $"{name}: several {type.Name} rows hold the same {fields.Match} in cache version '{references.Version}', and a value they give differently replaces nothing: the record is held."));
        }

        var gives = new SortedSet<string>(StringComparer.Ordinal);
        var several = new List<string>();
        foreach (var item in type.Items)
        {
            if (type.Value(item, fields.Field) is not { } value || value.Node is JsonArray { Count: 0 })
            {
                continue;
            }

            if (ReplaceTables.SingleText(value) is { } text)
            {
                gives.Add(text);
            }
            else
            {
                several.Add(item.Id);
            }
        }

        if (several.Count > 0)
        {
            issues.Add(ValidationIssue.Warning(
                $"{name}: {several.Count} {type.Name} row(s) hold several values or an object at '{fields.Field}' in cache version '{references.Version}' ({Listed(several)}); a value matching one of them holds its record, since a replace gives one value."));
        }

        return gives.ToList();
    }

    /// <summary>
    /// When the value the replace at <paramref name="index"/> gives is what the entry's findBy lines look up in the cache,
    /// the values it can give that find nothing, listed as a warning. Only a replace followed by nothing, or by case and
    /// spacing modifiers the check applies itself, is checked; any other modifier after it changes the value in a way only
    /// a row shows.
    /// </summary>
    private static void CheckReplacedValuesResolve(
        MappingEntry entry, int index, IReadOnlyCollection<string> gives, ReferenceSnapshot references, List<ValidationIssue> issues, string name)
    {
        if (gives.Count == 0 || entry.Source is not { Kind: MappingSourceKind.Cache, CacheType: { } typeName } || references.Type(typeName) is not { } target)
        {
            return;
        }

        var lines = entry.FindBy.Where(f => f.Column is not null).ToList();
        if (lines.Count == 0)
        {
            return;
        }

        var after = entry.Modifiers.Skip(index + 1).ToList();
        if (after.Any(m => m.Kind is not (ModifierKind.Trim or ModifierKind.Upper or ModifierKind.Lower)))
        {
            return;
        }

        var unresolved = new List<string>();
        foreach (var given in gives)
        {
            var value = after.Aggregate(given, (text, m) => m.Kind switch
            {
                ModifierKind.Upper => text.ToUpperInvariant(),
                ModifierKind.Lower => text.ToLowerInvariant(),
                _ => text.Trim(),
            }).Trim();
            if (value.Length == 0)
            {
                continue;
            }

            var resolves = (!target.IsLookup && RecordId().Match(value) is { Success: true } id && target.Match("id", id.Groups["id"].Value) is not null)
                || lines.Any(find => target.Find(find.Field, value, entry.IgnoreSeparators).Item is not null);
            if (!resolves)
            {
                unresolved.Add(value);
            }
        }

        if (unresolved.Count > 0)
        {
            var by = string.Join("/", lines.Select(f => ReferenceField.Normalize(f.Field)).Distinct(StringComparer.OrdinalIgnoreCase));
            issues.Add(ValidationIssue.Warning(
                $"{name}: {unresolved.Count} of the {gives.Count} value(s) its replace can give find no {target.Name} by {by} in cache version '{references.Version}': {Listed(unresolved)}. A record whose value becomes one of them is {(entry.Required ? "held" : "left without it")}."));
        }
    }

    /// <summary>Values as a finding lists them: the first ten, quoted, and how many more.</summary>
    private static string Listed(IReadOnlyList<string> values)
        => string.Join(", ", values.Take(10).Select(v => $"'{v}'")) + (values.Count > 10 ? $" and {values.Count - 10} more" : string.Empty);

    /// <summary>
    /// 8, for a cached field written to a relationship (OSDU's own translations cache the id of the record a value stands
    /// for, <c>ExternalUnitOfMeasure.UnitOfMeasureID</c>): every value the field holds in the cache version is an OSDU record
    /// id of an entity type the relationship allows, or the mapping is refused, since a render would write a reference that
    /// is no reference, or one to the wrong kind of record. Where the cache holds the entity type an id names, an id it holds
    /// no record for is listed as a warning: a render holds the records that meet it.
    /// </summary>
    private static void CheckCachedReferences(
        MappingEntry entry, TemplateVariable variable, ReferenceType cached, ReferenceSnapshot references, List<ValidationIssue> issues, string name)
    {
        var field = entry.Source!.CacheField!;
        var values = cached.Items
            .Select(item => cached.Value(item, field))
            .OfType<ReferenceValue>()
            .SelectMany(value => value.Terms)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (values.Count == 0)
        {
            return;
        }

        var points = string.Join(" or ", variable.Relationships);
        var parsed = values.Select(value => (Value: value, Reference: CachedReferences.Parse(value))).ToList();
        var notIds = parsed.Where(p => p.Reference is null).Select(p => p.Value).ToList();
        if (notIds.Count > 0)
        {
            issues.Add(ValidationIssue.Error(
                $"{name} writes '{field}' of {cached.Name} to {entry.Target.Text}, which points to {points}, and {notIds.Count} of the {values.Count} value(s) it holds in cache version '{references.Version}' are not OSDU record ids: {Listed(notIds)}. Read a field that holds record ids, or the id of the cached record."));
        }

        var ids = parsed.Where(p => p.Reference is not null).Select(p => (p.Value, Reference: p.Reference!.Value)).ToList();
        var otherType = ids.Where(p => !Points(variable.Relationships, p.Reference.EntityType)).ToList();
        if (otherType.Count > 0)
        {
            var kinds = string.Join(", ", otherType.Select(p => p.Reference.EntityType).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
            issues.Add(ValidationIssue.Error(
                $"{name} writes '{field}' of {cached.Name} to {entry.Target.Text}, which points to {points}, and {otherType.Count} of the ids it holds in cache version '{references.Version}' name records of another entity type ({kinds}): {Listed(otherType.Select(p => p.Value).ToList())}."));
        }

        var missing = new List<string>();
        foreach (var (value, reference) in ids.Where(p => Points(variable.Relationships, p.Reference.EntityType)))
        {
            var holding = CachedReferences.Holding(references, reference.EntityType);
            if (holding.Count > 0 && CachedReferences.Find(holding, reference) is null)
            {
                missing.Add(value);
            }
        }

        if (missing.Count > 0)
        {
            issues.Add(ValidationIssue.Warning(
                $"{name} writes '{field}' of {cached.Name} to {entry.Target.Text}, and {missing.Count} of the ids it holds name records cache version '{references.Version}' does not hold: {Listed(missing)}. A record whose value is one of them is held, since its reference would point at nothing."));
        }
    }

    /// <summary>Whether an entity type (<c>master-data--Wellbore</c>) is one a relationship allows; a group type alone (<c>dataset</c>) allows every entity of the group.</summary>
    private static bool Points(IReadOnlyList<string> relationships, string entityType) => Rendering.IdValues.Allows(relationships, entityType);

    private static void CheckColumns(MappingDefinition mapping, IReadOnlyDictionary<string, IReadOnlySet<string>> sourceColumns, List<ValidationIssue> issues, string where)
    {
        void Require(DatasetColumn column, string reader)
        {
            var scope = column.Child ?? SourceDatasets.Record;
            if (!sourceColumns.TryGetValue(scope, out var columns))
            {
                issues.Add(ValidationIssue.Error($"{where}: {reader} reads child dataset '{column.Child}', which the flow does not declare under source.datasets."));
            }
            else if (!columns.Contains(column.Column))
            {
                issues.Add(ValidationIssue.Error(
                    $"{where}: {reader} reads {column}, which {(column.Child is null ? "the record table" : "child dataset '" + column.Child + "'")} does not hold. Columns: {string.Join(", ", columns.OrderBy(c => c, StringComparer.Ordinal))}."));
            }
        }

        foreach (var key in mapping.Dataset.Key)
        {
            Require(new DatasetColumn(null, key), "dataset.key");
        }

        if (mapping.Dataset.Label is { } label)
        {
            foreach (Match token in MappingMapper.LabelToken().Matches(label))
            {
                Require(new DatasetColumn(null, token.Groups["column"].Value), "dataset.label");
            }
        }

        foreach (var entry in mapping.Entries)
        {
            if (entry.IsRepeater && !sourceColumns.ContainsKey(entry.Source!.Child!))
            {
                issues.Add(ValidationIssue.Error($"{where}: {entry.Where} repeats child dataset '{entry.Source.Child}', which the flow does not declare under source.datasets."));
            }

            foreach (var column in entry.Columns)
            {
                Require(column, entry.Where);
            }
        }
    }

    [GeneratedRegex(@"^(?<id>[\w\-\.]+:(?<entity>[\w\-\.]+--[\w\-\.]+):[\w\-\.\%]+):?[0-9]*$")]
    private static partial Regex RecordId();
}

public enum IssueSeverity
{
    Warning,
    Error,
}

/// <summary>
/// One finding of a check. <paramref name="Target"/> is the template variable it concerns (<c>osdu.data.FacilityName</c>)
/// for a finding about one variable, so a view of the template can show it there; null for a finding about the mapping as
/// a whole, such as a parameter.
/// </summary>
public sealed record ValidationIssue(IssueSeverity Severity, string Message, string? Target = null)
{
    public static ValidationIssue Error(string message, string? target = null) => new(IssueSeverity.Error, message, target);

    public static ValidationIssue Warning(string message, string? target = null) => new(IssueSeverity.Warning, message, target);

    public override string ToString() => $"{Severity.ToString().ToLowerInvariant()}: {Message}";
}
