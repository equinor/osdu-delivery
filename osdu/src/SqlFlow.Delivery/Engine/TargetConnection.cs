using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// A flow's target as one runtime reaches it: one HTTP runtime under the flow's reliability, made when something first
/// talks to the target and shared by everything that does (the protocol, the reference check, the record search), and
/// disposed with the runtime. The render resolves before the runtime exists and its searches reach the target later, so
/// the connection is made first and handed to both.
/// </summary>
internal sealed class TargetConnection : IDisposable
{
    private readonly FlowDefinition _flow;
    private readonly EngineContext _context;
    private readonly Lock _gate = new();
    private HttpRuntime? _http;
    private Task<OsduHttpClient>? _client;
    private bool _disposed;

    /// <summary>How many runs of a fan-out share the flow's rate limit with this one; 1 when it has the rate to itself.</summary>
    private int _rateShare = 1;

    public TargetConnection(EngineContext context, FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(flow);
        _context = context;
        _flow = flow;
    }

    /// <summary>The HTTP runtime every call to the target goes through, made on first use.</summary>
    public HttpRuntime Http
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _http ??= NewHttp();
            }
        }
    }

    /// <summary>
    /// The client for the flow's endpoint, auth and headers, every reference in them resolved once for the runtime. A
    /// resolution that fails is not kept, so the next caller tries again rather than inheriting the failure.
    /// </summary>
    public async Task<OsduHttpClient> ClientAsync(CancellationToken ct)
    {
        Task<OsduHttpClient> pending;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            pending = _client ??= ProtocolFactory.ClientAsync(
                _http ??= NewHttp(),
                _flow.Target.Endpoint,
                _flow.Target.Auth,
                _flow.Target.Headers,
                _context.Secrets,
                CancellationToken.None);
        }

        try
        {
            return await pending.WaitAsync(ct).ConfigureAwait(false);
        }
        catch when (pending.IsFaulted || pending.IsCanceled)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_client, pending))
                {
                    _client = null;
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Has this connection send under <paramref name="runs"/>'s share of the flow's <c>reliability.rateLimitRps</c> from now on,
    /// whether its stack is made yet or not: the runs of a fan-out each hold a connection of their own and divide the rate
    /// between them (osdu/docs/reference/concepts/submissions.md, Many nodes). 1 takes the whole rate back.
    /// </summary>
    public void ShareRateLimit(int runs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(runs, 1);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _rateShare = runs;
            _http?.RateLimit.Share(runs);
        }
    }

    /// <summary>The stack every call to the target goes through, watched by the run's trace when the context serves a run.</summary>
    private HttpRuntime NewHttp()
    {
        var http = new HttpRuntime(_flow.Reliability, _context.Secrets, _context.Time, allowLoopback: EngineContext.LoopbackAllowed, observer: _context.HttpObserver);
        http.RateLimit.Share(_rateShare);
        return http;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _http?.Dispose();
        }
    }
}
