using SqlFlow.Delivery.Setup;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How the setup host reaches the command line for what only it does (osdu/docs/reference/cli/dev.md, The setup): the .NET
/// SDK found on the PATH or where an installer put it, the command line built in the background while the questions are
/// asked, and run from its build output once the build is through.
/// </summary>
public sealed class DevCommandLineTests : IDisposable
{
    private readonly DevCheckoutScope _scope = new();

    public void Dispose() => _scope.Dispose();

    [Fact]
    public void The_sdk_is_the_paths_else_the_one_an_installer_put_in_its_usual_place()
    {
        _scope.Environment["ProgramFiles"] = @"C:\Program Files";
        var installed = Path.Combine(@"C:\Program Files", "dotnet", "dotnet.exe");
        var machine = _scope.Machine(new ScriptedPrompter());

        Assert.Null(DotnetLocator.Find(machine));

        _scope.Files.Add(installed);
        Assert.Equal(installed, DotnetLocator.Find(machine));
        Assert.True(DotnetLocator.OffPath(machine));

        _scope.Tools.Installed.Add("dotnet");
        Assert.Equal("dotnet", DotnetLocator.Find(machine));
        Assert.False(DotnetLocator.OffPath(machine));
    }

    [Fact]
    public void On_a_mac_the_installers_place_is_usr_local_share_dotnet()
    {
        _scope.Files.Add("/usr/local/share/dotnet/dotnet");

        Assert.Equal("/usr/local/share/dotnet/dotnet", DotnetLocator.Find(_scope.Machine(new ScriptedPrompter(), DevPlatform.MacOS)));
    }

    [Fact]
    public async Task Without_an_sdk_the_command_line_cannot_run_and_says_why()
    {
        var commandLine = new CheckoutCommandLine(_scope.Tools, _scope.Checkout, _scope.Machine(new ScriptedPrompter()));

        Assert.Equal("the .NET SDK is not installed on this machine", commandLine.Unavailable);
        commandLine.Prepare(CancellationToken.None);
        Assert.Empty(_scope.Tools.Runs);
        Assert.False((await commandLine.RunAsync(["db", "migrate"], new Dictionary<string, string?>(), CancellationToken.None)).Found);
    }

    [Fact]
    public async Task It_builds_in_the_background_and_runs_its_build_output_with_the_settings()
    {
        _scope.Tools.Installed.Add("dotnet");
        _scope.Tools.Results["dotnet build"] = FakeToolRunner.Ok();
        var commandLine = new CheckoutCommandLine(_scope.Tools, _scope.Checkout, _scope.Machine(new ScriptedPrompter()));

        commandLine.Prepare(CancellationToken.None);
        commandLine.Prepare(CancellationToken.None);
        var result = await commandLine.RunAsync(["db", "migrate", "--create"], new Dictionary<string, string?> { ["SQLFLOW_CATALOG_DB"] = "Server=x;Database=y" }, CancellationToken.None);

        Assert.True(result.Succeeded);
        var build = Assert.Single(_scope.Tools.Runs, r => r.Tool == "dotnet");
        Assert.Equal(["build", CheckoutCommandLine.Project, "--verbosity", "quiet", "-nologo"], build.Arguments);
        Assert.False(build.Run.Attached);
        Assert.Equal(_scope.Root, build.Run.WorkingDirectory);
        var run = Assert.Single(_scope.Tools.Runs, r => r.Tool == commandLine.Executable);
        Assert.Equal(["db", "migrate", "--create"], run.Arguments);
        Assert.True(run.Run.Attached);
        Assert.Equal("Server=x;Database=y", run.Run.Environment!["SQLFLOW_CATALOG_DB"]);
        Assert.EndsWith(Path.Combine("bin", "Debug", "net9.0", "sqlflow.exe"), commandLine.Executable, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_build_is_said_with_its_last_lines_and_nothing_runs()
    {
        _scope.Tools.Installed.Add("dotnet");
        _scope.Tools.Results["dotnet build"] = new ToolResult(true, 1, "Program.cs(3,1): error CS1002: ; expected", string.Empty, false);
        var prompter = new ScriptedPrompter();
        var commandLine = new CheckoutCommandLine(_scope.Tools, _scope.Checkout, _scope.Machine(prompter));

        commandLine.Prepare(CancellationToken.None);
        await commandLine.FinishAsync(prompter, CancellationToken.None);
        var result = await commandLine.RunAsync(["partition", "add", "dev"], new Dictionary<string, string?>(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("error CS1002", prompter.Transcript, StringComparison.Ordinal);
        Assert.Contains("did not build", prompter.Transcript, StringComparison.Ordinal);
        Assert.DoesNotContain(_scope.Tools.Runs, r => r.Tool == commandLine.Executable);
    }
}
