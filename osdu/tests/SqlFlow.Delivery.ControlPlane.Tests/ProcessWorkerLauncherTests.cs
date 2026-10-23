using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Catalog.Modules;
using SqlFlow.ControlPlane.Security;
using SqlFlow.Delivery.ControlPlane.ManagedWorkers;
using SqlFlow.Dispatch.ManagedWorkers;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The <c>process</c> launcher with real child processes: a stand-in worker (a script that records what it was started
/// with, then waits) is started with the node's options and a credential in its environment only, listed while it runs,
/// stopped on request, reported with its exit code when it ends by itself, and ended with the launcher.
/// </summary>
public sealed class ProcessWorkerLauncherTests : IDisposable
{
    /// <summary>A control plane setting no other test reads, set while one test checks it is kept from the worker.</summary>
    private const string ProbeVariable = "ControlPlane__ProcessLauncherTests__Probe";

    /// <summary>The connection the stand-in control plane opens its module database on.</summary>
    private const string ModuleConnection = "Server=module-db-for-tests;Database=OsduLauncherTests";

    private readonly string _folder = Directory.CreateTempSubdirectory("sqlflow-process-launcher-").FullName;

    [Fact]
    public async Task AStartedWorker_GetsTheNodesOptions_AndItsCredentialInItsEnvironmentOnly()
    {
        using var launcher = Launcher(Script(sleeps: true), arguments: ["worker"]);

        await launcher.LaunchAsync(new WorkerLaunch("worker-heavy-0123456789", "heavy"), CancellationToken.None);
        await WaitForAsync(() => File.Exists(Output("name.txt")));

        var args = await ReadAsync("args.txt");
        Assert.StartsWith("worker --url http://localhost:5299/ --node-name worker-heavy-0123456789 --drain-seconds 3600 --pool heavy", args, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJ", args, StringComparison.Ordinal); // no credential on the command line
        Assert.Equal("worker-heavy-0123456789", await ReadAsync("name.txt"));
        Assert.Equal("set", await ReadAsync("token.txt"));

        var worker = Assert.Single(await launcher.ListAsync(CancellationToken.None));
        Assert.Equal(("worker-heavy-0123456789", LaunchedWorkerState.Running), (worker.Name, worker.State));
    }

    [Fact]
    public async Task WhatOnlyTheControlPlaneHolds_NeverReachesAWorker()
    {
        Environment.SetEnvironmentVariable(ProbeVariable, "a-control-plane-setting");
        try
        {
            using var launcher = Launcher(Script(sleeps: true));

            await launcher.LaunchAsync(new WorkerLaunch("worker-default-0123456789", ""), CancellationToken.None);
            await WaitForAsync(() => File.Exists(Output("name.txt")));

            Assert.Equal("scrubbed", await ReadAsync("probe.txt"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProbeVariable, null);
        }
    }

    [Fact]
    public async Task AStartedWorker_OpensTheModuleDatabaseTheControlPlaneOpens()
    {
        // The osdu schema lives in the catalog's database, which a node reaches through SQLFLOW_CATALOG_DB alone: the worker
        // is handed the connection the control plane resolved, whatever the control plane's own environment holds, or it
        // refuses to start.
        using var launcher = Launcher(Script(sleeps: true));

        await launcher.LaunchAsync(new WorkerLaunch("worker-default-0123456789", ""), CancellationToken.None);

        Assert.Equal(ModuleConnection, await ReadAsync("catalog.txt"));
        Assert.DoesNotContain("module-db-for-tests", await ReadAsync("args.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModuleDatabaseThatCannotBeResolved_StartsNothing_AndSaysWhy()
    {
        using var launcher = Launcher(Script(sleeps: true), moduleConnection: () => throw new ModuleDatabaseException(
            "osdu", "The database of module 'osdu' uses the catalog connection, but the catalog connection is not set."));

        var ex = await Assert.ThrowsAsync<WorkerLauncherException>(
            () => launcher.LaunchAsync(new WorkerLaunch("worker-default-0123456789", ""), CancellationToken.None));

        Assert.False(ex.MayHaveStarted);
        Assert.Contains("the module database this control plane uses could not be resolved", ex.Message, StringComparison.Ordinal);
        Assert.Contains("the catalog connection is not set", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await launcher.ListAsync(CancellationToken.None));
        Assert.False(File.Exists(Output("args.txt")));
    }

    [Fact]
    public async Task AModuleDatabaseThatResolvesToNothing_StartsNothing()
    {
        using var launcher = Launcher(Script(sleeps: true), moduleConnection: () => " ");

        var ex = await Assert.ThrowsAsync<WorkerLauncherException>(
            () => launcher.LaunchAsync(new WorkerLaunch("worker-default-0123456789", ""), CancellationToken.None));

        Assert.False(ex.MayHaveStarted);
        Assert.Contains("resolved to an empty connection", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await launcher.ListAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("ControlPlane__Jwt__SigningKey", true)]
    [InlineData("controlplane__catalog__connectionreference", true)]
    [InlineData("ControlPlane:Jwt:BootstrapSecret", true)]
    [InlineData("SQLFLOW_CATALOG_DB", false)]
    [InlineData("SQLFLOW_CONN_DWH", false)]
    [InlineData("SQLFLOW_AZURE_AUTH", false)]
    public void TheControlPlanesOwnVariables_AreTheOnesKeptBack(string key, bool kept)
        => Assert.Equal(kept, ProcessWorkerLauncher.IsControlPlaneOnly(key));

    [Fact]
    public void TheWorkerTimeLimit_IsTheCredentialsLifetime()
    {
        using var launcher = Launcher(Script(sleeps: true));

        // A 12 h worker lifetime, two 60 min drain windows and an hour's margin.
        Assert.Equal(TimeSpan.FromHours(15), launcher.WorkerTimeLimit);
    }

    [Fact]
    public async Task ADefaultPoolWorker_IsStartedWithoutAPool()
    {
        using var launcher = Launcher(Script(sleeps: true));

        await launcher.LaunchAsync(new WorkerLaunch("worker-default-0123456789", ""), CancellationToken.None);
        await WaitForAsync(() => File.Exists(Output("name.txt")));

        Assert.DoesNotContain("--pool", await ReadAsync("args.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStoppedWorker_Ends_AndSaysTheControlPlaneStoppedIt()
    {
        using var launcher = Launcher(Script(sleeps: true));
        await launcher.LaunchAsync(new WorkerLaunch("worker-default-0123456789", ""), CancellationToken.None);
        await WaitForAsync(() => File.Exists(Output("name.txt")));

        await launcher.StopAsync("worker-default-0123456789", CancellationToken.None);

        await WaitForAsync(async () => (await launcher.ListAsync(CancellationToken.None)).Single().State == LaunchedWorkerState.Ended);
        Assert.Equal("stopped by the control plane", (await launcher.ListAsync(CancellationToken.None)).Single().Detail);
    }

    [Fact]
    public async Task AWorkerThatExitsByItself_IsReportedWithItsExitCode()
    {
        using var launcher = Launcher(Script(sleeps: false));

        await launcher.LaunchAsync(new WorkerLaunch("worker-default-0123456789", ""), CancellationToken.None);

        await WaitForAsync(async () => (await launcher.ListAsync(CancellationToken.None)).Single().State == LaunchedWorkerState.Ended);
        Assert.Equal("exited with code 3", (await launcher.ListAsync(CancellationToken.None)).Single().Detail);
    }

    [Fact]
    public async Task StoppingAWorkerItDoesNotHave_DoesNothing()
    {
        using var launcher = Launcher(Script(sleeps: true));

        await launcher.StopAsync("worker-default-0123456789", CancellationToken.None);

        Assert.Empty(await launcher.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DisposingTheLauncher_EndsItsWorkers()
    {
        var launcher = Launcher(Script(sleeps: true));
        await launcher.LaunchAsync(new WorkerLaunch("worker-default-0123456789", ""), CancellationToken.None);
        await WaitForAsync(() => File.Exists(Output("name.txt")));
        var pid = int.Parse((await ReadAsync("pid.txt")).Trim(), System.Globalization.CultureInfo.InvariantCulture);

        launcher.Dispose();

        await WaitForAsync(() => !IsRunning(pid));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => launcher.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AWorkerHostThatDoesNotExist_StartsNothing_AndSaysSo()
    {
        using var launcher = Launcher(Path.Combine(_folder, "missing-worker-host"));

        var ex = await Assert.ThrowsAsync<WorkerLauncherException>(
            () => launcher.LaunchAsync(new WorkerLaunch("worker-default-0123456789", ""), CancellationToken.None));

        Assert.False(ex.MayHaveStarted);
        Assert.Contains("could not be started", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await launcher.ListAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("http://+:5000", "http://localhost:5000/")]
    [InlineData("http://*:5000", "http://localhost:5000/")]
    [InlineData("http://0.0.0.0:5000", "http://localhost:5000/")]
    [InlineData("http://[::]:5000", "http://localhost:5000/")]
    [InlineData("http://localhost:5000", "http://localhost:5000/")]
    public async Task WithoutAConfiguredUrl_WorkersPollThisHostsOwnAddress(string address, string expected)
    {
        using var launcher = Launcher(Script(sleeps: true), controlPlaneUrl: null, serverAddress: address);

        await launcher.LaunchAsync(new WorkerLaunch("worker-default-0123456789", ""), CancellationToken.None);
        await WaitForAsync(() => File.Exists(Output("name.txt")));

        Assert.Contains($"--url {expected} ", await ReadAsync("args.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoAddressAndNoUrl_AStartIsRefused_NamingTheSetting()
    {
        using var launcher = Launcher(Script(sleeps: true), controlPlaneUrl: null, serverAddress: null);

        var ex = await Assert.ThrowsAsync<WorkerLauncherException>(
            () => launcher.LaunchAsync(new WorkerLaunch("worker-default-0123456789", ""), CancellationToken.None));

        Assert.Contains("ControlPlane:ManagedWorkers:Process:ControlPlaneUrl", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommand_IsRequired_AndMustExist()
    {
        var missing = Assert.Throws<InvalidOperationException>(() => new ProcessLauncherOptions().Validate());
        Assert.Contains("ControlPlane:ManagedWorkers:Process:Command is required", missing.Message, StringComparison.Ordinal);

        var absent = Assert.Throws<InvalidOperationException>(
            () => new ProcessLauncherOptions { Command = Path.Combine(_folder, "nope.exe") }.Validate());
        Assert.Contains("does not exist", absent.Message, StringComparison.Ordinal);

        var url = Assert.Throws<InvalidOperationException>(
            () => new ProcessLauncherOptions { Command = Script(sleeps: true), ControlPlaneUrl = new Uri("ftp://host/") }.Validate());
        Assert.Contains("ControlPlaneUrl must be an absolute http or https URL", url.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A stand-in that is still being torn down may hold its folder for a moment; the temp folder is the OS's.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // -------------------------------------------------------------------------------------------------- support -----

    private static ProcessWorkerLauncher Launcher(
        string command, string[]? arguments = null, Uri? controlPlaneUrl = null, string? serverAddress = "http://localhost:5299",
        Func<string>? moduleConnection = null)
    {
        var options = new ProcessLauncherOptions
        {
            Command = command,
            Arguments = arguments ?? [],
            ControlPlaneUrl = controlPlaneUrl,
            DrainMinutes = 60,
        };
        var tokens = new TokenIssuer(Options.Create(new ControlPlaneOptions
        {
            Jwt = new JwtOptions { SigningKey = "process-launcher-tests-signing-key-0123456789abcdef", Issuer = "tests", Audience = "tests" },
        }));
        return new ProcessWorkerLauncher(
            options, maxLifetimeHours: 12, tokens, new FakeServer(serverAddress), moduleConnection ?? (() => ModuleConnection),
            TimeProvider.System, NullLogger<ProcessWorkerLauncher>.Instance);
    }

    /// <summary>A stand-in worker: records its arguments, its node name, its process id, the catalog connection it was given
    /// and whether it was given a credential (never the credential), then waits a minute or exits with code 3.</summary>
    private string Script(bool sleeps)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(_folder, sleeps ? "worker.cmd" : "worker-exits.cmd");
            File.WriteAllText(path, string.Join("\r\n",
                "@echo off",
                "echo %*> \"%~dp0args.txt\"",
                "if defined SQLFLOW_TOKEN (echo set> \"%~dp0token.txt\") else (echo missing> \"%~dp0token.txt\")",
                "if defined " + ProbeVariable + " (echo leaked> \"%~dp0probe.txt\") else (echo scrubbed> \"%~dp0probe.txt\")",
                "powershell -NoProfile -Command \"(Get-CimInstance Win32_Process -Filter ('ProcessId=' + $PID)).ParentProcessId\" > \"%~dp0pid.txt\"",
                "echo %SQLFLOW_CATALOG_DB%> \"%~dp0catalog.txt\"",
                "echo %SQLFLOW_NODE_NAME%> \"%~dp0name.txt\"",
                sleeps ? "ping -n 60 127.0.0.1 > nul" : "exit /b 3",
                string.Empty));
            return path;
        }

        var script = Path.Combine(_folder, sleeps ? "worker.sh" : "worker-exits.sh");
        File.WriteAllText(script, string.Join("\n",
            "#!/bin/sh",
            "dir=$(dirname \"$0\")",
            "echo \"$@\" > \"$dir/args.txt\"",
            "if [ -n \"$SQLFLOW_TOKEN\" ]; then echo set > \"$dir/token.txt\"; else echo missing > \"$dir/token.txt\"; fi",
            "if [ -n \"$" + ProbeVariable + "\" ]; then echo leaked > \"$dir/probe.txt\"; else echo scrubbed > \"$dir/probe.txt\"; fi",
            "echo $$ > \"$dir/pid.txt\"",
            "echo \"$SQLFLOW_CATALOG_DB\" > \"$dir/catalog.txt\"",
            "echo \"$SQLFLOW_NODE_NAME\" > \"$dir/name.txt\"",
            sleeps ? "sleep 60" : "exit 3",
            string.Empty));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private string Output(string name) => Path.Combine(_folder, name);

    private async Task<string> ReadAsync(string name)
    {
        await WaitForAsync(() => File.Exists(Output(name)));
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return (await File.ReadAllTextAsync(Output(name))).Trim();
            }
            catch (IOException) when (attempt < 50)
            {
                await Task.Delay(100); // the script is still writing it
            }
        }
    }

    private static Task WaitForAsync(Func<bool> condition) => WaitForAsync(() => Task.FromResult(condition()));

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the stand-in worker did not get there within 30 s");
            await Task.Delay(100);
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A server that reports one address (or none), as Kestrel does once it listens.</summary>
    private sealed class FakeServer : IServer
    {
        public FakeServer(string? address)
        {
            var addresses = new ServerAddressesFeature();
            if (address is not null)
            {
                addresses.Addresses.Add(address);
            }

            Features.Set<IServerAddressesFeature>(addresses);
        }

        public IFeatureCollection Features { get; } = new FeatureCollection();

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull
            => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
