using System.Text.RegularExpressions;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Delivery.Setup;

/// <summary>
/// The setup's questions (osdu/docs/reference/cli/dev.md, The setup): the SQL Server (starting one in Docker when the machine
/// has none), the two databases, the first admin, the OSDU platform and its client (tested on request), a flow repository,
/// and the optional switches. Nothing changes until the user has seen the plan and said yes: then the env file is written
/// (the one it replaces kept beside it), the databases created or adjusted, and the partition registered, each through the
/// path that already does it (the command line's own db migrate and partition add). A current file's values are offered as
/// the defaults, and every name it set that no question asks about is kept.
/// </summary>
internal sealed partial class DevSetupWizard(DevCheckout checkout, DevEnvTemplate template, DevMachine machine, ICommandLine commandLine)
{
    private readonly DevQuestions _ask = new(machine.Prompter);
    private readonly DevAdvice _advice = new(machine.Platform, checkout.Show(checkout.EnvFile), machine.Account);

    /// <summary>How long the setup waits for a SQL Server container it started to accept a sign-in.</summary>
    internal TimeSpan ContainerWait { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>How long it waits between tries.</summary>
    internal TimeSpan ContainerPoll { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Asks the questions, then writes and acts. False when the user stopped before anything was written.</summary>
    public async Task<bool> RunAsync(CancellationToken ct)
    {
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        IReadOnlyList<string> parked = [];
        if (File.Exists(checkout.EnvFile))
        {
            var content = LocalEnvFile.Read(checkout.EnvFile);
            foreach (var entry in content.Entries)
            {
                current.TryAdd(entry.Name, entry.Value);
            }

            parked = DevEnvFile.CommentedSettings(await File.ReadAllLinesAsync(checkout.EnvFile, ct).ConfigureAwait(false));
            if (content.Problems.Count > 0)
            {
                _ask.Note($"The current {_advice.EnvFile} has {content.Problems.Count} line(s) the hosts refuse; the new file leaves them out (the old one is kept beside it).");
            }
        }

        var values = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in current)
        {
            values[name] = value;
        }

        machine.Prompter.Say(string.Empty);
        machine.Prompter.Say("OSDU Delivery: setting up this machine for a local run", Tone.Heading);
        _ask.Note($"This writes {_advice.EnvFile} (git-ignored, on this machine only), the settings {_advice.Launcher}, the control plane and the");
        _ask.Note("command line run with. Press Enter to take the value in [brackets]. Nothing changes until you have seen the plan");
        _ask.Note("and said yes; Ctrl+C stops without writing anything.");

        Dotnet(ct);
        var sql = await SqlServerAsync(current, ct).ConfigureAwait(false);
        var databases = await DatabasesAsync(current, values, sql, ct).ConfigureAwait(false);
        var admin = await SignInAsync(current, values, sql, databases, ct).ConfigureAwait(false);
        var partition = await OsduAsync(current, values, ct).ConfigureAwait(false);
        Flows(values, databases.Contents);
        Optional(current, values);

        return await WriteAsync(values, parked, sql, databases, admin, partition, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // .NET
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts building the command line, which creating the catalog and registering the partition need, so it is ready by
    /// the time the answers are. The launchers install .NET before they build the setup, so it is missing only for a setup
    /// started some other way: then the setup says how to install it and goes on without the steps that need it.
    /// </summary>
    private void Dotnet(CancellationToken ct)
    {
        if (commandLine.Unavailable is { } why)
        {
            _ask.Heading(".NET: what OSDU Delivery builds and runs on");
            _ask.Note($"{char.ToUpperInvariant(why[0])}{why[1..]}. The control plane, the workers and the command line all run on it.");
            _ask.Note(_advice.InstallDotnet);
            _ask.Note($"The setup goes on without it: it writes the file and makes what it can, and the catalog and the partition");
            _ask.Note($"are made once .NET is installed and {_advice.Launcher} setup runs again (it offers what this one asked).");
        }

        commandLine.Prepare(ct);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // 1. SQL Server
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>The server the answers chose, how to sign in to it, and whether it answered.</summary>
    private sealed record SqlChoice(string Server, string? User, string? Password, ServerFacts? Facts)
    {
        public bool Answered => Facts is { Answered: true };

        public SqlTarget Target(string database) => SqlTarget.TryParse(SqlTarget.Compose(Server, database, User, Password))!;
    }

    private async Task<SqlChoice> SqlServerAsync(Dictionary<string, string> current, CancellationToken ct)
    {
        _ask.Heading("1. SQL Server: where the catalog and the data database live");
        var (instances, docker) = await LocalSqlServers.FindAsync(machine.Tools, machine.IsWindows, ct).ConfigureAwait(false);
        if (instances.Count > 0)
        {
            _ask.Note($"On this machine: {string.Join(", ", instances)}");
        }
        else
        {
            _ask.Note("No SQL Server is installed on this machine.");
        }

        var old = SqlTarget.TryParse(Current(current, "SQLFLOW_CATALOG_DB"));
        var server = old?.Server ?? instances.FirstOrDefault(i => i.Running)?.Server ?? "localhost";
        string? user = old is { Integrated: false, User.Length: > 0 } ? old.User : null;
        string? password = user is not null ? old!.Builder.Password : null;

        if (!instances.Any(i => i.Running) && (old is null || old.Place == SqlServerPlace.ThisMachine))
        {
            if (docker == DockerState.Running)
            {
                if (_ask.YesNo($"Start SQL Server 2022 in Docker now (container {LocalSqlServers.ContainerName}, port 1433, its databases kept in the volume {LocalSqlServers.ContainerName})?", true))
                {
                    (server, user, password) = await StartContainerAsync(instances, user == "sa" ? password : null, ct).ConfigureAwait(false);
                }
            }
            else
            {
                foreach (var line in _advice.GetSqlServer)
                {
                    _ask.Note(line);
                }

                if (docker == DockerState.NotRunning)
                {
                    _ask.Note("Docker is installed but not running: start Docker Desktop, then run the setup again to have it start SQL Server.");
                }
            }
        }

        if (!machine.IsWindows)
        {
            _ask.Note("Windows authentication needs Windows, so this machine signs in with a SQL login.");
        }

        while (true)
        {
            server = _ask.Answer("SQL Server name", server, CheckServer);
            var windows = machine.IsWindows && _ask.YesNo($"Sign in with your Windows account ({machine.Account})? n for a SQL login", user is null);
            if (windows)
            {
                user = null;
                password = null;
            }
            else
            {
                var newUser = _ask.Answer("SQL login", user ?? "sa");
                if (!string.Equals(newUser, user, StringComparison.Ordinal))
                {
                    password = null;
                }

                user = newUser;
                password = PasswordFor(user, password);
            }

            var probe = SqlTarget.TryParse(SqlTarget.Compose(server, "master", user, password))!;
            var facts = await machine.Sql.ProbeServerAsync(probe, ct).ConfigureAwait(false);
            if (facts.Answered)
            {
                _ask.Say($"Connected: {facts.Release} ({facts.Version}), {facts.Edition}, as {facts.Login}.", Tone.Good);
                if (facts.Major is > 0 and < 16)
                {
                    _ask.Say("OSDU Delivery is tested on SQL Server 2022; an older one may refuse what the engine sends.", Tone.Caution);
                }

                return new SqlChoice(server, user, password, facts);
            }

            _ask.Say($"{server} does not answer: {facts.Error}", Tone.Caution);
            foreach (var line in DevReadiness.ServerFix(_advice, instances, docker, probe, facts))
            {
                _ask.Say($"-> {line}");
            }

            var next = machine.Prompter.Ask($"  Enter to try again, another server name, or s to go on without it (fix it, then {_advice.Launcher} check)");
            if (next.Equals("s", StringComparison.OrdinalIgnoreCase))
            {
                return new SqlChoice(server, user, password, null);
            }

            if (next.Length > 0)
            {
                server = next;
            }
        }
    }

    private string PasswordFor(string user, string? password)
    {
        while (true)
        {
            var typed = _ask.Secret(password is not null ? $"Password for {user} (Enter keeps the current one)" : $"Password for {user}");
            if (typed.Length > 0)
            {
                return typed;
            }

            if (password is not null)
            {
                return password;
            }

            _ask.Say("A SQL login needs its password.", Tone.Caution);
        }
    }

    private static string? CheckServer(string server)
        => SqlTarget.TryParse(SqlTarget.Compose(server, "master", null, null)) is null ? "That is not a server name a connection string can carry." : null;

    /// <summary>
    /// Starts the setup's SQL Server container (or the stopped one it made before) and waits for it to accept the sa login.
    /// The password reaches docker through its environment, never its command line. Returns the server and sign-in to ask
    /// about next; the password is null when the container's databases were made with one this setup does not know.
    /// </summary>
    private async Task<(string Server, string? User, string? Password)> StartContainerAsync(IReadOnlyList<SqlInstance> instances, string? known, CancellationToken ct)
    {
        var existing = instances.FirstOrDefault(i => i.Kind == SqlInstanceKind.Container && i.Name == LocalSqlServers.ContainerName);
        string? password = known;
        if (existing is null)
        {
            password ??= DevValues.SqlServerPassword();
            _ask.Say("Starting SQL Server 2022 in Docker; the first start downloads its image (about 1.5 GB) ...");
            var run = await machine.Tools.RunAsync(
                "docker",
                ["run", "--detach", "--name", LocalSqlServers.ContainerName, "--platform", "linux/amd64", "--env", "ACCEPT_EULA=Y", "--env", "MSSQL_SA_PASSWORD",
                    "--publish", "1433:1433", "--volume", $"{LocalSqlServers.ContainerName}:/var/opt/mssql", LocalSqlServers.Image],
                new ToolRun(Environment: new Dictionary<string, string?>(StringComparer.Ordinal) { ["MSSQL_SA_PASSWORD"] = password }, Attached: true),
                ct).ConfigureAwait(false);
            if (!run.Succeeded)
            {
                _ask.Say($"docker run did not start it (exit code {run.ExitCode}); its output above says why. Is another server on port 1433?", Tone.Bad);
                return ("localhost", "sa", null);
            }
        }
        else
        {
            if (password is null)
            {
                _ask.Say($"The container {existing.Name} exists, and its databases were made with an sa password this setup does not know:", Tone.Caution);
                _ask.Say("give it below, or remove the container and its volume (its databases go with them), then run the setup again:");
                _ask.Say($"docker rm -f {existing.Name} && docker volume rm {existing.Name}");
            }

            if (!existing.Running)
            {
                var start = await machine.Tools.RunAsync("docker", ["start", existing.Name], new ToolRun(), ct).ConfigureAwait(false);
                if (!start.Succeeded)
                {
                    _ask.Say($"docker start {existing.Name} failed: {start.Error}", Tone.Bad);
                    return ("localhost", "sa", password);
                }
            }

            if (password is null)
            {
                return ("localhost", "sa", null);
            }
        }

        _ask.Say("Waiting for SQL Server to accept the sa login ...");
        var target = SqlTarget.TryParse(SqlTarget.Compose("localhost", "master", "sa", password))!;
        var deadline = machine.Now() + ContainerWait;
        while (true)
        {
            var facts = await machine.Sql.ProbeServerAsync(target, ct).ConfigureAwait(false);
            if (facts.Answered)
            {
                _ask.Say($"SQL Server is up in {LocalSqlServers.ContainerName}.", Tone.Good);
                return ("localhost", "sa", password);
            }

            if (facts.ErrorNumber == 18456)
            {
                _ask.Say($"It refused the sa password: the volume {LocalSqlServers.ContainerName} keeps databases made with another one. Give that one below, or remove both:", Tone.Caution);
                _ask.Say($"docker rm -f {LocalSqlServers.ContainerName} && docker volume rm {LocalSqlServers.ContainerName}");
                return ("localhost", "sa", null);
            }

            if (machine.Now() >= deadline)
            {
                _ask.Say($"It did not answer within {ContainerWait.TotalMinutes:0} minutes; docker logs {LocalSqlServers.ContainerName} says why.", Tone.Caution);
                return ("localhost", "sa", password);
            }

            await Task.Delay(ContainerPoll, ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // 2. Databases
    // ------------------------------------------------------------------------------------------------------------------

    private sealed record DatabaseChoice(string Catalog, string Data, bool CreateCatalog, bool CreateData, bool AllowSnapshot, CatalogFacts? Contents)
    {
        public bool CatalogReady => Contents is { HasCatalog: true };
    }

    private async Task<DatabaseChoice> DatabasesAsync(Dictionary<string, string> current, OrderedDictionary<string, string> values, SqlChoice sql, CancellationToken ct)
    {
        _ask.Heading("2. Databases");
        _ask.Note("The catalog holds SQLFlow's metadata and the delivery ledger; the data database holds the source and ingestion");
        _ask.Note("tables. Flows name them in three-part names (OsduData.silver.Wellbore), so keep OsduDelivery and OsduData unless");
        _ask.Note("your flows name others.");
        var oldCatalog = SqlTarget.TryParse(Current(current, "SQLFLOW_CATALOG_DB"));
        var oldData = SqlTarget.TryParse(Current(current, "OSDU_DATA_DB"));

        var catalog = oldCatalog is { Database.Length: > 0 } ? oldCatalog.Database : "OsduDelivery";
        var createCatalog = false;
        CatalogFacts? contents = null;
        while (true)
        {
            catalog = _ask.Answer("Catalog database", catalog, CheckDatabaseName);
            createCatalog = false;
            contents = null;
            if (!sql.Answered)
            {
                break;
            }

            var target = sql.Target(catalog);
            var facts = await machine.Sql.ProbeDatabaseAsync(target, catalog, ct).ConfigureAwait(false);
            if (facts.Error is { } unread)
            {
                _ask.Say($"{catalog} could not be read: {unread}", Tone.Caution);
                break;
            }

            if (!facts.Exists)
            {
                createCatalog = _ask.YesNo($"{catalog} does not exist on {sql.Server}. Create it, with the catalog and the osdu schema, once the file is written?", true);
                break;
            }

            if (!facts.Online || !facts.Access)
            {
                _ask.Say($"{catalog} is {facts.State.ToLowerInvariant()}{(facts.Access ? string.Empty : $", and {sql.Facts!.Login} may not open it")}; {_advice.Launcher} check says more.", Tone.Caution);
                break;
            }

            contents = await machine.Sql.ReadCatalogAsync(target, Current(current, "ControlPlane__Bootstrap__AdminUsername") ?? "admin", ct).ConfigureAwait(false);
            if (!contents.HasCatalog && contents.Problems.Count > 0)
            {
                // Read nothing is not the same as found nothing: an empty database is not offered the catalog on a guess.
                _ask.Say($"{catalog} could not be read: {contents.Problems[0]}", Tone.Caution);
                contents = null;
                break;
            }

            if (contents.HasCatalog)
            {
                _ask.Say($"{catalog} holds a catalog with {contents.Users?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "an unknown number of"} user(s); {_advice.Launcher} applies its pending migrations at start.", Tone.Good);
                break;
            }

            if (contents.Tables == 0)
            {
                createCatalog = _ask.YesNo($"{catalog} exists and is empty. Create the catalog in it once the file is written?", true);
                break;
            }

            _ask.Say($"{catalog} holds {contents.Tables} tables but no catalog: it is another application's database.", Tone.Caution);
            if (_ask.YesNo("Use it anyway (the catalog is then created beside its tables, with 'db migrate --create')?", false))
            {
                createCatalog = true;
                break;
            }

            catalog = "OsduDelivery";
        }

        var data = _ask.Answer("Data database", oldData is { Database.Length: > 0 } ? oldData.Database : "OsduData", CheckDatabaseName);
        values["SQLFLOW_CATALOG_DB"] = SqlTarget.Compose(sql.Server, catalog, sql.User, sql.Password);
        values["OSDU_DATA_DB"] = SqlTarget.Compose(sql.Server, data, sql.User, sql.Password);

        // A catalog reference that only repeats the catalog goes: it defaults to ${env:SQLFLOW_CATALOG_DB}.
        if (Current(current, "ControlPlane__Catalog__ConnectionReference") is { } reference
            && (reference == "${env:SQLFLOW_CATALOG_DB}" || reference == Current(current, "SQLFLOW_CATALOG_DB")))
        {
            values.Remove("ControlPlane__Catalog__ConnectionReference");
        }

        var createData = false;
        var allowSnapshot = false;
        if (sql.Answered)
        {
            var facts = await machine.Sql.ProbeDatabaseAsync(sql.Target(data), data, ct).ConfigureAwait(false);
            if (string.Equals(data, catalog, StringComparison.OrdinalIgnoreCase))
            {
                _ask.Note("One database for both works: the catalog keeps to its catalog and osdu schemas.");
                allowSnapshot = facts.Exists ? !facts.Snapshot && _ask.YesNo($"Allow snapshot isolation on {data} (a delivery reads under it)?", true) : createCatalog;
            }
            else if (facts.Error is { } unread)
            {
                _ask.Say($"{data} could not be read: {unread}", Tone.Caution);
            }
            else if (!facts.Exists)
            {
                createData = _ask.YesNo($"{data} does not exist on {sql.Server}. Create it, with snapshot isolation allowed (a delivery reads under it)?", true);
            }
            else if (!facts.Snapshot)
            {
                allowSnapshot = _ask.YesNo($"{data} does not allow snapshot isolation, which a delivery reads under. Allow it?", true);
            }
            else
            {
                _ask.Say($"{data} exists and allows snapshot isolation.", Tone.Good);
            }
        }

        return new DatabaseChoice(catalog, data, createCatalog, createData, allowSnapshot, contents);
    }

    private static string? CheckDatabaseName(string name)
        => DatabaseName().IsMatch(name) ? null : "A database name here is letters, digits, _ and -, starting with a letter or _, at most 128 characters.";

    // ------------------------------------------------------------------------------------------------------------------
    // 3. Signing in
    // ------------------------------------------------------------------------------------------------------------------

    private sealed record AdminChoice(string Name, bool Exists);

    private async Task<AdminChoice> SignInAsync(
        Dictionary<string, string> current, OrderedDictionary<string, string> values, SqlChoice sql, DatabaseChoice databases, CancellationToken ct)
    {
        _ask.Heading("3. Signing in to the GUI");
        var admin = _ask.Answer("First admin's user name", Current(current, "ControlPlane__Bootstrap__AdminUsername") ?? "admin",
            name => name.Length > 256 || name.Any(char.IsControl) ? "At most 256 characters, none a control character." : null);
        var exists = false;
        if (sql.Answered && databases.CatalogReady)
        {
            exists = (await machine.Sql.ReadCatalogAsync(sql.Target(databases.Catalog), admin, ct).ConfigureAwait(false)).AdminExists;
        }

        var existing = Current(current, "SQLFLOW_ADMIN_PASSWORD");
        string password;
        if (exists)
        {
            _ask.Note($"{admin} already exists in {databases.Catalog} and keeps its password: sign in with it. The first admin's");
            _ask.Note("password applies only when the user is created; change an existing one on the GUI's Users page.");
            password = existing is { Length: >= DevValues.MinPasswordLength } ? existing : DevValues.GenerateFor("SQLFLOW_ADMIN_PASSWORD");
        }
        else
        {
            password = AdminPassword(admin, existing);
        }

        values["ControlPlane__Bootstrap__AdminUsername"] = admin;
        values["ControlPlane__Bootstrap__AdminPasswordReference"] = DevValues.AdminPasswordReference;
        values["SQLFLOW_ADMIN_PASSWORD"] = password;
        var key = Current(current, "ControlPlane__Jwt__SigningKey");
        values["ControlPlane__Jwt__SigningKey"] = key is not null && DevValues.Problem("ControlPlane__Jwt__SigningKey", key, gui: true) is null
            ? key
            : DevValues.GenerateFor("ControlPlane__Jwt__SigningKey");
        values["ControlPlane__Cors__AllowedOrigins__0"] = DevValues.GuiOrigin;
        return new AdminChoice(admin, exists);
    }

    private string AdminPassword(string admin, string? existing)
    {
        var usable = existing is { Length: >= DevValues.MinPasswordLength } ? existing : null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var typed = _ask.Secret(usable is not null
                ? $"Password for {admin} (at least {DevValues.MinPasswordLength} characters; Enter keeps the current one)"
                : $"Password for {admin} (at least {DevValues.MinPasswordLength} characters; Enter generates one)");
            if (typed.Length == 0)
            {
                if (usable is not null)
                {
                    return usable;
                }

                _ask.Note($"Generated one: it is SQLFLOW_ADMIN_PASSWORD in {_advice.EnvFile}.");
                return DevValues.GenerateFor("SQLFLOW_ADMIN_PASSWORD");
            }

            if (typed.Length < DevValues.MinPasswordLength)
            {
                _ask.Say($"At least {DevValues.MinPasswordLength} characters.", Tone.Caution);
                continue;
            }

            if (!string.Equals(_ask.Secret("The same password again"), typed, StringComparison.Ordinal))
            {
                _ask.Say("The two do not match.", Tone.Caution);
                continue;
            }

            return typed;
        }

        _ask.Note($"Generated one instead: it is SQLFLOW_ADMIN_PASSWORD in {_advice.EnvFile}.");
        return DevValues.GenerateFor("SQLFLOW_ADMIN_PASSWORD");
    }

    // ------------------------------------------------------------------------------------------------------------------
    // 4. OSDU
    // ------------------------------------------------------------------------------------------------------------------

    private static readonly string[] OsduNames =
        ["OSDU_URL", "OSDU_DATA_PARTITION", "OSDU_TENANT_ID", "OSDU_TOKEN_URL", "OSDU_SCOPE", "OSDU_CLIENT_ID", "OSDU_CLIENT_SECRET", "OSDU_ACL_OWNER", "OSDU_ACL_VIEWER", "OSDU_LEGAL_TAG"];

    /// <summary>Asks for the OSDU platform and its client. Returns the partition to register, or null.</summary>
    private async Task<string?> OsduAsync(Dictionary<string, string> current, OrderedDictionary<string, string> values, CancellationToken ct)
    {
        _ask.Heading("4. OSDU: the platform the flows deliver to");
        if (!_ask.YesNo($"Set up an OSDU platform now? n leaves it for later: {_advice.Launcher} starts without it, and runs that reach OSDU fail", true))
        {
            if (Current(current, "OSDU_URL") is not null)
            {
                _ask.Note($"The OSDU settings already in {_advice.EnvFile} are kept.");
            }

            return Current(current, "OSDU_DATA_PARTITION") is { } kept && DevValues.IsPartitionId(kept) ? kept : null;
        }

        var answers = new Dictionary<string, string>(current, StringComparer.Ordinal);
        while (true)
        {
            var url = _ask.Answer("Platform base URL (https://...)", Current(answers, "OSDU_URL"),
                v => DevValues.IsHttpsUrl(v) ? null : "An https:// URL, such as https://<instance>.energy.azure.com.").TrimEnd('/');
            var adme = new Uri(url).Host.EndsWith(".energy.azure.com", StringComparison.OrdinalIgnoreCase);
            if (adme)
            {
                _ask.Note("Azure Data Manager for Energy: the defaults below follow its conventions.");
            }

            var partition = _ask.Answer("Data partition id (data-partition-id)", Current(answers, "OSDU_DATA_PARTITION"),
                v => DevValues.IsPartitionId(v) ? null : "Letters, digits, _, - and . only.");
            var token = _ask.Answer("Entra tenant id, or the token URL", Current(answers, "OSDU_TOKEN_URL") ?? Current(answers, "OSDU_TENANT_ID"),
                v => Guid.TryParse(v, out _) || DevValues.IsHttpsUrl(v) ? null : "A tenant id (a GUID) or an https:// token URL.");
            string? tenant = null;
            if (Guid.TryParse(token, out var tenantId))
            {
                tenant = tenantId.ToString();
                token = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token";
                _ask.Note($"Token URL: {token}");
            }
            else if (EntraTokenUrl().Match(token) is { Success: true } entra)
            {
                tenant = entra.Groups["tenant"].Value;
            }

            var scope = _ask.Answer("Scope the token is asked for", Current(answers, "OSDU_SCOPE") ?? (adme ? "https://energy.azure.com/.default" : null));
            var client = _ask.Answer("Client (application) id", Current(answers, "OSDU_CLIENT_ID"));
            var secret = ClientSecret(Current(answers, "OSDU_CLIENT_SECRET"));
            var domain = adme ? $"{partition}.dataservices.energy" : null;
            var owners = _ask.Answer("Owners group of what the flows deliver", Current(answers, "OSDU_ACL_OWNER") ?? (domain is null ? null : $"data.default.owners@{domain}"),
                v => DevValues.IsGroupEmail(v) ? null : "A group e-mail, such as data.default.owners@dev.dataservices.energy.");
            var viewers = _ask.Answer("Viewers group", Current(answers, "OSDU_ACL_VIEWER") ?? (domain is null ? null : $"data.default.viewers@{domain}"),
                v => DevValues.IsGroupEmail(v) ? null : "A group e-mail, such as data.default.viewers@dev.dataservices.energy.");
            var legalTag = _ask.Answer("Legal tag the records carry (one the partition holds)", Current(answers, "OSDU_LEGAL_TAG"));

            answers["OSDU_URL"] = url;
            answers["OSDU_DATA_PARTITION"] = partition;
            answers["OSDU_TOKEN_URL"] = token;
            if (tenant is not null)
            {
                answers["OSDU_TENANT_ID"] = tenant;
            }

            answers["OSDU_SCOPE"] = scope;
            answers["OSDU_CLIENT_ID"] = client;
            answers["OSDU_CLIENT_SECRET"] = secret;
            answers["OSDU_ACL_OWNER"] = owners;
            answers["OSDU_ACL_VIEWER"] = viewers;
            answers["OSDU_LEGAL_TAG"] = legalTag;

            var retry = false;
            if (_ask.YesNo("Test these now: sign in with the client and read the legal tag from OSDU (writes nothing)?", true))
            {
                var result = await machine.Osdu.TestAsync(new Uri(token), client, secret, scope, new Uri(url), partition, legalTag, ct).ConfigureAwait(false);
                var findings = DevReadiness.OsduFindings(result, name => answers.TryGetValue(name, out var v) ? v : null, _advice);
                DevReport.WriteFindings(machine.Prompter, findings);
                retry = findings.Any(f => f.Status != FindingStatus.Ok) && _ask.YesNo("Enter the OSDU settings again?", true);
            }

            if (!retry)
            {
                foreach (var name in OsduNames.Where(answers.ContainsKey))
                {
                    values[name] = answers[name];
                }

                return partition;
            }
        }
    }

    private string ClientSecret(string? existing)
    {
        while (true)
        {
            var typed = _ask.Secret(existing is not null ? "Client secret (Enter keeps the current one)" : "Client secret");
            if (typed.Length > 0)
            {
                return typed;
            }

            if (existing is not null)
            {
                return existing;
            }

            _ask.Say("The client secret is needed to sign in.", Tone.Caution);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // 5. Flows
    // ------------------------------------------------------------------------------------------------------------------

    private void Flows(OrderedDictionary<string, string> values, CatalogFacts? contents)
    {
        _ask.Heading("5. Flows: the repository the control plane runs");
        var registered = contents?.Repositories ?? [];
        if (registered.Count > 0)
        {
            _ask.Note("Registered in the catalog: " + string.Join(", ", registered.Select(r => $"{r.Name} ({(r.LocalPath.Length > 0 ? r.LocalPath : r.RemoteUrl)})")));
        }

        foreach (var key in values.Keys.Where(k => DevFactsCollector.RepositorySetting().Match(k) is { Success: true } m && m.Groups["key"].Value is "LocalPath" or "RemoteUrl"))
        {
            _ask.Note($"Configured in {_advice.EnvFile}: {key}={values[key]}");
        }

        _ask.Note($"A folder on this machine is read live (its uncommitted edits too); a git URL is cloned and pulled. The sample estate is {Path.Combine(checkout.Root, "osdu", "samples")}.");
        var location = _ask.Answer("Folder or git URL to register", null, CheckLocation, optional: true);
        if (location.Length == 0)
        {
            return;
        }

        var isUrl = UrlScheme().IsMatch(location);
        var normal = isUrl ? location.TrimEnd('/') : Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
        var same = registered.FirstOrDefault(r =>
            (r.LocalPath.Length > 0 && string.Equals(Path.TrimEndingDirectorySeparator(r.LocalPath), normal, StringComparison.OrdinalIgnoreCase))
            || (r.RemoteUrl.Length > 0 && string.Equals(r.RemoteUrl.TrimEnd('/'), normal, StringComparison.OrdinalIgnoreCase)));
        if (same is not null)
        {
            _ask.Note($"The catalog registers it already, as {same.Name}; nothing to add.");
            return;
        }

        var leaf = isUrl ? new Uri(normal).Segments[^1].Trim('/') : Path.GetFileName(normal);
        if (leaf.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            leaf = leaf[..^4];
        }

        var suggested = NotNameCharacters().Replace(leaf, "_").Trim('_');
        var name = _ask.Answer("Name it", suggested.Length > 0 ? suggested : "flows",
            v => RepositoryName().IsMatch(v) ? null : "Letters, digits and _ only, since the name is part of a setting's name here.");
        var prefix = $"ControlPlane__Bootstrap__Repositories__{name}__";
        foreach (var key in values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            values.Remove(key);
        }

        if (!isUrl)
        {
            values[prefix + "LocalPath"] = normal;
            return;
        }

        values[prefix + "RemoteUrl"] = normal;
        values[prefix + "Branch"] = _ask.Answer("Branch", "main");

        // A GitHub repository is best read with a GitHub App: it belongs to the organization rather than a person, and the
        // hosts mint a short-lived, read-only token for the repository alone. A source naming no credential reads with it.
        if (new Uri(normal).Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
        {
            if (values.TryGetValue(DevValues.GitHubAppIdName, out var configured) && configured.Length > 0)
            {
                _ask.Note($"Read with the GitHub App {configured} this file names, where the App is installed on the repository.");
                return;
            }

            _ask.Note("A private GitHub repository is best read with a GitHub App: read-only (Contents), installed on the repository (sqlflow/docs/reference/guides/github-app.md).");
            var appId = _ask.Answer("GitHub App client ID (Enter to use an access token instead)", null, v => DevValues.Problem(DevValues.GitHubAppIdName, v, gui: false), optional: true);
            if (appId.Length > 0)
            {
                var pem = _ask.Answer("The App's private key: the full path of its .pem file", null, v => DevValues.Problem(DevValues.GitHubAppKeyPathName, v, gui: false));
                values[DevValues.GitHubAppIdName] = appId.Trim();
                values[DevValues.GitHubAppKeyPathName] = pem.Trim().Trim('"');
                values.Remove(DevValues.GitHubAppKeyName);
                return;
            }
        }

        var token = _ask.Secret("Access token, for a private repository (Enter for none)");
        if (token.Length > 0)
        {
            values["SQLFLOW_GIT_TOKEN"] = token;
            values[prefix + "CredentialReference"] = "${env:SQLFLOW_GIT_TOKEN}";
            var host = new Uri(normal).Host;
            var user = host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase) ? "x-access-token"
                : host.EndsWith("bitbucket.org", StringComparison.OrdinalIgnoreCase) ? "x-bitbucket-api-token-auth"
                : null;
            values[prefix + "CredentialUsername"] = _ask.Answer("User name the token signs in with", user);
        }
    }

    private static string? CheckLocation(string location)
    {
        if (UrlScheme().IsMatch(location))
        {
            return Uri.TryCreate(location, UriKind.Absolute, out _) ? null : "That is not a URL.";
        }

        try
        {
            return Directory.Exists(location) ? null : $"{location} is not a folder on this machine.";
        }
        catch (ArgumentException)
        {
            return $"{location} is not a folder on this machine.";
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // 6. Optional
    // ------------------------------------------------------------------------------------------------------------------

    private void Optional(Dictionary<string, string> current, OrderedDictionary<string, string> values)
    {
        _ask.Heading("6. Optional");
        var mode = (Current(current, "SQLFLOW_AZURE_AUTH") ?? string.Empty).ToLowerInvariant();
        if (mode is "" or "cli" or "azurecli" or "azlogin")
        {
            if (_ask.YesNo("Do flows or this file read Azure Key Vault secrets or Azure storage (through your az login)?", mode.Length > 0))
            {
                values["SQLFLOW_AZURE_AUTH"] = "cli";
            }
            else
            {
                values.Remove("SQLFLOW_AZURE_AUTH");
            }
        }
        else
        {
            _ask.Note($"SQLFLOW_AZURE_AUTH is {mode}, kept as it is.");
        }

        if (_ask.YesNo("Turn on ! questions in the assistant (read-only queries over AI contexts an admin approved)?", Current(current, "ControlPlane__AiContext__Enabled") == "true"))
        {
            values["ControlPlane__AiContext__Enabled"] = "true";
        }
        else
        {
            values.Remove("ControlPlane__AiContext__Enabled");
        }

        values.TryAdd("ControlPlane__Bootstrap__ApplyMigrations", "false");
        values.TryAdd("ControlPlane__Bootstrap__AllowCreate", "false");
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Writing
    // ------------------------------------------------------------------------------------------------------------------

    private async Task<bool> WriteAsync(
        OrderedDictionary<string, string> values, IReadOnlyList<string> parked, SqlChoice sql, DatabaseChoice databases, AdminChoice admin, string? partition, CancellationToken ct)
    {
        _ask.Heading("Writing");
        var registerPartition = partition is not null && !(databases.Contents?.Partitions.Any(p => string.Equals(p.Name, partition, StringComparison.OrdinalIgnoreCase)) ?? false);
        var plan = new List<string> { $"write {_advice.EnvFile}{(File.Exists(checkout.EnvFile) ? ", keeping the current one beside it" : string.Empty)}" };
        if (databases.CreateData)
        {
            plan.Add($"create {databases.Data} on {sql.Server}, with snapshot isolation allowed");
        }

        if (databases.AllowSnapshot && !databases.CreateData)
        {
            plan.Add($"allow snapshot isolation on {databases.Data}");
        }

        var cliReady = commandLine.Unavailable is null;
        if (databases.CreateCatalog)
        {
            plan.Add(cliReady
                ? $"create the catalog {databases.Catalog} ('db migrate --create')"
                : $"not yet: create the catalog {databases.Catalog}, which needs .NET ({_advice.Launcher} setup does it once .NET is installed)");
        }

        if (registerPartition && (databases.CatalogReady || databases.CreateCatalog))
        {
            if (!cliReady)
            {
                plan.Add($"not yet: register the partition {partition}, which needs .NET ({_advice.Launcher} setup does it once .NET is installed)");
            }
            else
            {
                if (!databases.CreateCatalog)
                {
                    plan.Add($"apply the catalog's pending migrations ('db migrate', as {_advice.Launcher} does at every start)");
                }

                plan.Add($"register the partition {partition} ('partition add')");
            }
        }

        foreach (var line in plan)
        {
            machine.Prompter.Say($"  - {line}");
        }

        if (!_ask.YesNo("Go ahead?", true))
        {
            return false;
        }

        var now = machine.Now();
        var backup = await DevEnvFile.WriteAsync(checkout.EnvFile, DevEnvFile.Render(template, [.. values], parked, now), now, ct).ConfigureAwait(false);
        if (backup is not null)
        {
            _ask.Note($"Kept the previous file as {checkout.Show(backup)}.");
        }

        _ask.Say($"Wrote {_advice.EnvFile}.", Tone.Good);

        var environment = values.ToDictionary(v => v.Key, v => (string?)v.Value, StringComparer.Ordinal);
        if (databases.CreateData)
        {
            await TryAsync($"Created {databases.Data}.", $"Could not create {databases.Data}", () => machine.Sql.CreateDatabaseAsync(sql.Target("master"), databases.Data, ct)).ConfigureAwait(false);
        }

        var catalogReady = databases.CatalogReady;
        if (databases.CreateCatalog && cliReady)
        {
            _ask.Say($"Creating the catalog {databases.Catalog} ...");
            catalogReady = await CliAsync(["db", "migrate", "--create"], environment, $"Created the catalog {databases.Catalog}.", ct).ConfigureAwait(false);
        }

        if (databases.AllowSnapshot && !databases.CreateData)
        {
            await TryAsync($"Allowed snapshot isolation on {databases.Data}.", $"Could not change {databases.Data}", () => machine.Sql.AllowSnapshotIsolationAsync(sql.Target("master"), databases.Data, ct)).ConfigureAwait(false);
        }

        if (registerPartition && catalogReady && cliReady)
        {
            var partitions = (await machine.Sql.ReadCatalogAsync(sql.Target(databases.Catalog), admin.Name, ct).ConfigureAwait(false)).Partitions;
            if (partitions.Any(p => string.Equals(p.Name, partition, StringComparison.OrdinalIgnoreCase)))
            {
                _ask.Note($"The catalog already registers the partition {partition}.");
            }
            else if (databases.CreateCatalog || await CliAsync(["db", "migrate"], environment, "Applied the catalog's pending migrations.", ct).ConfigureAwait(false))
            {
                await CliAsync(partitions.Count == 0 ? ["partition", "add", partition!, "--default"] : ["partition", "add", partition!], environment, $"Registered the partition {partition}.", ct).ConfigureAwait(false);
            }
        }

        machine.Prompter.Say(string.Empty);
        machine.Prompter.Say(admin.Exists
            ? $"  Sign in at {DevValues.GuiOrigin} as {admin.Name}, with its password."
            : $"  Sign in at {DevValues.GuiOrigin} as {admin.Name}; the password is SQLFLOW_ADMIN_PASSWORD in {_advice.EnvFile}.", Tone.Heading);
        return true;
    }

    /// <summary>Runs one of the command line's own verbs on this console, with the written settings in its environment.</summary>
    private async Task<bool> CliAsync(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment, string done, CancellationToken ct)
    {
        var result = await commandLine.RunAsync(arguments, environment, ct).ConfigureAwait(false);
        if (result.Succeeded)
        {
            _ask.Say(done, Tone.Good);
            return true;
        }

        _ask.Say($"'{string.Join(' ', arguments)}' failed (exit code {result.ExitCode}); its output above says why. {_advice.Launcher} check reports what is still missing.", Tone.Bad);
        return false;
    }

    private async Task TryAsync(string done, string failed, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            _ask.Say(done, Tone.Good);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ask.Say($"{failed}: {SecretHygiene.RedactedMessage(ex).Split('\n')[0].Trim()}", Tone.Bad);
        }
    }

    /// <summary>A current value worth offering: set, and not a template's placeholder.</summary>
    private static string? Current(IReadOnlyDictionary<string, string> current, string name)
        => current.TryGetValue(name, out var value) && value.Length > 0 && !DevValues.IsPlaceholder(value) ? value : null;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_\-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseName();

    [GeneratedRegex(@"^[A-Za-z0-9_]{1,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryName();

    [GeneratedRegex("[^A-Za-z0-9_]", RegexOptions.CultureInvariant)]
    private static partial Regex NotNameCharacters();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]+://", RegexOptions.CultureInvariant)]
    private static partial Regex UrlScheme();

    [GeneratedRegex(@"^https://login\.microsoftonline\.com/(?<tenant>[0-9a-fA-F\-]{36})/", RegexOptions.CultureInvariant)]
    private static partial Regex EntraTokenUrl();
}
