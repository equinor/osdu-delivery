namespace SqlFlow.Delivery.Setup;

/// <summary>
/// The OSDU command line, which the setup runs for what only it does: creating the catalog with its migrations
/// (<c>db migrate --create</c>), applying them (<c>db migrate</c>) and registering a partition (<c>partition add</c>).
/// </summary>
internal interface ICommandLine
{
    /// <summary>Why it cannot run on this machine (the .NET SDK is missing), or null when it can.</summary>
    string? Unavailable { get; }

    /// <summary>Starts making it ready (a build) and returns at once; a no-op when it is ready already or cannot run.</summary>
    void Prepare(CancellationToken ct);

    /// <summary>Runs one of its verbs on this console, with <paramref name="environment"/> set for it, once it is ready.</summary>
    Task<ToolResult> RunAsync(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment, CancellationToken ct);

    /// <summary>Waits for a preparation <see cref="Prepare"/> started, so none outlives the setup.</summary>
    Task FinishAsync(IPrompter prompter, CancellationToken ct);
}

/// <summary>The command line this process is (<c>sqlflow dev</c>): ready already, and run again for a verb.</summary>
internal sealed class RunningCommandLine(IToolRunner tools, SelfCommand self, string workingDirectory) : ICommandLine
{
    public string? Unavailable => null;

    public void Prepare(CancellationToken ct)
    {
    }

    public Task<ToolResult> RunAsync(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment, CancellationToken ct)
        => tools.RunAsync(self.Program, [.. self.Prefix, .. arguments], new ToolRun(WorkingDirectory: workingDirectory, Environment: environment, Attached: true), ct);

    public Task FinishAsync(IPrompter prompter, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// The checkout's command line, for the setup host (<c>osdu-delivery-setup</c>), which does not carry it: built with the
/// .NET SDK found on this machine (on the PATH, or where its installers put it, for an SDK installed since this terminal
/// started), in the background while the questions are asked, and run from its build output.
/// </summary>
internal sealed class CheckoutCommandLine : ICommandLine
{
    /// <summary>The command line's project, relative to the checkout.</summary>
    public static readonly string Project = Path.Combine("osdu", "hosts", "SqlFlow.Delivery.Cli.Host");

    /// <summary>How long its build may take before it is given up: a first build restores every package.</summary>
    private static readonly TimeSpan BuildLimit = TimeSpan.FromMinutes(30);

    private readonly IToolRunner _tools;
    private readonly DevCheckout _checkout;
    private readonly DevMachine _machine;
    private readonly string? _dotnet;
    private Task<ToolResult>? _build;

    public CheckoutCommandLine(IToolRunner tools, DevCheckout checkout, DevMachine machine)
    {
        _tools = tools;
        _checkout = checkout;
        _machine = machine;
        _dotnet = DotnetLocator.Find(machine);
    }

    public string? Unavailable => _dotnet is null ? "the .NET SDK is not installed on this machine" : null;

    /// <summary>The command line's build output: its apphost, named as the platform names executables.</summary>
    public string Executable => Path.Combine(
        _checkout.Root, Project, "bin", "Debug", "net9.0", _machine.IsWindows ? "sqlflow.exe" : "sqlflow");

    public void Prepare(CancellationToken ct)
    {
        if (_dotnet is null || _build is not null)
        {
            return;
        }

        _build = _tools.RunAsync(
            _dotnet, ["build", Project, "--verbosity", "quiet", "-nologo"], new ToolRun(WorkingDirectory: _checkout.Root, Timeout: BuildLimit), ct);
    }

    public async Task<ToolResult> RunAsync(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment, CancellationToken ct)
    {
        if (_dotnet is null)
        {
            return ToolResult.NotFound;
        }

        var built = _build is null
            ? await _tools.RunAsync(_dotnet, ["build", Project, "--verbosity", "quiet", "-nologo"], new ToolRun(WorkingDirectory: _checkout.Root, Attached: true), ct).ConfigureAwait(false)
            : await _build.ConfigureAwait(false);
        if (!built.Succeeded)
        {
            _machine.Prompter.Say("    The command line did not build:", Tone.Bad);
            foreach (var line in Tail(built))
            {
                _machine.Prompter.Say($"    {line}");
            }

            // A failed build is not kept: the next run builds again, attached, so its output is seen whole.
            _build = null;
            return built;
        }

        return await _tools.RunAsync(Executable, arguments, new ToolRun(WorkingDirectory: _checkout.Root, Environment: environment, Attached: true), ct).ConfigureAwait(false);
    }

    public async Task FinishAsync(IPrompter prompter, CancellationToken ct)
    {
        if (_build is null)
        {
            return;
        }

        if (!_build.IsCompleted)
        {
            prompter.Say("  Finishing the command line's build, which the start needs next ...", Tone.Note);
        }

        var built = await _build.ConfigureAwait(false);
        if (!built.Succeeded)
        {
            prompter.Say("  The command line did not build; the start builds it again and shows why:", Tone.Caution);
            foreach (var line in Tail(built))
            {
                prompter.Say($"    {line}");
            }
        }
    }

    private static IEnumerable<string> Tail(ToolResult result)
    {
        var text = string.Join('\n', result.Output, result.Error).Trim();
        if (result.TimedOut)
        {
            return [$"It ran for more than {BuildLimit.TotalMinutes:0} minutes and was stopped."];
        }

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? [$"dotnet build exited {result.ExitCode} without saying why."] : lines.TakeLast(15);
    }
}

/// <summary>
/// Where the .NET SDK is: the <c>dotnet</c> the PATH names, else the one an installer put in its usual place (winget and
/// the Windows installer in Program Files, Homebrew and the macOS installer in /usr/local/share/dotnet, a distribution
/// package in /usr/share or /usr/lib, the install script in ~/.dotnet), which a terminal started before the install
/// does not have on its PATH.
/// </summary>
internal static class DotnetLocator
{
    public static string? Find(DevMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return machine.Tools.Find("dotnet") ?? Candidates(machine).FirstOrDefault(machine.FileExists);
    }

    /// <summary>True when the SDK found is not the PATH's, so this terminal cannot run it by name.</summary>
    public static bool OffPath(DevMachine machine) => machine.Tools.Find("dotnet") is null && Find(machine) is not null;

    private static IEnumerable<string> Candidates(DevMachine machine)
    {
        string? Variable(string name) => machine.Environment.TryGetValue(name, out var value) && value.Length > 0 ? value : null;
        var home = Variable(machine.IsWindows ? "USERPROFILE" : "HOME");
        if (machine.IsWindows)
        {
            if (Variable("ProgramFiles") is { } programFiles)
            {
                yield return Path.Combine(programFiles, "dotnet", "dotnet.exe");
            }

            if (Variable("LOCALAPPDATA") is { } local)
            {
                yield return Path.Combine(local, "Microsoft", "dotnet", "dotnet.exe");
            }

            if (home is not null)
            {
                yield return Path.Combine(home, ".dotnet", "dotnet.exe");
            }

            yield break;
        }

        yield return machine.Platform == DevPlatform.MacOS ? "/usr/local/share/dotnet/dotnet" : "/usr/share/dotnet/dotnet";
        if (machine.Platform == DevPlatform.Linux)
        {
            yield return "/usr/lib/dotnet/dotnet";
        }

        if (home is not null)
        {
            yield return Path.Combine(home, ".dotnet", "dotnet");
        }
    }
}
