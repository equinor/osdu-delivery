namespace SqlFlow.Delivery.Templates;

/// <summary>
/// What a difference between two versions of a template asks of a mapping written for the older one, in the words a report
/// on moving a mapping uses (osdu/docs/reference/concepts/template-versions.md, The report): a few words on what happened to
/// a variable, a sentence on each field that changed with its value on both sides, and what to do about a variable that was
/// added or removed. Values are written through a <c>quote</c> the caller gives, so Markdown can set them as code and JSON as
/// plain text.
/// </summary>
public static class TemplateChangeAdvice
{
    /// <summary>The field of a change that says whether the variable is required.</summary>
    public const string RequiredField = "required";

    /// <summary>The field of a change that holds the variable's description, where OSDU writes a deprecation (<see cref="TemplateDeprecation"/>).</summary>
    public const string DescriptionField = "description";

    private const string Yes = "yes";

    /// <summary>
    /// What happened to the variable, in a few words: what a removed one was, what a new one is, or which fields of a changed
    /// one differ. A variable a mapping does not fill says who does.
    /// </summary>
    public static string Summary(TemplateVariableChange change, Func<string, string> quote)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(quote);
        var who = change.Role == TemplateVariableRole.Mapping ? string.Empty : $" ({Writer(change.Role)})";
        return change.Change switch
        {
            TemplateVariableChangeKind.Removed => $"removed; it was {Described(change, removed: true, quote)}{who}",
            TemplateVariableChangeKind.Added => $"added: {Described(change, removed: false, quote)}{who}",
            _ => $"changed: {string.Join(", ", change.Fields.Select(FieldLabel))}{who}",
        };
    }

    /// <summary>The field a change is said to differ in, a description that deprecates or restores its property said so.</summary>
    private static string FieldLabel(TemplateFieldChange field)
        => field.Field != DescriptionField ? Label(field.Field) : Deprecates(field) ? "description, now deprecated" : Restores(field) ? "description, no longer deprecated" : field.Field;

    /// <summary>Whether a field change is a description that now marks its variable deprecated, where it did not before.</summary>
    public static bool Deprecates(TemplateFieldChange field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.Field == DescriptionField && TemplateDeprecation.IsDeprecated(field.After) && !TemplateDeprecation.IsDeprecated(field.Before);
    }

    /// <summary>Whether a field change is a description that no longer marks its variable deprecated.</summary>
    private static bool Restores(TemplateFieldChange field)
        => field.Field == DescriptionField && TemplateDeprecation.IsDeprecated(field.Before) && !TemplateDeprecation.IsDeprecated(field.After);

    /// <summary>
    /// What a mapping does about a variable that was added or removed; null for a changed one, whose fields each say it
    /// (<see cref="Note"/>).
    /// </summary>
    public static string? Action(TemplateVariableChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Change == TemplateVariableChangeKind.Removed)
        {
            return change.Role == TemplateVariableRole.Mapping
                ? "Remove what writes it: the newer version has no such property."
                : $"No mapping writes it: {Writer(change.Role)}.";
        }

        if (change.Change == TemplateVariableChangeKind.Added)
        {
            if (change.Role != TemplateVariableRole.Mapping)
            {
                return $"Nothing for a mapping to do: {Writer(change.Role)}.";
            }

            var required = change.Fields.Any(f => f.Field == RequiredField);
            return change.Impact == TemplateChangeImpact.Breaking
                ? "Fill it on every record: the newer version requires it inside an object a mapping of the older version already writes."
                : required
                    ? "Fill it wherever the object that holds it is filled: it is required there, and that object is new in this version."
                    : "A mapping may fill it.";
        }

        return null;
    }

    /// <summary>One sentence on a field of a changed variable: its value on both sides, and what that asks of a mapping.</summary>
    public static string Note(TemplateFieldChange field, Func<string, string> quote)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(quote);
        var before = field.Before;
        var after = field.After;
        return field.Field switch
        {
            "type" => $"Its type is {Value(after)}, was {Value(before)}: what fills it has to give a value of the new type.",
            "format" => before is null
                ? $"It now has format {quote(after!)}: its values have to be written in it."
                : after is null
                    ? $"It no longer has a format; it had {quote(before)}."
                    : $"Its format is {quote(after)}, was {quote(before)}: its values have to be written in the new one.",
            "pattern" => after is null
                ? "It no longer has a pattern."
                : before is null
                    ? $"Its values now have to match {quote(after)}."
                    : $"Its values have to match {quote(after)}, where they matched {quote(before)}.",
            "keyValueType" => $"Its free keys take {Value(after, "any value")}, took {Value(before, "any value")}.",
            "unitContext" => $"Its unit context is {Value(after, "none")}, was {Value(before, "none")}: check the unit its values are written in.",
            "role" => Role(before, after),
            "nested" => after == Yes
                ? "It is now a list of objects inside the items of another list, which a mapping does not fill."
                : "It is no longer inside the items of another list: a mapping may fill it as a list of its own.",
            RequiredField => after == RequiredField ? "It is now required: fill it on every record." : "It is no longer required.",
            "relationships" => Relationships(before, after, quote),
            "title" => $"Its title is {Value(after, "none")}, was {Value(before, "none")}.",
            DescriptionField => Deprecates(field)
                ? "It is now deprecated: OSDU still accepts it, and its description says what replaces it."
                : Restores(field)
                    ? "It is no longer deprecated."
                    : before is null
                        ? "It now has a description."
                        : after is null ? "It no longer has a description." : "Its description reads differently.",
            _ => $"Its {field.Field} is {Value(after, "none")}, was {Value(before, "none")}.",
        };

        string Value(string? value, string none = "none") => value is null ? none : quote(value);
    }

    /// <summary>The name a field of a change is read by (<c>keyValueType</c> is the free key type).</summary>
    public static string Label(string field) => field switch
    {
        "keyValueType" => "free key type",
        "unitContext" => "unit context",
        "role" => "written by",
        "nested" => "nested list",
        "relationships" => "points to",
        _ => field,
    };

    /// <summary>Who writes a variable no mapping fills.</summary>
    public static string Writer(TemplateVariableRole role) => role switch
    {
        TemplateVariableRole.Engine => "OSDU Delivery writes it",
        TemplateVariableRole.Osdu => "OSDU sets it",
        _ => "a mapping fills it",
    };

    /// <summary>A new or a removed variable, from the fields the comparison describes it by: its type, whether it is required, what it points to.</summary>
    private static string Described(TemplateVariableChange change, bool removed, Func<string, string> quote)
    {
        string? Side(string field) => change.Fields.FirstOrDefault(f => f.Field == field) is { } found ? removed ? found.Before : found.After : null;
        var parts = new List<string> { Side("type") is { } type ? quote(type) : "a variable" };
        if (Side(RequiredField) is not null)
        {
            parts.Add("required");
        }

        if (Side("relationships") is { } relationships)
        {
            parts.Add($"pointing to {string.Join(", ", Split(relationships).Select(quote))}");
        }

        return string.Join(", ", parts);
    }

    private static string Role(string? before, string? after)
    {
        var was = Parse(before);
        var now = Parse(after);
        if (was == TemplateVariableRole.Mapping)
        {
            return $"{Capitalized(Writer(now))} now, not a mapping: remove the entry that writes it.";
        }

        return now == TemplateVariableRole.Mapping
            ? $"A mapping fills it now; before, {Writer(was)}. Add an entry when the record is to carry it."
            : $"{Capitalized(Writer(now))} now; before, {Writer(was)}.";

        static TemplateVariableRole Parse(string? text) => Enum.TryParse<TemplateVariableRole>(text, out var role) ? role : TemplateVariableRole.Mapping;
    }

    private static string Relationships(string? before, string? after, Func<string, string> quote)
    {
        var was = Split(before);
        var now = Split(after);
        var dropped = was.Except(now, StringComparer.Ordinal).ToList();
        var added = now.Except(was, StringComparer.Ordinal).ToList();
        var parts = new List<string>();
        if (dropped.Count > 0)
        {
            parts.Add($"It no longer points to {string.Join(", ", dropped.Select(quote))}: a value that points there no longer fits it.");
        }

        if (added.Count > 0)
        {
            parts.Add($"It may now also point to {string.Join(", ", added.Select(quote))}.");
        }

        return parts.Count == 0 ? "What it points to is listed differently." : string.Join(" ", parts);
    }

    /// <summary>The kinds a relationships field lists, as the comparison joins them.</summary>
    private static List<string> Split(string? joined)
        => joined is null ? [] : joined.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
