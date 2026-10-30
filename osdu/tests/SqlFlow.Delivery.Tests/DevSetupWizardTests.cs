using SqlFlow.Delivery.Setup;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The setup's questions (osdu/docs/reference/cli/dev.md, The setup): what a first start writes, what a second one keeps,
/// that nothing changes before the user says yes, and that each action goes through the path that already does it.
/// </summary>
public sealed class DevSetupWizardTests : IDisposable
{
    private const string Tenant = "00000000-0000-0000-0000-000000000001";

    private readonly DevCheckoutScope _scope = new();

    public void Dispose() => _scope.Dispose();

    /// <summary>The OSDU answers of a first setup on Azure Data Manager for Energy, the defaults taken where there are any.</summary>
    private static readonly string[] OsduAnswers =
        ["", "https://example.energy.azure.com/", "dev", Tenant, "", "client-1", "secret-1", "", "", "dev-tag", "n"];

    private async Task<(bool Written, ScriptedPrompter Prompter)> RunAsync(DevPlatform platform, params string[] answers)
    {
        var prompter = new ScriptedPrompter(answers);
        var wizard = new DevSetupWizard(_scope.Checkout, _scope.Template, _scope.Machine(prompter, platform), _scope.CommandLine) { ContainerPoll = TimeSpan.Zero };
        var written = await wizard.RunAsync(CancellationToken.None);
        return (written, prompter);
    }

    [Fact]
    public async Task A_first_setup_writes_every_answer_generates_the_secrets_and_runs_nothing_it_does_not_need()
    {
        _scope.WithReadyServer();

        var (written, prompter) = await RunAsync(DevPlatform.Windows, ["", "", "", "", "", .. OsduAnswers, "", "", "", ""]);

        Assert.True(written);
        Assert.Equal(0, prompter.Left);
        var env = _scope.ReadEnv();
        Assert.Equal("Server=localhost;Database=OsduDelivery;Integrated Security=True;TrustServerCertificate=True", env["SQLFLOW_CATALOG_DB"]);
        Assert.Equal("Server=localhost;Database=OsduData;Integrated Security=True;TrustServerCertificate=True", env["OSDU_DATA_DB"]);
        Assert.Equal("https://example.energy.azure.com", env["OSDU_URL"]);
        Assert.Equal($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token", env["OSDU_TOKEN_URL"]);
        Assert.Equal(Tenant, env["OSDU_TENANT_ID"]);
        Assert.Equal("https://energy.azure.com/.default", env["OSDU_SCOPE"]);
        Assert.Equal("data.default.owners@dev.dataservices.energy", env["OSDU_ACL_OWNER"]);
        Assert.Equal("data.default.viewers@dev.dataservices.energy", env["OSDU_ACL_VIEWER"]);
        Assert.Equal("secret-1", env["OSDU_CLIENT_SECRET"]);
        Assert.Equal(64, env["ControlPlane__Jwt__SigningKey"].Length);
        Assert.True(env["SQLFLOW_ADMIN_PASSWORD"].Length >= DevValues.MinPasswordLength);
        Assert.Equal("${env:SQLFLOW_ADMIN_PASSWORD}", env["ControlPlane__Bootstrap__AdminPasswordReference"]);
        Assert.Equal("false", env["ControlPlane__Bootstrap__ApplyMigrations"]);
        Assert.False(env.ContainsKey("SQLFLOW_AZURE_AUTH"));

        // The admin exists, so no password was asked; the catalog registers dev, so the command line never ran.
        Assert.Single(prompter.SecretsAsked);
        Assert.Empty(_scope.CommandLine.Runs);
        Assert.Contains("Sign in at http://localhost:5173 as admin, with its password.", prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_catalog_is_created_and_the_partition_registered_through_the_command_line()
    {
        _scope.WithReadyServer();
        _scope.Sql.Databases.Remove("OsduDelivery");
        _scope.Sql.Catalogs.Clear();

        var (written, prompter) = await RunAsync(DevPlatform.Windows, ["", "", "", "", "", "", "", .. OsduAnswers, "", "", "", ""]);

        Assert.True(written);
        Assert.Equal(0, prompter.Left);
        var runs = _scope.CommandLine.Runs;
        Assert.Equal(2, runs.Count);
        Assert.Equal(["db", "migrate", "--create"], runs[0].Arguments);
        Assert.Equal(["partition", "add", "dev", "--default"], runs[1].Arguments);
        Assert.Equal(_scope.ReadEnv()["SQLFLOW_CATALOG_DB"], runs[0].Environment["SQLFLOW_CATALOG_DB"]);
        Assert.True(_scope.CommandLine.Prepared);
        Assert.Contains("Generated one", prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_data_database_is_created_with_snapshot_isolation_and_one_without_it_is_allowed_it()
    {
        _scope.WithReadyServer();
        _scope.Sql.Databases.Remove("OsduData");
        var (created, _) = await RunAsync(DevPlatform.Windows, ["", "", "", "", "", "", "n", "", "", "", ""]);

        Assert.True(created);
        Assert.Equal(["OsduData"], _scope.Sql.Created);

        _scope.Sql.Databases["OsduData"] = FakeSqlProbe.Present(snapshot: false);
        var (allowed, _) = await RunAsync(DevPlatform.Windows, ["", "", "", "", "", "", "n", "", "", "", ""]);

        Assert.True(allowed);
        Assert.Equal(["OsduData"], _scope.Sql.SnapshotAllowed);
    }

    [Fact]
    public async Task Saying_no_to_the_plan_writes_nothing_and_ending_the_input_stops_the_same_way()
    {
        _scope.WithReadyServer();

        var (written, _) = await RunAsync(DevPlatform.Windows, ["", "", "", "", "", "n", "", "", "", "n"]);
        Assert.False(written);
        Assert.False(File.Exists(_scope.Checkout.EnvFile));

        await Assert.ThrowsAsync<SetupStoppedException>(() => RunAsync(DevPlatform.Windows, "", ""));
        Assert.False(File.Exists(_scope.Checkout.EnvFile));
        Assert.Empty(_scope.Sql.Created);
    }

    [Fact]
    public async Task Running_it_again_keeps_every_value_and_line_it_does_not_ask_about_and_the_old_file()
    {
        _scope.WithReadyServer();
        var catalog = "Server=localhost;Database=OsduDelivery;Integrated Security=True;TrustServerCertificate=True";
        _scope.WriteEnv(
            $"SQLFLOW_CATALOG_DB={catalog}",
            $"ControlPlane__Catalog__ConnectionReference={catalog}",
            "ControlPlane__Jwt__SigningKey=" + new string('k', 64),
            "ControlPlane__Bootstrap__AdminUsername=admin",
            "ControlPlane__Bootstrap__AdminPasswordReference=${env:SQLFLOW_ADMIN_PASSWORD}",
            "SQLFLOW_ADMIN_PASSWORD=kept-admin-password",
            "OSDU_DATA_DB=Server=localhost;Database=OsduData;Integrated Security=True;TrustServerCertificate=True",
            "OSDU_URL=https://kept.energy.azure.com",
            "OSDU_DATA_PARTITION=dev",
            $"OSDU_TOKEN_URL=https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token",
            "OSDU_SCOPE=https://energy.azure.com/.default",
            "OSDU_CLIENT_ID=kept-client",
            "OSDU_CLIENT_SECRET=kept-secret",
            "OSDU_ACL_OWNER=data.kept.owners@dev.dataservices.energy",
            "OSDU_ACL_VIEWER=data.kept.viewers@dev.dataservices.energy",
            "OSDU_LEGAL_TAG=kept-tag",
            "SQLFLOW_AZURE_AUTH=cli",
            "ControlPlane__ManagedSync__Enabled=true",
            "OSDU_LOGTYPE_ACL_OWNER=data.logs.owners@dev.dataservices.energy",
            "# OSDU_DATA_PARTITION=test",
            "# OSDU_CLIENT_ID=test-client");

        var (written, prompter) = await RunAsync(DevPlatform.Windows, ["", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "n", "", "", "", ""]);

        Assert.True(written);
        Assert.Equal(0, prompter.Left);
        var env = _scope.ReadEnv();
        foreach (var (name, value) in new Dictionary<string, string>
        {
            ["SQLFLOW_CATALOG_DB"] = catalog,
            ["SQLFLOW_ADMIN_PASSWORD"] = "kept-admin-password",
            ["OSDU_URL"] = "https://kept.energy.azure.com",
            ["OSDU_CLIENT_SECRET"] = "kept-secret",
            ["OSDU_ACL_OWNER"] = "data.kept.owners@dev.dataservices.energy",
            ["OSDU_LEGAL_TAG"] = "kept-tag",
            ["SQLFLOW_AZURE_AUTH"] = "cli",
            ["ControlPlane__ManagedSync__Enabled"] = "true",
            ["OSDU_LOGTYPE_ACL_OWNER"] = "data.logs.owners@dev.dataservices.energy",
        })
        {
            Assert.Equal(value, env[name]);
        }

        Assert.Equal(new string('k', 64), env["ControlPlane__Jwt__SigningKey"]);
        Assert.False(env.ContainsKey("ControlPlane__Catalog__ConnectionReference"));
        var text = File.ReadAllText(_scope.Checkout.EnvFile);
        Assert.Contains("# OSDU_DATA_PARTITION=test", text, StringComparison.Ordinal);
        Assert.Contains("# OSDU_CLIENT_ID=test-client", text, StringComparison.Ordinal);
        var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(_scope.Checkout.EnvFile)!, "env.*.bak"));
        Assert.Contains("ControlPlane__Catalog__ConnectionReference", File.ReadAllText(backup), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_secret_dev_bat_could_not_set_is_asked_for_again()
    {
        _scope.WithReadyServer();

        var (written, prompter) = await RunAsync(DevPlatform.Windows,
            ["", "", "", "", "", "", "https://osdu.example.com", "dev", "https://login.example.com/token", "api://osdu/.default", "client-1", "bad\"&secret", "good-secret",
                "owners@x.example.com", "viewers@x.example.com", "tag", "n", "", "", "", ""]);

        Assert.True(written);
        Assert.Equal("good-secret", _scope.ReadEnv()["OSDU_CLIENT_SECRET"]);
        Assert.Contains("double quote", prompter.Transcript, StringComparison.Ordinal);
        Assert.False(_scope.ReadEnv().ContainsKey("OSDU_TENANT_ID"));
    }

    [Fact]
    public async Task A_failed_osdu_test_offers_the_questions_again_with_the_answers_as_defaults()
    {
        _scope.WithReadyServer();
        _scope.Osdu = new FakeOsduSignIn(new OsduSignInResult("login.example.com", false, 401, "AADSTS7000215: Invalid client secret provided.", null, null));

        var (written, prompter) = await RunAsync(DevPlatform.Windows,
            ["", "", "", "", "", "", "https://osdu.example.com", "dev", "https://login.example.com/token", "api://osdu/.default", "client-1", "wrong",
                "owners@x.example.com", "viewers@x.example.com", "tag", "y", "y",
                "", "", "", "", "", "right", "", "", "", "n", "", "", "", ""]);

        Assert.True(written);
        Assert.Equal(0, prompter.Left);
        Assert.Equal("right", _scope.ReadEnv()["OSDU_CLIENT_SECRET"]);
        Assert.Equal("client-1", _scope.ReadEnv()["OSDU_CLIENT_ID"]);
        Assert.Contains("AADSTS7000215", prompter.Transcript, StringComparison.Ordinal);
        Assert.Single(_scope.Osdu.Tests);
    }

    [Fact]
    public async Task A_folder_becomes_a_repository_unless_the_catalog_has_it_and_a_git_url_takes_its_token_by_reference()
    {
        _scope.WithReadyServer();
        var folder = Directory.CreateDirectory(Path.Combine(_scope.Root, "my-flows")).FullName;

        var (folderWritten, _) = await RunAsync(DevPlatform.Windows, ["", "", "", "", "", "n", $"\"{folder}\"", "", "", "", ""]);
        Assert.True(folderWritten);
        Assert.Equal(folder, _scope.ReadEnv()["ControlPlane__Bootstrap__Repositories__my_flows__LocalPath"]);

        File.Delete(_scope.Checkout.EnvFile);
        _scope.Sql.Catalogs["OsduDelivery"] = _scope.Sql.Catalogs["OsduDelivery"] with { Repositories = [new RepositoryFact("welldb", string.Empty, folder, "main", true, string.Empty)] };
        var (sameWritten, same) = await RunAsync(DevPlatform.Windows, ["", "", "", "", "", "n", folder, "", "", ""]);
        Assert.True(sameWritten);
        Assert.Contains("registers it already, as welldb", same.Transcript, StringComparison.Ordinal);
        Assert.DoesNotContain(_scope.ReadEnv().Keys, k => k.StartsWith("ControlPlane__Bootstrap__Repositories__", StringComparison.Ordinal));

        File.Delete(_scope.Checkout.EnvFile);
        var (gitWritten, _) = await RunAsync(DevPlatform.Windows, ["", "", "", "", "", "n", "https://github.com/org/flows-repo.git", "", "", "", "token-1", "", "", "", ""]);
        Assert.True(gitWritten);
        var env = _scope.ReadEnv();
        Assert.Equal("https://github.com/org/flows-repo.git", env["ControlPlane__Bootstrap__Repositories__flows_repo__RemoteUrl"]);
        Assert.Equal("main", env["ControlPlane__Bootstrap__Repositories__flows_repo__Branch"]);
        Assert.Equal("${env:SQLFLOW_GIT_TOKEN}", env["ControlPlane__Bootstrap__Repositories__flows_repo__CredentialReference"]);
        Assert.Equal("x-access-token", env["ControlPlane__Bootstrap__Repositories__flows_repo__CredentialUsername"]);
        Assert.Equal("token-1", env["SQLFLOW_GIT_TOKEN"]);
    }

    [Fact]
    public async Task A_github_repository_can_be_read_with_a_github_app_whose_key_stays_in_its_file()
    {
        _scope.WithReadyServer();
        var pem = Path.Combine(_scope.Root, "flows-reader.private-key.pem");
        File.WriteAllText(pem, "-----BEGIN RSA PRIVATE KEY-----");

        var (written, prompter) = await RunAsync(DevPlatform.Windows, ["", "", "", "", "", "n", "https://github.com/org/flows-repo.git", "", "", "Iv23liAbCdEf", pem, "", "", "", ""]);

        Assert.True(written);
        var env = _scope.ReadEnv();
        Assert.Equal("https://github.com/org/flows-repo.git", env["ControlPlane__Bootstrap__Repositories__flows_repo__RemoteUrl"]);
        Assert.Equal("Iv23liAbCdEf", env["SQLFLOW_GITHUB_APP_ID"]);
        Assert.Equal(pem, env["SQLFLOW_GITHUB_APP_PRIVATE_KEY_PATH"]);
        Assert.False(env.ContainsKey("ControlPlane__Bootstrap__Repositories__flows_repo__CredentialReference"));
        Assert.False(env.ContainsKey("SQLFLOW_GIT_TOKEN"));
        Assert.Contains("read-only (Contents)", prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_a_mac_with_no_sql_server_it_starts_one_in_docker_with_the_password_kept_off_the_command_line()
    {
        _scope.WithReadyServer();
        _scope.Tools.Results.Remove("powershell");
        _scope.Tools.Results["docker ps"] = FakeToolRunner.Ok(string.Empty);
        _scope.Tools.Results["docker run"] = FakeToolRunner.Ok("container-id");
        _scope.Sql.ServerSequence.Enqueue(ServerFacts.Failed(-1, "starting"));

        var (written, prompter) = await RunAsync(DevPlatform.MacOS, ["", "", "", "", "", "", "", "n", "", "", "", ""]);

        Assert.True(written);
        Assert.Equal(0, prompter.Left);
        var run = Assert.Single(_scope.Tools.Runs, r => r.Tool == "docker" && r.Arguments[0] == "run");
        var password = run.Run.Environment!["MSSQL_SA_PASSWORD"]!;
        Assert.DoesNotContain(run.Arguments, a => a.Contains(password, StringComparison.Ordinal));
        Assert.Contains("MSSQL_SA_PASSWORD", run.Arguments);
        Assert.Contains("osdu-delivery-sql:/var/opt/mssql", run.Arguments);
        var catalog = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_scope.ReadEnv()["SQLFLOW_CATALOG_DB"]);
        Assert.Equal("sa", catalog.UserID);
        Assert.Equal(password, catalog.Password);
        Assert.False(catalog.IntegratedSecurity);
        Assert.DoesNotContain(prompter.Asked, q => q.Contains("Windows account", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_container_whose_databases_were_made_with_another_password_asks_for_it()
    {
        _scope.WithReadyServer();
        _scope.Tools.Results.Remove("powershell");
        _scope.Tools.Results["docker ps"] = FakeToolRunner.Ok(string.Empty);
        _scope.Tools.Results["docker run"] = FakeToolRunner.Ok("container-id");
        _scope.Sql.ServerSequence.Enqueue(ServerFacts.Failed(18456, "Login failed for user 'sa'."));

        var (written, prompter) = await RunAsync(DevPlatform.MacOS, ["", "", "", "the-old-password", "", "", "", "n", "", "", "", ""]);

        Assert.True(written);
        Assert.Contains("docker volume rm osdu-delivery-sql", prompter.Transcript, StringComparison.Ordinal);
        Assert.Equal("the-old-password", new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_scope.ReadEnv()["SQLFLOW_CATALOG_DB"]).Password);
    }

    [Fact]
    public async Task Without_dotnet_the_setup_writes_what_it_can_and_leaves_the_command_lines_steps_for_later()
    {
        _scope.WithReadyServer();
        _scope.Sql.Databases.Remove("OsduDelivery");
        _scope.Sql.Catalogs.Clear();
        _scope.Tools.Installed.Add("winget");
        _scope.CommandLine.Missing = "the .NET SDK is not installed on this machine";

        var (written, prompter) = await RunAsync(DevPlatform.Windows, ["", "", "", "", "", "", "", "n", "", "", "", ""]);

        Assert.True(written);
        Assert.Equal(0, prompter.Left);
        Assert.Contains("The .NET SDK is not installed on this machine.", prompter.Transcript, StringComparison.Ordinal);
        Assert.Contains("winget install Microsoft.DotNet.SDK.9", prompter.Transcript, StringComparison.Ordinal);
        Assert.DoesNotContain(prompter.Asked, q => q.Contains(".NET", StringComparison.Ordinal));
        Assert.Contains("not yet: create the catalog OsduDelivery, which needs .NET", prompter.Transcript, StringComparison.Ordinal);
        Assert.Empty(_scope.CommandLine.Runs);
        Assert.False(_scope.CommandLine.Prepared);
        Assert.DoesNotContain(_scope.Tools.Runs, r => r.Tool == "winget");
        Assert.True(File.Exists(_scope.Checkout.EnvFile));
    }

    [Fact]
    public async Task A_catalog_the_login_cannot_read_is_not_taken_for_an_empty_one()
    {
        _scope.WithReadyServer();
        _scope.Sql.Catalogs["OsduDelivery"] = CatalogFacts.Unread("could not read OsduDelivery: The SELECT permission was denied");

        var (written, prompter) = await RunAsync(DevPlatform.Windows, ["", "", "", "", "", "", "n", "", "", "", ""]);

        Assert.True(written);
        Assert.Equal(0, prompter.Left);
        Assert.Contains("OsduDelivery could not be read: could not read OsduDelivery: The SELECT permission was denied", prompter.Transcript, StringComparison.Ordinal);
        Assert.DoesNotContain(prompter.Asked, q => q.Contains("Create the catalog in it", StringComparison.Ordinal));
        Assert.Empty(_scope.CommandLine.Runs);
    }

    [Fact]
    public async Task A_server_that_does_not_answer_can_be_left_for_later_and_nothing_is_created_on_it()
    {
        _scope.WithReadyServer();
        _scope.Sql.Servers.Clear();

        var (written, prompter) = await RunAsync(DevPlatform.Windows, ["", "", "s", "", "", "", "", "n", "", "", "", ""]);

        Assert.True(written);
        Assert.Contains("does not answer", prompter.Transcript, StringComparison.Ordinal);
        Assert.Empty(_scope.Sql.Created);
        Assert.Empty(_scope.CommandLine.Runs);
    }
}
