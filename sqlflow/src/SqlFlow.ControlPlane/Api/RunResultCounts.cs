using System.Text.Json;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The counts a run's result reports, as a list of runs carries them when asked (<c>GET /runs?resultCounts=true</c>,
/// <see cref="RunSummaryDto.ResultCounts"/>): each whole-number field at the top of the result object, under the name the
/// result gives it. A flow kind whose runs count something other than rows (records sent, held, failed) shows its own
/// figures on a list of its runs from these, without the list carrying every result whole. Text, fractions, arrays and
/// nested objects are left out; a run's own page reads them from the result itself.
/// </summary>
public static class RunResultCounts
{
    /// <summary>
    /// The whole-number fields at the top of <paramref name="resultJson"/>, the first of a name given twice; empty for a
    /// result that reports none. Null for a run that recorded no result, and for a result that is not a JSON object: the
    /// run's page shows such a result as the text it is, and a list has no figure to take from it.
    /// </summary>
    public static IReadOnlyDictionary<string, long>? Of(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(resultJson);
        }
        catch (JsonException)
        {
            // A result is recorded as the run wrote it; text that does not parse is shown whole on the run's page, and a
            // list row that cannot count from it shows no counts rather than failing the page it is on.
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var value))
                {
                    counts.TryAdd(property.Name, value);
                }
            }

            return counts;
        }
    }
}
