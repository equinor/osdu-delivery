using System.Text.Json.Nodes;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Hosting;
using SqlFlow.Delivery.Setup;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What <c>sqlflow dev check</c> says about a local run's facts (osdu/docs/reference/cli/dev.md, What the check reports):
/// a ready machine is ready; each thing a start needs that is missing or wrong is an error with its fix; each thing a run
/// needs is a warning; the fix names this platform's commands.
/// </summary>
public sealed class DevReadinessTests : IDisposable
{
    private static readonly string[] ReadyEnv =
    [
        "SQLFLOW_CATALOG_DB=Server=localhost;Database=OsduDelivery;Integrated Security=True;TrustServerCertificate=True",
        "ControlPlane__Jwt__SigningKey=" + new string('k', 64),
        "ControlPlane__Cors__AllowedOrigins__0=http://localhost:5173",
        "ControlPlane__Bootstrap__AdminUsername=admin",
        "ControlPlane__Bootstrap__AdminPasswordReference=${env:SQLFLOW_ADMIN_PASSWORD}",
        "SQLFLOW_ADMIN_PASSWORD=a-long-enough-password",
        "OSDU_DATA_DB=Server=localhost;Database=OsduData;Integrated Security=True;TrustServerCertificate=True",
        "OSDU_URL=https://osdu.example.com",
        "OSDU_DATA_PARTITION=dev",
        "OSDU_TOKEN_URL=https://login.example.com/token",
        "OSDU_SCOPE=api://osdu/.default",
        "OSDU_CLIENT_ID=client-1",
        "OSDU_CLIENT_SECRET=secret-value-1",
        "OSDU_ACL_OWNER=data.default.owners@dev.example.com",
        "OSDU_ACL_VIEWER=data.default.viewers@dev.example.com",
        "OSDU_LEGAL_TAG=dev-public",
        "ControlPlane__Bootstrap__ApplyMigrations=false",
        "ControlPlane__Bootstrap__AllowCreate=false",
    ];

    private readonly DevCheckoutScope _scope = new();

    public void Dispose() => _scope.Dispose();

    /// <summary>The facts of a machine, ready unless a test changes them.</summary>
    private sealed class Spec
    {
        public string[]? Env { get; set; } = ReadyEnv;

        public Dictionary<string, string> Terminal { get; } = new(StringComparer.Ordinal);

        public DevPlatform Platform { get; set; } = DevPlatform.Windows;

        public bool Gui { get; set; } = true;

        public ToolResult Dotnet { get; set; } = FakeToolRunner.Ok("9.0.305");

        public ToolResult Node { get; set; } = FakeToolRunner.Ok("v22.12.0");

        public bool Npm { get; set; } = true;

        public ToolResult? Azure { get; set; }

        public string? DotnetOffPath { get; set; }

        public List<SqlInstance> Instances { get; } = [new("localhost", SqlInstanceKind.Service, "MSSQLSERVER", "running", false)];

        public DockerState Docker { get; set; } = DockerState.NotInstalled;

        public ServerFacts? CatalogServer { get; set; } = FakeSqlProbe.Answering();

        public DatabaseFacts? CatalogDatabase { get; set; } = FakeSqlProbe.Present();

        public CatalogFacts? Contents { get; set; } = FakeSqlProbe.Catalog(1, true, new PartitionFact("dev", true));

        public ServerFacts? DataServer { get; set; } = FakeSqlProbe.Answering();

        public DatabaseFacts? DataDatabase { get; set; } = FakeSqlProbe.Present();

        public List<RepositoryScan>? Repositories { get; set; }

        public List<ReferenceUse>? References { get; set; } = [];

        public OsduSignInResult? Osdu { get; set; }

        public string? OsduNotTested { get; set; }
    }

    private DevReport Evaluate(Spec spec)
    {
        LocalEnvFileContent? file = null;
        if (spec.Env is not null)
        {
            _scope.WriteEnv(spec.Env);
            file = LocalEnvFile.Read(_scope.Checkout.EnvFile);
        }

        var template = _scope.Template;
        var settings = DevSettings.From(file, spec.Terminal, template.Settings.Where(s => !s.IsFamily).Select(s => s.Name));
        var catalogText = file is null ? template.Find("SQLFLOW_CATALOG_DB")!.Value : settings.Get("SQLFLOW_CATALOG_DB");
        var catalogTarget = catalogText is null || DevValues.IsPlaceholder(catalogText) ? null : SqlTarget.TryParse(catalogText);
        var dataTarget = file is null ? null : SqlTarget.TryParse(settings.Get("OSDU_DATA_DB"));
        var repositories = spec.Repositories ?? [new RepositoryScan("flows", _scope.Root, _scope.Root, true, true, string.Empty, false, true)];
        var facts = new DevFacts(
            new DevAdvice(spec.Platform, ".sqlflow/env", "DOMAIN\\dev"), template, file is not null, file, settings, spec.Terminal, spec.Gui,
            new ToolFacts(spec.Dotnet, spec.Node, spec.Npm, DevFactsCollector.AzureNeeded(settings), spec.Azure, spec.DotnetOffPath),
            spec.Instances, spec.Docker,
            catalogTarget is null ? null : new DatabaseSide("SQLFLOW_CATALOG_DB", catalogTarget, null, spec.CatalogServer, spec.CatalogServer is { Answered: true } ? spec.CatalogDatabase : null),
            spec.Contents,
            dataTarget is null ? null : new DatabaseSide("OSDU_DATA_DB", dataTarget, null, spec.DataServer, spec.DataServer is { Answered: true } ? spec.DataDatabase : null),
            repositories, file is null ? null : spec.References, 12, spec.Osdu, spec.OsduNotTested);
        return DevReadiness.Evaluate(facts);
    }

    private static Finding Single(DevReport report, string label)
        => Assert.Single(report.Sections.SelectMany(s => s.Findings), f => f.Label == label);

    private static IEnumerable<Finding> All(DevReport report) => report.Sections.SelectMany(s => s.Findings);

    [Fact]
    public void A_ready_machine_is_ready_with_nothing_to_fix()
    {
        var report = Evaluate(new Spec());

        Assert.True(report.Ready, string.Join('\n', All(report).Where(f => f.Status is FindingStatus.Error or FindingStatus.Warning).Select(f => $"{f.Label}: {f.Detail}")));
        Assert.Equal(0, report.Warnings);
        Assert.Equal("Ready to start.", report.Verdict);
        Assert.Equal(FindingStatus.Ok, Single(report, "OSDU_CLIENT_SECRET").Status);
        Assert.Equal("set, 14 characters", Single(report, "OSDU_CLIENT_SECRET").Detail);
        Assert.DoesNotContain(All(report), f => f.Detail.Contains("secret-value-1", StringComparison.Ordinal));
    }

    [Fact]
    public void A_whole_github_app_is_reported_ready_and_a_half_named_one_is_warned()
    {
        var pem = Path.Combine(_scope.Root, "app.private-key.pem");
        File.WriteAllText(pem, "-----BEGIN RSA PRIVATE KEY-----");

        var whole = Evaluate(new Spec { Env = [.. ReadyEnv, "SQLFLOW_GITHUB_APP_ID=Iv23liAbCdEf", $"SQLFLOW_GITHUB_APP_PRIVATE_KEY_PATH={pem}"] });
        var app = Single(whole, "GitHub App");
        Assert.Equal(FindingStatus.Ok, app.Status);
        Assert.Contains("Iv23liAbCdEf reads the private GitHub repositories", app.Detail, StringComparison.Ordinal);
        Assert.True(whole.Ready);

        var keyOnly = Single(Evaluate(new Spec { Env = [.. ReadyEnv, $"SQLFLOW_GITHUB_APP_PRIVATE_KEY_PATH={pem}"] }), "GitHub App");
        Assert.Equal(FindingStatus.Warning, keyOnly.Status);
        Assert.Contains("SQLFLOW_GITHUB_APP_ID is not", keyOnly.Detail, StringComparison.Ordinal);

        var idOnly = Single(Evaluate(new Spec { Env = [.. ReadyEnv, "SQLFLOW_GITHUB_APP_ID=Iv23liAbCdEf"] }), "GitHub App");
        Assert.Equal(FindingStatus.Warning, idOnly.Status);
        Assert.Contains("has no private key", idOnly.Detail, StringComparison.Ordinal);

        var both = Single(Evaluate(new Spec { Env = [.. ReadyEnv, "SQLFLOW_GITHUB_APP_ID=Iv23liAbCdEf", $"SQLFLOW_GITHUB_APP_PRIVATE_KEY_PATH={pem}", "SQLFLOW_GITHUB_APP_PRIVATE_KEY=LS0tLS1CRUdJTg=="] }), "GitHub App");
        Assert.Contains("set one", both.Detail, StringComparison.Ordinal);

        Assert.DoesNotContain(All(Evaluate(new Spec())), f => f.Label == "GitHub App");
    }

    [Fact]
    public void Without_a_file_the_check_names_it_and_still_says_whether_the_server_is_there()
    {
        var report = Evaluate(new Spec { Env = null });

        var missing = Single(report, ".sqlflow/env");
        Assert.Equal(FindingStatus.Error, missing.Status);
        Assert.Contains("dev.bat writes it on its next start", missing.Fixes[0], StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Ok, Single(report, "localhost").Status);
        Assert.DoesNotContain(report.Sections, s => s.Title.StartsWith("Settings needed", StringComparison.Ordinal));
    }

    [Fact]
    public void Lines_the_hosts_refuse_names_set_twice_and_values_cmd_cannot_carry_are_named()
    {
        var report = Evaluate(new Spec { Env = [.. ReadyEnv, "a pasted secret", "OSDU_URL=https://second.example.com", "EXTRA=quote \" & more"] });

        Assert.Contains(All(report), f => f.Status == FindingStatus.Error && f.Detail.StartsWith($"line {ReadyEnv.Length + 1}: expected KEY=VALUE", StringComparison.Ordinal));
        Assert.DoesNotContain(All(report), f => f.Detail.Contains("pasted", StringComparison.Ordinal));
        Assert.Contains(All(report), f => f.Label == "OSDU_URL" && f.Status == FindingStatus.Warning && f.Detail.Contains("set twice", StringComparison.Ordinal));
        Assert.Equal(FindingStatus.Ok, All(report).First(f => f.Label == "OSDU_URL" && !f.Detail.Contains("twice", StringComparison.Ordinal)).Status);
        Assert.Contains(All(report), f => f.Label == "EXTRA" && f.Status == FindingStatus.Error && f.Detail.Contains("double quote", StringComparison.Ordinal));
        Assert.False(report.Ready);
    }

    [Fact]
    public void A_terminal_value_that_differs_from_the_files_is_warned_and_one_the_file_lacks_is_used()
    {
        var spec = new Spec { Env = [.. ReadyEnv.Where(l => !l.StartsWith("OSDU_LEGAL_TAG", StringComparison.Ordinal))] };
        spec.Terminal["OSDU_URL"] = "https://other.example.com";
        spec.Terminal["OSDU_LEGAL_TAG"] = "from-terminal";

        var report = Evaluate(spec);

        Assert.Contains(All(report), f => f.Label == "OSDU_URL" && f.Status == FindingStatus.Warning && f.Detail.Contains("this terminal", StringComparison.Ordinal));
        Assert.Equal("from-terminal (from this terminal's environment)", Single(report, "OSDU_LEGAL_TAG").Detail);
    }

    [Fact]
    public void A_missing_start_setting_is_an_error_and_a_missing_run_setting_a_warning()
    {
        var report = Evaluate(new Spec
        {
            Env = [.. ReadyEnv.Where(l => !l.StartsWith("ControlPlane__Cors", StringComparison.Ordinal) && !l.StartsWith("OSDU_SCOPE", StringComparison.Ordinal))],
        });

        Assert.Equal(FindingStatus.Error, Single(report, "ControlPlane__Cors__AllowedOrigins__0").Status);
        Assert.Equal(FindingStatus.Warning, Single(report, "OSDU_SCOPE").Status);
        Assert.Contains("the template has http://localhost:5173", Single(report, "ControlPlane__Cors__AllowedOrigins__0").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_osdu_platform_not_set_up_at_all_is_one_warning()
    {
        var report = Evaluate(new Spec { Env = [.. ReadyEnv.Where(l => !l.StartsWith("OSDU_", StringComparison.Ordinal) || l.StartsWith("OSDU_DATA_DB", StringComparison.Ordinal))] });

        var platform = Single(report, "OSDU platform");
        Assert.Equal(FindingStatus.Warning, platform.Status);
        Assert.DoesNotContain(All(report), f => f.Label == "OSDU_URL");
        Assert.True(report.Ready);
    }

    [Fact]
    public void The_first_admin_is_not_needed_once_the_catalog_has_users()
    {
        var env = ReadyEnv.Where(l => !l.Contains("Admin", StringComparison.Ordinal) && !l.StartsWith("SQLFLOW_ADMIN", StringComparison.Ordinal)).ToArray();

        var withUsers = Evaluate(new Spec { Env = env });
        var empty = Evaluate(new Spec { Env = env, Contents = FakeSqlProbe.Catalog(0, false, new PartitionFact("dev", true)) });

        Assert.Equal(FindingStatus.Off, Single(withUsers, "ControlPlane__Bootstrap__AdminUsername").Status);
        Assert.True(withUsers.Ready);
        Assert.Equal(FindingStatus.Error, Single(empty, "ControlPlane__Bootstrap__AdminUsername").Status);
        Assert.Equal(FindingStatus.Error, Single(empty, "Sign-in").Status);
    }

    [Fact]
    public void Wrong_values_are_errors_with_their_fix()
    {
        var report = Evaluate(new Spec
        {
            Env = [.. ReadyEnv.Select(l => l.StartsWith("ControlPlane__Jwt", StringComparison.Ordinal) ? "ControlPlane__Jwt__SigningKey=short" : l.StartsWith("SQLFLOW_ADMIN_PASSWORD", StringComparison.Ordinal) ? "SQLFLOW_ADMIN_PASSWORD=short" : l)],
        });

        var key = Single(report, "ControlPlane__Jwt__SigningKey");
        Assert.Equal(FindingStatus.Error, key.Status);
        Assert.Contains("<generated>", key.Fixes[0], StringComparison.Ordinal);
        Assert.Contains(All(report), f => f.Label == "SQLFLOW_ADMIN_PASSWORD" && f.Status == FindingStatus.Error && f.Detail.Contains("12 characters", StringComparison.Ordinal));
    }

    [Fact]
    public void The_cors_origin_matters_only_when_the_gui_starts()
    {
        var env = ReadyEnv.Select(l => l.StartsWith("ControlPlane__Cors", StringComparison.Ordinal) ? "ControlPlane__Cors__AllowedOrigins__0=http://localhost:3000" : l).ToArray();

        Assert.Equal(FindingStatus.Error, Single(Evaluate(new Spec { Env = env }), "ControlPlane__Cors__AllowedOrigins__0").Status);
        Assert.Equal(FindingStatus.Ok, Single(Evaluate(new Spec { Env = env, Gui = false }), "ControlPlane__Cors__AllowedOrigins__0").Status);
    }

    [Fact]
    public void Windows_authentication_off_windows_is_named()
    {
        var report = Evaluate(new Spec { Platform = DevPlatform.MacOS });

        var catalog = Single(report, "SQLFLOW_CATALOG_DB");
        Assert.Equal(FindingStatus.Error, catalog.Status);
        Assert.Contains("./dev.sh setup", catalog.Fixes[0], StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Warning, Single(report, "OSDU_DATA_DB").Status);
    }

    [Fact]
    public void A_retired_module_connection_is_an_error_that_stops_the_launcher_and_says_whether_anything_moves()
    {
        // The osdu schema lives in the catalog's database, so a line naming that database again goes with nothing lost.
        var same = Evaluate(new Spec { Env = [.. ReadyEnv, "SQLFLOW_OSDU_DB=Server=LOCALHOST,1433;Database=osdudelivery;Integrated Security=True"] });

        var finding = Single(same, "SQLFLOW_OSDU_DB");
        Assert.Equal(FindingStatus.Error, finding.Status);
        Assert.Contains("every host refuses to start while it is set", finding.Detail, StringComparison.Ordinal);
        Assert.Equal("It names the catalog's database, so nothing is lost: remove it from .sqlflow/env.", Assert.Single(finding.Fixes));
        Assert.False(same.Ready);
        Assert.DoesNotContain(All(same), f => f.Label == "Also set");

        // One naming another database holds a ledger, which has to move before the line goes; one set in the terminal is
        // cleared there.
        var spec = new Spec { Env = [.. ReadyEnv, "Osdu__Database__Connection=${keyvault:vault/ledger}"] };
        spec.Terminal["SQLFLOW_OSDU_DB"] = "Server=localhost;Database=OsduLedger;Integrated Security=True";
        var other = Evaluate(spec);

        Assert.Contains("bring that database's osdu schema into the catalog's database first, then remove it from .sqlflow/env.", Assert.Single(Single(other, "Osdu__Database__Connection").Fixes), StringComparison.Ordinal);
        Assert.EndsWith("then clear it in this terminal (set SQLFLOW_OSDU_DB=) and in Windows' environment variables.", Assert.Single(Single(other, "SQLFLOW_OSDU_DB").Fixes), StringComparison.Ordinal);
        Assert.DoesNotContain(All(other), f => f.Detail.Contains("OsduLedger", StringComparison.Ordinal));
    }

    [Fact]
    public void The_retired_module_connections_are_the_ones_the_hosts_refuse()
        => Assert.Equal(
            OsduModuleDatabase.RetiredSettings.Select(name => name.Replace(":", "__", StringComparison.Ordinal)).Order(StringComparer.Ordinal),
            DevValues.RetiredSettings.Order(StringComparer.Ordinal));

    [Fact]
    public void A_misspelt_name_is_suggested_and_other_names_are_listed()
    {
        var report = Evaluate(new Spec { Env = [.. ReadyEnv, "ControlPlane__Cors__AllowedOrigin__0=http://localhost:5173", "MY_FLOW_VALUE=1", "Osdu__Partitions__dev__Description=x"] });

        Assert.Contains("did you mean ControlPlane__Cors__AllowedOrigins__0?", Single(report, "ControlPlane__Cors__AllowedOrigin__0").Detail, StringComparison.Ordinal);
        Assert.Equal("MY_FLOW_VALUE", Single(report, "Also set").Detail);
    }

    [Fact]
    public void No_sql_server_on_windows_names_the_installer_and_on_a_mac_docker()
    {
        var windows = new Spec { CatalogServer = ServerFacts.Failed(-1, "not found"), DataServer = ServerFacts.Failed(-1, "not found") };
        windows.Instances.Clear();
        var mac = new Spec
        {
            Platform = DevPlatform.MacOS, CatalogServer = ServerFacts.Failed(-1, "not found"), DataServer = ServerFacts.Failed(-1, "not found"),
            Env = [.. ReadyEnv.Select(l => l.Replace("Integrated Security=True", "User ID=sa;Password=Str0ng-pass", StringComparison.Ordinal))],
        };
        mac.Instances.Clear();

        var onWindows = Single(Evaluate(windows), "localhost");
        var onMac = Single(Evaluate(mac), "localhost");

        Assert.Equal(FindingStatus.Error, onWindows.Status);
        Assert.Contains("winget install Microsoft.SQLServer.2022.Developer", onWindows.Fixes[0], StringComparison.Ordinal);
        Assert.Contains("brew install --cask docker", onMac.Fixes[0], StringComparison.Ordinal);
        Assert.Contains("./dev.sh setup starts SQL Server 2022 in a container", string.Join(' ', onMac.Fixes), StringComparison.Ordinal);
        Assert.Contains(All(Evaluate(windows)), f => f.Label == "Data OsduData" && f.Status == FindingStatus.Info && f.Detail.Contains("(above)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_stopped_or_disabled_service_and_a_stopped_container_name_what_starts_them()
    {
        var stopped = new Spec { CatalogServer = ServerFacts.Failed(-1, "x"), DataServer = ServerFacts.Failed(-1, "x") };
        stopped.Instances.Clear();
        stopped.Instances.Add(new SqlInstance("localhost", SqlInstanceKind.Service, "MSSQLSERVER", "stopped", true));
        var container = new Spec { CatalogServer = ServerFacts.Failed(-1, "x"), DataServer = ServerFacts.Failed(-1, "x"), Docker = DockerState.Running };
        container.Instances.Clear();
        container.Instances.Add(new SqlInstance("localhost", SqlInstanceKind.Container, "osdu-delivery-sql", "exited", false));

        var service = Single(Evaluate(stopped), "localhost").Fixes;
        Assert.Contains(service, f => f.Contains("Set-Service 'MSSQLSERVER' -StartupType Automatic", StringComparison.Ordinal));
        Assert.Contains(service, f => f.Contains("Start-Service 'MSSQLSERVER'", StringComparison.Ordinal));
        Assert.Equal(["Start the container: docker start osdu-delivery-sql"], Single(Evaluate(container), "localhost").Fixes);
    }

    [Fact]
    public void A_refused_windows_login_names_the_account_and_a_certificate_error_the_trust_setting()
    {
        var login = Single(Evaluate(new Spec { CatalogServer = ServerFacts.Failed(18456, "Login failed"), DataServer = ServerFacts.Failed(18456, "Login failed") }), "localhost");
        var certificate = Single(Evaluate(new Spec { CatalogServer = ServerFacts.Failed(-2146893019, "The certificate chain was issued by an authority that is not trusted.") }), "localhost");

        Assert.Contains("CREATE LOGIN [DOMAIN\\dev] FROM WINDOWS", login.Fixes[1], StringComparison.Ordinal);
        Assert.Contains("TrustServerCertificate=True", certificate.Fixes[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Each_state_of_the_catalog_database_says_what_to_do()
    {
        var missing = Single(Evaluate(new Spec { CatalogDatabase = FakeSqlProbe.Absent, Contents = null }), "Catalog OsduDelivery");
        var empty = Single(Evaluate(new Spec { Contents = new CatalogFacts(false, 0, null, false, false, [], [], [], []) }), "Catalog OsduDelivery");
        var foreign = Single(Evaluate(new Spec { Contents = new CatalogFacts(false, 12, null, false, false, [], [], [], []) }), "Catalog OsduDelivery");
        var offline = Single(Evaluate(new Spec { CatalogDatabase = new DatabaseFacts(true, false, "OFFLINE", true, true, null), Contents = null }), "Catalog OsduDelivery");
        var closed = Single(Evaluate(new Spec { CatalogDatabase = new DatabaseFacts(true, true, "ONLINE", false, true, null), Contents = null }), "Catalog OsduDelivery");

        Assert.All(new[] { missing, empty, foreign, offline, closed }, f => Assert.Equal(FindingStatus.Error, f.Status));
        Assert.Contains("db migrate --create", missing.Fixes[0], StringComparison.Ordinal);
        Assert.Contains("db migrate --create", empty.Fixes[0], StringComparison.Ordinal);
        Assert.Contains("holds 12 tables but no catalog", foreign.Detail, StringComparison.Ordinal);
        Assert.Equal("is OFFLINE", offline.Detail);
        Assert.Contains("may not open it", closed.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_older_sql_server_is_warned_and_the_data_databases_states_are_warnings()
    {
        Assert.Equal(FindingStatus.Warning, Single(Evaluate(new Spec { CatalogServer = FakeSqlProbe.Answering(15) }), "localhost").Status);
        Assert.Contains("does not exist", Single(Evaluate(new Spec { DataDatabase = FakeSqlProbe.Absent }), "Data OsduData").Detail, StringComparison.Ordinal);
        var snapshot = Single(Evaluate(new Spec { DataDatabase = FakeSqlProbe.Present(snapshot: false) }), "Data OsduData");
        Assert.Equal(FindingStatus.Warning, snapshot.Status);
        Assert.Contains("ALLOW_SNAPSHOT_ISOLATION ON", snapshot.Fixes[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Partitions_are_ok_registered_or_configured_and_warned_when_none()
    {
        var none = Single(Evaluate(new Spec { Contents = FakeSqlProbe.Catalog(1, true) }), "Partitions");
        var configured = Single(Evaluate(new Spec { Contents = FakeSqlProbe.Catalog(1, true), Env = [.. ReadyEnv, "Osdu__Partitions__dev__Description=Development"] }), "Partitions");
        var other = Evaluate(new Spec { Contents = FakeSqlProbe.Catalog(1, true, new PartitionFact("test", true)) });

        Assert.Equal(FindingStatus.Warning, none.Status);
        Assert.Contains("partition add dev --default", none.Fixes[0], StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Ok, configured.Status);
        Assert.Contains(All(other), f => f.Label == "Partitions" && f.Status == FindingStatus.Warning && f.Detail.Contains("OSDU_DATA_PARTITION is dev", StringComparison.Ordinal));
    }

    [Fact]
    public void A_repository_whose_folder_is_gone_or_whose_sync_failed_is_warned_and_none_at_all_too()
    {
        var gone = Evaluate(new Spec { Repositories = [new RepositoryScan("recall", "C:\\elsewhere", "C:\\elsewhere", false, true, string.Empty, false, true)] });
        var failing = Evaluate(new Spec { Repositories = [new RepositoryScan("recall", _scope.Root, _scope.Root, true, true, "fatal: not a git repository\nmore", false, true)] });
        var none = Evaluate(new Spec { Repositories = [] });

        Assert.Contains("does not exist on this machine", Single(gone, "Repository recall").Detail, StringComparison.Ordinal);
        Assert.Contains("its last sync failed: fatal: not a git repository", Single(failing, "Repository recall").Detail, StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Warning, Single(none, "Flow repositories").Status);
    }

    [Fact]
    public void A_reference_the_flows_name_is_warned_unless_something_sets_it()
    {
        var spec = new Spec
        {
            References =
            [
                new ReferenceUse("OSDU_URL", ["flows:a.yaml"]),
                new ReferenceUse("FROM_TERMINAL", ["flows:a.yaml"]),
                new ReferenceUse("FROM_CONFIG", ["flows:b.yaml"]),
                new ReferenceUse("FROM_PROPERTIES", ["flows:b.yaml"]),
                new ReferenceUse("NOWHERE", ["flows:a.yaml", "flows:b.yaml"]),
            ],
            Contents = FakeSqlProbe.Catalog(1, true, new PartitionFact("dev", true)) with { ConfigNames = ["FROM_CONFIG"] },
            Env = [.. ReadyEnv, "Osdu__Partitions__dev__Properties__FROM_PROPERTIES=x"],
        };
        spec.Terminal["FROM_TERMINAL"] = "x";

        var report = Evaluate(spec);

        var nowhere = Single(report, "${env:NOWHERE}");
        Assert.Equal("named by flows:a.yaml and 1 more, set nowhere: a run that reads it fails", nowhere.Detail);
        Assert.DoesNotContain(All(report), f => f.Label is "${env:OSDU_URL}" or "${env:FROM_TERMINAL}" or "${env:FROM_CONFIG}" or "${env:FROM_PROPERTIES}");
    }

    [Fact]
    public void Tools_say_what_is_missing_for_this_start()
    {
        var noNode = Evaluate(new Spec { Node = ToolResult.NotFound });
        var api = Evaluate(new Spec { Node = ToolResult.NotFound, Gui = false });
        var oldNode = Evaluate(new Spec { Node = FakeToolRunner.Ok("v20.11.0") });
        var noDotnet = Evaluate(new Spec { Dotnet = ToolResult.NotFound, Platform = DevPlatform.MacOS, Env = [.. ReadyEnv.Select(l => l.Replace("Integrated Security=True", "User ID=sa;Password=Str0ng-pass", StringComparison.Ordinal))] });

        Assert.Equal(FindingStatus.Error, Single(noNode, "Node.js").Status);
        Assert.Equal(FindingStatus.Off, Single(api, "Node.js").Status);
        Assert.Equal(FindingStatus.Warning, Single(oldNode, "Node.js").Status);
        Assert.Contains("brew install --cask dotnet-sdk", Single(noDotnet, ".NET SDK").Fixes[0], StringComparison.Ordinal);
    }

    [Fact]
    public void An_sdk_this_terminals_path_does_not_have_yet_is_a_warning_to_open_a_new_terminal()
    {
        var dotnet = Single(Evaluate(new Spec { DotnetOffPath = @"C:\Program Files\dotnet\dotnet.exe" }), ".NET SDK");

        Assert.Equal(FindingStatus.Warning, dotnet.Status);
        Assert.Contains("which this terminal's PATH does not have yet", dotnet.Detail, StringComparison.Ordinal);
        Assert.Contains("Open a new terminal", dotnet.Fixes[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_azure_cli_is_needed_for_its_own_mode_or_a_vault_reference_under_the_default_chain()
    {
        var signedOut = Evaluate(new Spec { Env = [.. ReadyEnv, "SQLFLOW_AZURE_AUTH=cli"], Azure = FakeToolRunner.Failed(1, "Please run 'az login'") });
        var vault = Evaluate(new Spec { Env = [.. ReadyEnv.Select(l => l.StartsWith("OSDU_CLIENT_SECRET", StringComparison.Ordinal) ? "OSDU_CLIENT_SECRET=${keyvault:vault/secret}" : l)], Azure = FakeToolRunner.Ok("dev@example.com") });
        var principal = Evaluate(new Spec { Env = [.. ReadyEnv, "SQLFLOW_AZURE_AUTH=sp", "X=${keyvault:vault/x}"] });

        Assert.Equal("Sign in: az login", Single(signedOut, "Azure CLI").Fixes[0]);
        Assert.Equal("signed in as dev@example.com", Single(vault, "Azure CLI").Detail);
        Assert.Equal(FindingStatus.Off, Single(principal, "Azure CLI").Status);
    }

    [Fact]
    public void The_osdu_test_reports_the_sign_in_and_the_legal_tag()
    {
        var refused = Evaluate(new Spec { Osdu = new OsduSignInResult("login.example.com", false, 401, "AADSTS7000215: Invalid client secret provided.", null, null) });
        var missingTag = Evaluate(new Spec { Osdu = new OsduSignInResult("login.example.com", true, 200, null, 404, "LegalTag not found") });
        var fine = Evaluate(new Spec { Osdu = new OsduSignInResult("login.example.com", true, 200, null, 200, null) });
        var untested = Evaluate(new Spec { OsduNotTested = "not tested: OSDU_SCOPE not set" });

        Assert.Contains("HTTP 401: AADSTS7000215", Single(refused, "OSDU sign-in").Detail, StringComparison.Ordinal);
        Assert.Equal("dev holds no legal tag dev-public", Single(missingTag, "OSDU legal tag").Detail);
        Assert.Equal("dev holds dev-public, read from osdu.example.com", Single(fine, "OSDU legal tag").Detail);
        Assert.Equal(FindingStatus.Off, Single(untested, "OSDU sign-in").Status);
    }

    [Fact]
    public void The_report_as_json_carries_every_finding_and_the_verdict()
    {
        var report = Evaluate(new Spec { DataDatabase = FakeSqlProbe.Absent });

        var json = JsonNode.Parse(report.ToJson().ToJsonString())!;

        Assert.True(json["ready"]!.GetValue<bool>());
        Assert.Equal(1, json["warnings"]!.GetValue<int>());
        Assert.Contains(json["sections"]!.AsArray().SelectMany(s => s!["findings"]!.AsArray()), f => f!["status"]!.GetValue<string>() == "warning" && f["label"]!.GetValue<string>() == "Data OsduData");
    }
}
