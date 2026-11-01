using System.Collections;
using System.Text.Json;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Delivery.Setup;

/// <summary>Which command line the setup runs for the verbs only it has.</summary>
public enum DevCommandLineKind
{
    /// <summary>The process running the setup is the command line (<c>sqlflow dev</c>): it runs itself again.</summary>
    Self,

    /// <summary>The setup host (<c>osdu-delivery-setup</c>): it builds the checkout's command line and runs that.</summary>
    Checkout,
}

/// <summary>
/// The local run's setup and check (osdu/docs/reference/cli/dev.md), as <c>osdu-delivery-setup</c> and <c>sqlflow dev</c>
/// both run them: <c>setup</c> asks the questions and writes <c>.sqlflow/env</c>; <c>check</c> says what the run has and what
/// it is missing, each with its fix; <c>env</c> prints the file's settings for dev.bat and dev.sh to set. The env file is read
/// here, never applied first, so a file the hosts would refuse is still checked and reported line by line.
/// </summary>
public static class DevCommand
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The usage lines, for <paramref name="program"/> (<c>osdu-delivery-setup</c>, or <c>sqlflow dev</c>).</summary>
    public static IReadOnlyList<string> UsageFor(string program)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        return
        [
            $"{program} setup [--env-file <path>]",
            "                                   Set this checkout up for a local run: asks for the SQL Server (and starts",
            "                                   SQL Server in Docker when the machine has none), the databases, the first",
            "                                   admin, the OSDU platform and its client, and a flow repository; shows the",
            "                                   plan; then writes .sqlflow/env from dev.env.example (keeping the file it",
            "                                   replaces), creates the databases, registers the partition, and checks.",
            "                                   dev.bat and dev.sh run it when .sqlflow/env is missing, and 'setup' again",
            $"{program} demo [--reset] [--env-file <path>] [--api]",
            "                                   The setup with nothing to ask, for a clean machine: the sample estates, synthetic",
            "                                   data, and a stand-in for OSDU on this machine. It reaches no real OSDU, and it",
            "                                   refuses a file or a catalog it did not make. dev.bat demo runs it, then 'tour'.",
            "                                   --reset: first remove the databases and what the stand-in holds, all of which an",
            "                                   earlier demo made, for a start from nothing",
            $"{program} tour [--env-file <path>] [--url <control plane>]",
            "                                   Fires both estates' schedules through the control plane in order, and says whether",
            "                                   every run succeeded and every assertion was evaluated and passed. Exits 1 when not",
            $"{program} check [--env-file <path>] [--api] [--test-osdu] [--prepare] [--json]",
            "                                   What a local run has and what it is missing, each with its fix: the tools,",
            "                                   every setting, the SQL Server and both databases, sign-in, partitions,",
            "                                   repositories and the ${env:...} their flows name. --api: no GUI, so no",
            "                                   Node.js; --test-osdu signs in to OSDU and reads the legal tag (writes",
            "                                   nothing); --prepare first generates a copied template's <generated> values",
            "                                   and signs in to Azure when the file reads it. Exits 2 when a start would fail",
            $"{program} env [--env-file <path>]",
            "                                   The file's settings as NAME=value lines, read as the hosts read them, for",
            "                                   dev.bat and dev.sh to set. Exits 2, printing none, for a file they refuse",
        ];
    }

    /// <summary>The options that take a value.</summary>
    public static IReadOnlyList<string> ValueOptions { get; } = ["--env-file", "--url"];

    /// <summary>The plain flags.</summary>
    public static IReadOnlyList<string> Flags { get; } = ["--api", "--test-osdu", "--prepare", "--reset"];

    /// <summary>Runs the command on this machine; <paramref name="args"/> start with the subcommand.</summary>
    /// <param name="args">The subcommand and its options.</param>
    /// <param name="program">What the usage lines call the command.</param>
    /// <param name="output">Where its output goes.</param>
    /// <param name="error">Where notes and errors go.</param>
    /// <param name="workingDirectory">Where the checkout is looked for, from there upwards.</param>
    /// <param name="commandLine">Which command line runs the verbs only it has.</param>
    /// <param name="ct">Stops it.</param>
    public static Task<int> RunAsync(
        IReadOnlyList<string> args, string program, TextWriter output, TextWriter error, string workingDirectory, DevCommandLineKind commandLine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        var machine = CreateMachine(output, commandLine, ct);
        return RunAsync(args, program, machine, output, error, workingDirectory, ct);
    }

    /// <summary>The command, over the machine it reaches.</summary>
    internal static async Task<int> RunAsync(
        IReadOnlyList<string> args, string program, DevMachine machine, TextWriter output, TextWriter error, string workingDirectory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var parsed = DevArguments.Parse(args);
        if (parsed.Help)
        {
            WriteUsage(output, program);
            return 0;
        }

        if (parsed.Problem is { } problem)
        {
            return UsageError(error, program, problem);
        }

        if (parsed.Json && parsed.Subcommand != "check")
        {
            return UsageError(error, program, "--json applies to 'check' alone.");
        }

        var checkout = DevCheckout.Find(workingDirectory, parsed.EnvFile);
        if (parsed.Subcommand == "env")
        {
            return Env(checkout, output, error, machine.Platform);
        }

        if (parsed.Subcommand == "tour")
        {
            return await TourAsync(checkout, parsed.Url, machine, error, ct).ConfigureAwait(false);
        }

        var template = await DevEnvTemplate.LoadAsync(checkout.TemplatePath, ct).ConfigureAwait(false);
        if (parsed.Reset && parsed.Subcommand != "demo")
        {
            return UsageError(error, program, "reset applies to 'demo' alone.");
        }

        if (parsed.Subcommand is "setup" or "demo")
        {
            var commandLine = machine.CommandLineFor(checkout, machine);
            try
            {
                var wizard = new DevSetupWizard(checkout, template, machine, commandLine);
                if (!await (parsed.Subcommand == "demo" ? wizard.RunDemoAsync(parsed.Reset, ct) : wizard.RunAsync(ct)).ConfigureAwait(false))
                {
                    machine.Prompter.Say("Stopped; nothing was written.", Tone.Caution);
                    return 1;
                }
            }
            catch (SetupStoppedException stopped)
            {
                machine.Prompter.Say(string.Empty);
                machine.Prompter.Say(stopped.Message, Tone.Caution);
                return 1;
            }
            finally
            {
                // A build the setup started for the command line finishes before the setup ends, so none outlives it.
                await commandLine.FinishAsync(machine.Prompter, CancellationToken.None).ConfigureAwait(false);
            }

            return await CheckAsync(checkout, template, machine, parsed.Gui, testOsdu: false, json: false, signIn: false, output, ct).ConfigureAwait(false);
        }

        if (parsed.Prepare && File.Exists(checkout.EnvFile))
        {
            var generated = await DevEnvFile.CompleteGeneratedAsync(checkout.EnvFile, ct).ConfigureAwait(false);
            if (generated.Count > 0)
            {
                error.WriteLine($"Generated {string.Join(" and ", generated)} in {checkout.Show(checkout.EnvFile)}.");
            }
        }

        return await CheckAsync(checkout, template, machine, parsed.Gui, parsed.TestOsdu, parsed.Json, parsed.Prepare, output, ct).ConfigureAwait(false);
    }

    private static async Task<int> CheckAsync(
        DevCheckout checkout, DevEnvTemplate template, DevMachine machine, bool gui, bool testOsdu, bool json, bool signIn, TextWriter output, CancellationToken ct)
    {
        var facts = await DevFactsCollector.CollectAsync(checkout, template, machine, gui, testOsdu, ct).ConfigureAwait(false);

        // A start that reads Azure through the Azure CLI signs in here rather than failing on its first Key Vault reference.
        if (signIn && !json && facts.Tools is { AzureNeeded: true, AzureAccount: { Found: true, Succeeded: false } } && !Console.IsInputRedirected)
        {
            machine.Prompter.Say("Not signed in to Azure, which this file reads through the Azure CLI; opening the sign-in ...");
            await machine.Tools.RunAsync("az", ["login", "--output", "none"], new ToolRun(Attached: true), ct).ConfigureAwait(false);
            facts = await DevFactsCollector.CollectAsync(checkout, template, machine, gui, testOsdu, ct).ConfigureAwait(false);
        }

        var report = DevReadiness.Evaluate(facts);
        if (json)
        {
            output.WriteLine(report.ToJson().ToJsonString(Indented));
        }
        else
        {
            report.Write(machine.Prompter);
        }

        return report.Ready ? 0 : 2;
    }

    /// <summary>Signs in as the file's first admin and runs the tour against the control plane at <paramref name="url"/>.</summary>
    private static async Task<int> TourAsync(DevCheckout checkout, string? url, DevMachine machine, TextWriter error, CancellationToken ct)
    {
        var settings = File.Exists(checkout.EnvFile) ? LocalEnvFile.Read(checkout.EnvFile).Entries : [];
        var user = settings.FirstOrDefault(e => e.Name == "ControlPlane__Bootstrap__AdminUsername")?.Value;
        var password = settings.FirstOrDefault(e => e.Name == "SQLFLOW_ADMIN_PASSWORD")?.Value;
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
        {
            error.WriteLine($"ERROR  {checkout.Show(checkout.EnvFile)} names no first admin to sign in with; run the demo first.");
            return 2;
        }

        if (!Uri.TryCreate(url ?? DevValues.ControlPlaneUrl, UriKind.Absolute, out var address))
        {
            error.WriteLine($"ERROR  '{url}' is not a URL.");
            return 1;
        }

        using var http = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromMinutes(2) };
        var standIn = settings.FirstOrDefault(e => e.Name == DevSetupWizard.StandInSwitch)?.Value == "true"
            ? settings.FirstOrDefault(e => e.Name == "OSDU_URL")?.Value
            : null;
        var tour = new DevTour(http, machine.Prompter, machine.Now, static (time, token) => Task.Delay(time, token), standIn);
        try
        {
            return await tour.RunAsync(user, password, DevValues.GuiOrigin, ct).ConfigureAwait(false) ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            machine.Prompter.Say($"The tour lost the control plane at {address}: {SecretHygiene.RedactedMessage(ex)}", Tone.Bad);
            machine.Prompter.Say("-> Its window says why. The estates and what they delivered are kept: dev.bat demo (./dev.sh demo) runs the tour again.");
            return 1;
        }
    }

    /// <summary>
    /// The file's settings, one NAME=value line each, the first of a name set twice, as the hosts read the file; a file they
    /// refuse prints nothing (exit 2), each refused line named on <paramref name="error"/>.
    /// </summary>
    private static int Env(DevCheckout checkout, TextWriter output, TextWriter error, DevPlatform platform)
    {
        var launcher = platform == DevPlatform.Windows ? "dev.bat" : "./dev.sh";
        if (!File.Exists(checkout.EnvFile))
        {
            error.WriteLine($"ERROR  {checkout.Show(checkout.EnvFile)} is missing; {launcher} setup writes it.");
            return 2;
        }

        var content = LocalEnvFile.Read(checkout.EnvFile);
        if (content.Problems.Count > 0)
        {
            foreach (var problem in content.Problems)
            {
                error.WriteLine($"ERROR  {checkout.Show(checkout.EnvFile)}({problem.Line}): {problem.Message}");
            }

            return 2;
        }

        foreach (var entry in content.Entries.DistinctBy(e => e.Name, StringComparer.Ordinal))
        {
            output.WriteLine($"{entry.Name}={entry.Value}");
        }

        return 0;
    }

    private static void WriteUsage(TextWriter writer, string program)
    {
        writer.WriteLine("Usage:");
        foreach (var line in UsageFor(program))
        {
            writer.WriteLine($"  {line}");
        }
    }

    private static int UsageError(TextWriter error, string program, string reason)
    {
        error.WriteLine($"ERROR  {reason}");
        WriteUsage(error, program);
        return 1;
    }

    /// <summary>The machine this process runs on, as the command reaches it.</summary>
    internal static DevMachine CreateMachine(TextWriter output, DevCommandLineKind commandLine, CancellationToken ct)
    {
        var platform = DevMachine.CurrentPlatform;
        var environment = new Dictionary<string, string>(platform == DevPlatform.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string name && variable.Value is string value)
            {
                environment[name] = value;
            }
        }

        var account = platform == DevPlatform.Windows ? $"{Environment.UserDomainName}\\{Environment.UserName}" : Environment.UserName;
        var tools = new ProcessToolRunner();
        Func<DevCheckout, DevMachine, ICommandLine> factory = commandLine == DevCommandLineKind.Self
            ? (checkout, _) => new RunningCommandLine(tools, SelfCommand.Current(), checkout.Root)
            : (checkout, machine) => new CheckoutCommandLine(tools, checkout, machine);
        return new DevMachine(
            tools, new SqlServerProbe(), new OsduSignIn(), new ConsolePrompter(output, ct), environment, platform, account,
            () => DateTimeOffset.Now, factory, File.Exists);
    }
}

/// <summary>The command's arguments: the subcommand, the env file, the flags, or what is wrong with them.</summary>
internal sealed record DevArguments(string? Subcommand, string? EnvFile, string? Url, bool Api, bool TestOsdu, bool Prepare, bool Json, bool Help, string? Problem, bool Reset = false)
{
    /// <summary>True when the GUI starts too.</summary>
    public bool Gui => !Api;

    /// <summary>
    /// Reads <paramref name="args"/>: one subcommand (setup, demo, check, tour or env), <c>--env-file &lt;path&gt;</c>, the flags, and
    /// <c>--json</c>; <c>-v</c> and <c>--verbose</c> are taken and ignored, as every verb of the command line takes them.
    /// </summary>
    public static DevArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? subcommand = null;
        string? envFile = null;
        string? url = null;
        bool api = false, testOsdu = false, prepare = false, json = false, help = false, reset = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i] ?? string.Empty;
            switch (arg)
            {
                case "-h" or "--help":
                    help = true;
                    continue;
                case "-v" or "--verbose":
                    continue;
                case "--api":
                    api = true;
                    continue;
                case "--test-osdu":
                    testOsdu = true;
                    continue;
                case "--prepare":
                    prepare = true;
                    continue;
                case "--json":
                    json = true;
                    continue;
                case "--reset":
                    reset = true;
                    continue;
                case "--env-file":
                    if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        return Fail("--env-file needs the path of the env file.");
                    }

                    envFile = args[++i];
                    continue;
                case "--url":
                    if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        return Fail("--url needs the control plane's address.");
                    }

                    url = args[++i];
                    continue;
            }

            if (arg.StartsWith('-'))
            {
                return Fail($"'{arg}' is not an option of this command; it takes --env-file <path>, --url <address>, --api, --test-osdu, --prepare, --reset and --json.");
            }

            if (subcommand is not null)
            {
                return Fail($"'{arg}' follows the subcommand '{subcommand}', which takes no other word.");
            }

            subcommand = arg.ToLowerInvariant();
        }

        if (help)
        {
            return new DevArguments(subcommand, envFile, url, api, testOsdu, prepare, json, true, null, reset);
        }

        if (subcommand is not ("setup" or "demo" or "check" or "tour" or "env"))
        {
            return Fail(subcommand is null ? "name what to do: setup, demo, check, tour or env." : $"'{subcommand}' is none of setup, demo, check, tour and env.");
        }

        return new DevArguments(subcommand, envFile, url, api, testOsdu, prepare, json, false, null, reset);

        static DevArguments Fail(string problem) => new(null, null, null, false, false, false, false, false, problem);
    }
}
