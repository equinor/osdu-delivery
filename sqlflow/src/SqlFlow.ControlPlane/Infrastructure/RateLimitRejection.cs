using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;

namespace SqlFlow.ControlPlane.Infrastructure;

/// <summary>
/// What a call the rate limiter refuses is answered with, besides its 429: a <c>Retry-After</c> header in whole seconds,
/// read from the refusing window (<see cref="FixedWindowLimiter"/>), so a client waits exactly until calls are taken again;
/// and a problem body saying the same in words, with the correlation id, as every other refusal of the API says why.
/// </summary>
public static class RateLimitRejection
{
    /// <summary>The problem's title, which a client shows as the error's headline.</summary>
    public const string Title = "Too many requests";

    /// <summary>The <c>OnRejected</c> callback of the control plane's rate limiter.</summary>
    public static async ValueTask WriteAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var http = context.HttpContext;
        var seconds = RetryAfterSeconds(context.Lease);
        if (seconds is { } wait)
        {
            http.Response.Headers.RetryAfter = wait.ToString(CultureInfo.InvariantCulture);
        }

        var correlationId = http.Items.TryGetValue(CorrelationIdMiddleware.HeaderName, out var id) ? id?.ToString() : http.TraceIdentifier;
        var problems = http.RequestServices.GetRequiredService<IProblemDetailsService>();
        // A caller that accepts no JSON (a stream's Accept: text/event-stream) is answered with the status and the header
        // alone, which is all it reads of a refusal.
        await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails =
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = Title,
                Detail = seconds is { } again
                    ? string.Create(CultureInfo.InvariantCulture, $"This caller has made more calls than the control plane takes in its rate limit's window; it takes them again in {again} s.")
                    : "This caller has made more calls than the control plane takes in its rate limit's window; it takes them again once the window ends.",
                Extensions = { ["correlationId"] = correlationId },
            },
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// How long the refusing window has left, in whole seconds rounded up and at least one (Retry-After counts whole
    /// seconds, and a client told zero would call again at once); null when the lease does not say.
    /// </summary>
    public static int? RetryAfterSeconds(RateLimitLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return lease.TryGetMetadata(MetadataName.RetryAfter, out var wait)
            ? (int)Math.Max(1, Math.Ceiling(wait.TotalSeconds))
            : null;
    }
}
