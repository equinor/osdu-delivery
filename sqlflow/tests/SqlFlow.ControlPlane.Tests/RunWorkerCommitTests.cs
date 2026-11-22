using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Runs;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;
using SqlFlow.Execution;
using SqlFlow.Node;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A registered kind's executor is told which commit of its repository the run executes (<see cref="DocumentExecutionOptions.Commit"/>),
/// so what it records can name the version of the estate it worked under. The worker is driven through a transport that
/// hands out one run of a test-only kind, whose executor keeps the options it was given.
/// </summary>
public sealed class RunWorkerCommitTests : IDisposable
{
    private const string ProbeFlow = """
        flowType: probe
        name: commit-probe
        """;

    private readonly List<string> _snapshots = [];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow_commit_" + Guid.NewGuid().ToString("N"));

    public RunWorkerCommitTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "flows"));
        File.WriteAllText(Path.Combine(_root, "flows", "probe.yaml"), ProbeFlow);
    }

    public void Dispose()
    {
        foreach (var snapshot in _snapshots.Where(Directory.Exists))
        {
            Directory.Delete(snapshot, recursive: true);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task APinnedRun_TellsItsExecutorTheCommitItExecutes()
    {
        // The snapshot of the pinned commit's flow file, which the node fetches from the catalog: no git access needed.
        var hash = "commit-probe-" + Guid.NewGuid().ToString("N");
        _snapshots.Add(Path.Combine(GitMaterializer.DefaultCacheRoot, "yaml", hash));
        var spec = new RunSpec(
            RepoId: Guid.NewGuid(), PipelineId: Guid.NewGuid(), FlowName: "commit-probe", RepoName: "estate",
            RepoRemoteUrl: null, RepoRootPath: null, PipelineRelativePath: "flows/probe.yaml", CommitSha: "852db64a9c0e4f7b8d1e2a3b4c5d6e7f80912345",
            FlowVersionHash: hash, Parameters: RunParameters.None, CredentialReference: null, CredentialUsername: null);

        var options = await ExecuteOnceAsync(new OneRunTransport(spec, ProbeFlow));

        Assert.Equal("852db64a9c0e4f7b8d1e2a3b4c5d6e7f80912345", options.Commit);
    }

    [Fact]
    public async Task AnUnpinnedRun_TellsItsExecutorNoCommit()
    {
        var spec = new RunSpec(
            RepoId: Guid.NewGuid(), PipelineId: Guid.NewGuid(), FlowName: "commit-probe", RepoName: "estate",
            RepoRemoteUrl: null, RepoRootPath: _root, PipelineRelativePath: "flows/probe.yaml", CommitSha: null,
            FlowVersionHash: null, Parameters: RunParameters.None, CredentialReference: null, CredentialUsername: null);

        var options = await ExecuteOnceAsync(new OneRunTransport(spec, null));

        Assert.Null(options.Commit);
        Assert.Contains("(no commit pinned)", options.Origin, StringComparison.Ordinal);
    }

    /// <summary>Runs the worker until the handed-out run reports its outcome, then returns the options its executor was given.</summary>
    private static async Task<DocumentExecutionOptions> ExecuteOnceAsync(OneRunTransport transport)
    {
        var executor = new ProbeExecutor();
        var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IFlowDocumentKind, ProbeKind>()
            .AddSingleton<IFlowDocumentExecutor>(executor)
            .AddSqlFlowEngine()
            .BuildServiceProvider();
        using var worker = new RunWorker(
            provider, transport, new DocumentExecutor(provider), TimeProvider.System, NullLogger<RunWorker>.Instance);
        using var stop = new CancellationTokenSource();
        var loop = worker.RunAsync(new RunWorkerOptions { PollWait = TimeSpan.FromSeconds(1) }, stop.Token);

        var reported = await Task.WhenAny(transport.Outcome, Task.Delay(TimeSpan.FromMinutes(2)));
        await stop.CancelAsync();
        await loop;

        Assert.True(reported == transport.Outcome, "The handed-out run never reported an outcome.");
        return Assert.Single(executor.Given);
    }

    /// <summary>A test-only kind whose documents name nothing but themselves.</summary>
    private sealed class ProbeKind : IFlowDocumentKind
    {
        public string FlowType => "probe";

        public string Description => "a test-only probe flow";

        public IReadOnlyList<FlowKindOperation> Operations { get; } = [new("run", "Run", "Runs the probe.", WritesTarget: false)];

        public void ValidateParameters(RunParameters parameters) => ArgumentNullException.ThrowIfNull(parameters);

        public RegisteredFlowDocument Parse(string yaml, string source) => new ProbeDocument();
    }

    private sealed record ProbeDocument : RegisteredFlowDocument
    {
        public override string Name => "commit-probe";

        public override string Kind => "probe";
    }

    /// <summary>Keeps the options of every run it executes, and writes no run history, so the worker reports the run failed.</summary>
    private sealed class ProbeExecutor : IFlowDocumentExecutor
    {
        private readonly ConcurrentQueue<DocumentExecutionOptions> _given = new();

        public IReadOnlyList<DocumentExecutionOptions> Given => [.. _given];

        public bool CanExecute(RegisteredFlowDocument document) => document is ProbeDocument;

        public Task<DocumentExecutionResult> ExecuteAsync(RegisteredFlowDocument document, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
        {
            _given.Enqueue(options);
            return Task.FromResult(new DocumentExecutionResult
            {
                FlowName = document.Name,
                FlowKind = document.Kind,
                Success = false,
                Error = "the probe writes no run history",
                RunId = options.RunId ?? Guid.Empty,
                Result = new object(),
            });
        }
    }

    /// <summary>Hands out one run on the first poll, then nothing; serves the snapshot it was given and completes
    /// <see cref="Outcome"/> when the run reports.</summary>
    private sealed class OneRunTransport(RunSpec spec, string? snapshot) : INodeTransport
    {
        private readonly TaskCompletionSource<bool> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _handedOut;

        public Task<bool> Outcome => _outcome.Task;

        public async Task<NodePollResponse> PollAsync(NodePollRequest request, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _handedOut, 1) == 0)
            {
                return new NodePollResponse([new RunHandout(Guid.NewGuid(), 1, spec)], [], [], [], [], [], false, 90);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
            return NodePollResponse.Empty(90);
        }

        public Task<string?> GetFlowVersionAsync(string contentHash, CancellationToken ct)
            => Task.FromResult(string.Equals(contentHash, spec.FlowVersionHash, StringComparison.Ordinal) ? snapshot : null);

        public Task<RunContextResponse> ResolveRunContextAsync(Guid runId, RunContextRequest request, CancellationToken ct)
            => Task.FromResult(new RunContextResponse(true, null, null));

        public Task<bool> ReportTraceAsync(Guid runId, RunTraceBatch batch, CancellationToken ct) => Task.FromResult(true);

        public Task<RunOutcomeStatus> ReportRunOutcomeAsync(Guid runId, RunOutcomeRequest request, CancellationToken ct)
        {
            _outcome.TrySetResult(true);
            return Task.FromResult(RunOutcomeStatus.Recorded);
        }

        public Task<bool> ReportTaskOutcomeAsync(Guid taskId, TaskOutcomeRequest request, CancellationToken ct)
            => Task.FromResult(true);

        public Task<FanOutResponse> EnqueueFanOutAsync(Guid rootRunId, FanOutRequest request, CancellationToken ct)
            => Task.FromResult(FanOutResponse.NotHeld);

        public Task<FanOutStateResponse> GetFanOutStateAsync(Guid rootRunId, Guid groupId, FanOutFence fence, CancellationToken ct)
            => Task.FromResult(FanOutStateResponse.NotHeld);

        public Task<FanOutCancelResponse> CancelFanOutAsync(Guid rootRunId, Guid groupId, FanOutFence fence, CancellationToken ct)
            => Task.FromResult(FanOutCancelResponse.NotHeld);
    }
}
