using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog.Modules;
using SqlFlow.ControlPlane.Infrastructure;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.ManagedWorkers;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Hosting;
using SqlFlow.Delivery.Tests;
using SqlFlow.Dispatch.ManagedWorkers;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The OSDU module's launchers for SQLFlow's managed workers: none while the feature is off, both offered while it is on
/// (so a misnamed launcher is refused naming them), and the selected one's settings checked when the host starts. None of
/// this needs a database: the host starts against an unreachable catalog, so dispatch never activates and no launcher is
/// ever called.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class ManagedWorkersModuleTests
{
    [Fact]
    public async Task Off_TheModuleOffersNoLauncher()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();

        Assert.Empty(factory.Services.GetServices<IWorkerLauncher>());
        Assert.Null(factory.Services.GetService<ManagedFleet>());
    }

    [Fact]
    public async Task OnWithRadix_TheFleetStartsWorkersAsRadixJobs()
    {
        await using var factory = Host()
            .WithSetting("ControlPlane:ManagedWorkers:Enabled", "true")
            .WithSetting("ControlPlane:ManagedWorkers:Launcher", "radix")
            .WithSetting("ControlPlane:ManagedWorkers:Radix:JobSchedulerUrl", "http://worker:8080");
        using var client = factory.CreateClient();

        Assert.Equal(
            ["process", "radix"],
            factory.Services.GetServices<IWorkerLauncher>().Select(l => l.Name).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(RadixWorkerLauncher.LauncherName, factory.Services.GetRequiredService<ManagedFleet>().LauncherName);
    }

    [Fact]
    public void OnWithRadix_WithoutTheSchedulerUrl_TheHostRefusesToStart()
    {
        using var factory = Host()
            .WithSetting("ControlPlane:ManagedWorkers:Enabled", "true")
            .WithSetting("ControlPlane:ManagedWorkers:Launcher", "radix");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("ControlPlane:ManagedWorkers:Radix:JobSchedulerUrl is required", Flatten(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void OnWithRadix_AJobTimeLimitTheWorkersOutlive_IsRefused()
    {
        using var factory = Host()
            .WithSetting("ControlPlane:ManagedWorkers:Enabled", "true")
            .WithSetting("ControlPlane:ManagedWorkers:Launcher", "radix")
            .WithSetting("ControlPlane:ManagedWorkers:MaxLifetimeHours", "24")
            .WithSetting("ControlPlane:ManagedWorkers:Radix:JobSchedulerUrl", "http://worker:8080");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("greater than ControlPlane:ManagedWorkers:MaxLifetimeHours (24)", Flatten(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void OnWithTheProcessLauncher_WithoutAWorkerHost_TheHostRefusesToStart()
    {
        using var factory = Host()
            .WithSetting("ControlPlane:ManagedWorkers:Enabled", "true")
            .WithSetting("ControlPlane:ManagedWorkers:Launcher", "process");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("ControlPlane:ManagedWorkers:Process:Command is required", Flatten(ex), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheProcessLauncher_HandsAWorkerTheModuleDatabaseThisControlPlaneOpens()
    {
        // The osdu schema lives in the catalog's database, which a worker reaches through SQLFLOW_CATALOG_DB alone, so it
        // is handed the connection this control plane opens the catalog and the module database on, or it refuses to start.
        var folder = Directory.CreateTempSubdirectory("sqlflow-module-launcher-").FullName;
        try
        {
            await using var factory = Host()
                .WithSetting("ControlPlane:ManagedWorkers:Enabled", "true")
                .WithSetting("ControlPlane:ManagedWorkers:Launcher", "process")
                .WithSetting("ControlPlane:ManagedWorkers:Process:Command", StandInWorker(folder))
                .WithSetting("ControlPlane:ManagedWorkers:Process:ControlPlaneUrl", "http://localhost:5299");
            using var client = factory.CreateClient();
            var launcher = factory.Services.GetServices<IWorkerLauncher>().Single(l => l.Name == ProcessWorkerLauncher.LauncherName);

            await launcher.LaunchAsync(new WorkerLaunch("worker-default-0123456789", ""), CancellationToken.None);
            var handed = await ReadWhenWrittenAsync(Path.Combine(folder, "catalog.txt"));

            var opened = factory.Services.GetRequiredService<IModuleDatabaseConnections>()
                .ConnectionString(factory.Services.GetRequiredService<ModuleDatabase<OsduDbContext>>());
            Assert.Equal(opened, handed);
            Assert.Equal(factory.Services.GetRequiredService<CatalogConnectionProvider>().ConnectionString, handed);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // The stand-in may still hold its folder for a moment; the temp folder is the OS's.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public void OnWithAMisnamedLauncher_TheHostRefusesToStart_NamingTheOnesItHas()
    {
        using var factory = Host()
            .WithSetting("ControlPlane:ManagedWorkers:Enabled", "true")
            .WithSetting("ControlPlane:ManagedWorkers:Launcher", "kubernetes");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        var message = Flatten(ex);
        Assert.Contains("Launcher is 'kubernetes'", message, StringComparison.Ordinal);
        Assert.Contains("'radix'", message, StringComparison.Ordinal);
        Assert.Contains("'process'", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheUnselectedLaunchersSettings_AreNotChecked()
    {
        // The process launcher has no command here, and does not need one while Radix is the launcher in use.
        await using var factory = Host()
            .WithSetting("ControlPlane:ManagedWorkers:Enabled", "true")
            .WithSetting("ControlPlane:ManagedWorkers:Launcher", "radix")
            .WithSetting("ControlPlane:ManagedWorkers:Radix:JobSchedulerUrl", "http://worker:8080");
        using var client = factory.CreateClient();

        Assert.NotNull(factory.Services.GetRequiredService<ManagedFleet>());
    }

    private static ControlPlaneAppFactory Host()
        => new ControlPlaneAppFactory()
            .WithModules(new DeliveryControlPlaneModule())
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");

    /// <summary>A stand-in worker host that records the catalog connection it was handed, then exits.</summary>
    private static string StandInWorker(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(folder, "worker.cmd");
            // The redirection comes first: a value ending in a digit (ConnectRetryCount=0) would otherwise read as "0>".
            File.WriteAllText(path, "@echo off\r\n> \"%~dp0catalog.txt\" echo %" + OsduModuleDatabase.CatalogConnectionVariable + "%\r\n");
            return path;
        }

        var script = Path.Combine(folder, "worker.sh");
        File.WriteAllText(script, "#!/bin/sh\necho \"$" + OsduModuleDatabase.CatalogConnectionVariable + "\" > \"$(dirname \"$0\")/catalog.txt\"\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private static async Task<string> ReadWhenWrittenAsync(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                if (File.Exists(path) && (await File.ReadAllTextAsync(path)).Trim() is { Length: > 0 } text)
                {
                    return text;
                }
            }
            catch (IOException)
            {
                // The stand-in is still writing it.
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The stand-in worker wrote nothing to {path} within 30 seconds.");
            }

            await Task.Delay(100);
        }
    }

    private static string Flatten(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
            if (current is AggregateException aggregate)
            {
                messages.AddRange(aggregate.InnerExceptions.Select(Flatten));
            }
        }

        return string.Join(" | ", messages);
    }
}
