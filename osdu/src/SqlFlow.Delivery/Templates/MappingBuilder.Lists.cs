using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;

namespace SqlFlow.Delivery.Templates;

// What a list and a coalesce entry lack: items or alternatives, each one value of its own input that is complete
// as an entry of that input would be, a fixed item, and an item of a list of objects with the properties it fills.
public static partial class MappingBuilder
{
    /// <summary>
    /// What a list lacks: items, each one value of its own input that is complete as an entry of that input would be, a
    /// fixed item holding one value; or, for a list of objects, items that are each an object input or a fixed object
    /// (<see cref="ObjectItemIssues"/>). The access lists keep a fixed value no record goes without, and the legal lists
    /// hold fixed values alone, since the legal service checks them before a run.
    /// </summary>
    private static void ListIssues(MappingDraft draft, MappingDraftEntry entry, string target, string? scope, Action<string, string?> error)
    {
        var objects = IsObjectList(entry);
        if (objects && TemplatePath.TryParse(target, out var path, out _) && path!.Repeater is { } outer)
        {
            error($"{target} is a list of objects inside the items of {outer.Text}, and a list of objects inside the items of another array is not supported.", target);
            return;
        }

        if (target is "osdu.legal.legaltags" or "osdu.legal.otherRelevantDataCountries")
        {
            error($"{target}: the legal service checks a record's tags and countries before a run, which it can only do for a list the same on every record; give fixed values.", target);
        }
        else if (target is "osdu.acl.owners" or "osdu.acl.viewers"
            && !entry.Items.Any(item => item.Input == MappingDraftInput.Static && string.IsNullOrWhiteSpace(item.When)))
        {
            error($"{target}: every record carries it, so list at least one fixed value without a condition beside the values entries read.", target);
        }

        if (entry.Items.Count == 0)
        {
            error($"{target}: add the list's items, each a fixed value or a value read from the dataset, the cache, a lookup or a search, or for a list of objects each an object whose properties read them.", target);
        }

        if (!string.IsNullOrWhiteSpace(entry.When))
        {
            error(objects
                ? $"{target}: a list of objects is written as its items alone, so it takes no condition; give the properties that need one a condition of their own."
                : $"{target}: a list of values is written as its items alone, so it takes no condition; give the items that need one a condition of their own.", target);
        }

        for (var i = 0; i < entry.Items.Count; i++)
        {
            var item = entry.Items[i];
            var name = $"{target} item {i + 1}";
            if (objects)
            {
                ObjectItemIssues(draft, item, name, target, scope, error);
                continue;
            }

            if (item.Input is MappingDraftInput.Repeat or MappingDraftInput.Coalesce or MappingDraftInput.List)
            {
                error($"{name}: an item gives values of its own; choose a fixed value, a dataset column, an expression, the cache, a lookup or a search.", target);
                continue;
            }

            if (item.Input == MappingDraftInput.Static)
            {
                ItemLiteralIssue(item, name, target, error);
            }
            else
            {
                ValueIssues(draft, item, name, target, scope, error);
            }

            if (item.Assertions.Count > 0)
            {
                error($"{name}: an assertion judges the value of a property, and an item gives one of the values of the list, which has no value of its own to judge; a list of values takes no assertions.", target);
            }

            if (!string.IsNullOrWhiteSpace(item.When) && MappingMapper.ReadCondition(item.When.Trim(), MappingMapper.WhenKey, scope, out var whenProblem) is null)
            {
                error($"{name}: {whenProblem}", target);
            }
        }
    }

    /// <summary>A fixed item of a list: one text, number or true/false, never an object or a list of its own.</summary>
    private static void ItemLiteralIssue(MappingDraftEntry item, string name, string target, Action<string, string?> error)
    {
        JsonNode? node;
        try
        {
            node = string.IsNullOrWhiteSpace(item.Static) ? null : JsonNode.Parse(item.Static);
        }
        catch (JsonException)
        {
            error($"{name}: the fixed value is not valid JSON.", target);
            return;
        }

        if (node is null || (node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length == 0))
        {
            error($"{name}: give the fixed value.", target);
        }
        else if (node is not JsonValue)
        {
            error($"{name}: an item of a list of values is one value, a text, a number or true/false; a list of objects holds objects, each an object input or a fixed object.", target);
        }
        else if (MappingMapper.LiteralTokenProblem(node) is { } problem)
        {
            error($"{name}: {problem}", target);
        }
    }

    /// <summary>
    /// What an item of a list of objects lacks: an object input whose properties each fill a variable inside the list's items
    /// once and are complete as entries of their own input would be, or a fixed object. An item takes no condition of its
    /// own, since a condition goes on the properties it decides for. A property never repeats rows or holds a list of objects
    /// of its own, since a path steps into one array at most. What a property lacks concerns the list, whose entry it is
    /// part of, so it names the list's target.
    /// </summary>
    private static void ObjectItemIssues(MappingDraft draft, MappingDraftEntry item, string name, string target, string? scope, Action<string, string?> error)
    {
        if (item.Input == MappingDraftInput.Static)
        {
            ObjectLiteralIssue(item, name, target, error);
            return;
        }

        if (item.Input != MappingDraftInput.Group)
        {
            error($"{name}: the items of a list are all objects or all values, and this list holds objects; make the item an object whose properties read the values.", target);
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.When) || !string.IsNullOrWhiteSpace(item.Description) || !item.Required || item.RestrictWhenMissing)
        {
            error($"{name}: an object item is written as its properties alone, so it takes no condition, required flag, {MappingMapper.RestrictWhenMissingKey} or description; give them to its properties.", target);
        }

        if (item.Properties.Count == 0)
        {
            error($"{name}: add the item's properties, each a variable of the items of {target}, such as {target}[].Name.", target);
            return;
        }

        void AtList(string message, string? _) => error(message, target);
        var placed = new List<(string Name, TreeSlot Slot)>();
        var leftOut = new List<LeftOutEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in item.Properties)
        {
            var propertyTarget = property.Target?.Trim() ?? string.Empty;
            if (!TemplatePath.TryParse(propertyTarget, out var path, out _) || path!.Repeater?.Text != target)
            {
                error($"{name}: {(propertyTarget.Length == 0 ? "a property names no variable" : $"{propertyTarget} is not a variable of the items of {target}")}; an item's property fills {target}[].<name>.", target);
                continue;
            }

            var at = $"{propertyTarget} in {name}";
            if (!seen.Add(propertyTarget))
            {
                error($"{at} has more than one entry.", target);
                continue;
            }

            Place(placed, path.WithinItem, property, propertyTarget, leftOut);
            switch (property.Input)
            {
                case MappingDraftInput.Repeat:
                    error($"{at} repeats a child dataset's rows, and a repeated array inside an item of a list is not supported.", target);
                    continue;
                case MappingDraftInput.Group:
                    error($"{at}: an object input is an item of a list of objects; the item's own properties are its entries.", target);
                    continue;
                case MappingDraftInput.List when IsObjectList(property):
                    error($"{at} is a list of objects inside an item of a list, which is not supported.", target);
                    continue;
                case MappingDraftInput.List:
                    ListIssues(draft, property, at, scope, AtList);
                    break;
                case MappingDraftInput.Coalesce:
                    CoalesceIssues(draft, property, at, scope, AtList);
                    break;
                default:
                    ValueIssues(draft, property, at, target, scope, error);
                    break;
            }

            foreach (var problem in AssertionProblems(property))
            {
                error($"{at}: {problem}", target);
            }

            if (!string.IsNullOrWhiteSpace(property.When) && MappingMapper.ReadCondition(property.When.Trim(), MappingMapper.WhenKey, scope, out var whenProblem) is null)
            {
                error($"{at}: {whenProblem}", target);
            }
        }

        foreach (var left in leftOut.Where(l => l.Reason is not null))
        {
            error($"{name}: {left.Reason}.", target);
        }
    }

    /// <summary>
    /// True for a list of objects: one with a group input, or a fixed object written as the object it is, among its items,
    /// which is how the loader reads the list the draft is written as. A fixed value with a condition or a description is
    /// written under <c>$value</c>, a node, so it makes no list one of objects.
    /// </summary>
    private static bool IsObjectList(MappingDraftEntry list)
        => list.Items.Any(item => item.Input == MappingDraftInput.Group
            || (item.Input == MappingDraftInput.Static && StaticNode(item.Static) is JsonObject { Count: > 0 }
                && string.IsNullOrWhiteSpace(item.When) && string.IsNullOrWhiteSpace(item.Description)));

    /// <summary>A fixed value's JSON, or null when it is empty or not JSON.</summary>
    private static JsonNode? StaticNode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A fixed item of a list of objects: one object, written as the object it is, so it takes no condition or description.</summary>
    private static void ObjectLiteralIssue(MappingDraftEntry item, string name, string target, Action<string, string?> error)
    {
        JsonNode? node;
        try
        {
            node = string.IsNullOrWhiteSpace(item.Static) ? null : JsonNode.Parse(item.Static);
        }
        catch (JsonException)
        {
            error($"{name}: the fixed value is not valid JSON.", target);
            return;
        }

        if (node is not JsonObject { Count: > 0 })
        {
            error($"{name}: a fixed item of a list of objects is one object holding the item's properties, such as {{\"Name\": \"MD\"}}.", target);
        }
        else if (MappingMapper.LiteralTokenProblem(node) is { } problem)
        {
            error($"{name}: {problem}", target);
        }

        if (!string.IsNullOrWhiteSpace(item.When) || !string.IsNullOrWhiteSpace(item.Description))
        {
            error($"{name}: a fixed item is written as the object it is, so it takes no condition or description; make it an object item and give its properties one.", target);
        }
    }

    /// <summary>
    /// What a coalesce entry lacks: two alternatives or more, each one value of its own input that is complete as an entry
    /// of that input would be, and a fixed value only as the last, since it always gives a value.
    /// </summary>
    private static void CoalesceIssues(MappingDraft draft, MappingDraftEntry entry, string target, string? scope, Action<string, string?> error)
    {
        if (entry.Alternatives.Count < 2)
        {
            error($"{target}: {MappingMapper.CoalesceKey} takes the first of two or more alternatives that gives a value; add an alternative, or choose one input.", target);
        }

        for (var i = 0; i < entry.Alternatives.Count; i++)
        {
            var alternative = entry.Alternatives[i];
            var name = $"{target} alternative {i + 1}";
            if (alternative.Input is MappingDraftInput.Repeat or MappingDraftInput.Coalesce or MappingDraftInput.List or MappingDraftInput.Group)
            {
                error($"{name}: an alternative reads one value; choose a dataset column, an expression, the cache, a lookup, a search or a fixed value.", target);
                continue;
            }

            if (alternative.Input == MappingDraftInput.Child)
            {
                error($"{name}: a child link gives nothing while OSDU does not hold the child, which no other value stands in for; make it an entry of its own, or, to link several children, the items of a list.", target);
                continue;
            }

            if (i > 0 && entry.Alternatives[i - 1].Input == MappingDraftInput.Static)
            {
                error($"{name} is never tried: the fixed value before it always gives a value. Make the fixed value the last alternative.", target);
            }

            if (alternative.Assertions.Count > 0)
            {
                error($"{name}: an assertion decides for the whole {MappingMapper.CoalesceKey} entry, whichever alternative gives its value; give it to the entry, not to one of its alternatives.", target);
            }

            ValueIssues(draft, alternative, name, target, scope, error);
        }
    }
}
