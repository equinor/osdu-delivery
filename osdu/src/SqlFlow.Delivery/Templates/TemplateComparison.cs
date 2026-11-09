using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Templates;

/// <summary>What a difference between two versions of a template means for a mapping written for the older one.</summary>
public enum TemplateChangeImpact
{
    /// <summary>Only a title or a description reads differently.</summary>
    Wording,

    /// <summary>Something a mapping may now use; nothing a mapping of the older version relies on changed.</summary>
    Additive,

    /// <summary>A mapping of the older version can stop rendering, or render a record the newer version does not accept.</summary>
    Breaking,
}

/// <summary>Whether a variable is new in the newer version, gone from it, or there in both and different.</summary>
public enum TemplateVariableChangeKind
{
    Added,

    Removed,

    Changed,
}

/// <summary>One thing about a variable that differs between the versions (its type, whether it is required, what it points to), with its value in each.</summary>
public sealed record TemplateFieldChange(string Field, string? Before, string? After, TemplateChangeImpact Impact);

/// <summary>A variable that differs between the versions: how, what that means for a mapping, and every field that differs.</summary>
public sealed record TemplateVariableChange(
    string Path, TemplateVariableChangeKind Change, TemplateChangeImpact Impact, TemplateVariableRole Role, IReadOnlyList<TemplateFieldChange> Fields);

/// <summary>
/// A shared schema file two bundles refer to, paired by its name without the version (<c>AbstractFacility</c>): its path in
/// each bundle, null where a bundle does not refer to it.
/// </summary>
public sealed record ReferencedFilePair(string Name, string? BeforePath, string? AfterPath);

/// <summary>Two versions of a template compared: every variable that differs, in schema order, and how many are the same in both.</summary>
public sealed record TemplateComparison(IReadOnlyList<TemplateVariableChange> Changes, int Unchanged)
{
    public int Count(TemplateChangeImpact impact) => Changes.Count(c => c.Impact == impact);
}

/// <summary>
/// Compares two versions of an OSDU template variable by variable, and tells apart what a mapping author has to act on
/// (<see cref="TemplateChangeImpact.Breaking"/>) from what they may use (<see cref="TemplateChangeImpact.Additive"/>) and
/// what only reads differently (<see cref="TemplateChangeImpact.Wording"/>). Also compares two published schema files as
/// the repository holds them.
/// </summary>
public static class TemplateComparer
{
    /// <summary>Every variable that differs between <paramref name="before"/> and <paramref name="after"/>, in the newer version's order, a removed variable where it stood.</summary>
    public static TemplateComparison Compare(OsduTemplate before, OsduTemplate after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var older = new Dictionary<string, TemplateVariable>(StringComparer.Ordinal);
        foreach (var variable in before.Variables)
        {
            older.TryAdd(variable.Path.Text, variable);
        }

        var newerAt = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < after.Variables.Count; i++)
        {
            newerAt.TryAdd(after.Variables[i].Path.Text, i);
        }

        var ordered = new List<(int Anchor, int Step, int Order, TemplateVariableChange Change)>();
        var unchanged = 0;
        for (var i = 0; i < after.Variables.Count; i++)
        {
            var variable = after.Variables[i];
            if (!older.TryGetValue(variable.Path.Text, out var previous))
            {
                ordered.Add((i, 0, 0, Added(variable, older)));
                continue;
            }

            var fields = Differences(previous, variable);
            if (fields.Count == 0)
            {
                unchanged++;
                continue;
            }

            ordered.Add((i, 0, 0, new TemplateVariableChange(
                variable.Path.Text, TemplateVariableChangeKind.Changed, fields.Max(f => f.Impact), variable.Role, fields)));
        }

        // A removed variable is listed right after the last variable before it that both versions have.
        var anchor = -1;
        for (var i = 0; i < before.Variables.Count; i++)
        {
            var variable = before.Variables[i];
            if (newerAt.TryGetValue(variable.Path.Text, out var at))
            {
                anchor = at;
                continue;
            }

            ordered.Add((anchor, 1, i, new TemplateVariableChange(
                variable.Path.Text, TemplateVariableChangeKind.Removed, TemplateChangeImpact.Breaking, variable.Role,
                Described(variable, added: false, TemplateChangeImpact.Breaking))));
        }

        var changes = ordered.OrderBy(o => o.Anchor).ThenBy(o => o.Step).ThenBy(o => o.Order).Select(o => o.Change).ToList();
        return new TemplateComparison(changes, unchanged);
    }

    /// <summary>
    /// Pairs the shared schema files two bundles read (each list starts with the kind's own file, which is left out) by
    /// their name without the version, so <c>AbstractFacility.1.0.0</c> meets <c>AbstractFacility.1.1.0</c>. A bundle that
    /// refers to several versions of one shared schema pairs those version by version. Ordered by name.
    /// </summary>
    public static IReadOnlyList<ReferencedFilePair> PairReferencedFiles(IReadOnlyList<string> beforeFiles, IReadOnlyList<string> afterFiles)
    {
        ArgumentNullException.ThrowIfNull(beforeFiles);
        ArgumentNullException.ThrowIfNull(afterFiles);
        var before = ByName(beforeFiles.Skip(1));
        var after = ByName(afterFiles.Skip(1));
        var pairs = new List<ReferencedFilePair>();
        foreach (var name in before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var older = before.GetValueOrDefault(name) ?? [];
            var newer = after.GetValueOrDefault(name) ?? [];
            if (older.Count <= 1 && newer.Count <= 1)
            {
                pairs.Add(new ReferencedFilePair(name, older.FirstOrDefault(), newer.FirstOrDefault()));
                continue;
            }

            foreach (var stem in older.Select(FileStem).Union(newer.Select(FileStem), StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                pairs.Add(new ReferencedFilePair(
                    stem,
                    older.FirstOrDefault(p => string.Equals(FileStem(p), stem, StringComparison.Ordinal)),
                    newer.FirstOrDefault(p => string.Equals(FileStem(p), stem, StringComparison.Ordinal))));
            }
        }

        return pairs;
    }

    /// <summary>Whether two schema files hold the same JSON, whatever their whitespace and key order.</summary>
    public static bool SameContent(string beforeText, string afterText)
    {
        ArgumentNullException.ThrowIfNull(beforeText);
        ArgumentNullException.ThrowIfNull(afterText);
        try
        {
            return string.Equals(CanonicalJson.Canonicalize(beforeText), CanonicalJson.Canonicalize(afterText), StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return string.Equals(beforeText, afterText, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Whether two published schema files of a kind say the same thing once each file's own identifiers are set aside: the
    /// kind (<c>x-osdu-schema-source</c>) and the file name (<c>$id</c>) that every new version rewrites.
    /// </summary>
    public static bool DifferOnlyInIdentifiers(string beforeText, string beforeKind, string afterText, string afterKind)
    {
        ArgumentNullException.ThrowIfNull(beforeText);
        ArgumentNullException.ThrowIfNull(afterText);
        ArgumentException.ThrowIfNullOrWhiteSpace(beforeKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(afterKind);
        JsonNode? before;
        JsonNode? after;
        try
        {
            before = JsonNode.Parse(beforeText);
            after = JsonNode.Parse(afterText);
        }
        catch (JsonException)
        {
            return false;
        }

        Rename(before, beforeKind, afterKind, FileName(beforeKind), FileName(afterKind));
        return string.Equals(CanonicalJson.ToString(before), CanonicalJson.ToString(after), StringComparison.Ordinal);
    }

    private static TemplateVariableChange Added(TemplateVariable variable, Dictionary<string, TemplateVariable> older)
    {
        // A new required property is breaking only where a mapping of the older version already writes the object that
        // holds it; under a new object, it is part of something new.
        var holder = HolderPath(variable.Path.Text);
        var holderExisted = holder is null || older.ContainsKey(holder);
        var impact = variable.Required && variable.Role == TemplateVariableRole.Mapping && holderExisted
            ? TemplateChangeImpact.Breaking
            : TemplateChangeImpact.Additive;
        return new TemplateVariableChange(variable.Path.Text, TemplateVariableChangeKind.Added, impact, variable.Role, Described(variable, added: true, impact));
    }

    private static List<TemplateFieldChange> Differences(TemplateVariable a, TemplateVariable b)
    {
        var fields = new List<TemplateFieldChange>();
        Note(fields, "type", ShapeText(a), ShapeText(b), TemplateChangeImpact.Breaking);
        Note(fields, "format", a.Format, b.Format, TemplateChangeImpact.Breaking);
        Note(fields, "pattern", a.Pattern, b.Pattern, TemplateChangeImpact.Breaking);
        Note(fields, "keyValueType", a.KeyValueType, b.KeyValueType, TemplateChangeImpact.Breaking);
        Note(fields, "unitContext", a.UnitContext, b.UnitContext, TemplateChangeImpact.Breaking);
        Note(fields, "role", a.Role.ToString(), b.Role.ToString(), TemplateChangeImpact.Breaking);
        Note(fields, "nested", a.Nested ? "yes" : "no", b.Nested ? "yes" : "no", TemplateChangeImpact.Breaking);
        Note(fields, "required", a.Required ? "required" : "optional", b.Required ? "required" : "optional",
            b.Required ? TemplateChangeImpact.Breaking : TemplateChangeImpact.Additive);
        var dropped = a.Relationships.Except(b.Relationships, StringComparer.Ordinal).Any();
        Note(fields, "relationships", Join(a.Relationships), Join(b.Relationships), dropped ? TemplateChangeImpact.Breaking : TemplateChangeImpact.Additive);
        Note(fields, "title", a.Title, b.Title, TemplateChangeImpact.Wording);
        Note(fields, "description", a.Description, b.Description, TemplateChangeImpact.Wording);
        return fields;
    }

    /// <summary>What a new or a removed variable is, as fields with a value on one side only.</summary>
    private static List<TemplateFieldChange> Described(TemplateVariable variable, bool added, TemplateChangeImpact impact)
    {
        var fields = new List<TemplateFieldChange>();
        void Side(string field, string value) => fields.Add(new TemplateFieldChange(field, added ? null : value, added ? value : null, impact));
        Side("type", ShapeText(variable));
        if (variable.Required)
        {
            Side("required", "required");
        }

        if (Join(variable.Relationships) is { } relationships)
        {
            Side("relationships", relationships);
        }

        return fields;
    }

    private static void Note(List<TemplateFieldChange> fields, string field, string? before, string? after, TemplateChangeImpact impact)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            fields.Add(new TemplateFieldChange(field, before, after, impact));
        }
    }

    private static string ShapeText(TemplateVariable variable) => variable.Shape switch
    {
        TemplateVariableShape.ValueList => $"list of {variable.ItemType ?? "any"}",
        TemplateVariableShape.Group => "object",
        TemplateVariableShape.GroupList => "list of objects",
        TemplateVariableShape.Whole => $"whole {variable.Type}",
        _ => variable.Type,
    };

    private static string? Join(IReadOnlyList<string> values)
        => values.Count == 0 ? null : string.Join(", ", values.Order(StringComparer.Ordinal));

    /// <summary>The variable that holds <paramref name="path"/>, or null for a property of the record itself.</summary>
    private static string? HolderPath(string path)
        => TemplatePath.TryParse(path, out var parsed, out _) ? parsed!.Parent?.Text : null;

    private static Dictionary<string, List<string>> ByName(IEnumerable<string> files)
        => files.Distinct(StringComparer.Ordinal)
            .GroupBy(path => Unversioned(FileStem(path)), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

    private static string FileStem(string path)
    {
        var file = path[(path.LastIndexOf('/') + 1)..];
        return file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? file[..^".json".Length] : file;
    }

    /// <summary><c>AbstractFacility.1.1.0</c> without its version; a name without a three-part version stays as it is.</summary>
    private static string Unversioned(string stem)
    {
        var parts = stem.Split('.');
        return parts.Length > 3 && parts[^3..].All(part => part.Length > 0 && part.All(char.IsAsciiDigit))
            ? string.Join('.', parts[..^3])
            : stem;
    }

    private static string FileName(string kind)
    {
        var path = SchemaBundler.KindPath(kind);
        return path[(path.LastIndexOf('/') + 1)..];
    }

    private static void Rename(JsonNode? node, string fromKind, string toKind, string fromFile, string toFile)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        obj[key] = Renamed(text, fromKind, toKind, fromFile, toFile);
                    }
                    else
                    {
                        Rename(obj[key], fromKind, toKind, fromFile, toFile);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        array[i] = Renamed(text, fromKind, toKind, fromFile, toFile);
                    }
                    else
                    {
                        Rename(array[i], fromKind, toKind, fromFile, toFile);
                    }
                }

                break;
        }
    }

    private static string Renamed(string text, string fromKind, string toKind, string fromFile, string toFile)
        => text.Replace(fromKind, toKind, StringComparison.Ordinal).Replace(fromFile, toFile, StringComparison.Ordinal);
}
