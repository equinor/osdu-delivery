using System.Globalization;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Delivery.Setup;

/// <summary>
/// The setup with nothing to ask (<c>demo</c>, osdu/docs/reference/cli/dev.md): a local environment on a clean machine, whose
/// OSDU is the stand-in on this machine and whose flows are the two public sample estates. It reuses the setup's own steps
/// (the SQL Server, the databases, writing the file, the catalog and the partition) and takes the default of each. It never
/// reaches a real OSDU, and it refuses a machine that already holds an environment of another kind, so it cannot touch one.
/// </summary>
internal sealed partial class DevSetupWizard
{
    /// <summary>Where the stand-in listens: on this machine alone.</summary>
    internal const string StandInUrl = "http://127.0.0.1:5301";

    /// <summary>The setting that makes a file a demo's, and tells the launchers to start the stand-in.</summary>
    internal const string StandInSwitch = "OSDU_STANDIN_WRITES";

    /// <summary>The partition the sample estates name.</summary>
    internal const string DemoPartition = "dev";

    /// <summary>The database the SQLFlow estate's flows name.</summary>
    internal const string DemoDatabase = "SqlFlowDemo";

    internal const string OsduEstateUrl = "https://github.com/TahirRiaz/osdu-delivery-estate.git";

    internal const string SqlFlowEstateUrl = "https://github.com/TahirRiaz/sqlflow-estate.git";

    /// <summary>Where the launchers have the stand-in keep what was delivered, beside the env file.</summary>
    internal const string StandInStateFile = "osdu-standin.json";

    /// <summary>Where a demo's nodes keep their checkouts, beside the env file, so they and their run history are its own.</summary>
    internal const string CheckoutsFolder = "node-cache";

    /// <summary>
    /// A person on one machine clicking from page to page: each page polls, so the 120 a minute a deployment shares between its
    /// users answers 429 before a first look round is done.
    /// </summary>
    private const string DemoPermitsPerWindow = "6000";

    private const string DemoCatalog = "OsduDelivery";

    private const string DemoData = "OsduData";

    private const string DemoAdmin = "admin";

    /// <summary>
    /// Sets the environment up without a question. False when it stopped, having said why. With <paramref name="reset"/> it
    /// first removes what an earlier demo made, which only a file of the demo's own lets it do.
    /// </summary>
    public async Task<bool> RunDemoAsync(bool reset, CancellationToken ct)
    {
        machine.Prompter.Say(string.Empty);
        machine.Prompter.Say("OSDU Delivery: a local environment, with a stand-in for OSDU", Tone.Heading);
        _ask.Note("It uses the sample estates and synthetic data, and it reaches no real OSDU.");

        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        var ours = false;
        if (File.Exists(checkout.EnvFile))
        {
            foreach (var entry in LocalEnvFile.Read(checkout.EnvFile).Entries)
            {
                current.TryAdd(entry.Name, entry.Value);
            }

            ours = string.Equals(Current(current, StandInSwitch), "true", StringComparison.OrdinalIgnoreCase);
            if (!ours)
            {
                _ask.Say($"{_advice.EnvFile} already sets up an environment of another kind, so the demo leaves it alone.", Tone.Bad);
                _ask.Say($"-> Clone the repository into a folder of its own and run the demo there; {_advice.Launcher} starts the environment this file names.");
                return false;
            }
        }

        if (reset && !ours)
        {
            _ask.Say("There is no demo here to start over from: this checkout has not run one.", Tone.Bad);
            return false;
        }

        Dotnet(ct);
        if (await DemoServerAsync(current, ct).ConfigureAwait(false) is not { } sql)
        {
            return false;
        }

        if (reset)
        {
            foreach (var database in new[] { DemoCatalog, DemoData, DemoDatabase })
            {
                await machine.Sql.DropDatabaseAsync(sql.Target("master"), database, ct).ConfigureAwait(false);
            }

            var held = Path.Combine(DemoFolder, StandInStateFile);
            if (File.Exists(held))
            {
                File.Delete(held);
            }

            // Run history sits in the checkouts, and a flow takes its watermark from it: kept, it would skip a load whose
            // table is gone with the database.
            RemoveFolder(Path.Combine(DemoFolder, CheckoutsFolder));
            _ask.Say("Removed what the last demo made.", Tone.Good);
        }

        if (await DemoDatabasesAsync(sql, ours, ct).ConfigureAwait(false) is not { } databases)
        {
            return false;
        }

        // What a demo's own file adds to these stays: a branch of an estate, a setting of the person's.
        var values = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in current.Concat(DemoValues(current, sql)))
        {
            values[name] = value;
        }

        var exists = databases.CatalogReady
            && (await machine.Sql.ReadCatalogAsync(sql.Target(databases.Catalog), DemoAdmin, ct).ConfigureAwait(false)).AdminExists;
        if (!await WriteAsync(values, [], sql, databases, new AdminChoice(DemoAdmin, exists), DemoPartition, ct, confirm: false).ConfigureAwait(false))
        {
            return false;
        }

        var demo = await machine.Sql.ProbeDatabaseAsync(sql.Target(DemoDatabase), DemoDatabase, ct).ConfigureAwait(false);
        if (!demo.Exists)
        {
            await TryAsync($"Created {DemoDatabase}.", $"Could not create {DemoDatabase}", () => machine.Sql.CreateDatabaseAsync(sql.Target("master"), DemoDatabase, ct)).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>The folder a demo keeps what it makes in: the env file's.</summary>
    private string DemoFolder => Path.GetDirectoryName(checkout.EnvFile)!;

    /// <summary>Removes a folder of checkouts, whose git objects are read-only.</summary>
    private static void RemoveFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(folder, recursive: true);
    }

    /// <summary>What the file holds: the sign-in and the stand-in's coordinates, each secret kept across a second run.</summary>
    private IEnumerable<KeyValuePair<string, string>> DemoValues(Dictionary<string, string> current, SqlChoice sql)
    {
        const string Owners = "data.default.owners@dev.dataservices.energy";
        const string Viewers = "data.default.viewers@dev.dataservices.energy";
        var connection = (string database) => SqlTarget.Compose(sql.Server, database, sql.User, sql.Password);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SQLFLOW_CATALOG_DB"] = connection(DemoCatalog),
            ["ControlPlane__Jwt__SigningKey"] = Current(current, "ControlPlane__Jwt__SigningKey") ?? DevValues.GenerateFor("ControlPlane__Jwt__SigningKey"),
            ["ControlPlane__Cors__AllowedOrigins__0"] = DevValues.GuiOrigin,
            ["ControlPlane__Bootstrap__AdminUsername"] = DemoAdmin,
            ["ControlPlane__Bootstrap__AdminPasswordReference"] = DevValues.AdminPasswordReference,
            ["SQLFLOW_ADMIN_PASSWORD"] = Current(current, "SQLFLOW_ADMIN_PASSWORD") ?? DevValues.GenerateFor("SQLFLOW_ADMIN_PASSWORD"),
            ["OSDU_DATA_DB"] = connection(DemoData),
            ["OSDU_URL"] = StandInUrl,
            ["OSDU_DATA_PARTITION"] = DemoPartition,
            ["OSDU_TOKEN_URL"] = StandInUrl + "/token",
            ["OSDU_SCOPE"] = "stand-in",
            ["OSDU_CLIENT_ID"] = "stand-in",
            ["OSDU_CLIENT_SECRET"] = "stand-in",
            ["OSDU_ACL_OWNER"] = Owners,
            ["OSDU_ACL_VIEWER"] = Viewers,
            ["OSDU_LOGTYPE_ACL_OWNER"] = Owners,
            ["OSDU_LOGTYPE_ACL_VIEWER"] = Viewers,
            ["OSDU_LEGAL_TAG"] = "dev-sample-data",
            ["SQLFLOW_DEMO_DB"] = connection(DemoDatabase),
            [StandInSwitch] = "true",
            ["SQLFLOW_NODE_CACHE"] = Path.Combine(DemoFolder, CheckoutsFolder),
            ["SQLFLOW_DELIVERY_ALLOW_LOOPBACK"] = "true",
            ["ControlPlane__RateLimit__PermitPerWindow"] = DemoPermitsPerWindow,
            ["ControlPlane__Bootstrap__Repositories__osdu_estate__RemoteUrl"] = Current(current, "ControlPlane__Bootstrap__Repositories__osdu_estate__RemoteUrl") ?? OsduEstateUrl,
            ["ControlPlane__Bootstrap__Repositories__sqlflow_estate__RemoteUrl"] = Current(current, "ControlPlane__Bootstrap__Repositories__sqlflow_estate__RemoteUrl") ?? SqlFlowEstateUrl,
        };
    }

    /// <summary>The server the file names, a SQL Server running here, or one started in Docker; null, having said what to do, when there is none.</summary>
    private async Task<SqlChoice?> DemoServerAsync(Dictionary<string, string> current, CancellationToken ct)
    {
        _ask.Heading("1. SQL Server");
        var (instances, docker) = await LocalSqlServers.FindAsync(machine.Tools, machine.IsWindows, ct).ConfigureAwait(false);
        var old = SqlTarget.TryParse(Current(current, "SQLFLOW_CATALOG_DB"));
        var server = old?.Server;
        var user = old is { Integrated: false, User.Length: > 0 } ? old.User : null;
        var password = user is not null ? old!.Builder.Password : null;
        if (server is null)
        {
            if (instances.FirstOrDefault(i => i.Running) is { } running)
            {
                server = running.Server;
                if (!machine.IsWindows)
                {
                    user = "sa";
                    password = PasswordFor(user, null);
                }
            }
            else if (docker == DockerState.Running)
            {
                (server, user, password) = await StartContainerAsync(instances, null, ct).ConfigureAwait(false);
                if (password is null)
                {
                    return null;
                }
            }
            else
            {
                _ask.Say("This machine has no SQL Server, and Docker's engine is not running.", Tone.Bad);
                foreach (var line in _advice.GetSqlServer)
                {
                    _ask.Say($"-> {line}");
                }

                return null;
            }
        }

        var probe = SqlTarget.TryParse(SqlTarget.Compose(server, "master", user, password))!;
        var facts = await machine.Sql.ProbeServerAsync(probe, ct).ConfigureAwait(false);
        if (!facts.Answered)
        {
            _ask.Say($"{server} does not answer: {facts.Error}", Tone.Bad);
            foreach (var line in DevReadiness.ServerFix(_advice, instances, docker, probe, facts))
            {
                _ask.Say($"-> {line}");
            }

            return null;
        }

        _ask.Say($"Connected: {facts.Release} ({facts.Version}), {facts.Edition}, as {facts.Login}.", Tone.Good);
        return new SqlChoice(server, user, password, facts);
    }

    /// <summary>
    /// The databases, made here or made by an earlier demo. A catalog this checkout did not make holds someone's environment
    /// and a database of another application holds someone's tables, so either stops the demo: flows name these databases by
    /// name, so it cannot step around them.
    /// </summary>
    private async Task<DatabaseChoice?> DemoDatabasesAsync(SqlChoice sql, bool ours, CancellationToken ct)
    {
        _ask.Heading("2. Databases");
        var facts = await machine.Sql.ProbeDatabaseAsync(sql.Target(DemoCatalog), DemoCatalog, ct).ConfigureAwait(false);
        if (facts.Error is { } unread)
        {
            _ask.Say($"{DemoCatalog} could not be read: {unread}", Tone.Bad);
            return null;
        }

        CatalogFacts? contents = null;
        if (facts.Exists)
        {
            contents = await machine.Sql.ReadCatalogAsync(sql.Target(DemoCatalog), DemoAdmin, ct).ConfigureAwait(false);
            if (!ours && (contents.HasCatalog || contents.Tables > 0))
            {
                _ask.Say($"{DemoCatalog} on {sql.Server} already holds {(contents.HasCatalog ? "a catalog" : string.Create(CultureInfo.InvariantCulture, $"{contents.Tables} tables"))}, which this checkout did not make, so the demo leaves it alone.", Tone.Bad);
                _ask.Say("-> Run the demo on a machine or a SQL Server of its own: the sample flows name their databases, so it cannot use others.");
                return null;
            }
        }

        var data = await machine.Sql.ProbeDatabaseAsync(sql.Target(DemoData), DemoData, ct).ConfigureAwait(false);
        return new DatabaseChoice(DemoCatalog, DemoData, !facts.Exists || contents is { HasCatalog: false }, !data.Exists, data.Exists && !data.Snapshot, contents);
    }
}
