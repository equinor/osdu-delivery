using SqlFlow.Delivery.Setup;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The setup with nothing to ask (osdu/docs/reference/cli/dev.md, demo): what a clean machine gets, that it is hermetic, and
/// that it leaves alone every environment it did not make.
/// </summary>
public sealed class DevDemoTests : IDisposable
{
    private readonly DevCheckoutScope _scope = new();

    public void Dispose() => _scope.Dispose();

    private async Task<(bool Done, ScriptedPrompter Prompter)> RunAsync(bool reset = false, DevPlatform platform = DevPlatform.Windows, params string[] answers)
    {
        var prompter = new ScriptedPrompter(answers);
        var wizard = new DevSetupWizard(_scope.Checkout, _scope.Template, _scope.Machine(prompter, platform), _scope.CommandLine) { ContainerPoll = TimeSpan.Zero };
        return (await wizard.RunDemoAsync(reset, CancellationToken.None), prompter);
    }

    /// <summary>A SQL Server on this machine that holds nothing yet.</summary>
    private void CleanServer()
    {
        _scope.WithReadyServer();
        _scope.Sql.Databases.Clear();
        _scope.Sql.Catalogs.Clear();
    }

    private void DemoFile(params string[] more)
        => _scope.WriteEnv(
        [
            "OSDU_STANDIN_WRITES=true",
            "SQLFLOW_CATALOG_DB=Server=localhost;Database=OsduDelivery;Integrated Security=True;TrustServerCertificate=True",
            "SQLFLOW_ADMIN_PASSWORD=the-password-the-demo-made",
            "ControlPlane__Jwt__SigningKey=the-key-the-demo-made-0123456789012345678901234567890123456789",
            .. more,
        ]);

    [Fact]
    public async Task A_clean_machine_is_set_up_without_a_single_question()
    {
        CleanServer();

        var (done, prompter) = await RunAsync();

        Assert.True(done);
        Assert.Empty(prompter.Asked);
        Assert.Equal(["OsduData", "SqlFlowDemo"], _scope.Sql.Created);
        Assert.Contains(_scope.CommandLine.Runs, r => r.Arguments.SequenceEqual(["db", "migrate", "--create"]));
        Assert.Contains(_scope.CommandLine.Runs, r => r.Arguments.SequenceEqual(["partition", "add", "dev", "--default"]));
    }

    [Fact]
    public async Task The_file_names_only_the_stand_in_and_the_two_public_estates()
    {
        CleanServer();

        await RunAsync();

        var env = _scope.ReadEnv();
        Assert.Equal("http://127.0.0.1:5301", env["OSDU_URL"]);
        Assert.Equal("http://127.0.0.1:5301/token", env["OSDU_TOKEN_URL"]);
        Assert.Equal("dev", env["OSDU_DATA_PARTITION"]);
        Assert.Equal("true", env["OSDU_STANDIN_WRITES"]);
        Assert.Equal("true", env["SQLFLOW_DELIVERY_ALLOW_LOOPBACK"]);
        Assert.Equal("6000", env["ControlPlane__RateLimit__PermitPerWindow"]);
        Assert.Equal("https://github.com/TahirRiaz/osdu-delivery-estate.git", env["ControlPlane__Bootstrap__Repositories__osdu_estate__RemoteUrl"]);
        Assert.Equal("https://github.com/TahirRiaz/sqlflow-estate.git", env["ControlPlane__Bootstrap__Repositories__sqlflow_estate__RemoteUrl"]);
        Assert.Contains("Database=SqlFlowDemo", env["SQLFLOW_DEMO_DB"], StringComparison.Ordinal);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(_scope.Checkout.EnvFile)!, "node-cache"), env["SQLFLOW_NODE_CACHE"]);
        Assert.All(new[] { "OSDU_URL", "OSDU_TOKEN_URL" }, name => Assert.Null(DevValues.Problem(name, env[name], gui: true)));
    }

    [Fact]
    public async Task Every_value_the_flows_of_the_two_estates_reference_is_set()
    {
        CleanServer();

        await RunAsync();

        var env = _scope.ReadEnv();
        string[] referenced =
        [
            "OSDU_URL", "OSDU_TOKEN_URL", "OSDU_SCOPE", "OSDU_CLIENT_ID", "OSDU_CLIENT_SECRET", "OSDU_LEGAL_TAG", "OSDU_DATA_DB",
            "OSDU_LOGTYPE_ACL_OWNER", "OSDU_LOGTYPE_ACL_VIEWER", "SQLFLOW_CATALOG_DB", "SQLFLOW_DEMO_DB",
        ];
        Assert.All(referenced, name => Assert.False(string.IsNullOrEmpty(env.GetValueOrDefault(name)), $"{name} is not set"));
        Assert.DoesNotContain(env, e => DevValues.IsPlaceholder(e.Value));
    }

    [Fact]
    public async Task A_file_of_another_kind_is_left_exactly_as_it_is()
    {
        CleanServer();
        _scope.WriteEnv("OSDU_URL=https://real.energy.azure.com", "OSDU_DATA_PARTITION=production");
        var before = await File.ReadAllTextAsync(_scope.Checkout.EnvFile);

        var (done, prompter) = await RunAsync();

        Assert.False(done);
        Assert.Contains("leaves it alone", prompter.Transcript, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(_scope.Checkout.EnvFile));
        Assert.Empty(_scope.Sql.Created);
        Assert.Empty(_scope.CommandLine.Runs);
    }

    [Fact]
    public async Task A_catalog_this_checkout_did_not_make_is_left_alone()
    {
        _scope.WithReadyServer();

        var (done, prompter) = await RunAsync();

        Assert.False(done);
        Assert.Contains("already holds a catalog", prompter.Transcript, StringComparison.Ordinal);
        Assert.False(File.Exists(_scope.Checkout.EnvFile));
        Assert.Empty(_scope.Sql.Created);
    }

    [Fact]
    public async Task A_database_of_another_application_under_the_catalogs_name_is_left_alone()
    {
        CleanServer();
        _scope.Sql.Databases["OsduDelivery"] = FakeSqlProbe.Present();
        _scope.Sql.Catalogs["OsduDelivery"] = new CatalogFacts(false, 12, null, false, false, [], [], [], []);

        var (done, prompter) = await RunAsync();

        Assert.False(done);
        Assert.Contains("already holds 12 tables", prompter.Transcript, StringComparison.Ordinal);
        Assert.False(File.Exists(_scope.Checkout.EnvFile));
    }

    [Fact]
    public async Task An_empty_database_under_the_catalogs_name_takes_the_catalog()
    {
        CleanServer();
        _scope.Sql.Databases["OsduDelivery"] = FakeSqlProbe.Present();
        _scope.Sql.Catalogs["OsduDelivery"] = new CatalogFacts(false, 0, null, false, false, [], [], [], []);

        var (done, _) = await RunAsync();

        Assert.True(done);
        Assert.Contains(_scope.CommandLine.Runs, r => r.Arguments.SequenceEqual(["db", "migrate", "--create"]));
    }

    [Fact]
    public async Task A_second_run_keeps_what_the_first_made()
    {
        _scope.WithReadyServer();
        _scope.Sql.Databases["SqlFlowDemo"] = FakeSqlProbe.Present();
        DemoFile();

        var (done, prompter) = await RunAsync();

        Assert.True(done);
        Assert.Empty(prompter.Asked);
        Assert.Empty(_scope.Sql.Created);
        Assert.DoesNotContain(_scope.CommandLine.Runs, r => r.Arguments.Contains("--create"));
        var env = _scope.ReadEnv();
        Assert.Equal("the-password-the-demo-made", env["SQLFLOW_ADMIN_PASSWORD"]);
        Assert.Equal("the-key-the-demo-made-0123456789012345678901234567890123456789", env["ControlPlane__Jwt__SigningKey"]);
    }

    [Fact]
    public async Task A_fork_of_an_estate_that_a_demo_file_names_stays()
    {
        _scope.WithReadyServer();
        _scope.Sql.Databases["SqlFlowDemo"] = FakeSqlProbe.Present();
        DemoFile("ControlPlane__Bootstrap__Repositories__sqlflow_estate__RemoteUrl=https://github.com/someone/sqlflow-estate.git");

        await RunAsync();

        Assert.Equal("https://github.com/someone/sqlflow-estate.git", _scope.ReadEnv()["ControlPlane__Bootstrap__Repositories__sqlflow_estate__RemoteUrl"]);
    }

    [Fact]
    public async Task A_setting_a_demo_file_adds_stays()
    {
        _scope.WithReadyServer();
        _scope.Sql.Databases["SqlFlowDemo"] = FakeSqlProbe.Present();
        DemoFile("ControlPlane__Bootstrap__Repositories__osdu_estate__Branch=a-branch", "SOMETHING_OF_MINE=kept");

        await RunAsync();

        var env = _scope.ReadEnv();
        Assert.Equal("a-branch", env["ControlPlane__Bootstrap__Repositories__osdu_estate__Branch"]);
        Assert.Equal("kept", env["SOMETHING_OF_MINE"]);
    }

    [Fact]
    public async Task A_data_database_without_snapshot_isolation_is_given_it()
    {
        CleanServer();
        _scope.Sql.Databases["OsduData"] = FakeSqlProbe.Present(snapshot: false);

        var (done, _) = await RunAsync();

        Assert.True(done);
        Assert.Equal(["OsduData"], _scope.Sql.SnapshotAllowed);
    }

    [Fact]
    public async Task A_machine_with_neither_SQL_Server_nor_Docker_is_told_what_to_install_and_nothing_is_written()
    {
        CleanServer();
        _scope.Tools.Results.Remove("powershell");

        var (done, prompter) = await RunAsync();

        Assert.False(done);
        Assert.Contains("no SQL Server", prompter.Transcript, StringComparison.Ordinal);
        Assert.Contains("->", prompter.Transcript, StringComparison.Ordinal);
        Assert.False(File.Exists(_scope.Checkout.EnvFile));
    }

    [Fact]
    public async Task With_Docker_running_it_starts_SQL_Server_with_the_password_off_the_command_line()
    {
        CleanServer();
        _scope.Tools.Results.Remove("powershell");
        _scope.Tools.Results["docker ps"] = FakeToolRunner.Ok(string.Empty);
        _scope.Tools.Results["docker run"] = FakeToolRunner.Ok("container-id");

        var (done, prompter) = await RunAsync();

        Assert.True(done);
        Assert.Empty(prompter.Asked);
        var run = Assert.Single(_scope.Tools.Runs, r => r.Tool == "docker" && r.Arguments[0] == "run");
        var password = run.Run.Environment!["MSSQL_SA_PASSWORD"]!;
        Assert.DoesNotContain(run.Arguments, a => a.Contains(password, StringComparison.Ordinal));
        Assert.Contains("User ID=sa", _scope.ReadEnv()["SQLFLOW_CATALOG_DB"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_that_does_not_answer_stops_it_with_the_fix()
    {
        CleanServer();
        _scope.Sql.Servers.Clear();

        var (done, prompter) = await RunAsync();

        Assert.False(done);
        Assert.Contains("does not answer", prompter.Transcript, StringComparison.Ordinal);
        Assert.False(File.Exists(_scope.Checkout.EnvFile));
    }

    [Fact]
    public async Task A_catalog_it_cannot_create_says_so_rather_than_starting_on_nothing()
    {
        CleanServer();
        _scope.CommandLine.Result = FakeToolRunner.Failed(3);

        var (_, prompter) = await RunAsync();

        Assert.Contains("'db migrate --create' failed (exit code 3)", prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reset_removes_the_three_databases_and_what_the_stand_in_holds_and_starts_again()
    {
        _scope.WithReadyServer();
        _scope.Sql.Databases["SqlFlowDemo"] = FakeSqlProbe.Present();
        DemoFile();
        var folder = Path.GetDirectoryName(_scope.Checkout.EnvFile)!;
        var state = Path.Combine(folder, DevSetupWizard.StandInStateFile);
        await File.WriteAllTextAsync(state, "{}");

        // A run's history in the demo's checkouts, read-only as a git object is: a watermark in it would skip a load after the
        // reset, with the table it loaded gone.
        var checkouts = Path.Combine(folder, DevSetupWizard.CheckoutsFolder);
        var history = Path.Combine(checkouts, "repo", "commit", ".sqlflow", "runs", "run.json");
        Directory.CreateDirectory(Path.GetDirectoryName(history)!);
        await File.WriteAllTextAsync(history, "{}");
        File.SetAttributes(history, FileAttributes.ReadOnly);

        var (done, prompter) = await RunAsync(reset: true);

        Assert.True(done);
        Assert.Equal(["OsduDelivery", "OsduData", "SqlFlowDemo"], _scope.Sql.Dropped);
        Assert.False(File.Exists(state));
        Assert.False(Directory.Exists(checkouts));
        Assert.Contains("OsduData", _scope.Sql.Created);
        Assert.Contains("SqlFlowDemo", _scope.Sql.Created);
        Assert.Contains(_scope.CommandLine.Runs, r => r.Arguments.SequenceEqual(["db", "migrate", "--create"]));
        Assert.Empty(prompter.Asked);
    }

    [Fact]
    public async Task A_reset_with_no_demo_here_removes_nothing()
    {
        _scope.WithReadyServer();

        var (done, prompter) = await RunAsync(reset: true);

        Assert.False(done);
        Assert.Empty(_scope.Sql.Dropped);
        Assert.Contains("no demo here", prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reset_over_a_file_of_another_kind_removes_nothing()
    {
        _scope.WithReadyServer();
        _scope.WriteEnv("OSDU_URL=https://real.energy.azure.com");

        var (done, _) = await RunAsync(reset: true);

        Assert.False(done);
        Assert.Empty(_scope.Sql.Dropped);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5301")]
    [InlineData("http://localhost:5301/token")]
    public void A_url_on_this_machine_may_be_http(string url)
        => Assert.Null(DevValues.Problem("OSDU_URL", url, gui: true));

    [Theory]
    [InlineData("http://osdu.example.com")]
    [InlineData("http://10.0.0.5:5301")]
    public void A_url_anywhere_else_may_not(string url)
        => Assert.Equal("not an https:// URL", DevValues.Problem("OSDU_URL", url, gui: true));
}
