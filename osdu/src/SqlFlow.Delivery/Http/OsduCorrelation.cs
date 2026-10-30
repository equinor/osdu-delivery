using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Http;

/// <summary>
/// The correlation id every OSDU request carries in the <c>correlation-id</c> header, so the services' own logs can be
/// followed back to what sent them (osdu/docs/reference/concepts/request-correlation.md). It is the one request header the
/// platform's Azure services log beside the partition and the content type: they put it on every log line of the request
/// and pass it on to the messages they publish. None of the storage, search, file, legal, workflow or wellbore DDMS
/// OpenAPI descriptions declares the header (the policy service's does), but the storage service answers a request with
/// the id it was sent; a failed call quotes the id the service answered with.
/// <list type="bullet">
/// <item><description>A run's requests carry <c>osdu-delivery-&lt;repository&gt;-&lt;operation&gt;-&lt;run id&gt;</c>: the
/// code of the repository the flow is in, the flow's operation code and the run's id (<see cref="ForRun"/>). A flow in no
/// repository that names a code leaves that segment out.</description></item>
/// <item><description>An operation of the product's own (a read the explorer makes, a probe) carries
/// <c>osdu-delivery-&lt;operation&gt;-&lt;id&gt;</c>, a new id each time (<see cref="Begin"/>).</description></item>
/// <item><description>A request sent outside both carries <c>osdu-delivery-&lt;id&gt;</c>, a new id per request
/// (<see cref="Value"/>).</description></item>
/// </list>
/// </summary>
public static partial class OsduCorrelation
{
    public const string HeaderName = "correlation-id";

    /// <summary>The first segment of every value: what sent the request.</summary>
    public const string Product = "osdu-delivery";

    /// <summary>The longest repository or operation code (the pattern of <see cref="CodePattern"/> says the same).</summary>
    public const int MaxCodeLength = 16;

    internal static readonly AsyncLocal<string?> Ambient = new();

    /// <summary>The value of the run or operation in progress on this flow of execution, or null outside one.</summary>
    public static string? Current => Ambient.Value;

    /// <summary>What a request sent now carries: the value in progress, or a value of its own outside any.</summary>
    public static string Value => Ambient.Value ?? $"{Product}-{Guid.NewGuid():D}";

    /// <summary>What a code must be, for a message refusing one.</summary>
    public static string CodeRule { get; } = string.Create(
        CultureInfo.InvariantCulture,
        $"a lower-case letter, then lower-case letters and digits, at most {MaxCodeLength} in all and no hyphen, so the correlation id splits on its hyphens");

    /// <summary>
    /// Starts a run: every OSDU request sent until the scope is disposed carries <paramref name="runId"/> under the
    /// repository's code and the flow's operation code, whatever was in progress before.
    /// </summary>
    /// <param name="repository">The code of the repository the flow is in, or null when none names one.</param>
    /// <param name="operation">The flow's operation code.</param>
    /// <param name="runId">The run's id.</param>
    public static OsduCorrelationScope ForRun(string? repository, string operation, Guid runId)
    {
        if (repository is not null)
        {
            RequireCode(repository, nameof(repository));
        }

        RequireCode(operation, nameof(operation));
        return new(Compose(repository, operation, runId));
    }

    /// <summary>
    /// Starts one operation of the product's own (<paramref name="operation"/> names it): inside a run its requests are the
    /// run's and carry the run's value; outside one they carry a value of their own, with a new id.
    /// </summary>
    public static OsduCorrelationScope Begin(string operation)
    {
        RequireCode(operation, nameof(operation));
        return new(Ambient.Value ?? Compose(null, operation, Guid.NewGuid()));
    }

    /// <summary>
    /// Starts an operation of the product's own that outlives what started it (a reading kept for every later ask): its requests
    /// carry a value of its own, with a new id, whatever is in progress.
    /// </summary>
    public static OsduCorrelationScope Detach(string operation)
    {
        RequireCode(operation, nameof(operation));
        return new(Compose(null, operation, Guid.NewGuid()));
    }

    /// <summary>Sets the request's header to <see cref="Value"/>, replacing any it carried.</summary>
    public static void ApplyTo(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.Remove(HeaderName);
        request.Headers.TryAddWithoutValidation(HeaderName, Value);
    }

    /// <summary>
    /// The correlation id a text holds, as a person copies it from a platform's log: a whole value, a bare id, or a log line
    /// quoting either. The first found is read; null when the text holds none. A value with one code names its operation,
    /// and with two its repository, then its operation.
    /// </summary>
    public static OsduCorrelationValue? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = ValuePattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var first = match.Groups["first"];
        var second = match.Groups["second"];
        return new OsduCorrelationValue(
            second.Success ? first.Value : null,
            second.Success ? second.Value : first.Success ? first.Value : null,
            Guid.ParseExact(match.Groups["id"].Value, "D"));
    }

    /// <summary>True when <paramref name="code"/> can be a repository or operation code (<see cref="CodeRule"/>).</summary>
    public static bool IsCode(string? code) => code is not null && CodePattern().IsMatch(code);

    private static string Compose(string? repository, string operation, Guid id)
        => repository is null ? $"{Product}-{operation}-{id:D}" : $"{Product}-{repository}-{operation}-{id:D}";

    private static void RequireCode(string code, string parameter)
    {
        if (!IsCode(code))
        {
            throw new ArgumentException($"'{code}' is not a code: {CodeRule}.", parameter);
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9]{0,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex(
        @"\b(?:osdu-delivery-(?:(?<first>[a-z][a-z0-9]{0,15})-(?:(?<second>[a-z][a-z0-9]{0,15})-)?)?)?(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex ValuePattern();
}

/// <summary>What a correlation id names: the repository's and the operation's codes, when it carries them, and its id.</summary>
public sealed record OsduCorrelationValue(string? Repository, string? Operation, Guid Id);

/// <summary>One run's or operation's correlation id; disposing it restores the enclosing one.</summary>
public sealed class OsduCorrelationScope : IDisposable
{
    private readonly string? _previous;

    internal OsduCorrelationScope(string id)
    {
        _previous = OsduCorrelation.Ambient.Value;
        Id = id;
        OsduCorrelation.Ambient.Value = id;
    }

    public string Id { get; }

    public void Dispose() => OsduCorrelation.Ambient.Value = _previous;
}
