using System.Text.Json.Nodes;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Rendering;

// Values made of other nodes, each evaluated as a node of its own (osdu/docs/reference/flow/mapping-values.md): a node
// marked $restrictWhenMissing, asked again as a required node when it gives nothing; a $coalesce node, the first of its
// alternatives that gives a value; a list of values, item by item; and an item of a list of objects, from its
// properties.
internal static partial class EntryValues
{
    /// <summary>
    /// The value of a node marked <c>$restrictWhenMissing</c> (osdu/docs/reference/flow/mapping-restricted-access.md): the node is
    /// evaluated as <c>$required: false</c> evaluates it, so a value it should give and does not leaves the variable out, and
    /// everything that holds the record whatever <c>$required</c> says (a value that cannot be read, several records answering
    /// to it, a parent OSDU does not hold) still holds it. When the node left its variable out for want of a value (its
    /// <c>$when</c> held, it held nothing, and it waits for no answer from the platform), it is asked again as a required node,
    /// whose hold says why, as <see cref="WhyEmpty"/> asks it, and the trail notes the record restricted for that reason. A node
    /// left out for another reason (an assertion whose <c>onFail</c> is <c>omit</c>) gives no reason, and restricts nothing. The
    /// second evaluation reads what the first already read, so it asks the platform nothing new and records nothing.
    /// </summary>
    private static JsonNode? Restricting(
        MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched, out bool applied)
    {
        var held = holds.Count;
        var waiting = searched.Unanswered.Count;
        var value = Evaluate(entry with { Required = false, RestrictWhenMissing = false }, root, item, renderer, holds, usages, searched, out applied);
        if (value is not null || !applied || holds.Count > held || searched.Unanswered.Count > waiting)
        {
            return value;
        }

        var said = new List<string>();
        Evaluate(entry with { RestrictWhenMissing = false, AppliesWhen = null }, root, item, renderer, said, [], new RenderTrail { Base = searched.Base });
        if (said.Count > 0)
        {
            searched.Restrict(Missed(said[0], entry.Target.Text));
        }

        return null;
    }

    /// <summary>
    /// What a required node's hold says, as a record restricted for it says it: the variable, then why it has no value, without
    /// the clause that the entry is required (<c>osdu.tags['dra.securityClassification']: dataset.Lra_securityClassification is empty</c>).
    /// </summary>
    private static string Missed(string said, string path)
    {
        var reason = said.StartsWith(path + ": ", StringComparison.Ordinal) ? said[(path.Length + 2)..] : said;
        var clause = reason.IndexOf(RequiredClause, StringComparison.Ordinal);
        return $"{path}: {(clause < 0 ? reason : reason.Remove(clause, RequiredClause.Length))}";
    }

    /// <summary>
    /// The value of a <c>$coalesce</c> node: the first of its alternatives that gives one, each tried as an optional node
    /// of its own. An alternative that gives nothing (an empty column, no record in the cache, a key its table does not
    /// list, an id the partition holds no record under) passes to the next. One that meets a mistake (a date that is not a
    /// date, a value several records answer to) holds the record as it would on its own, since what it would have given
    /// is unknown, not absent. One still waiting for a search stops the node there, and the render finishes once the
    /// answer is in, so a later alternative never stands in for an earlier one that has not been asked yet. What every
    /// alternative tried read from the cache is recorded, so a later version that would let an earlier one give a value
    /// reaches the record. When none gives one, the node's required flag decides, naming why each gave nothing.
    /// </summary>
    private static JsonNode? Coalesced(
        MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched)
    {
        var alternatives = Alternatives(entry);
        for (var i = 0; i < alternatives.Count; i++)
        {
            var mistakes = new List<string>();
            var waiting = searched.Unanswered.Count;
            var read = usages.Count;
            var value = Evaluate(alternatives[i], root, item, renderer, mistakes, usages, searched);
            if (mistakes.Count > 0)
            {
                holds.AddRange(mistakes);
                return null;
            }

            if (searched.Unanswered.Count > waiting)
            {
                return null;
            }

            if (value is not null)
            {
                var unverified = usages.Skip(read).Any(u => u.Kind == CacheUsageKind.Unverified);
                // The origin names what the alternative reads; whether the id it wrote was unverified is the choice's own flag.
                searched.Choose(entry.Target.Text, i + 1, alternatives.Count, Origin(alternatives[i] with { Unverified = false }), unverified);
                return value;
            }
        }

        if (entry.Required)
        {
            var path = entry.Target.Text;
            var reasons = new List<string>(alternatives.Count);
            for (var i = 0; i < alternatives.Count; i++)
            {
                // Asked again as a required node, each alternative says why it gave nothing; what it reads is already known.
                var said = new List<string>();
                Evaluate(alternatives[i] with { Required = true }, root, item, renderer, said, [], new RenderTrail { Base = searched.Base });
                reasons.Add($"{i + 1}. {(said.Count > 0 ? Reason(said[0], path) : "gives no value")}");
            }

            holds.Add($"{path}: none of the {alternatives.Count} alternatives of $coalesce gives a value, and the entry is required: {string.Join("; ", reasons)}");
        }

        return null;
    }

    /// <summary>
    /// A <c>$coalesce</c> node's alternatives as nodes of their own, in order: the entry itself without the node's settings,
    /// then the rest. Each is optional, so an alternative that gives nothing passes to the next.
    /// </summary>
    internal static IReadOnlyList<MappingEntry> Alternatives(MappingEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return [entry with { Alternatives = [], AppliesWhen = null, Required = false, RestrictWhenMissing = false, Location = null, Assertions = [] }, .. entry.Alternatives];
    }

    /// <summary>
    /// The value of a list of values: what each item gives, one after another in the order the list writes them, a value
    /// given twice (whatever its case) written once, where it is first given. An item that gives nothing adds nothing; one
    /// that holds holds the record, as it would on its own. A list none of whose items gives a value is left out.
    /// </summary>
    private static JsonNode? Listed(
        MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched)
    {
        var values = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in entry.Parts)
        {
            if (Evaluate(part, root, item, renderer, holds, usages, searched) is not { } given)
            {
                continue;
            }

            foreach (var value in given is JsonArray set ? set.ToList() : [given])
            {
                if (value is null)
                {
                    continue;
                }

                var key = value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text.Trim() : CanonicalJson.ToString(value);
                if (seen.Add(key))
                {
                    values.Add(value.DeepClone());
                }
            }
        }

        return values.Count > 0 ? values : null;
    }

    /// <summary>
    /// The value of an item of a list of objects: the object its properties give, each written at its place in the item as
    /// the record's own properties are, or null when none of them gives one, which leaves the item out of the list. A list
    /// the item defines and that gives nothing is written empty in an item that is written. A property that holds holds the
    /// record, as it would anywhere in it.
    /// </summary>
    private static JsonNode? Assembled(
        MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched)
    {
        var written = new JsonObject();
        List<MappingEntry>? empty = null;
        foreach (var property in entry.Properties)
        {
            var held = holds.Count;
            var waiting = searched.Unanswered.Count;
            if (Evaluate(property, root, item, renderer, holds, usages, searched) is { } value)
            {
                MappingRenderer.SetPath(written, property.Target.WithinItem, value);
            }
            else if (holds.Count == held && searched.Unanswered.Count == waiting && renderer.WritesList(property))
            {
                (empty ??= []).Add(property);
            }
        }

        if (written.Count == 0)
        {
            return null;
        }

        foreach (var property in empty ?? [])
        {
            MappingRenderer.SetEmptyList(written, property.Target.WithinItem);
        }

        return written;
    }
}
