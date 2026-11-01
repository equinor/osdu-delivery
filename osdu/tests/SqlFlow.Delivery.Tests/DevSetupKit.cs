using SqlFlow.Delivery.Setup;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Stand-ins for what <c>sqlflow dev</c> reaches outside itself, for the suites of the local setup: a prompter answering
/// from a script, a tool runner answering from a table, a SQL Server probe answering from facts, and an OSDU sign-in
/// answering from a result. Each records what it was asked, so a test can say what the verb did.
/// </summary>
internal sealed class ScriptedPrompter(params string[] answers) : IPrompter
{
    private readonly Queue<string> _answers = new(answers);

    /// <summary>Every question asked, secrets included, in order.</summary>
    public List<string> Asked { get; } = [];

    /// <summary>Every line said.</summary>
    public List<string> Said { get; } = [];

    /// <summary>The questions asked for secrets.</summary>
    public List<string> SecretsAsked { get; } = [];

    /// <summary>How many scripted answers are left.</summary>
    public int Left => _answers.Count;

    public string Ask(string prompt)
    {
        Asked.Add(prompt);
        return _answers.Count > 0 ? _answers.Dequeue().Trim() : throw new SetupStoppedException("The input ended before the setup was done; nothing was written.");
    }

    public string AskSecret(string prompt)
    {
        SecretsAsked.Add(prompt);
        return Ask(prompt);
    }

    public void Say(string line, Tone tone = Tone.Plain) => Said.Add(line);

    /// <summary>Everything said, one line each.</summary>
    public string Transcript => string.Join('\n', Said);
}

/// <summary>A tool runner answering from a table of tool name (and first argument) to result.</summary>
internal sealed class FakeToolRunner : IToolRunner
{
    /// <summary>Results by "tool" or "tool first-argument"; the longer key wins.</summary>
    public Dictionary<string, ToolResult> Results { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The tools on the PATH (Find answers for these, and for anything with a result).</summary>
    public HashSet<string> Installed { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every run: the tool, its arguments and how it ran.</summary>
    public List<(string Tool, IReadOnlyList<string> Arguments, ToolRun Run)> Runs { get; } = [];

    public static ToolResult Ok(string output = "") => new(true, 0, output, string.Empty, false);

    public static ToolResult Failed(int code = 1, string error = "") => new(true, code, string.Empty, error, false);

    public string? Find(string tool)
        => Path.IsPathRooted(tool) || Installed.Contains(tool) || Results.Keys.Any(k => k.Split(' ')[0].Equals(tool, StringComparison.OrdinalIgnoreCase)) ? tool : null;

    public Task<ToolResult> RunAsync(string tool, IReadOnlyList<string> arguments, ToolRun run, CancellationToken ct)
    {
        lock (Runs)
        {
            Runs.Add((tool, arguments, run));
        }

        if (arguments.Count > 0 && Results.TryGetValue($"{tool} {arguments[0]}", out var specific))
        {
            return Task.FromResult(specific);
        }

        if (Results.TryGetValue(tool, out var result))
        {
            return Task.FromResult(result);
        }

        return Task.FromResult(Find(tool) is null ? ToolResult.NotFound : Ok());
    }
}

/// <summary>A SQL Server probe answering from facts set per server and per database.</summary>
internal sealed class FakeSqlProbe : ISqlServerProbe
{
    /// <summary>What a server answers, by server name; one not listed does not answer.</summary>
    public Dictionary<string, ServerFacts> Servers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Answers served in turn before <see cref="Servers"/> (a server still starting, say).</summary>
    public Queue<ServerFacts> ServerSequence { get; } = new();

    /// <summary>Databases by name.</summary>
    public Dictionary<string, DatabaseFacts> Databases { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Catalog contents by database name.</summary>
    public Dictionary<string, CatalogFacts> Catalogs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Databases created, and databases allowed snapshot isolation.</summary>
    public List<string> Created { get; } = [];

    public List<string> SnapshotAllowed { get; } = [];

    /// <summary>The sign-ins probed, as the connection strings were composed.</summary>
    public List<SqlTarget> Probed { get; } = [];

    public static ServerFacts Answering(int major = 16) => new(true, 0, null, $"{major}.0.4135.4", major, "Developer Edition (64-bit)", true, true, "DOMAIN\\dev");

    public static DatabaseFacts Present(bool snapshot = true) => new(true, true, "ONLINE", true, snapshot, null);

    public static DatabaseFacts Absent => new(false, false, string.Empty, false, false, null);

    public static CatalogFacts Catalog(int users = 1, bool adminExists = true, params PartitionFact[] partitions)
        => new(true, 40, users, adminExists, true, partitions, [], [], []);

    public Task<ServerFacts> ProbeServerAsync(SqlTarget target, CancellationToken ct)
    {
        Probed.Add(target);
        if (ServerSequence.Count > 0)
        {
            return Task.FromResult(ServerSequence.Dequeue());
        }

        return Task.FromResult(Servers.TryGetValue(target.Server, out var facts) ? facts : ServerFacts.Failed(-1, $"{target.Server} was not found"));
    }

    public Task<DatabaseFacts> ProbeDatabaseAsync(SqlTarget target, string database, CancellationToken ct)
        => Task.FromResult(Databases.TryGetValue(database, out var facts) ? facts : Absent);

    public Task<CatalogFacts> ReadCatalogAsync(SqlTarget target, string? adminName, CancellationToken ct)
        => Task.FromResult(Catalogs.TryGetValue(target.Database, out var facts) ? facts : CatalogFacts.Unread("no such catalog"));

    public Task CreateDatabaseAsync(SqlTarget target, string database, CancellationToken ct)
    {
        Created.Add(database);
        Databases[database] = Present();
        return Task.CompletedTask;
    }

    public List<string> Dropped { get; } = [];

    public Task DropDatabaseAsync(SqlTarget target, string database, CancellationToken ct)
    {
        Dropped.Add(database);
        Databases.Remove(database);
        Catalogs.Remove(database);
        return Task.CompletedTask;
    }

    public Task AllowSnapshotIsolationAsync(SqlTarget target, string database, CancellationToken ct)
    {
        SnapshotAllowed.Add(database);
        return Task.CompletedTask;
    }
}

/// <summary>The command line, answering from a result, recording each verb it was asked to run.</summary>
internal sealed class FakeCommandLine : ICommandLine
{
    /// <summary>Why it cannot run (no .NET), or null when it can.</summary>
    public string? Missing { get; set; }

    /// <summary>What a run answers.</summary>
    public ToolResult Result { get; set; } = FakeToolRunner.Ok();

    public bool Prepared { get; private set; }

    public bool Finished { get; private set; }

    /// <summary>Every verb run, with the environment it was given.</summary>
    public List<(IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string?> Environment)> Runs { get; } = [];

    public string? Unavailable => Missing;

    public void Prepare(CancellationToken ct) => Prepared = Missing is null;

    public Task<ToolResult> RunAsync(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment, CancellationToken ct)
    {
        Runs.Add((arguments, environment));
        return Task.FromResult(Result);
    }

    public Task FinishAsync(IPrompter prompter, CancellationToken ct)
    {
        Finished = true;
        return Task.CompletedTask;
    }
}

/// <summary>An OSDU sign-in answering one result, recording what it was given.</summary>
internal sealed class FakeOsduSignIn(OsduSignInResult result) : IOsduSignIn
{
    public List<(Uri TokenUrl, string ClientId, string Scope, string? Partition, string? LegalTag)> Tests { get; } = [];

    public Task<OsduSignInResult> TestAsync(
        Uri tokenUrl, string clientId, string clientSecret, string scope, Uri? platform, string? partition, string? legalTag, CancellationToken ct)
    {
        Tests.Add((tokenUrl, clientId, scope, partition, legalTag));
        return Task.FromResult(result);
    }
}

/// <summary>A checkout in a temporary folder holding the repository's own template, and the machine a test runs on.</summary>
internal sealed class DevCheckoutScope : IDisposable
{
    public DevCheckoutScope()
    {
        Root = Directory.CreateTempSubdirectory("osdu-dev-setup-").FullName;
        File.Copy(RepositoryRoot.Combine(DevCheckout.TemplateName), Path.Combine(Root, DevCheckout.TemplateName));
        Checkout = new DevCheckout(Root, Path.Combine(Root, ".sqlflow", "env"));
    }

    public string Root { get; }

    public DevCheckout Checkout { get; }

    public FakeToolRunner Tools { get; } = new();

    public FakeSqlProbe Sql { get; } = new();

    public FakeOsduSignIn Osdu { get; set; } = new(new OsduSignInResult("login.example.com", true, 200, null, 200, null));

    public Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal);

    /// <summary>The command line every machine of the scope gets.</summary>
    public FakeCommandLine CommandLine { get; } = new();

    /// <summary>The files the machine says are there (where a .NET SDK is looked for off the PATH).</summary>
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset Now { get; set; } = new(2026, 10, 17, 9, 30, 0, TimeSpan.Zero);

    public DevEnvTemplate Template => DevEnvTemplate.Parse(File.ReadAllLines(Checkout.TemplatePath), Checkout.TemplatePath);

    /// <summary>The machine, answering through <paramref name="prompter"/>.</summary>
    public DevMachine Machine(IPrompter prompter, DevPlatform platform = DevPlatform.Windows)
        => new(Tools, Sql, Osdu, prompter, Environment, platform, "DOMAIN\\dev", () => Now, (_, _) => CommandLine, Files.Contains);

    /// <summary>Writes the env file.</summary>
    public void WriteEnv(params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Checkout.EnvFile)!);
        File.WriteAllLines(Checkout.EnvFile, lines);
    }

    /// <summary>The env file's settings, the first of each name.</summary>
    public Dictionary<string, string> ReadEnv()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Core.Secrets.LocalEnvFile.Read(Checkout.EnvFile).Entries)
        {
            values.TryAdd(entry.Name, entry.Value);
        }

        return values;
    }

    /// <summary>A machine with a SQL Server on localhost holding a ready catalog and data database.</summary>
    public DevCheckoutScope WithReadyServer(params PartitionFact[] partitions)
    {
        Tools.Results["dotnet"] = FakeToolRunner.Ok("9.0.305");
        Tools.Results["node"] = FakeToolRunner.Ok("v22.12.0");
        Tools.Installed.Add("npm");
        Tools.Results["powershell"] = FakeToolRunner.Ok("MSSQLSERVER|Running|Automatic");
        Sql.Servers["localhost"] = FakeSqlProbe.Answering();
        Sql.Databases["OsduDelivery"] = FakeSqlProbe.Present();
        Sql.Databases["OsduData"] = FakeSqlProbe.Present();
        Sql.Catalogs["OsduDelivery"] = FakeSqlProbe.Catalog(1, true, partitions.Length > 0 ? partitions : [new PartitionFact("dev", true)]);
        return this;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A file a test left open on Windows; the temporary folder is the system's to clear.
        }
    }
}
