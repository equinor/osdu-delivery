using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Setup;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How the setup writes a local run's env file: the template's sections with the answers in place, what the replaced file
/// held kept, one move over the old file with a copy kept beside it, readable by its owner alone; and how the SQL Servers
/// on a machine are found.
/// </summary>
public sealed class DevEnvFileTests : IDisposable
{
    private readonly DevCheckoutScope _scope = new();

    public void Dispose() => _scope.Dispose();

    private static IReadOnlyList<KeyValuePair<string, string>> Values(params (string Name, string Value)[] values)
        => [.. values.Select(v => KeyValuePair.Create(v.Name, v.Value))];

    [Fact]
    public void Answers_take_the_template_lines_and_the_rest_keeps_the_template()
    {
        var lines = DevEnvFile.Render(
            _scope.Template,
            Values(("SQLFLOW_CATALOG_DB", "Server=db;Database=Cat;Integrated Security=True"), ("SQLFLOW_AZURE_AUTH", "cli"), ("OSDU_URL", "https://osdu.example.com")),
            [], _scope.Now);

        Assert.Contains("SQLFLOW_CATALOG_DB=Server=db;Database=Cat;Integrated Security=True", lines);
        Assert.Contains("SQLFLOW_AZURE_AUTH=cli", lines);
        Assert.DoesNotContain("# SQLFLOW_AZURE_AUTH=cli", lines);
        Assert.Contains("OSDU_URL=https://osdu.example.com", lines);
        Assert.Contains(lines, l => l.StartsWith("OSDU_LEGAL_TAG=<", StringComparison.Ordinal));
        Assert.Contains("# ===== Needed to start: dev.bat and ./dev.sh stop until each of these is set =====", lines);
        Assert.StartsWith("# OSDU Delivery: the settings of a local run on this machine.", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_values_are_made_for_a_template_line_no_answer_took()
    {
        var lines = DevEnvFile.Render(_scope.Template, [], [], _scope.Now);

        var key = Assert.Single(lines, l => l.StartsWith("ControlPlane__Jwt__SigningKey=", StringComparison.Ordinal));
        Assert.Equal(64, key["ControlPlane__Jwt__SigningKey=".Length..].Length);
        Assert.DoesNotContain(lines, l => l.Contains(DevValues.Generated, StringComparison.Ordinal) && !l.StartsWith('#'));
    }

    [Fact]
    public void A_repository_goes_before_the_commented_pattern_and_unknown_names_and_parked_lines_are_kept()
    {
        var lines = DevEnvFile.Render(
            _scope.Template,
            Values(("ControlPlane__Bootstrap__Repositories__flows__LocalPath", "C:\\flows"), ("OSDU_WELLS_ACL_OWNER", "data.logs.owners@dev.dataservices.energy")),
            ["# OSDU_DATA_PARTITION=test", "# SQLFLOW_AZURE_AUTH=cli"], _scope.Now).ToList();

        var member = lines.IndexOf("ControlPlane__Bootstrap__Repositories__flows__LocalPath=C:\\flows");
        var pattern = lines.FindIndex(l => l.StartsWith("# ControlPlane__Bootstrap__Repositories__<name>__LocalPath=", StringComparison.Ordinal));
        Assert.True(member >= 0 && member < pattern);
        var kept = lines.IndexOf(DevEnvFile.KeptTitle);
        Assert.True(kept > pattern);
        Assert.Contains("OSDU_WELLS_ACL_OWNER=data.logs.owners@dev.dataservices.energy", lines.Skip(kept));
        Assert.Contains("# OSDU_DATA_PARTITION=test", lines.Skip(kept));

        // A parked line the template writes itself is not repeated.
        Assert.Single(lines, l => l == "# SQLFLOW_AZURE_AUTH=cli");
    }

    [Fact]
    public void Writing_the_same_answers_again_gives_the_same_file()
    {
        var values = Values(("SQLFLOW_CATALOG_DB", "Server=db;Database=Cat;Integrated Security=True"), ("ControlPlane__Jwt__SigningKey", new string('k', 64)),
            ("SQLFLOW_ADMIN_PASSWORD", "a-long-password"), ("EXTRA_NAME", "x"));
        var first = DevEnvFile.Render(_scope.Template, values, ["# PARKED=1"], _scope.Now);

        var again = LocalEnvFile.Read(Write(first)).Entries.Select(e => KeyValuePair.Create(e.Name, e.Value)).ToList();
        var second = DevEnvFile.Render(_scope.Template, again, DevEnvFile.CommentedSettings(first), _scope.Now);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task A_write_replaces_the_file_whole_and_keeps_the_one_it_replaced()
    {
        _scope.WriteEnv("OLD=1");

        var backup = await DevEnvFile.WriteAsync(_scope.Checkout.EnvFile, ["NEW=2"], _scope.Now, CancellationToken.None);
        var second = await DevEnvFile.WriteAsync(_scope.Checkout.EnvFile, ["NEWER=3"], _scope.Now, CancellationToken.None);

        Assert.Equal(["NEWER=3"], File.ReadAllLines(_scope.Checkout.EnvFile));
        Assert.EndsWith(".20261017-093000.bak", backup, StringComparison.Ordinal);
        Assert.Equal(["OLD=1"], File.ReadAllLines(backup!));
        Assert.EndsWith(".20261017-093000-2.bak", second, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(_scope.Checkout.EnvFile)!, "*.writing"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_scope.Checkout.EnvFile));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(backup!));
        }
    }

    [Fact]
    public async Task A_copied_templates_generated_values_are_filled_in_and_nothing_else_moves()
    {
        File.Copy(_scope.Checkout.TemplatePath, Directory.CreateDirectory(Path.GetDirectoryName(_scope.Checkout.EnvFile)!).FullName + "/env");
        var before = File.ReadAllLines(_scope.Checkout.EnvFile);

        var generated = await DevEnvFile.CompleteGeneratedAsync(_scope.Checkout.EnvFile, CancellationToken.None);

        var after = File.ReadAllLines(_scope.Checkout.EnvFile);
        Assert.Equal(["ControlPlane__Jwt__SigningKey", "SQLFLOW_ADMIN_PASSWORD"], generated);
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(2, before.Zip(after).Count(p => p.First != p.Second));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(_scope.Checkout.EnvFile)!, "*.bak"));
        Assert.Empty(await DevEnvFile.CompleteGeneratedAsync(_scope.Checkout.EnvFile, CancellationToken.None));
    }

    [Fact]
    public void Services_LocalDb_and_containers_are_read_as_the_servers_they_are()
    {
        var services = LocalSqlServers.ParseServices("MSSQLSERVER|Running|Automatic\nMSSQL$SQLEXPRESS|Stopped|Disabled\n|junk\n").ToList();
        var localDb = LocalSqlServers.ParseLocalDb("MSSQLLocalDB\r\nProjects\r\n").ToList();
        var containers = LocalSqlServers.ParseContainers(
            "osdu-delivery-sql|mcr.microsoft.com/mssql/server:2022-latest|running|0.0.0.0:1433->1433/tcp, [::]:1433->1433/tcp\n"
            + "other|mcr.microsoft.com/mssql/server:2019-latest|exited|\n"
            + "edge|mcr.microsoft.com/azure-sql-edge|running|0.0.0.0:14330->1433/tcp\n"
            + "web|nginx|running|0.0.0.0:80->80/tcp\n").ToList();

        Assert.Equal([("localhost", "running", false), ("localhost\\SQLEXPRESS", "stopped", true)], services.Select(s => (s.Server, s.State, s.Disabled)));
        Assert.Equal(["(localdb)\\MSSQLLocalDB", "(localdb)\\Projects"], localDb.Select(l => l.Server));
        Assert.Equal([("localhost", "running"), ("localhost", "exited"), ("localhost,14330", "running")], containers.Select(c => (c.Server, c.State)));
        Assert.True(containers[0].Running);
    }

    [Fact]
    public async Task Finding_servers_asks_the_windows_hosts_only_on_windows_and_says_whether_docker_answers()
    {
        var tools = new FakeToolRunner();
        tools.Results["powershell"] = FakeToolRunner.Ok("MSSQLSERVER|Running|Automatic");
        tools.Results["sqllocaldb"] = FakeToolRunner.Ok("MSSQLLocalDB");
        tools.Results["docker"] = FakeToolRunner.Failed(1, "Cannot connect to the Docker daemon");

        var (onWindows, dockerThere) = await LocalSqlServers.FindAsync(tools, isWindows: true, CancellationToken.None);
        var (onMac, _) = await LocalSqlServers.FindAsync(tools, isWindows: false, CancellationToken.None);

        Assert.Equal(2, onWindows.Count);
        Assert.Equal(DockerState.NotRunning, dockerThere);
        Assert.Empty(onMac);
        tools.Results.Remove("docker");
        Assert.Equal(DockerState.NotInstalled, (await LocalSqlServers.FindAsync(tools, isWindows: false, CancellationToken.None)).Docker);
    }

    private string Write(IReadOnlyList<string> lines)
    {
        var path = Path.Combine(_scope.Root, "rendered.env");
        File.WriteAllLines(path, lines);
        return path;
    }
}
