using System.Net;
using SqlFlow.Delivery.Model;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Delivery.Http;

/// <summary>
/// Composes the per-flow HTTP stack the way SQLFlow's engine does: one client, one executor for data, one for
/// auth (with an empty allowlist, since token endpoints live on identity providers), and the auth resolver. Both reach only
/// the addresses the deployment's <see cref="NetworkPolicy"/> allows.
/// </summary>
public sealed class HttpRuntime : IDisposable
{
    private readonly HttpClient _client;

    /// <param name="reliability">The flow's reliability settings.</param>
    /// <param name="secrets">Resolves every reference the flow reaches its target with: the endpoint, the headers and the secrets auth names.</param>
    /// <param name="time">The clock; the system clock when null.</param>
    /// <param name="handler">A handler to send through instead of the network (the tests).</param>
    /// <param name="allowLoopback">Whether loopback addresses are reachable.</param>
    /// <param name="privateNetworks">The private ranges the deployment reaches; the ones it lists under <see cref="NetworkPolicy.PrivateNetworksVariable"/> when null.</param>
    /// <param name="observer">Told of every attempt and retry either executor makes (a run's live trace); null tells nobody.</param>
    public HttpRuntime(
        FlowReliability reliability, ISecretResolver secrets, TimeProvider? time = null, HttpMessageHandler? handler = null, bool allowLoopback = false,
        IReadOnlyList<IPNetwork>? privateNetworks = null, IHttpObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(reliability);
        ArgumentNullException.ThrowIfNull(secrets);
        var clock = time ?? TimeProvider.System;
        Network = new NetworkPolicy
        {
            AllowLoopback = allowLoopback,
            PrivateNetworks = privateNetworks ?? NetworkPolicy.PrivateNetworksFromEnvironment(),
        };
        _client = handler is null
            ? HttpClientBuilder.Build(reliability, Network)
            : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(reliability.TimeoutSeconds) };

        Observer = observer;
        Retry = new RetryPolicy(reliability.Retry, clock);
        RateLimit = new RateLimiter(reliability.RateLimitRps, clock);
        Data = new HttpExecutor(_client, Retry, RateLimit, new UrlGuard(reliability.UrlAllowlist, Network), reliability.MaxResponseBytes, clock, observer);
        Auth = new HttpExecutor(_client, Retry, new RateLimiter(0, clock), new UrlGuard([], Network), 1024 * 1024, clock, observer);
        Secrets = secrets;
        AuthResolver = new AuthResolver(secrets, clock);
    }

    /// <summary>
    /// What resolves every reference this stack reaches the target with: the endpoint, the headers and the credentials. A
    /// run's or an operation's own, carrying the central configuration it was given ahead of the process's environment.
    /// </summary>
    public ISecretResolver Secrets { get; }

    /// <summary>The addresses this stack reaches.</summary>
    public NetworkPolicy Network { get; }

    /// <summary>What watches the calls this stack sends, when anything does; a stack built beside it can report to the same.</summary>
    public IHttpObserver? Observer { get; }

    /// <summary>
    /// The invoker every request of this flow goes through. The ETP route's WebSocket upgrade is sent with it, so a
    /// <c>wss</c> connection opens under exactly the same address policy, TLS setting and connect timeout as the
    /// flow's HTTP calls, rather than under a second stack of its own.
    /// </summary>
    public HttpMessageInvoker Invoker => _client;

    public HttpExecutor Data { get; }

    /// <summary>
    /// The flow's <c>reliability.rateLimitRps</c> as every data request takes its turn under it; a fan-out divides it between
    /// its runs (<see cref="RateLimiter.Share"/>). Token requests are not limited.
    /// </summary>
    public RateLimiter RateLimit { get; }

    public HttpExecutor Auth { get; }

    public AuthResolver AuthResolver { get; }

    public RetryPolicy Retry { get; }

    public void Dispose() => _client.Dispose();
}
