using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Delivery.Setup;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The rules a local run's settings are held to, the connection strings the setup writes and reads, and the template every
/// env file is written from (osdu/docs/reference/cli/dev.md).
/// </summary>
public sealed partial class DevValuesTests
{
    [Theory]
    [InlineData("plain", null)]
    [InlineData("has \"quotes\" only", null)]
    [InlineData("has & alone", null)]
    [InlineData("quote \" then & operator", "double quote")]
    [InlineData("quote \" then | pipe", "double quote")]
    [InlineData("quote \" then ^ caret", "double quote")]
    public void A_value_a_launcher_cannot_set_is_named(string value, string? problem)
    {
        var said = DevValues.LauncherProblem(value);
        if (problem is null)
        {
            Assert.Null(said);
        }
        else
        {
            Assert.Contains(problem, said, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_value_longer_than_cmd_can_carry_is_named()
        => Assert.Contains("more than dev.bat can set", DevValues.LauncherProblem(new string('x', DevValues.MaxLauncherValueLength + 1)), StringComparison.Ordinal);

    [Theory]
    [InlineData("p;a'ss")]
    [InlineData("it's=x; y")]
    [InlineData(" leading and trailing ")]
    [InlineData("quote\"inside")]
    [InlineData("plain-Password_123")]
    public void A_password_survives_the_connection_string_the_setup_writes(string password)
    {
        var text = SqlTarget.Compose("localhost", "OsduDelivery", "sa", password);

        var read = new SqlConnectionStringBuilder(text);

        Assert.Equal(password, read.Password);
        Assert.Equal("sa", read.UserID);
        Assert.Equal("OsduDelivery", read.InitialCatalog);
        Assert.DoesNotContain('"', text.Replace(password, string.Empty, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("localhost", "ThisMachine", "")]
    [InlineData(".", "ThisMachine", "")]
    [InlineData("(local)\\SQLEXPRESS", "ThisMachine", "SQLEXPRESS")]
    [InlineData("tcp:127.0.0.1,1433", "ThisMachine", "")]
    [InlineData("localhost,14330", "ThisMachine", "")]
    [InlineData("(localdb)\\MSSQLLocalDB", "LocalDb", "MSSQLLocalDB")]
    [InlineData("sql.example.com", "Elsewhere", "")]
    [InlineData("tcp:sql.example.com,1433", "Elsewhere", "")]
    public void A_server_name_says_where_the_server_is(string server, string place, string instance)
        => Assert.Equal((Enum.Parse<SqlServerPlace>(place), instance), SqlTarget.PlaceOf(server));

    [Fact]
    public void A_server_on_this_machine_is_trusted_and_one_elsewhere_is_verified()
    {
        var local = new SqlConnectionStringBuilder(SqlTarget.Compose("localhost", "OsduData", null, null));
        var remote = new SqlConnectionStringBuilder(SqlTarget.Compose("sql.example.com", "OsduData", null, null));

        Assert.True(local.IntegratedSecurity);
        Assert.True(local.TrustServerCertificate);
        Assert.False(remote.TrustServerCertificate);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, remote.Encrypt);
    }

    [Fact]
    public void A_probe_connects_briefly_and_alone_and_says_who_it_is()
    {
        var target = SqlTarget.TryParse("Server=localhost;Database=OsduDelivery;Integrated Security=True;TrustServerCertificate=True")!;

        var probe = new SqlConnectionStringBuilder(target.For("master"));

        Assert.Equal("master", probe.InitialCatalog);
        Assert.Equal(8, probe.ConnectTimeout);
        Assert.False(probe.Pooling);
        Assert.True(probe.TrustServerCertificate);
        Assert.Equal("OSDU Delivery dev setup", probe.ApplicationName);
    }

    [Theory]
    [InlineData("Server=localhost;Database=X;Integrated Security=True", "localhost / X, Windows authentication")]
    [InlineData("Server=db;Database=X;User ID=sa;Password=secret-value", "db / X, SQL login sa")]
    [InlineData("Server=db;Database=X;Authentication=Active Directory Default", "db / X, Authentication=ActiveDirectoryDefault")]
    public void A_connection_string_is_described_without_its_password(string text, string described)
    {
        var description = SqlTarget.TryParse(text)!.Describe();

        Assert.Equal(described, description);
        Assert.DoesNotContain("secret-value", description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a connection string")]
    [InlineData("Database=NoServer")]
    [InlineData("Server=x;Bogus Keyword=1")]
    public void A_string_the_client_cannot_read_is_not_a_target(string text) => Assert.Null(SqlTarget.TryParse(text));

    [Theory]
    [InlineData("ControlPlane__Jwt__SigningKey", "short", "32 bytes")]
    [InlineData("ControlPlane__Bootstrap__AdminPasswordReference", "literal", "not a reference")]
    [InlineData("ControlPlane__Cors__AllowedOrigins__0", "http://localhost:3000", "CORS")]
    [InlineData("OSDU_URL", "http://osdu.example.com", "https://")]
    [InlineData("OSDU_DATA_PARTITION", "dev partition", "data-partition-id")]
    [InlineData("OSDU_ACL_OWNER", "owners", "group e-mail")]
    [InlineData("SQLFLOW_CATALOG_DB", "Server=x", "names no Database")]
    [InlineData("OSDU_DATA_DB", "garbage", "connection string")]
    public void A_wrong_value_is_named(string name, string value, string problem)
        => Assert.Contains(problem, DevValues.Problem(name, value, gui: true), StringComparison.Ordinal);

    [Theory]
    [InlineData("ControlPlane__Cors__AllowedOrigins__0", "http://localhost:3000")]
    [InlineData("OSDU_URL", "${env:SOMEWHERE}")]
    [InlineData("ControlPlane__Jwt__SigningKey", "${keyvault:vault/key}")]
    [InlineData("OSDU_DATA_DB", "${keyvault:vault/data-db}")]
    public void A_value_with_no_check_here_passes(string name, string value)
        => Assert.Null(DevValues.Problem(name, value, gui: false));

    [Theory]
    [InlineData("OSDU_CLIENT_SECRET", "abcdef", "set, 6 characters")]
    [InlineData("SQLFLOW_GIT_TOKEN", "abc", "set, 3 characters")]
    [InlineData("SQLFLOW_GITHUB_APP_PRIVATE_KEY", "LS0tLS1CRUdJTiBSU0EgUFJJVkFURSBLRVktLS0tLQ==", "set, 44 characters")]
    [InlineData("SQLFLOW_GITHUB_APP_PRIVATE_KEY", "${keyvault:vault/github-app-key}", "${keyvault:vault/github-app-key}")]
    [InlineData("ControlPlane__Jwt__SigningKey", "0123456789", "set, 10 characters")]
    [InlineData("ControlPlane__Bootstrap__AdminPasswordReference", "${env:SQLFLOW_ADMIN_PASSWORD}", "${env:SQLFLOW_ADMIN_PASSWORD}")]
    [InlineData("OSDU_DATA_DB", "Server=db;Database=D;User ID=u;Password=p4ss", "db / D, SQL login u")]
    public void A_secret_shows_its_length_and_a_connection_its_target(string name, string value, string shown)
        => Assert.Equal(shown, DevValues.Show(name, value));

    [Theory]
    [InlineData("my app")]
    [InlineData("acme/app")]
    public void A_github_app_id_that_github_could_not_have_issued_is_named(string value)
        => Assert.Contains("client ID", DevValues.Problem(DevValues.GitHubAppIdName, value, gui: false), StringComparison.Ordinal);

    [Fact]
    public void A_github_app_key_path_must_be_absolute_and_hold_a_file()
    {
        Assert.Contains("not an absolute path", DevValues.Problem(DevValues.GitHubAppKeyPathName, "keys/app.pem", gui: false), StringComparison.Ordinal);
        Assert.Contains("no file there", DevValues.Problem(DevValues.GitHubAppKeyPathName, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pem"), gui: false), StringComparison.Ordinal);

        var pem = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pem");
        File.WriteAllText(pem, "-----BEGIN RSA PRIVATE KEY-----");
        try
        {
            Assert.Null(DevValues.Problem(DevValues.GitHubAppKeyPathName, pem, gui: false));
            Assert.Null(DevValues.Problem(DevValues.GitHubAppIdName, "Iv23liAbCdEf", gui: false));
        }
        finally
        {
            File.Delete(pem);
        }
    }

    [Fact]
    public void Generated_values_are_long_enough_and_safe_to_carry()
    {
        var key = DevValues.GenerateFor("ControlPlane__Jwt__SigningKey");
        var password = DevValues.GenerateFor("SQLFLOW_ADMIN_PASSWORD");
        var sa = DevValues.SqlServerPassword();

        Assert.Equal(64, key.Length);
        Assert.True(password.Length >= DevValues.MinPasswordLength);
        Assert.All(new[] { key, password, sa }, v => Assert.Matches("^[A-Za-z0-9_-]+$", v));
        Assert.Matches("[A-Z]", sa);
        Assert.Matches("[a-z]", sa);
        Assert.Matches("[0-9]", sa);
        Assert.NotEqual(key, DevValues.GenerateFor("ControlPlane__Jwt__SigningKey"));
    }

    [Fact]
    public void The_shipped_template_reads_with_every_need_and_every_name_a_launcher_can_set()
    {
        var template = DevEnvTemplate.Parse(File.ReadAllLines(RepositoryRoot.Combine(DevCheckout.TemplateName)), DevCheckout.TemplateName);

        Assert.Equal(
            ["SQLFLOW_CATALOG_DB", "ControlPlane__Jwt__SigningKey", "ControlPlane__Cors__AllowedOrigins__0", "ControlPlane__Bootstrap__AdminUsername",
                "ControlPlane__Bootstrap__AdminPasswordReference", "SQLFLOW_ADMIN_PASSWORD"],
            template.Settings.Where(s => s.Need == SettingNeed.Start).Select(s => s.Name));
        Assert.Contains(template.Settings, s => s.Need == SettingNeed.Flows && s.Name == "OSDU_DATA_DB");
        Assert.All(template.Settings.Where(s => !s.IsFamily), s => Assert.Matches("^[A-Za-z_][A-Za-z0-9_]*$", s.Name));
        Assert.All(template.Settings.Where(s => s.IsFamily), s => Assert.True(s.Commented, $"{s.Name} is a pattern, so it is written commented out"));
        Assert.Equal(
            ["ControlPlane__Jwt__SigningKey", "SQLFLOW_ADMIN_PASSWORD"],
            template.Settings.Where(s => s.Value == DevValues.Generated).Select(s => s.Name));
        Assert.All(template.Settings.Where(s => !s.Commented && !DevValues.IsPlaceholder(s.Value)), s => Assert.Null(DevValues.LauncherProblem(s.Value)));
        Assert.DoesNotContain(File.ReadAllText(RepositoryRoot.Combine(DevCheckout.TemplateName)), c => c == (char)0x2014);
    }

    [Fact]
    public void Every_reference_the_shipped_samples_name_is_a_setting_of_the_template()
    {
        var template = DevEnvTemplate.Parse(File.ReadAllLines(RepositoryRoot.Combine(DevCheckout.TemplateName)), DevCheckout.TemplateName);
        var named = Directory.EnumerateFiles(RepositoryRoot.Combine("osdu", "samples"), "*.yaml", SearchOption.AllDirectories)
            .SelectMany(f => EnvReference().Matches(File.ReadAllText(f)).Select(m => m.Groups["name"].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(named);
        Assert.All(named, name => Assert.NotNull(template.Find(name)));
    }

    [Fact]
    public void A_template_with_a_section_none_of_the_three_is_refused_naming_its_line()
    {
        var error = Assert.Throws<FlowValidationException>(() => DevEnvTemplate.Parse(["# header", "# ===== Needed sometimes =====", "A=1"], "t"));
        Assert.Contains("t(2)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_template_naming_no_setting_is_refused()
        => Assert.Throws<FlowValidationException>(() => DevEnvTemplate.Parse(["# only a header", "A=1"], "t"));

    [GeneratedRegex(@"\$\{env:(?<name>[A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex EnvReference();
}
