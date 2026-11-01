using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Validation;

/// <summary>
/// An example record of an OSDU kind as the OSDU data definitions publish it (their <c>Examples</c> folder, one record per
/// kind version), which shows how each property of the kind is written.
/// </summary>
/// <param name="Kind">The kind the example is of.</param>
/// <param name="Release">The release of the data definitions it was read from.</param>
/// <param name="Path">Its path in the repository, such as <c>Examples/work-product-component/WellLog.1.4.0.json</c>.</param>
/// <param name="WebUrl">Where a person reads it.</param>
/// <param name="Record">The example record.</param>
public sealed record OfficialExample(string Kind, string Release, string Path, Uri WebUrl, JsonObject Record);

/// <summary>Where the example record the OSDU data definitions publish for a kind is read from.</summary>
public interface IOfficialExamples
{
    /// <summary>
    /// The example the data definitions publish for <paramref name="kind"/> (an <c>osdu</c> kind), or null when they publish
    /// none for it. A repository that cannot be reached throws, so a caller can say why no example is shown.
    /// </summary>
    Task<OfficialExample?> ExampleAsync(string kind, CancellationToken ct = default);
}

/// <summary>How a check reads the example its guidance quotes, which never fails the check.</summary>
public static class OfficialExampleReads
{
    /// <summary>
    /// The example the OSDU data definitions publish for <paramref name="kind"/>, and why none is quoted when one was looked for:
    /// they publish none for the kind, or could not be reached. A kind not of OSDU's own, or a host keeping no copy of the data
    /// definitions (null <paramref name="examples"/>), has no example to look for, and says nothing.
    /// </summary>
    public static async Task<(OfficialExample? Example, string? Note)> ReadAsync(IOfficialExamples? examples, string kind, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (examples is null || OsduDataDefinitions.ExamplePath(kind) is null)
        {
            return (null, null);
        }

        try
        {
            var example = await examples.ExampleAsync(kind, ct).ConfigureAwait(false);
            return (example, example is null ? $"The OSDU data definitions publish no example record of {kind}." : null);
        }
        catch (Exception ex) when (ex is DeliveryException or HttpRequestException or IOException or UnauthorizedAccessException or JsonException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return (null, $"OSDU's example record of {kind} could not be read: {HeaderRedaction.RedactMessage(ex.Message)}");
        }
    }
}
