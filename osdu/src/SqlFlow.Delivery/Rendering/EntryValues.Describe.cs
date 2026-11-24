using System.Text.Json.Nodes;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Rendering;

// A node told in words, with no row to read: the value of an entry in a record's shape, a placeholder standing for what
// a row or the cache would give, and where a node's value comes from (its column, its lookup and its modifiers) as
// placeholders and holds name it.
internal static partial class EntryValues
{
    /// <summary>
    /// The value of <paramref name="entry"/> in a record's shape, without a row: a static value converted exactly as a
    /// render converts it, or a placeholder naming the type the template gives the variable and where the value comes from
    /// (a list of one where the variable is a list of values). A value that could not be written whatever the row, a single
    /// value on an object, is noted and left out, as a render holds it.
    /// </summary>
    public static JsonNode? Describe(MappingEntry entry, MappingRenderer renderer, List<string> notes)
    {
        var path = entry.Target.Text;
        var property = renderer.Schema.Resolve(entry.Target.SchemaPath);
        if (entry.IsList)
        {
            // Each item as it is drawn on its own, one after another: a literal as it renders, a node as its placeholder.
            var items = new JsonArray();
            foreach (var part in entry.Parts)
            {
                var drawn = Describe(part, renderer, notes);
                foreach (var value in drawn is JsonArray set ? set.ToList() : [drawn])
                {
                    if (value is not null)
                    {
                        items.Add(value.DeepClone());
                    }
                }
            }

            return items.Count > 0 ? items : null;
        }

        if (entry.IsObject)
        {
            // An item of a list of objects as its properties are drawn, each at its place in the item.
            var drawn = new JsonObject();
            foreach (var itemProperty in entry.Properties)
            {
                if (Describe(itemProperty, renderer, notes) is { } value)
                {
                    MappingRenderer.SetPath(drawn, itemProperty.Target.WithinItem, value);
                }
            }

            return drawn.Count > 0 ? drawn : null;
        }

        if (entry.Static is { } fixedValue)
        {
            return Convert(Expand(fixedValue, renderer), property, path, notes);
        }

        var type = property?.Type ?? SchemaType.Any;
        switch (type)
        {
            case SchemaType.String:
                return JsonValue.Create(Placeholder(entry, property!.Format is { } format ? $"{format} string" : Name(type)));
            case SchemaType.Number:
            case SchemaType.Integer:
            case SchemaType.Boolean:
                return JsonValue.Create(Placeholder(entry, Name(type)));
            case SchemaType.Any:
                return JsonValue.Create(Placeholder(entry, "value"));
        }

        if (type == SchemaType.Array && property!.ItemScalarType is { } itemType && itemType != SchemaType.Object)
        {
            var itemFormat = property?.ItemFormat;
            return new JsonArray(JsonValue.Create(Placeholder(entry, itemFormat is not null ? $"{itemFormat} string" : Name(itemType))));
        }

        // An object, or a list of objects: a cached field, a section of the record's parent or of the record a fragment adds
        // to can carry one whole, and a dataset column never can.
        if (entry.ValueNodes.Any(node => node.Source?.Kind is MappingSourceKind.Cache or MappingSourceKind.Parent or MappingSourceKind.Base))
        {
            return JsonValue.Create(Placeholder(entry, Name(type)));
        }

        notes.Add($"{path}: a single value from {entry.Source} cannot be written where the template takes an {Name(type)}");
        return null;
    }

    /// <summary>
    /// A placeholder for a value read from a row or the cache: <c>&lt;number from dataset.depth | trim, optional&gt;</c>, and
    /// for a <c>$coalesce</c> node its alternatives in order: <c>&lt;string from dataset.log_name, else dataset.log_source&gt;</c>.
    /// </summary>
    private static string Placeholder(MappingEntry entry, string type)
    {
        var origin = string.Join(", else ", entry.ValueNodes.Select(Origin));
        var optional = entry.Required ? string.Empty : ", optional";
        var when = entry.AppliesWhen is { } condition ? $", when {condition}" : string.Empty;
        return $"<{type} from {origin}{optional}{when}>";
    }

    /// <summary>Where one value node reads its value, as a placeholder names it; a literal alternative is its text.</summary>
    internal static string Origin(MappingEntry node)
    {
        if (node.Static is { } literal)
        {
            return literal.ToJsonString();
        }

        var source = node.Source!;
        if (node.FindAll is { } findAll)
        {
            return $"{source} of every {findAll.Type} row where {FindAllText(node, findAll)}";
        }

        var origin = source.Resolves
            ? node.FindBy.Count == 0 ? source.ToString() : $"{source} by {FindText(node)}"
            : node.Modifiers.Count == 0 ? source.ToString() : $"{source} | {ModifierText(node.Modifiers)}";
        return node.Unverified ? origin + " (unverified)" : origin;
    }

    /// <summary>A <c>$findAll</c>'s lines as one phrase, its column operand with the modifiers that change it.</summary>
    private static string FindAllText(MappingEntry node, FindAllQuery findAll)
    {
        var operand = findAll.Operand.Column is { } column && node.Modifiers.Count > 0
            ? $"({column} | {ModifierText(node.Modifiers)})"
            : findAll.Operand.ToString();
        return string.Join(" and ", findAll.Empty.Select(field => $"{ReferenceField.Normalize(field)} is empty")
            .Concat(findAll.Contains.Select(line => $"{ReferenceField.Normalize(line.Field)} {FindAllContains.Word} {ReferenceField.Normalize(line.Part)}"))
            .Prepend($"{ReferenceField.Normalize(findAll.Field)} = {operand}"));
    }

    /// <summary>
    /// A cache source's findBy lines as one phrase, consecutive lines comparing the same value run together
    /// (<c>Code/Name = (dataset.unit | trim)</c>); the modifiers change the value compared, so they sit with it. A line of
    /// several comparisons reads as them joined by and, in brackets beside other lines:
    /// <c>(Well = dataset.well and Mnemonic = dataset.mnemonic) or Mnemonic = dataset.mnemonic</c>.
    /// </summary>
    private static string FindText(MappingEntry entry)
    {
        // A cached field is stored without its data. root, so either spelling names it; a search compares the path
        // exactly as it is written, so it is shown as written.
        var searched = entry.Source?.Kind == MappingSourceKind.Search;
        string Field(FindBy find) => searched ? find.Field : ReferenceField.Normalize(find.Field);
        string Operand(FindBy find) => find.Literal is not null
            ? $"'{find.Literal}'"
            : entry.Modifiers.Count == 0 ? find.Column!.ToString() : $"({find.Column} | {ModifierText(entry.Modifiers)})";

        var groups = new List<(List<string> Fields, string Operand, string? Composite)>();
        foreach (var find in entry.FindBy)
        {
            if (find.IsComposite)
            {
                groups.Add(([], string.Empty, string.Join(" and ", find.Comparisons.Select(c => $"{Field(c)} = {Operand(c)}"))));
                continue;
            }

            var operand = Operand(find);
            if (groups.Count > 0 && groups[^1].Composite is null && string.Equals(groups[^1].Operand, operand, StringComparison.Ordinal))
            {
                groups[^1].Fields.Add(Field(find));
            }
            else
            {
                groups.Add(([Field(find)], operand, null));
            }
        }

        return string.Join(" or ", groups.Select(g => g.Composite is null
            ? $"{string.Join('/', g.Fields)} = {g.Operand}"
            : groups.Count == 1 ? g.Composite : $"({g.Composite})"));
    }

    /// <summary>The modifiers as a pipeline; a replace of more than a few values is counted rather than listed.</summary>
    private static string ModifierText(IReadOnlyList<Modifier> modifiers) => string.Join(" | ", modifiers.Select(modifier => modifier.Kind switch
    {
        ModifierKind.Split => $"split('{modifier.Separator}', {modifier.Part})",
        ModifierKind.Replace when modifier.Replacements.Count > 3 => $"replace({modifier.Replacements.Count} values"
            + (modifier.Otherwise.Kind == ReplaceFallbackKind.Keep ? string.Empty : $"; otherwise {modifier.Otherwise}") + ")",
        _ => modifier.ToString(),
    }));
}
