using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Rendering;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The access a rendered document goes out under (osdu/docs/reference/flow/mapping-restricted-access.md): restricted until
/// complete, its ACL the groups of the access restriction its mapping's <c>restrictedAccess</c> names because values its
/// <c>$restrictWhenMissing</c> nodes should give are missing, with what is missing; or the ACL its mapping gives
/// (<see cref="Own"/>). It travels with the document in its work batch line, and a try that sends the document hands it to the
/// ledger, which keeps it as the access OSDU holds the record under (<c>osdu.Record.IncompleteRestrictionId</c>).
/// </summary>
/// <param name="RestrictionId">The registered access restriction whose groups are the document's ACL; null for its own access.</param>
/// <param name="Restriction">That restriction's name, as the partition registers it; null for its own access.</param>
/// <param name="Missing">What the document goes without, each as a hold would say it without its required clause; empty for its own access.</param>
public sealed record DocumentAccess(int? RestrictionId, string? Restriction, IReadOnlyList<string> Missing)
{
    /// <summary>The most characters of what is missing the ledger keeps (<c>osdu.Record.IncompleteMissing</c>).</summary>
    public const int MaxMissingLength = Data.DeliveryModel.MaxIncompleteMissingLength;

    /// <summary>The document carries the ACL its mapping gives.</summary>
    public static DocumentAccess Own { get; } = new(null, null, []);

    /// <summary>Whether the document goes out restricted until complete.</summary>
    public bool Restricted => RestrictionId is not null;

    /// <summary>The access <paramref name="render"/>'s document goes out under.</summary>
    public static DocumentAccess Of(RenderResult render)
    {
        ArgumentNullException.ThrowIfNull(render);
        return render.Restriction is { } restriction ? new DocumentAccess(restriction.RestrictionId, restriction.Name, render.Missing) : Own;
    }

    /// <summary>
    /// What is missing as the ledger keeps it: each reason on a line of its own, in the order the render met them, within
    /// <see cref="MaxMissingLength"/>. A reason is kept whole while there is room to say how many follow it; the first that does
    /// not fit, and every one after it, is left out, and a last line says how many. A first reason longer than the column alone is
    /// kept as far as it fits. Null for a document with its own access.
    /// </summary>
    public string? MissingText()
    {
        if (!Restricted)
        {
            return null;
        }

        var text = new StringBuilder();
        for (var i = 0; i < Missing.Count; i++)
        {
            var separator = text.Length == 0 ? string.Empty : "\n";
            var following = Missing.Count - i - 1;
            var note = following == 0 ? string.Empty : More(following);
            if (text.Length + separator.Length + Missing[i].Length + note.Length <= MaxMissingLength)
            {
                text.Append(separator).Append(Missing[i]);
                continue;
            }

            if (text.Length == 0)
            {
                // Cut between two characters, never inside a surrogate pair, so what is kept is still a text.
                var cut = MaxMissingLength - note.Length;
                cut -= char.IsHighSurrogate(Missing[i][cut - 1]) ? 1 : 0;
                return Missing[i][..cut] + note;
            }

            // The reasons kept left room to say how many follow them, which is this one and those after it.
            return text.Append(More(Missing.Count - i)).ToString();
        }

        return text.ToString();
    }

    /// <summary>The last line of a <see cref="MissingText"/> that leaves reasons out, saying how many.</summary>
    private static string More(int count) => string.Create(CultureInfo.InvariantCulture, $"\nand {count} more value(s) missing");

    /// <summary>
    /// The issue signature of what is missing (<see cref="ProblemSignature"/>): records restricted for the same values share it
    /// whatever row each read, as records held for the same reason share an issue. Null for a document with its own access.
    /// </summary>
    public long? MissingHash() => MissingText() is { } text ? ProblemSignature.Of(text) : null;

    /// <summary>The access as a work batch line carries it: compact JSON, or null for a document with its own access.</summary>
    public string? Encode()
    {
        if (!Restricted)
        {
            return null;
        }

        var node = new JsonObject
        {
            ["restrictionId"] = RestrictionId,
            ["restriction"] = Restriction,
            ["missing"] = new JsonArray(Missing.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
        };
        return node.ToJsonString();
    }

    /// <summary>
    /// The access a work batch line carries: <see cref="Own"/> for a line that carries none, which every line written before
    /// documents could be restricted is. A field that is not one is refused rather than read as open: the document would be sent
    /// with the access it carries, and the ledger would say otherwise.
    /// </summary>
    public static DocumentAccess Decode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Own;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"A work batch line's restricted access is not JSON ({ex.Message}).", ex);
        }

        if (node is not JsonObject access
            || access["restrictionId"] is not JsonValue id || !id.TryGetValue<int>(out var restrictionId)
            || access["restriction"] is not JsonValue name || !name.TryGetValue<string>(out var restriction) || string.IsNullOrWhiteSpace(restriction)
            || access["missing"] is not JsonArray listed || listed.Count == 0)
        {
            throw new DeliveryException("A work batch line's restricted access names no access restriction (restrictionId, restriction) or no missing value.");
        }

        var missing = new List<string>(listed.Count);
        foreach (var item in listed)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var reason) || string.IsNullOrWhiteSpace(reason))
            {
                throw new DeliveryException("A work batch line's restricted access lists a missing value that is not a text.");
            }

            missing.Add(reason);
        }

        return new DocumentAccess(restrictionId, restriction, missing);
    }
}
