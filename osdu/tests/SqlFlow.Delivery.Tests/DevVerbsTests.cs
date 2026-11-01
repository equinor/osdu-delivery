using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Delivery.Cli;
using SqlFlow.Delivery.Setup;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The setup's command as <c>osdu-delivery-setup</c> and <c>sqlflow dev</c> run it (osdu/docs/reference/cli/dev.md): its
/// subcommands and exit codes, the settings <c>env</c> prints for the launchers, and the report <c>check</c> prints as text
/// or JSON. The tests give the command line's arguments, from which the verb is taken off as the command line takes it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DevVerbsTests : IDisposable
{
    private readonly DevCheckoutScope _scope = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _error = new();

    public void Dispose()
    {
        _scope.Dispose();
        _out.Dispose();
        _error.Dispose();
    }

    private Task<int> RunAsync(IPrompter prompter, params string[] args)
        => DevCommand.RunAsync(
            DeliveryDevVerbs.Arguments(args, "dev"), "sqlflow dev", _scope.Machine(prompter), _out, _error, Path.Combine(_scope.Root, "nested"), CancellationToken.None);

    [Fact]
    public void The_verb_reads_the_env_file_itself()
        => Assert.True(Assert.Single(new DeliveryCliModule().Verbs, v => v.Name == "dev").ReadsLocalEnvFile);

    [Fact]
    public async Task Env_prints_the_files_settings_as_the_hosts_read_them()
    {
        Directory.CreateDirectory(Path.Combine(_scope.Root, "nested"));
        _scope.WriteEnv("# a comment", "FIRST=one", "QUOTED=' spaced '", "FIRST=again", "EMPTY=", "CONNECTION=Server=x;Database=y");

        var exit = await RunAsync(new ScriptedPrompter(), "dev", "env");

        Assert.Equal(0, exit);
        Assert.Equal(["FIRST=one", "QUOTED= spaced ", "EMPTY=", "CONNECTION=Server=x;Database=y"], _out.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Env_prints_nothing_for_a_file_the_hosts_refuse_or_one_that_is_missing()
    {
        Directory.CreateDirectory(Path.Combine(_scope.Root, "nested"));
        var missing = await RunAsync(new ScriptedPrompter(), "dev", "env");
        Assert.Equal(2, missing);
        Assert.Contains("is missing; dev.bat setup writes it", _error.ToString(), StringComparison.Ordinal);

        _scope.WriteEnv("GOOD=1", "pasted-secret-without-a-name");
        var refused = await RunAsync(new ScriptedPrompter(), "dev", "env");

        Assert.Equal(2, refused);
        Assert.Empty(_out.ToString());
        Assert.Contains("(2): expected KEY=VALUE", _error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("pasted-secret", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_exits_2_when_a_start_would_fail_and_prints_one_json_document_when_asked()
    {
        Directory.CreateDirectory(Path.Combine(_scope.Root, "nested"));
        _scope.WithReadyServer();

        var exit = await RunAsync(new ScriptedPrompter(), "dev", "check", "--json");

        Assert.Equal(2, exit);
        var json = JsonNode.Parse(_out.ToString())!;
        Assert.False(json["ready"]!.GetValue<bool>());
        Assert.Contains(json["sections"]!.AsArray().SelectMany(s => s!["findings"]!.AsArray()), f => f!["label"]!.GetValue<string>() == ".sqlflow/env".Replace('/', Path.DirectorySeparatorChar));
    }

    [Fact]
    public async Task Check_with_prepare_fills_a_copied_templates_generated_values_first()
    {
        Directory.CreateDirectory(Path.Combine(_scope.Root, "nested"));
        _scope.WithReadyServer();
        Directory.CreateDirectory(Path.GetDirectoryName(_scope.Checkout.EnvFile)!);
        File.Copy(_scope.Checkout.TemplatePath, _scope.Checkout.EnvFile);
        var prompter = new ScriptedPrompter();

        var exit = await RunAsync(prompter, "dev", "check", "--prepare");

        Assert.Equal(0, exit);
        Assert.Contains("Generated ControlPlane__Jwt__SigningKey and SQLFLOW_ADMIN_PASSWORD", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(64, _scope.ReadEnv()["ControlPlane__Jwt__SigningKey"].Length);
        Assert.Contains("Ready to start.", prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_stopped_by_the_user_exits_1_and_says_nothing_was_written()
    {
        Directory.CreateDirectory(Path.Combine(_scope.Root, "nested"));
        _scope.WithReadyServer();
        var prompter = new ScriptedPrompter("", "");

        var exit = await RunAsync(prompter, "dev", "setup");

        Assert.Equal(1, exit);
        Assert.Contains("nothing was written", prompter.Transcript, StringComparison.Ordinal);
        Assert.False(File.Exists(_scope.Checkout.EnvFile));
    }

    [Theory]
    [InlineData(new[] { "dev" }, "name what to do")]
    [InlineData(new[] { "dev", "start" }, "'start' is none of setup, demo, check, tour and env")]
    [InlineData(new[] { "dev", "setup", "--json" }, "--json applies to 'check' alone")]
    [InlineData(new[] { "dev", "check", "--bogus" }, "'--bogus' is not an option of this command")]
    [InlineData(new[] { "dev", "check", "--env-file" }, "--env-file needs the path of the env file")]
    [InlineData(new[] { "dev", "check", "again" }, "'again' follows the subcommand 'check'")]
    public async Task A_command_it_cannot_run_is_a_usage_error(string[] args, string reason)
    {
        Directory.CreateDirectory(Path.Combine(_scope.Root, "nested"));

        Assert.Equal(1, await RunAsync(new ScriptedPrompter(), args));
        Assert.Contains($"ERROR  {reason}", _error.ToString(), StringComparison.Ordinal);
        Assert.Contains("sqlflow dev check [--env-file <path>]", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_prints_the_usage_and_verbose_is_taken_as_every_verb_takes_it()
    {
        Assert.Equal(0, await DevCommand.RunAsync(["--help"], "osdu-delivery-setup", _scope.Machine(new ScriptedPrompter()), _out, _error, _scope.Root, CancellationToken.None));
        Assert.Contains("osdu-delivery-setup setup [--env-file <path>]", _out.ToString(), StringComparison.Ordinal);

        Directory.CreateDirectory(Path.Combine(_scope.Root, "nested"));
        _scope.WriteEnv("A=1");
        Assert.Equal(0, await RunAsync(new ScriptedPrompter(), "-v", "dev", "env", "--verbose"));
    }

    [Fact]
    public void The_command_line_takes_its_verb_off_the_arguments_once()
        => Assert.Equal(["check", "--env-file", "dev"], DeliveryDevVerbs.Arguments(["dev", "check", "--env-file", "dev"], "dev"));

    [Fact]
    public void Outside_a_checkout_the_verb_says_where_to_run_it()
    {
        var outside = Directory.CreateTempSubdirectory("osdu-dev-outside-").FullName;
        try
        {
            var error = Assert.Throws<FlowValidationException>(() => DevCheckout.Find(outside, null));
            Assert.Contains("from an OSDU Delivery checkout", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(outside);
        }
    }
}

/// <summary>The SQL Server probe against the suites' server: what it reads, and creating the scratch database.</summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DevSqlServerProbeTests
{
    [Fact]
    public async Task The_test_server_answers_and_its_database_is_read_and_a_missing_one_is_not_created()
    {
        var target = SqlTarget.TryParse(OsduTestServer.ConnectionString)!;
        var probe = new SqlServerProbe();

        var server = await probe.ProbeServerAsync(target, CancellationToken.None);
        var database = await probe.ProbeDatabaseAsync(target, target.Database, CancellationToken.None);
        var absent = $"{target.Database}_absent_{Guid.NewGuid():N}";
        var missing = await probe.ProbeDatabaseAsync(target, absent, CancellationToken.None);
        var catalog = await probe.ReadCatalogAsync(target, "admin", CancellationToken.None);

        Assert.True(server.Answered, server.Error);
        Assert.True(server.Major >= 13);
        Assert.NotEmpty(server.Login);
        Assert.True(database is { Exists: true, Online: true, Access: true });
        Assert.False(missing.Exists);
        Assert.Null(missing.Error);
        Assert.Empty(catalog.Problems);
        Assert.True(catalog.Tables >= 0);
        await using var connection = new SqlConnection(target.For("master"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_ID(@name)";
        command.Parameters.AddWithValue("@name", absent);
        Assert.Equal(DBNull.Value, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_server_that_refuses_the_connection_is_said_not_to_answer()
    {
        var facts = await new SqlServerProbe().ProbeServerAsync(SqlTarget.TryParse("Server=tcp:127.0.0.1,1;Database=x;Integrated Security=True;TrustServerCertificate=True")!, CancellationToken.None);

        Assert.False(facts.Answered);
        Assert.NotEqual(18456, facts.ErrorNumber);
        Assert.False(string.IsNullOrWhiteSpace(facts.Error));
    }

    [Fact]
    public async Task The_setup_creates_a_database_with_snapshot_isolation_and_allows_it_in_one_without()
    {
        await using var scratch = await OsduScratchDatabase.CreateAsync();
        var target = SqlTarget.TryParse(scratch.ConnectionString)!;
        var probe = new SqlServerProbe();

        Assert.False((await probe.ProbeDatabaseAsync(target, scratch.Name, CancellationToken.None)).Snapshot);
        await probe.AllowSnapshotIsolationAsync(target, scratch.Name, CancellationToken.None);
        Assert.True((await probe.ProbeDatabaseAsync(target, scratch.Name, CancellationToken.None)).Snapshot);

        // Dropped and made again, under the scratch name the suites keep for this.
        SqlConnection.ClearAllPools();
        await using (var connection = new SqlConnection(target.For("master")))
        {
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = """
                DECLARE @drop nvarchar(600) = N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@name);
                EXEC (@drop);
                """;
            drop.Parameters.AddWithValue("@name", scratch.Name);
            await drop.ExecuteNonQueryAsync();
        }

        await probe.CreateDatabaseAsync(target, scratch.Name, CancellationToken.None);

        var created = await probe.ProbeDatabaseAsync(target, scratch.Name, CancellationToken.None);
        Assert.True(created is { Exists: true, Online: true, Snapshot: true });
    }
}
