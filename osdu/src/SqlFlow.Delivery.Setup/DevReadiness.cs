using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Setup;

/// <summary>
/// What a local run's facts mean (osdu/docs/reference/cli/dev.md, What the check reports): a finding for every tool, every
/// setting the template names, both databases, the sign-in, the partitions, the repositories and what their flows name,
/// each with its fix. A setting "needed to start" that is missing or wrong is an error, and the launchers stop; one "needed
/// to run flows" is a warning; an optional one is reported when set. Nothing here reaches outside the facts.
/// </summary>
internal static partial class DevReadiness
{
    private static readonly string[] AdminSettings =
        ["ControlPlane__Bootstrap__AdminUsername", "ControlPlane__Bootstrap__AdminPasswordReference", "SQLFLOW_ADMIN_PASSWORD"];

    /// <summary>The report the facts make.</summary>
    public static DevReport Evaluate(DevFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var sections = new List<ReportSection>
        {
            new("Tools", Tools(facts)),
        };
        sections.AddRange(Settings(facts));
        sections.Add(new ReportSection("SQL Server", SqlServer(facts, out var catalogReady)));
        sections.Add(new ReportSection("Sign-in, partitions and flows", Contents(facts, catalogReady)));
        if (facts.Osdu is not null || facts.OsduNotTested is not null)
        {
            sections.Add(new ReportSection("OSDU", Osdu(facts)));
        }

        return new DevReport(sections, facts.Advice.Launcher);
    }

    private static List<Finding> Tools(DevFacts facts)
    {
        var advice = facts.Advice;
        var findings = new List<Finding>();
        var dotnet = facts.Tools.Dotnet;
        findings.Add(dotnet switch
        {
            { Succeeded: true } when facts.Tools.DotnetOffPath is { } path
                => Finding.Warning(".NET SDK", $"{dotnet.Output}, at {path}, which this terminal's PATH does not have yet", "Open a new terminal (an install since this one started is on the PATH of new ones)."),
            { Succeeded: true } => Finding.Ok(".NET SDK", dotnet.Output),
            { Found: false } => Finding.Error(".NET SDK", "not installed", advice.InstallDotnet, DevAdvice.NewTerminal),
            _ => Finding.Error(".NET SDK", "none matches global.json (9.0.100 or later)", advice.InstallDotnet),
        });

        var node = facts.Tools.Node;
        if (node.Succeeded && NodeVersion().Match(node.Output) is { Success: true } version)
        {
            var major = int.Parse(version.Groups["major"].Value, CultureInfo.InvariantCulture);
            findings.Add(major < 22
                ? Finding.Warning("Node.js", $"{node.Output}, older than the 22 the GUI is built with", advice.InstallNode)
                : !facts.Tools.Npm
                    ? Finding.Warning("Node.js", $"{node.Output}, but npm is not on the PATH", advice.InstallNode)
                    : Finding.Ok("Node.js", node.Output));
        }
        else if (!facts.Gui)
        {
            findings.Add(Finding.Off("Node.js", "not installed; not needed without the GUI"));
        }
        else
        {
            findings.Add(Finding.Error("Node.js", "not installed; the GUI needs it", advice.InstallNode, DevAdvice.NewTerminal, $"Or start the control plane alone: {advice.Launcher} api"));
        }

        if (!facts.Tools.AzureNeeded)
        {
            findings.Add(Finding.Off("Azure CLI", $"not needed: nothing in {advice.EnvFile} reads Azure through it"));
        }
        else
        {
            var account = facts.Tools.AzureAccount;
            findings.Add(account switch
            {
                { Succeeded: true } => Finding.Ok("Azure CLI", $"signed in as {account.Output}"),
                null or { Found: false } => Finding.Error("Azure CLI", $"not installed; {advice.EnvFile} reads Azure through it (SQLFLOW_AZURE_AUTH or a ${{keyvault:...}})", advice.InstallAzureCli, DevAdvice.NewTerminal),
                _ => Finding.Error("Azure CLI", "not signed in; Key Vault references and Azure storage read through it", "Sign in: az login"),
            });
        }

        // On Windows SQL Server runs as a service, so Docker is worth a line only when a SQL Server container is there or no
        // SQL Server runs; elsewhere SQL Server runs in it.
        var dockerMatters = facts.Advice.Platform != DevPlatform.Windows
            || facts.Instances.Any(i => i.Kind == SqlInstanceKind.Container)
            || (!facts.Instances.Any(i => i.Running) && facts.Docker != DockerState.NotInstalled);
        if (dockerMatters)
        {
            findings.Add(facts.Docker switch
            {
                DockerState.Running => Finding.Ok("Docker", "running"),
                DockerState.NotRunning => Finding.Info("Docker", "installed, not running", "Start Docker Desktop (or the Docker engine) when SQL Server runs in a container."),
                _ => Finding.Info("Docker", "not installed; SQL Server runs in it on this system", advice.InstallDocker),
            });
        }

        return findings;
    }

    private static List<ReportSection> Settings(DevFacts facts)
    {
        var advice = facts.Advice;
        var file = new List<Finding>();
        if (!facts.FileExists || facts.File is null)
        {
            file.Add(Finding.Error(advice.EnvFile, "missing", $"{advice.Launcher} writes it on its next start, asking for what it needs; or copy {DevCheckout.TemplateName} there and replace every <...>."));
            return [new ReportSection($"Settings ({advice.EnvFile})", file)];
        }

        foreach (var problem in facts.File.Problems)
        {
            file.Add(Finding.Error("file", $"line {problem.Line}: {problem.Message.TrimEnd('.')}; the hosts refuse the whole file", $"Fix or remove that line in {advice.EnvFile}."));
        }

        foreach (var group in facts.File.Entries.GroupBy(e => e.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            var lines = string.Join(" and ", group.Select(e => e.Line.ToString(CultureInfo.InvariantCulture)));
            file.Add(Finding.Warning(group.Key, $"set twice (lines {lines}): the first one counts", "Keep one of them."));
        }

        foreach (var entry in facts.File.Entries.DistinctBy(e => e.Name, StringComparer.Ordinal))
        {
            if (DevValues.LauncherProblem(entry.Value) is { } launcher)
            {
                file.Add(Finding.Error(entry.Name, launcher, "Quote a connection string's value with single quotes instead (Password='...'), remove the double quotes, or keep the value in a key vault and set a ${keyvault:...} reference."));
            }

            if (facts.Terminal.TryGetValue(entry.Name, out var terminal) && terminal.Length > 0 && !string.Equals(terminal, entry.Value, StringComparison.Ordinal))
            {
                file.Add(Finding.Warning(
                    entry.Name, $"this terminal's environment sets it too, to another value: {advice.Launcher} uses the file's, a command run by hand the terminal's",
                    advice.Platform == DevPlatform.Windows
                        ? $"Clear it in this terminal (set {entry.Name}=), and in Windows' environment variables if it is set there."
                        : $"Clear it in this shell (unset {entry.Name}) and in the profile that sets it."));
            }
        }

        file.AddRange(RetiredSettings(facts));
        var sections = new List<ReportSection>();
        if (file.Count > 0)
        {
            sections.Add(new ReportSection($"The file ({advice.EnvFile})", file));
        }

        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (need, title) in new[] { (SettingNeed.Start, "Settings needed to start"), (SettingNeed.Flows, "Settings needed to run flows"), (SettingNeed.Optional, "Optional settings") })
        {
            sections.Add(new ReportSection($"{title} ({advice.EnvFile})", NeedFindings(facts, need, named)));
        }

        sections[^1] = sections[^1] with { Findings = [.. sections[^1].Findings, .. CrossChecks(facts), .. Extras(facts)] };
        return sections;
    }

    private static List<Finding> NeedFindings(DevFacts facts, SettingNeed need, HashSet<string> named)
    {
        var advice = facts.Advice;
        var settings = facts.Settings;
        var findings = new List<Finding>();

        // An OSDU platform not set up at all is one line, not one per setting.
        var platform = facts.Template.Settings.Where(s => s.Need == need && !s.IsFamily && !s.Commented && s.Name.StartsWith("OSDU_", StringComparison.Ordinal) && s.Name != "OSDU_DATA_DB").ToList();
        if (platform.Count > 1 && platform.All(s => !settings.IsSet(s.Name)))
        {
            var names = string.Join(", ", platform.Select(s => s.Name));
            findings.Add(need == SettingNeed.Start
                ? Finding.Error("OSDU platform", $"not set up: {names}", $"Run {advice.Launcher} setup, which asks for them and can test them.")
                : Finding.Warning("OSDU platform", $"not set up: {names}", $"Run {advice.Launcher} setup, which asks for them and can test them; {advice.Launcher} starts without them, and runs that reach OSDU fail."));
            named.UnionWith(platform.Select(s => s.Name));
        }

        foreach (var setting in facts.Template.Settings.Where(s => s.Need == need && !s.IsFamily))
        {
            if (!named.Add(setting.Name))
            {
                continue;
            }

            var name = setting.Name;
            var value = settings.Get(name);
            var missing = need switch
            {
                SettingNeed.Start => FindingStatus.Error,
                SettingNeed.Flows => FindingStatus.Warning,
                _ => FindingStatus.Off,
            };

            // The first admin is needed only while the catalog has no user to sign in with.
            if (AdminSettings.Contains(name) && facts.CatalogContents is { Users: > 0 })
            {
                missing = FindingStatus.Off;
            }

            if (name == "SQLFLOW_ADMIN_PASSWORD" && settings.Get("ControlPlane__Bootstrap__AdminPasswordReference") is { } reference && reference != DevValues.AdminPasswordReference)
            {
                findings.Add(value is null
                    ? Finding.Off(name, "not needed: the admin's password reference names another source")
                    : Finding.Info(name, "set, but ControlPlane__Bootstrap__AdminPasswordReference does not name it"));
                continue;
            }

            var origin = settings.FromTerminal(name) ? " (from this terminal's environment)" : string.Empty;
            if (value is null)
            {
                findings.Add(missing switch
                {
                    FindingStatus.Off => Finding.Off(name, need != SettingNeed.Optional ? "not set; not needed, since the catalog has users" : setting.Commented ? "not set" : $"not set; the template sets {setting.Value}"),
                    _ => new Finding(missing, name, DevValues.IsPlaceholder(setting.Value) ? $"missing: {setting.Value}" : $"missing; the template has {setting.Value}", [$"Add it to {advice.EnvFile} ({advice.Launcher} setup asks for it)."]),
                });
                continue;
            }

            if (DevValues.IsPlaceholder(value))
            {
                findings.Add(missing == FindingStatus.Off
                    ? Finding.Off(name, $"still the template's placeholder {value}")
                    : new Finding(missing, name, $"still the template's placeholder: {value}", [$"Replace it in {advice.EnvFile}, or run {advice.Launcher} setup."]));
                continue;
            }

            if (DevValues.Problem(name, value, facts.Gui) is { } problem)
            {
                var status = need == SettingNeed.Optional ? FindingStatus.Warning : missing == FindingStatus.Off ? FindingStatus.Warning : missing;
                var fix = setting.Value == DevValues.Generated
                    ? $"Set it to {DevValues.Generated}: {advice.Launcher} generates a new one at its next start."
                    : $"Correct it in {advice.EnvFile}, or run {advice.Launcher} setup.";
                findings.Add(new Finding(status, name, problem, [fix]));
                continue;
            }

            if (DevValues.IsConnectionName(name) && advice.Platform != DevPlatform.Windows && SqlTarget.TryParse(value) is { Integrated: true })
            {
                findings.Add(new Finding(
                    need == SettingNeed.Start ? FindingStatus.Error : FindingStatus.Warning, name,
                    "uses Windows authentication, which needs Windows (or Kerberos set up for this server)",
                    [$"Use a SQL login: {advice.Launcher} setup asks for one."]));
                continue;
            }

            findings.Add(Finding.Ok(name, DevValues.Show(name, value) + origin));
        }

        return findings;
    }

    private static List<Finding> CrossChecks(DevFacts facts)
    {
        var settings = facts.Settings;
        var findings = new List<Finding>();
        var username = settings.Get("ControlPlane__Bootstrap__AdminUsername");
        var reference = settings.Get("ControlPlane__Bootstrap__AdminPasswordReference");
        if ((username is null) != (reference is null))
        {
            findings.Add(Finding.Error("First admin", "ControlPlane__Bootstrap__AdminUsername and AdminPasswordReference are set one without the other, which the control plane refuses", "Set both, or neither."));
        }

        if (reference == DevValues.AdminPasswordReference && settings.Get("SQLFLOW_ADMIN_PASSWORD") is { } password
            && !DevValues.IsPlaceholder(password) && password.Length < DevValues.MinPasswordLength)
        {
            findings.Add(Finding.Error("SQLFLOW_ADMIN_PASSWORD", $"shorter than {DevValues.MinPasswordLength} characters: the control plane does not create the admin", $"Set a longer one, or set it to {DevValues.Generated} for {facts.Advice.Launcher} to generate one."));
        }

        var catalogReference = settings.Get("ControlPlane__Catalog__ConnectionReference");
        if (catalogReference is not null && catalogReference != "${env:SQLFLOW_CATALOG_DB}" && catalogReference != settings.Get("SQLFLOW_CATALOG_DB"))
        {
            findings.Add(Finding.Warning("ControlPlane__Catalog__ConnectionReference", "differs from SQLFLOW_CATALOG_DB: the control plane uses another catalog than the one the launcher migrates", "Remove it (it defaults to ${env:SQLFLOW_CATALOG_DB}), or make both the same."));
        }

        return findings;
    }

    /// <summary>
    /// The settings that once gave the delivery module a database of its own, set in the file or in this terminal: an error
    /// each, since every host refuses to start while one is set, so the launchers stop before they start anything. The fix
    /// says whether removing it loses nothing (it names the catalog's own database) or a ledger has to move first.
    /// </summary>
    private static List<Finding> RetiredSettings(DevFacts facts)
    {
        var advice = facts.Advice;
        var catalog = facts.Settings.Get(DevValues.CatalogName);
        var findings = new List<Finding>();
        foreach (var name in DevValues.RetiredSettings)
        {
            var inFile = facts.File!.Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal))?.Value;
            var inTerminal = facts.Terminal.TryGetValue(name, out var terminal) ? terminal : null;
            var value = !string.IsNullOrWhiteSpace(inFile) ? inFile : !string.IsNullOrWhiteSpace(inTerminal) ? inTerminal : null;
            if (value is null)
            {
                continue;
            }

            var remove = value == inFile
                ? $"remove it from {advice.EnvFile}"
                : advice.Platform == DevPlatform.Windows
                    ? $"clear it in this terminal (set {name}=) and in Windows' environment variables"
                    : $"clear it in this shell (unset {name}) and in the profile that sets it";
            var fix = NamesTheCatalog(value, catalog)
                ? $"It names the catalog's database, so nothing is lost: {remove}."
                : $"It may name another database than {DevValues.CatalogName}, which would hold the ledger: bring that database's osdu schema into the catalog's database first, then {remove}.";
            findings.Add(Finding.Error(
                name, $"no longer a setting: the osdu schema lives in the catalog's database, which {DevValues.CatalogName} names, and every host refuses to start while it is set", fix));
        }

        return findings;
    }

    /// <summary>Whether a retired setting's value reaches the catalog's database: the same reference, or the same server and database.</summary>
    private static bool NamesTheCatalog(string value, string? catalog)
    {
        if (string.Equals(value, $"${{env:{DevValues.CatalogName}}}", StringComparison.Ordinal)
            || string.Equals(value, catalog, StringComparison.Ordinal))
        {
            return true;
        }

        return SqlTarget.TryParse(value) is { HasReference: false } retired
            && SqlTarget.TryParse(catalog) is { HasReference: false } current
            && retired.Database.Length > 0
            && SameServer(retired.Server, current.Server)
            && string.Equals(retired.Database, current.Database, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Names the file sets that the template does not: kept, and checked for a misspelling of one it does.</summary>
    private static List<Finding> Extras(DevFacts facts)
    {
        var findings = new List<Finding>();
        var known = facts.Template.Settings.Where(s => !s.IsFamily).Select(s => s.Name).ToList();
        var extra = new List<string>();
        foreach (var name in facts.File!.Entries.Select(e => e.Name).Distinct(StringComparer.Ordinal))
        {
            if (known.Contains(name, StringComparer.Ordinal) || DevFactsCollector.RepositorySetting().IsMatch(name) || name.StartsWith("Osdu__Partitions__", StringComparison.Ordinal)
                || name.StartsWith("Osdu__Properties__", StringComparison.Ordinal) || DevValues.RetiredSettings.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            var closest = known.Select(k => (Name: k, Distance: Distance(name, k))).OrderBy(k => k.Distance).FirstOrDefault();
            if (closest.Name is not null && closest.Distance <= 3)
            {
                findings.Add(Finding.Warning(name, $"not a setting the template names; did you mean {closest.Name}?", $"Rename it in {facts.Advice.EnvFile} if so."));
            }
            else
            {
                extra.Add(name);
            }
        }

        if (extra.Count > 0)
        {
            findings.Add(Finding.Info("Also set", string.Join(", ", extra)));
        }

        return findings;
    }

    private static List<Finding> SqlServer(DevFacts facts, out bool catalogReady)
    {
        var advice = facts.Advice;
        var findings = new List<Finding>
        {
            facts.Instances.Count > 0
                ? Finding.Info("On this machine", string.Join(", ", facts.Instances))
                : Finding.Info("On this machine", "no SQL Server instance"),
        };
        catalogReady = false;

        if (facts.Catalog is { } catalog)
        {
            var name = catalog.Target.Database;
            if (catalog.Tested is { } why)
            {
                findings.Add(Finding.Info($"Catalog {name}", $"{catalog.Target.Describe()}: not tested here, since {why}; the launcher's migration step shows whether it connects"));
            }
            else if (catalog.Server is not { Answered: true } server)
            {
                findings.Add(new Finding(FindingStatus.Error, catalog.Target.Server, $"does not answer: {catalog.Server?.Error}", ServerFix(advice, facts.Instances, facts.Docker, catalog.Target, catalog.Server!)));
            }
            else
            {
                var version = $"{server.Release} ({server.Version}), {server.Edition}, signed in as {server.Login}";
                findings.Add(server.Major is > 0 and < 16
                    ? Finding.Warning(catalog.Target.Server, $"{version}; OSDU Delivery is tested on SQL Server 2022", "Use SQL Server 2022 or later.")
                    : Finding.Ok(catalog.Target.Server, version));
                if (!server.FullText)
                {
                    findings.Add(Finding.Info("Full-text search", "not installed (optional): the catalog's search matches plain text instead, without word forms"));
                }

                var database = catalog.Database!;
                var contents = facts.CatalogContents;
                if (database.Error is { } unread)
                {
                    findings.Add(Finding.Error($"Catalog {name}", $"could not be read: {unread}"));
                }
                else if (!database.Exists)
                {
                    var fixes = new List<string> { $"Create it: {advice.Launcher} setup offers to, or run: {advice.CreateCatalog}" };
                    if (!server.CanCreate)
                    {
                        fixes.Add($"{server.Login} may not create databases on {catalog.Target.Server}: an administrator grants it dbcreator, or creates {name}.");
                    }

                    findings.Add(new Finding(FindingStatus.Error, $"Catalog {name}", "does not exist", fixes));
                }
                else if (!database.Online)
                {
                    findings.Add(Finding.Error($"Catalog {name}", $"is {database.State}", $"Bring it online on {catalog.Target.Server}."));
                }
                else if (!database.Access)
                {
                    findings.Add(Finding.Error($"Catalog {name}", $"exists, but {server.Login} may not open it", $"An administrator maps the login to a user in {name} with db_owner."));
                }
                else if (contents is null || (contents.Users is null && !contents.HasCatalog && contents.Problems.Count > 0))
                {
                    findings.Add(Finding.Error($"Catalog {name}", contents is { Problems.Count: > 0 } ? contents.Problems[0] : "could not be read"));
                }
                else if (!contents.HasCatalog)
                {
                    findings.Add(contents.Tables == 0
                        ? Finding.Error($"Catalog {name}", "exists but is empty", $"Create the catalog in it: {advice.Launcher} setup offers to, or run: {advice.CreateCatalog}")
                        : Finding.Error($"Catalog {name}", $"holds {contents.Tables} tables but no catalog: SQLFLOW_CATALOG_DB may name another database", $"Point SQLFLOW_CATALOG_DB at the catalog, or at a new database and run: {advice.CreateCatalog}"));
                }
                else
                {
                    catalogReady = true;
                    findings.Add(Finding.Ok($"Catalog {name}", contents.HasModule
                        ? $"exists; {advice.Launcher} applies its pending migrations at start"
                        : $"exists without the module's osdu schema; {advice.Launcher}'s migration step adds it"));
                    findings.AddRange(contents.Problems.Select(p => Finding.Warning($"Catalog {name}", p)));
                }
            }
        }

        if (facts.Data is { } data)
        {
            var name = data.Target.Database;
            var create = $"CREATE DATABASE [{name.Replace("]", "]]", StringComparison.Ordinal)}]; ALTER DATABASE [{name.Replace("]", "]]", StringComparison.Ordinal)}] SET ALLOW_SNAPSHOT_ISOLATION ON;";
            if (data.Tested is { } why)
            {
                findings.Add(Finding.Info($"Data {name}", $"{data.Target.Describe()}: not tested here, since {why}"));
            }
            else if (data.Server is not { Answered: true })
            {
                var sameServer = facts.Catalog?.Server is { Answered: false } && string.Equals(facts.Catalog.Target.ServerKey, data.Target.ServerKey, StringComparison.Ordinal);
                findings.Add(sameServer
                    ? Finding.Info($"Data {name}", $"not tested: {data.Target.Server} does not answer (above)")
                    : new Finding(FindingStatus.Warning, data.Target.Server, $"does not answer: {data.Server?.Error}", ServerFix(advice, facts.Instances, facts.Docker, data.Target, data.Server!)));
            }
            else if (data.Database is { Error: { } unread })
            {
                findings.Add(Finding.Warning($"Data {name}", $"could not be read: {unread}"));
            }
            else if (data.Database is not { Exists: true } database)
            {
                findings.Add(Finding.Warning($"Data {name}", "does not exist; pre-ingestion and ingestion flows have nowhere to write", $"Create it: {advice.Launcher} setup offers to, or run on {data.Target.Server}: {create}"));
            }
            else if (!database.Online)
            {
                findings.Add(Finding.Warning($"Data {name}", $"is {database.State}", $"Bring it online on {data.Target.Server}."));
            }
            else if (!database.Access)
            {
                findings.Add(Finding.Warning($"Data {name}", $"exists, but {data.Server.Login} may not open it", $"An administrator maps the login to a user in {name} with db_owner."));
            }
            else if (!database.Snapshot)
            {
                findings.Add(Finding.Warning($"Data {name}", "does not allow snapshot isolation, which a delivery reads under", $"{advice.Launcher} setup offers to allow it, or run on {data.Target.Server}: ALTER DATABASE [{name.Replace("]", "]]", StringComparison.Ordinal)}] SET ALLOW_SNAPSHOT_ISOLATION ON;"));
            }
            else
            {
                findings.Add(Finding.Ok($"Data {name}", "exists, snapshot isolation allowed"));
            }
        }

        return findings;
    }

    /// <summary>The fix for a server that did not answer, from what the error says and what this machine has installed.</summary>
    public static IReadOnlyList<string> ServerFix(DevAdvice advice, IReadOnlyList<SqlInstance> instances, DockerState docker, SqlTarget target, ServerFacts server)
    {
        ArgumentNullException.ThrowIfNull(advice);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(server);
        if (server.ErrorNumber == 18456)
        {
            return target.Integrated
                ? [$"{target.Server} has no login for your Windows account {advice.Account}. An administrator of the server adds one:", $"CREATE LOGIN [{advice.Account}] FROM WINDOWS; ALTER SERVER ROLE dbcreator ADD MEMBER [{advice.Account}];"]
                : [$"Check the SQL login's name and password ({advice.Launcher} setup asks for them again), and that the server allows SQL logins."];
        }

        if ((server.Error ?? string.Empty).Contains("certificate", StringComparison.OrdinalIgnoreCase))
        {
            return ["The server's certificate is not one this machine trusts. For a server on this machine, add TrustServerCertificate=True to the connection string."];
        }

        if (target.Place == SqlServerPlace.LocalDb)
        {
            var name = target.Instance.Length > 0 ? target.Instance : "MSSQLLocalDB";
            var instance = instances.FirstOrDefault(i => i.Kind == SqlInstanceKind.LocalDb && string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            return instance is null
                ? [$"LocalDB has no instance {name} here. Create it: sqllocaldb create {name} -s (LocalDB comes with SQL Server Express), or use another server: {advice.Launcher} setup."]
                : [$"Start it: sqllocaldb start {name}"];
        }

        if (target.Place == SqlServerPlace.Elsewhere)
        {
            return [$"Check the server's name, that {target.Server} accepts remote connections (TCP), and that no firewall is in between."];
        }

        var serviceName = target.Instance.Length > 0 ? "MSSQL$" + target.Instance : "MSSQLSERVER";
        var service = instances.FirstOrDefault(i => i.Kind == SqlInstanceKind.Service && string.Equals(i.Name, serviceName, StringComparison.OrdinalIgnoreCase));
        if (service is not null)
        {
            if (service.Running)
            {
                return [$"The service {serviceName} is running but did not answer: check its error log, and that it accepts connections."];
            }

            var fixes = new List<string>();
            if (service.Disabled)
            {
                fixes.Add($"The service {serviceName} is disabled. As an administrator: Set-Service '{serviceName}' -StartupType Automatic");
            }

            fixes.Add($"The service {serviceName} is {service.State}. Start it as an administrator: Start-Service '{serviceName}'");
            fixes.Add("Windows Update stops it while it installs a SQL Server update; wait for that to finish if one is running.");
            return fixes;
        }

        var container = instances.FirstOrDefault(i => i.Kind == SqlInstanceKind.Container && SameServer(i.Server, target.Server));
        if (container is not null)
        {
            return container.Running
                ? [$"The container {container.Name} runs but did not answer: it may still be starting, or see why with: docker logs {container.Name}"]
                : docker == DockerState.Running
                    ? [$"Start the container: docker start {container.Name}"]
                    : [$"Start Docker Desktop (or the Docker engine), then start the container: docker start {container.Name}"];
        }

        if (instances.Count > 0)
        {
            return [$"This machine has no SQL Server at {target.Server}, but has: {string.Join(", ", instances)}.", $"Point the connection at one of them: {advice.Launcher} setup"];
        }

        return advice.GetSqlServer;
    }

    private static bool SameServer(string a, string b)
    {
        static string Normal(string server)
        {
            var (place, instance) = SqlTarget.PlaceOf(server);
            var comma = server.IndexOf(',', StringComparison.Ordinal);
            var port = comma >= 0 ? server[(comma + 1)..].Trim() : "1433";
            return $"{place}|{instance}|{port}".ToUpperInvariant();
        }

        return string.Equals(Normal(a), Normal(b), StringComparison.Ordinal);
    }

    private static List<Finding> Contents(DevFacts facts, bool catalogReady)
    {
        var advice = facts.Advice;
        var settings = facts.Settings;
        var findings = new List<Finding>();
        var contents = facts.CatalogContents;
        if (!catalogReady || contents is null)
        {
            findings.Add(Finding.Off("Catalog contents", "not read: the catalog is not ready (above)"));
        }
        else
        {
            var admin = settings.Get("ControlPlane__Bootstrap__AdminUsername");
            if (contents.Users is { } users)
            {
                findings.Add(admin is not null && contents.AdminExists
                    ? Finding.Ok("Sign-in", $"{users} user(s); {admin} exists, with the password it was given when created or changed since")
                    : admin is not null
                        ? Finding.Ok("Sign-in", $"{users} user(s); {admin} is created at the next start, with the password in SQLFLOW_ADMIN_PASSWORD")
                        : users > 0
                            ? Finding.Ok("Sign-in", $"{users} user(s); no first admin configured, none needed")
                            : Finding.Error("Sign-in", "the catalog has no user and no first admin is configured: nobody can sign in", $"Run {advice.Launcher} setup, which configures one."));
            }

            var configured = settings.Values.Keys.Select(k => ConfiguredPartition().Match(k)).Where(m => m.Success).Select(m => m.Groups["name"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var partition = settings.IsSet("OSDU_DATA_PARTITION") ? settings.Get("OSDU_DATA_PARTITION") : null;
            if (contents.Partitions.Count > 0)
            {
                findings.Add(Finding.Ok("Partitions", "registered: " + string.Join(", ", contents.Partitions.Select(p => p.IsDefault ? $"{p.Name} (default)" : p.Name))));
                if (partition is not null && !contents.Partitions.Any(p => string.Equals(p.Name, partition, StringComparison.OrdinalIgnoreCase))
                    && !configured.Contains(partition, StringComparer.OrdinalIgnoreCase))
                {
                    findings.Add(Finding.Warning("Partitions", $"OSDU_DATA_PARTITION is {partition}, which the catalog does not register", $"Register it: {advice.Cli} partition add {partition}"));
                }
            }
            else if (configured.Count > 0)
            {
                findings.Add(Finding.Ok("Partitions", $"configured in {advice.EnvFile}: {string.Join(", ", configured)}, registered at the next start"));
            }
            else
            {
                findings.Add(Finding.Warning(
                    "Partitions", "none registered: the control plane reports itself not ready, and cache, dimension and assertion flows have no partition to work in",
                    partition is not null ? $"Register it: {advice.Cli} partition add {partition} --default" : $"Run {advice.Launcher} setup, which asks for it and registers it."));
            }
        }

        if (facts.Repositories.Count == 0 && catalogReady)
        {
            findings.Add(Finding.Warning("Flow repositories", "none: the control plane has no flows to run", $"Register one on the GUI's Repositories page, or run {advice.Launcher} setup."));
        }

        foreach (var repository in facts.Repositories)
        {
            var label = $"Repository {repository.Name}";
            if (!repository.Enabled)
            {
                findings.Add(Finding.Off(label, $"{repository.Where}, disabled"));
                continue;
            }

            if (repository.Folder is not null && !repository.FolderExists)
            {
                findings.Add(Finding.Warning(label, $"{repository.Folder} does not exist on this machine: its sync fails", $"Clone or copy it there, or change the source: on the GUI's Repositories page, or with {advice.Launcher} setup."));
                continue;
            }

            var pending = repository.Configured && !repository.Registered && catalogReady ? "; registered at the next start" : string.Empty;
            if (repository.LastError.Length > 0)
            {
                var error = repository.LastError.Split('\n', 2)[0].Trim();
                findings.Add(Finding.Warning(label, $"{repository.Where}; its last sync failed: {(error.Length > 160 ? error[..157] + "..." : error)}"));
            }
            else
            {
                findings.Add(Finding.Ok(label, repository.Where + pending));
            }
        }

        // The GitHub App the hosts read private GitHub repositories with: all of it, or none of it.
        if (GitHubApp(settings) is { } app)
        {
            findings.Add(app.Problem is null
                ? Finding.Ok("GitHub App", $"{app.Id} reads the private GitHub repositories it is installed on (read-only is enough)")
                : Finding.Warning("GitHub App", app.Problem + ": the hosts refuse to read a repository with it",
                    $"Fix it in {advice.EnvFile}, or run {advice.Launcher} setup (sqlflow/docs/reference/guides/github-app.md)."));
        }

        if (facts.References is { } references)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, value) in settings.Values)
            {
                if (value.Length > 0 && !DevValues.IsPlaceholder(value))
                {
                    set.Add(name);
                }

                if (PropertySetting().Match(name) is { Success: true } property)
                {
                    set.Add(property.Groups["name"].Value);
                }
            }

            set.UnionWith(facts.Terminal.Where(t => t.Value.Length > 0).Select(t => t.Key));
            set.UnionWith(contents?.ConfigNames ?? []);
            var unset = references.Where(r => !set.Contains(r.Name)).ToList();
            if (references.Count == 0)
            {
                findings.Add(Finding.Info("Flow references", $"no ${{env:...}} in the {facts.DocumentsRead} YAML document(s) read"));
            }
            else if (unset.Count == 0)
            {
                findings.Add(Finding.Ok("Flow references", $"every ${{env:...}} the flows name is set ({references.Count} name(s) in {facts.DocumentsRead} document(s))"));
            }

            foreach (var use in unset)
            {
                var more = use.Documents.Count > 1 ? $" and {use.Documents.Count - 1} more" : string.Empty;
                findings.Add(Finding.Warning(
                    $"${{env:{use.Name}}}", $"named by {use.Documents[0]}{more}, set nowhere: a run that reads it fails",
                    $"Add {use.Name}=<value> to {advice.EnvFile}, or set it in the central configuration (sqlflow config set)."));
            }
        }

        return findings;
    }

    private static List<Finding> Osdu(DevFacts facts)
    {
        if (facts.OsduNotTested is { } why)
        {
            return [Finding.Off("OSDU sign-in", why)];
        }

        return OsduFindings(facts.Osdu!, facts.Settings.Get, facts.Advice);
    }

    /// <summary>What an OSDU sign-in test found, as findings: the sign-in, then the legal tag.</summary>
    /// <param name="result">The test's result.</param>
    /// <param name="get">The setting values the test used, by name.</param>
    /// <param name="advice">The platform's commands.</param>
    public static List<Finding> OsduFindings(OsduSignInResult result, Func<string, string?> get, DevAdvice advice)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(advice);
        var findings = new List<Finding>();
        if (!result.TokenIssued)
        {
            var status = result.TokenStatus > 0 ? $"HTTP {result.TokenStatus}: " : string.Empty;
            findings.Add(Finding.Warning("OSDU sign-in", $"{result.TokenHost} refused the client: {status}{result.TokenReason}", $"Check OSDU_TOKEN_URL, OSDU_CLIENT_ID, OSDU_CLIENT_SECRET and OSDU_SCOPE ({advice.Launcher} setup asks for them again)."));
            return findings;
        }

        findings.Add(Finding.Ok("OSDU sign-in", $"{result.TokenHost} issued a token to client {get("OSDU_CLIENT_ID")}"));
        var partition = get("OSDU_DATA_PARTITION");
        var tag = get("OSDU_LEGAL_TAG");
        var host = Uri.TryCreate(get("OSDU_URL"), UriKind.Absolute, out var platform) ? platform.Host : "OSDU";
        findings.Add(result.LegalTagStatus switch
        {
            null => Finding.Off("OSDU legal tag", "not read: OSDU_URL, OSDU_DATA_PARTITION and OSDU_LEGAL_TAG are all needed"),
            200 => Finding.Ok("OSDU legal tag", $"{partition} holds {tag}, read from {host}"),
            404 => Finding.Warning("OSDU legal tag", $"{partition} holds no legal tag {tag}", "Set OSDU_LEGAL_TAG to a tag the partition holds; every record the flows write carries it."),
            401 => Finding.Warning("OSDU legal tag", $"OSDU did not accept the token (HTTP 401): {result.LegalTagReason}", "Check that OSDU_SCOPE is the scope this platform accepts, and OSDU_URL its base URL."),
            403 => Finding.Warning("OSDU legal tag", $"OSDU refused the client in partition {partition} (HTTP 403): {result.LegalTagReason}", $"Check OSDU_DATA_PARTITION, and that the client is entitled in {partition}."),
            0 => Finding.Warning("OSDU legal tag", $"{host} did not answer: {result.LegalTagReason}", "Check OSDU_URL: the platform's base URL, without a path."),
            _ => Finding.Warning("OSDU legal tag", $"{host} answered HTTP {result.LegalTagStatus}: {result.LegalTagReason}", "Check OSDU_URL: the platform's base URL, without a path."),
        });
        return findings;
    }

    /// <summary>The edit distance of two names, ignoring case: how many letters to add, remove or change.</summary>
    public static int Distance(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var x = a.ToUpperInvariant();
        var y = b.ToUpperInvariant();
        var previous = new int[y.Length + 1];
        var current = new int[y.Length + 1];
        for (var j = 0; j <= y.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= x.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= y.Length; j++)
            {
                var cost = x[i - 1] == y[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[y.Length];
    }

    [GeneratedRegex(@"^v?(?<major>\d+)\.", RegexOptions.CultureInvariant)]
    private static partial Regex NodeVersion();

    [GeneratedRegex(@"^Osdu__Partitions__(?<name>[A-Za-z0-9_.\-]+?)__", RegexOptions.CultureInvariant)]
    private static partial Regex ConfiguredPartition();

    [GeneratedRegex(@"^Osdu__(Partitions__[A-Za-z0-9_.\-]+?__)?Properties__(?<name>[A-Za-z0-9_]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex PropertySetting();

    /// <summary>
    /// The host's GitHub App as the env file names it, and what keeps the hosts from using it (an App ID without a key, a
    /// key without an App ID, or both key settings), as SQLFlow refuses the same; null when the file names none of it.
    /// </summary>
    private static (string Id, string? Problem)? GitHubApp(DevSettings settings)
    {
        var id = settings.Get(DevValues.GitHubAppIdName);
        var key = settings.Get(DevValues.GitHubAppKeyName);
        var path = settings.Get(DevValues.GitHubAppKeyPathName);
        if (id is null && key is null && path is null)
        {
            return null;
        }

        var problem = id is null
            ? $"{(key is not null ? DevValues.GitHubAppKeyName : DevValues.GitHubAppKeyPathName)} is set but {DevValues.GitHubAppIdName} is not"
            : key is null && path is null
                ? $"{DevValues.GitHubAppIdName} is set but the App has no private key ({DevValues.GitHubAppKeyPathName}, or {DevValues.GitHubAppKeyName})"
                : key is not null && path is not null
                    ? $"both {DevValues.GitHubAppKeyName} and {DevValues.GitHubAppKeyPathName} are set; set one"
                    : null;
        return (id ?? "(no App ID)", problem);
    }
}
