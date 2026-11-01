namespace SqlFlow.Delivery.Setup;

/// <summary>What a tool printed and how it ended.</summary>
/// <param name="Found">False when the tool is not on the PATH: nothing ran.</param>
/// <param name="ExitCode">Its exit code; -1 when it did not run or was stopped.</param>
/// <param name="Output">Its standard output, trimmed (empty when it ran attached to the console).</param>
/// <param name="Error">Its standard error, trimmed (empty when it ran attached to the console).</param>
/// <param name="TimedOut">True when it was stopped for running too long.</param>
internal sealed record ToolResult(bool Found, int ExitCode, string Output, string Error, bool TimedOut)
{
    /// <summary>The tool is not installed.</summary>
    public static ToolResult NotFound { get; } = new(false, -1, string.Empty, string.Empty, false);

    /// <summary>True when it ran and exited 0.</summary>
    public bool Succeeded => Found && !TimedOut && ExitCode == 0;
}

/// <summary>How a tool runs.</summary>
/// <param name="WorkingDirectory">The folder it runs in; the current one when null.</param>
/// <param name="Environment">Variables set for it on top of this process's; a null value removes one.</param>
/// <param name="Timeout">How long it may run before it is stopped; none for an attached tool the user answers.</param>
/// <param name="Attached">True to run it on this console (its output shown as it comes, its prompts answered by the user).</param>
internal sealed record ToolRun(
    string? WorkingDirectory = null, IReadOnlyDictionary<string, string?>? Environment = null, TimeSpan? Timeout = null, bool Attached = false);

/// <summary>Runs the command-line tools a local run uses: dotnet, node, az, docker, sc, sqllocaldb and the CLI itself.</summary>
internal interface IToolRunner
{
    /// <summary>The tool's full path on the PATH (with the system's executable extensions), or null when it is not installed.</summary>
    string? Find(string tool);

    /// <summary>Runs <paramref name="tool"/> (a name on the PATH, or a full path) with <paramref name="arguments"/>.</summary>
    Task<ToolResult> RunAsync(string tool, IReadOnlyList<string> arguments, ToolRun run, CancellationToken ct);
}

/// <summary>What a SQL Server answered to the setup's connection to its master database.</summary>
/// <param name="Answered">True when it accepted the connection.</param>
/// <param name="ErrorNumber">The SQL Server error number when it did not (18456 a refused login), 0 otherwise.</param>
/// <param name="Error">What went wrong, when it did not.</param>
/// <param name="Version">Its product version (16.0.4135.4).</param>
/// <param name="Major">The version's first part: 16 is SQL Server 2022.</param>
/// <param name="Edition">Its edition.</param>
/// <param name="FullText">True when full-text search is installed.</param>
/// <param name="CanCreate">True when the login may create a database.</param>
/// <param name="Login">The login it signed in as.</param>
internal sealed record ServerFacts(bool Answered, int ErrorNumber, string? Error, string Version, int Major, string Edition, bool FullText, bool CanCreate, string Login)
{
    /// <summary>A server that did not answer.</summary>
    public static ServerFacts Failed(int number, string error) => new(false, number, error, string.Empty, 0, string.Empty, false, false, string.Empty);

    /// <summary>The release's name: SQL Server 2022 for 16.</summary>
    public string Release => Major switch
    {
        13 => "SQL Server 2016",
        14 => "SQL Server 2017",
        15 => "SQL Server 2019",
        16 => "SQL Server 2022",
        17 => "SQL Server 2025",
        _ => $"SQL Server {Version}",
    };
}

/// <summary>What the server holds of one database.</summary>
/// <param name="Exists">True when the server has it.</param>
/// <param name="Online">True when it is online.</param>
/// <param name="State">Its state, as the server names it (ONLINE, OFFLINE, RESTORING).</param>
/// <param name="Access">True when the login may open it.</param>
/// <param name="Snapshot">True when it allows snapshot isolation.</param>
/// <param name="Error">What went wrong reading it, when something did.</param>
internal sealed record DatabaseFacts(bool Exists, bool Online, string State, bool Access, bool Snapshot, string? Error)
{
    /// <summary>A database that could not be read.</summary>
    public static DatabaseFacts Unread(string error) => new(false, false, string.Empty, false, false, error);
}

/// <summary>A partition the catalog registers.</summary>
internal sealed record PartitionFact(string Name, bool IsDefault);

/// <summary>A repository source the catalog registers.</summary>
internal sealed record RepositoryFact(string Name, string RemoteUrl, string LocalPath, string Branch, bool Enabled, string LastError);

/// <summary>What a catalog database holds that a start depends on.</summary>
/// <param name="HasCatalog">True when it holds SQLFlow's catalog.</param>
/// <param name="Tables">How many tables it holds, catalog or not.</param>
/// <param name="Users">How many users the catalog holds; null when it could not be read.</param>
/// <param name="AdminExists">True when the first admin's user name is one of them.</param>
/// <param name="HasModule">True when it holds the module's osdu schema.</param>
/// <param name="Partitions">The partitions the module registers.</param>
/// <param name="Repositories">The repository sources the catalog registers.</param>
/// <param name="ConfigNames">The names the central configuration sets, at any scope.</param>
/// <param name="Problems">Each read that failed, and why.</param>
internal sealed record CatalogFacts(
    bool HasCatalog, int Tables, int? Users, bool AdminExists, bool HasModule, IReadOnlyList<PartitionFact> Partitions,
    IReadOnlyList<RepositoryFact> Repositories, IReadOnlyList<string> ConfigNames, IReadOnlyList<string> Problems)
{
    /// <summary>A catalog database that could not be read at all.</summary>
    public static CatalogFacts Unread(string problem) => new(false, 0, null, false, false, [], [], [], [problem]);
}

/// <summary>Reads, and on the setup's request changes, the SQL Servers a local run uses.</summary>
internal interface ISqlServerProbe
{
    /// <summary>Connects to the server's master database and reads the server.</summary>
    Task<ServerFacts> ProbeServerAsync(SqlTarget target, CancellationToken ct);

    /// <summary>Reads what the server holds of <paramref name="database"/>, through its master database.</summary>
    Task<DatabaseFacts> ProbeDatabaseAsync(SqlTarget target, string database, CancellationToken ct);

    /// <summary>Reads the catalog database <paramref name="target"/> names.</summary>
    Task<CatalogFacts> ReadCatalogAsync(SqlTarget target, string? adminName, CancellationToken ct);

    /// <summary>Creates <paramref name="database"/>, allowing snapshot isolation in it.</summary>
    Task CreateDatabaseAsync(SqlTarget target, string database, CancellationToken ct);

    /// <summary>Allows snapshot isolation in <paramref name="database"/>.</summary>
    Task AllowSnapshotIsolationAsync(SqlTarget target, string database, CancellationToken ct);

    /// <summary>Removes a database, closing what holds it open; nothing when there is none.</summary>
    Task DropDatabaseAsync(SqlTarget target, string database, CancellationToken ct);
}

/// <summary>What an OSDU sign-in test found.</summary>
/// <param name="TokenHost">The token endpoint's host.</param>
/// <param name="TokenIssued">True when it issued a token.</param>
/// <param name="TokenStatus">Its HTTP status when it did not; 0 when it did not answer.</param>
/// <param name="TokenReason">Why it did not, as it said.</param>
/// <param name="LegalTagStatus">The legal service's HTTP status for the tag; null when the tag was not read.</param>
/// <param name="LegalTagReason">What the legal service said, when it did not answer 200.</param>
internal sealed record OsduSignInResult(
    string TokenHost, bool TokenIssued, int TokenStatus, string? TokenReason, int? LegalTagStatus, string? LegalTagReason);

/// <summary>Signs in with a flow's OSDU client and reads a legal tag, writing nothing.</summary>
internal interface IOsduSignIn
{
    /// <summary>Asks the token endpoint for a token, then reads <paramref name="legalTag"/> from the partition (when given).</summary>
    Task<OsduSignInResult> TestAsync(
        Uri tokenUrl, string clientId, string clientSecret, string scope, Uri? platform, string? partition, string? legalTag, CancellationToken ct);
}

/// <summary>The setup's console: questions asked and answered, and lines said.</summary>
internal interface IPrompter
{
    /// <summary>Asks <paramref name="prompt"/> and returns the answer, trimmed ("" for Enter).</summary>
    /// <exception cref="SetupStoppedException">The input ended before an answer came.</exception>
    string Ask(string prompt);

    /// <summary>Asks for a secret without showing what is typed, and returns it trimmed ("" for Enter).</summary>
    /// <exception cref="SetupStoppedException">The input ended before an answer came.</exception>
    string AskSecret(string prompt);

    /// <summary>Says a line, in a tone.</summary>
    void Say(string line, Tone tone = Tone.Plain);
}

/// <summary>How a line reads.</summary>
internal enum Tone
{
    Plain,
    Note,
    Good,
    Caution,
    Bad,
    Heading,
}

/// <summary>The setup stopped before it wrote anything: the input ended, or the user said no.</summary>
internal sealed class SetupStoppedException : Exception
{
    public SetupStoppedException()
    {
    }

    public SetupStoppedException(string message)
        : base(message)
    {
    }

    public SetupStoppedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Everything the setup and the check reach outside themselves, so a suite can stand in for each.</summary>
/// <param name="Tools">Runs command-line tools.</param>
/// <param name="Sql">Reads and changes SQL Servers.</param>
/// <param name="Osdu">Tests an OSDU sign-in.</param>
/// <param name="Prompter">Asks and says.</param>
/// <param name="Environment">This process's environment as the shell gave it (the env file not applied).</param>
/// <param name="Platform">The operating system: Windows has services, LocalDB and Windows authentication; the fixes name its installers.</param>
/// <param name="Account">The account this runs as (DOMAIN\user), for a login's fix.</param>
/// <param name="Now">The clock, for a backup's name and the file's header.</param>
/// <param name="CommandLineFor">The OSDU command line for a checkout, which runs what only it does (db migrate, partition add).</param>
/// <param name="FileExists">Whether a file is there, for finding a .NET SDK an installer put off the PATH.</param>
internal sealed record DevMachine(
    IToolRunner Tools, ISqlServerProbe Sql, IOsduSignIn Osdu, IPrompter Prompter, IReadOnlyDictionary<string, string> Environment,
    DevPlatform Platform, string Account, Func<DateTimeOffset> Now, Func<DevCheckout, DevMachine, ICommandLine> CommandLineFor, Func<string, bool> FileExists)
{
    /// <summary>True on Windows.</summary>
    public bool IsWindows => Platform == DevPlatform.Windows;

    /// <summary>The platform this process runs on.</summary>
    public static DevPlatform CurrentPlatform
        => OperatingSystem.IsWindows() ? DevPlatform.Windows : OperatingSystem.IsMacOS() ? DevPlatform.MacOS : DevPlatform.Linux;
}

/// <summary>The operating system a local run is on.</summary>
internal enum DevPlatform
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>How to run this command line again: the program, and the arguments before a verb's own.</summary>
internal sealed record SelfCommand(string Program, IReadOnlyList<string> Prefix)
{
    /// <summary>
    /// The command line this process is: its apphost, or <c>dotnet</c> with the entry assembly when it runs through dotnet.
    /// The entry assembly is the process's first argument, which names it however the process was started (an assembly
    /// inside a single-file app has no location of its own).
    /// </summary>
    public static SelfCommand Current()
    {
        var program = System.Environment.ProcessPath ?? throw new InvalidOperationException("This process has no path to run again.");
        var host = Path.GetFileNameWithoutExtension(program);
        if (string.Equals(host, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var arguments = System.Environment.GetCommandLineArgs();
            var entry = arguments.Length > 0 ? arguments[0] : null;
            if (string.IsNullOrEmpty(entry) || !File.Exists(entry))
            {
                throw new InvalidOperationException("This process runs through dotnet, and its entry assembly has no path to run again.");
            }

            return new SelfCommand(program, [entry]);
        }

        return new SelfCommand(program, []);
    }
}
