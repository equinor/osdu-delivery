using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.ControlPlane.Security;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Hosting;
using SqlFlow.Dispatch.ManagedWorkers;
using SqlFlow.Node;

namespace SqlFlow.Delivery.ControlPlane.ManagedWorkers;

/// <summary>
/// Starts managed workers as processes on the control plane's own machine: the development launcher <c>dev.bat</c> and
/// <c>./dev.sh</c> start with, so managed workers can be watched starting, taking work and leaving without a platform. Each
/// worker is the configured worker host started with the node's options and, in its environment only, a node
/// credential this control plane issues for it (valid a little longer than the worker may live, and accepted by the
/// node protocol alone). The worker inherits the control plane's environment, so it reaches the same databases and
/// secrets, except what only the control plane may hold: its own settings (<c>ControlPlane__*</c>, the token signing
/// key and the catalog connection reference among them). Its module database is this control plane's, the osdu schema
/// in the catalog's database: <c>SQLFLOW_CATALOG_DB</c> is set, in its environment only, to the connection the control
/// plane opens it on, whichever way the control plane was given that connection, because a node reads that variable
/// alone and refuses to start without it. Its output is written to the control plane's log under its name.
/// </summary>
/// <remarks>
/// The workers belong to this control plane: when it stops, they are ended with it (and any run they held is requeued
/// by the next one). A worker that outlives a crash keeps polling under its managed name, which the next control plane
/// does not know, so the managed fleet asks it to leave; <c>dev.bat</c> also ends stray workers before it starts.
/// </remarks>
public sealed partial class ProcessWorkerLauncher : IWorkerLauncher, IDisposable
{
    /// <summary>The launcher's name in <c>ControlPlane:ManagedWorkers:Launcher</c>.</summary>
    public const string LauncherName = "process";

    /// <summary>How long an ended worker is still reported, so the fleet sees how it ended.</summary>
    private static readonly TimeSpan KeepEnded = TimeSpan.FromMinutes(10);

    private readonly ProcessLauncherOptions _options;
    private readonly TimeSpan _credentialLifetime;
    private readonly TokenIssuer _tokens;
    private readonly IServer _server;
    private readonly Func<string> _moduleDatabaseConnection;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProcessWorkerLauncher> _logger;
    private readonly ConcurrentDictionary<string, LocalWorker> _workers = new(StringComparer.Ordinal);
    private int _disposed;

    /// <summary>Creates the launcher. <paramref name="maxLifetimeHours"/> is how long the fleet keeps a worker, which the
    /// credential it is given outlives by the drain window and an hour. <paramref name="moduleDatabaseConnection"/>
    /// resolves the connection this control plane opens the module database on, read at every start.</summary>
    public ProcessWorkerLauncher(
        ProcessLauncherOptions options, int maxLifetimeHours, TokenIssuer tokens, IServer server,
        Func<string> moduleDatabaseConnection, TimeProvider clock, ILogger<ProcessWorkerLauncher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(moduleDatabaseConnection);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        // The lifetime, two drain windows and an hour: the fleet's last-resort drain comes halfway between the lifetime and
        // this, so a worker it could not replace still has more than a whole drain window before its credential expires.
        _credentialLifetime = TimeSpan.FromHours(maxLifetimeHours) + TimeSpan.FromMinutes(2L * options.DrainMinutes) + TimeSpan.FromHours(1);
        _tokens = tokens;
        _server = server;
        _moduleDatabaseConnection = moduleDatabaseConnection;
        _clock = clock;
        _logger = logger;
    }

    public string Name => LauncherName;

    /// <summary>A worker's credential expires after this long, so the fleet drains it well before.</summary>
    public TimeSpan? WorkerTimeLimit => _credentialLifetime;

    public Task<IReadOnlyList<LaunchedWorker>> ListAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var now = _clock.GetUtcNow().UtcDateTime;
        var workers = new List<LaunchedWorker>(_workers.Count);
        foreach (var (name, worker) in _workers)
        {
            if (worker.EndedUtc is { } ended)
            {
                if (now - ended > KeepEnded)
                {
                    if (_workers.TryRemove(name, out var gone))
                    {
                        gone.Process.Dispose(); // ended long ago: its handle is released with its entry
                    }

                    continue;
                }

                workers.Add(new LaunchedWorker(name, LaunchedWorkerState.Ended, worker.StartedUtc, worker.EndDetail));
                continue;
            }

            workers.Add(new LaunchedWorker(name, LaunchedWorkerState.Running, worker.StartedUtc, $"process {worker.ProcessId}"));
        }

        return Task.FromResult<IReadOnlyList<LaunchedWorker>>(workers);
    }

    public Task LaunchAsync(WorkerLaunch launch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var url = ControlPlaneUrl();
        var moduleDatabase = ModuleDatabaseConnection();
        var now = _clock.GetUtcNow().UtcDateTime;
        var credential = _tokens.IssueNodeCredential(launch.Name, _credentialLifetime, now);

        var start = new ProcessStartInfo(_options.Command!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = WorkingDirectory(),
        };
        foreach (var argument in _options.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add("--url");
        start.ArgumentList.Add(url.ToString());
        start.ArgumentList.Add("--node-name");
        start.ArgumentList.Add(launch.Name);
        start.ArgumentList.Add("--drain-seconds");
        start.ArgumentList.Add((_options.DrainMinutes * 60).ToString(CultureInfo.InvariantCulture));
        if (launch.Pool.Length > 0)
        {
            start.ArgumentList.Add("--pool");
            start.ArgumentList.Add(launch.Pool);
        }

        // What only the control plane may hold never reaches a worker: its settings, the token signing key among them.
        foreach (var key in start.Environment.Keys.Where(IsControlPlaneOnly).ToList())
        {
            start.Environment.Remove(key);
        }

        // The credential and the module database travel in the worker's environment only: never on its command line,
        // never in a log line.
        start.Environment["SQLFLOW_TOKEN"] = credential.Token;
        start.Environment[OsduModuleDatabase.CatalogConnectionVariable] = moduleDatabase;
        start.Environment[NodeIdentity.EnvironmentVariable] = launch.Name;
        start.Environment.Remove("SQLFLOW_URL");
        start.Environment.Remove("SQLFLOW_WORKER_POOL");

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var worker = new LocalWorker(process, launch.Pool, now);
        // The worker writes its diagnostics to stderr like every CLI path, so both streams are its log.
        process.OutputDataReceived += (_, e) => Forward(launch.Name, e.Data);
        process.ErrorDataReceived += (_, e) => Forward(launch.Name, e.Data);
        process.Exited += (_, _) => Ended(launch.Name, worker);
        try
        {
            if (!process.Start())
            {
                throw new WorkerLauncherException($"the worker host '{_options.Command}' did not start", mayHaveStarted: false);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            process.Dispose();
            throw new WorkerLauncherException(
                $"the worker host '{_options.Command}' could not be started: {SecretHygiene.RedactedMessage(ex)}", mayHaveStarted: false, ex);
        }

        worker.ProcessId = process.Id;
        _workers[launch.Name] = worker;
        if (Volatile.Read(ref _disposed) != 0)
        {
            // Disposed while this worker was starting: it belongs to a control plane that is stopping, so it ends too.
            Kill(launch.Name, worker);
            throw new ObjectDisposedException(nameof(ProcessWorkerLauncher));
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        LogStarted(launch.Name, process.Id, launch.Pool.Length == 0 ? "default" : launch.Pool);
        return Task.CompletedTask;
    }

    /// <summary>Whether an environment variable belongs to the control plane alone: its settings (the token signing key,
    /// the bootstrap secret, the catalog connection reference). The catalog's connection itself is the module database's,
    /// which a worker is given as the control plane resolved it.</summary>
    public static bool IsControlPlaneOnly(string key)
        => key.StartsWith("ControlPlane__", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ControlPlane:", StringComparison.OrdinalIgnoreCase);

    public Task StopAsync(string workerName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerName);
        if (_workers.TryGetValue(workerName, out var worker) && worker.EndedUtc is null)
        {
            Kill(workerName, worker);
        }

        return Task.CompletedTask;
    }

    /// <summary>Ends every worker this launcher started; they belong to this control plane.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var (name, worker) in _workers)
        {
            if (worker.EndedUtc is null)
            {
                Kill(name, worker);
            }

            worker.Process.Dispose();
        }

        _workers.Clear();
    }

    /// <summary>The connection this control plane opens the module database on, for the worker to open it on too. A
    /// reference that cannot be resolved refuses the start, saying why, so the pool backs off with the reason on the
    /// Nodes page rather than starting a worker that refuses to run.</summary>
    private string ModuleDatabaseConnection()
    {
        string connection;
        try
        {
            connection = _moduleDatabaseConnection();
        }
        catch (Exception ex) when (ex is SqlFlowException or InvalidOperationException)
        {
            throw new WorkerLauncherException(
                $"the module database this control plane uses could not be resolved for the worker: {SecretHygiene.RedactedMessage(ex)}",
                mayHaveStarted: false, ex);
        }

        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new WorkerLauncherException(
                "the module database this control plane uses resolved to an empty connection, so a worker could not open it",
                mayHaveStarted: false);
        }

        return connection;
    }

    /// <summary>The URL the workers poll: the configured one, else this host's own first HTTP address with a wildcard host
    /// read as localhost.</summary>
    private Uri ControlPlaneUrl()
    {
        if (_options.ControlPlaneUrl is { } configured)
        {
            return configured;
        }

        var address = _server.Features.Get<IServerAddressesFeature>()?.Addresses
            .FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || a.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        if (address is null)
        {
            throw new WorkerLauncherException(
                $"this control plane reports no HTTP address its workers could poll; set {ProcessLauncherOptions.SectionName}:ControlPlaneUrl",
                mayHaveStarted: false);
        }

        var local = address.Replace("://+", "://localhost", StringComparison.Ordinal)
            .Replace("://*", "://localhost", StringComparison.Ordinal)
            .Replace("://0.0.0.0", "://localhost", StringComparison.Ordinal)
            .Replace("://[::]", "://localhost", StringComparison.Ordinal);
        return new Uri(local, UriKind.Absolute);
    }

    private string WorkingDirectory()
        => !string.IsNullOrWhiteSpace(_options.WorkingDirectory)
            ? _options.WorkingDirectory
            : Path.GetDirectoryName(Path.GetFullPath(_options.Command!)) ?? Environment.CurrentDirectory;

    private void Forward(string name, string? line)
    {
        if (!string.IsNullOrEmpty(line))
        {
            LogWorkerOutput(name, line);
        }
    }

    private void Ended(string name, LocalWorker worker)
    {
        int? code = null;
        try
        {
            code = worker.Process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            // The process object was released at shutdown; its exit code is no longer readable.
        }

        var detail = worker.Killed
            ? "stopped by the control plane"
            : code is { } exit ? $"exited with code {exit}" : "exited";
        worker.End(_clock.GetUtcNow().UtcDateTime, detail);
        LogEnded(name, detail);
    }

    private void Kill(string name, LocalWorker worker)
    {
        worker.Killed = true;
        try
        {
            worker.Process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It exited between the check and the kill.
        }
        catch (Win32Exception ex)
        {
            LogKillFailed(name, SecretHygiene.RedactedMessage(ex));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Managed worker '{Worker}' started as process {ProcessId} for pool '{Pool}'.")]
    private partial void LogStarted(string worker, int processId, string pool);

    [LoggerMessage(Level = LogLevel.Information, Message = "Managed worker '{Worker}' {Detail}.")]
    private partial void LogEnded(string worker, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Managed worker '{Worker}' could not be ended: {Error}")]
    private partial void LogKillFailed(string worker, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{Worker}] {Line}")]
    private partial void LogWorkerOutput(string worker, string line);

    /// <summary>One worker process and how it ended. The end is written by the process's exit event and read by the
    /// list, so it is read and written under one lock.</summary>
    private sealed class LocalWorker(Process process, string pool, DateTime startedUtc)
    {
        private readonly Lock _gate = new();
        private DateTime? _endedUtc;
        private string? _endDetail;
        private int _killed;

        public Process Process { get; } = process;

        public string Pool { get; } = pool;

        public DateTime StartedUtc { get; } = startedUtc;

        public int ProcessId { get; set; }

        public bool Killed
        {
            get => Volatile.Read(ref _killed) != 0;
            set => Volatile.Write(ref _killed, value ? 1 : 0);
        }

        public DateTime? EndedUtc
        {
            get
            {
                lock (_gate)
                {
                    return _endedUtc;
                }
            }
        }

        public string? EndDetail
        {
            get
            {
                lock (_gate)
                {
                    return _endDetail;
                }
            }
        }

        public void End(DateTime endedUtc, string detail)
        {
            lock (_gate)
            {
                _endedUtc ??= endedUtc;
                _endDetail ??= detail;
            }
        }
    }
}

/// <summary>
/// The <c>process</c> launcher (section <c>ControlPlane:ManagedWorkers:Process</c>): the worker host to start, the
/// arguments before the node's own options, where it runs, the URL it polls, and how long a worker stopped while busy
/// keeps finishing its runs. Validated only when managed workers use this launcher.
/// </summary>
public sealed class ProcessLauncherOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = ManagedWorkersOptions.Section + ":Process";

    /// <summary>The worker executable (the OSDU Delivery worker host). Required; <c>dev.bat</c> and <c>./dev.sh</c> set it.</summary>
    public string? Command { get; set; }

    /// <summary>Arguments placed before the node's own options (for the CLI host, <c>worker</c>).</summary>
    public string[] Arguments { get; set; } = [];

    /// <summary>Where the worker runs; the executable's folder when empty.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>The URL the workers poll; this host's own address when empty.</summary>
    public Uri? ControlPlaneUrl { get; set; }

    /// <summary>How long a worker stopped while busy keeps finishing its runs.</summary>
    public int DrainMinutes { get; set; } = 60;

    /// <summary>Rejects settings that cannot work, naming the setting.</summary>
    /// <exception cref="InvalidOperationException">A setting cannot work.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Command))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Command is required: the worker host to start (dev.bat and ./dev.sh set it to the built SqlFlow.Delivery.Worker.Host).");
        }

        if (Path.IsPathRooted(Command) && !File.Exists(Command))
        {
            throw new InvalidOperationException($"{SectionName}:Command '{Command}' does not exist; build the worker host first.");
        }

        if (!string.IsNullOrWhiteSpace(WorkingDirectory) && !Directory.Exists(WorkingDirectory))
        {
            throw new InvalidOperationException($"{SectionName}:WorkingDirectory '{WorkingDirectory}' does not exist.");
        }

        if (ControlPlaneUrl is { } url
            && (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)))
        {
            throw new InvalidOperationException($"{SectionName}:ControlPlaneUrl must be an absolute http or https URL.");
        }

        if (DrainMinutes is < 0 or > 1440)
        {
            throw new InvalidOperationException($"{SectionName}:DrainMinutes must be between 0 and 1440.");
        }
    }
}
