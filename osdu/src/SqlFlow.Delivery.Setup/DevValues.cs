using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Setup;

/// <summary>
/// The rules a local run's settings are held to (osdu/docs/reference/cli/dev.md): what a placeholder, a reference, a secret
/// and a connection string look like, what each well-known setting accepts, what keeps a launcher from setting a value, and
/// the values the setup generates.
/// </summary>
internal static partial class DevValues
{
    /// <summary>The placeholder the template writes where the setup generates the value.</summary>
    public const string Generated = "<generated>";

    /// <summary>The GUI's origin: Vite's port, where dev.bat and dev.sh start it.</summary>
    public const string GuiOrigin = "http://localhost:5173";

    /// <summary>The password reference the template's first admin names.</summary>
    public const string AdminPasswordReference = "${env:SQLFLOW_ADMIN_PASSWORD}";

    /// <summary>The longest value a launcher sets: cmd's command line ends at 8191 characters, the name and set included.</summary>
    public const int MaxLauncherValueLength = 8000;

    /// <summary>The shortest signing key the control plane accepts: 32 bytes, for HS256.</summary>
    public const int MinSigningKeyBytes = 32;

    /// <summary>The shortest password the control plane gives a local user.</summary>
    public const int MinPasswordLength = 12;

    /// <summary>
    /// The settings that once gave the delivery module a database of its own, as an environment spells them: every host now
    /// refuses to start while one is set (osdu/docs/reference/cli/db.md, Errors). The hosts' list is
    /// <c>OsduModuleDatabase.RetiredSettings</c>, which this project does not reference; a test holds the two alike.
    /// </summary>
    public static readonly IReadOnlyList<string> RetiredSettings = ["Osdu__Database__Connection", "SQLFLOW_OSDU_DB"];

    /// <summary>The catalog's connection, which holds the delivery module's <c>osdu</c> schema too.</summary>
    public const string CatalogName = "SQLFLOW_CATALOG_DB";

    // The host's GitHub App, as SQLFlow reads it (SqlFlow.SourceControl's GitHubAppEnvironment, and
    // sqlflow/docs/reference/guides/github-app.md): its client ID, and its private key as a value or as a file.
    public const string GitHubAppIdName = "SQLFLOW_GITHUB_APP_ID";
    public const string GitHubAppKeyName = "SQLFLOW_GITHUB_APP_PRIVATE_KEY";
    public const string GitHubAppKeyPathName = "SQLFLOW_GITHUB_APP_PRIVATE_KEY_PATH";

    /// <summary>A template value standing for what the user fills in: the whole value in angle brackets.</summary>
    public static bool IsPlaceholder(string? value) => value is { Length: >= 2 } && value[0] == '<' && value[^1] == '>';

    /// <summary>One <c>${env:NAME}</c> or <c>${keyvault:vault/secret}</c> reference and nothing else.</summary>
    public static bool IsReference(string? value) => value is not null && WholeReference().IsMatch(value.Trim());

    /// <summary>A name whose value is a secret: shown by its length alone.</summary>
    public static bool IsSecretName(string name) => SecretName().IsMatch(name);

    /// <summary>A name whose value is a SQL Server connection string: shown by its server, database and sign-in alone.</summary>
    public static bool IsConnectionName(string name)
        => name.EndsWith("_DB", StringComparison.OrdinalIgnoreCase) || name.EndsWith("ConnectionReference", StringComparison.OrdinalIgnoreCase);

    /// <summary>An absolute https:// URL.</summary>
    public static bool IsHttpsUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>A data-partition-id as runs name it.</summary>
    public static bool IsPartitionId(string? value) => value is not null && PartitionId().IsMatch(value);

    /// <summary>An entitlement group's e-mail.</summary>
    public static bool IsGroupEmail(string? value) => value is not null && GroupEmail().IsMatch(value);

    /// <summary>
    /// What keeps a launcher from setting <paramref name="value"/>, or null. dev.bat sets each value with cmd's
    /// <c>set "NAME=value"</c>: a double quote ends the quoting, so a cmd operator after it splits the line; and cmd's command
    /// line ends at 8191 characters.
    /// </summary>
    public static string? LauncherProblem(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('"', StringComparison.Ordinal) && value.AsSpan().IndexOfAny("&|<>^") >= 0)
        {
            return "holds a double quote together with &, |, <, > or ^, which dev.bat cannot set";
        }

        return value.Length > MaxLauncherValueLength
            ? $"is {value.Length} characters, more than dev.bat can set ({MaxLauncherValueLength})"
            : null;
    }

    /// <summary>
    /// What is wrong with the value of a well-known setting, or null. A connection string is read as the hosts read it; a
    /// reference is taken as it stands, since its value is not here to check.
    /// </summary>
    public static string? Problem(string name, string value, bool gui)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        switch (name)
        {
            case "ControlPlane__Jwt__SigningKey" when !IsReference(value) && Encoding.UTF8.GetByteCount(value) < MinSigningKeyBytes:
                return $"shorter than {MinSigningKeyBytes} bytes, which the control plane refuses";
            case "ControlPlane__Bootstrap__AdminPasswordReference" when !IsReference(value):
                return $"not a reference: write {AdminPasswordReference} and put the password in SQLFLOW_ADMIN_PASSWORD";
            case "ControlPlane__Cors__AllowedOrigins__0" when gui && !string.Equals(value.TrimEnd('/'), GuiOrigin, StringComparison.OrdinalIgnoreCase):
                return $"not {GuiOrigin}, where the GUI starts: its calls fail with a CORS error";
            case "OSDU_URL" or "OSDU_TOKEN_URL" when !IsReference(value) && !IsHttpsUrl(value):
                return "not an https:// URL";
            case "OSDU_DATA_PARTITION" when !IsReference(value) && !IsPartitionId(value):
                return "not a data-partition-id: letters, digits, _, - and . only";
            case "OSDU_ACL_OWNER" or "OSDU_ACL_VIEWER" when !IsReference(value) && !IsGroupEmail(value):
                return "not a group e-mail, such as data.default.owners@dev.dataservices.energy";
            case GitHubAppIdName when !GitHubAppId().IsMatch(value.Trim()):
                return "not a GitHub App's client ID (like Iv23liAbCdEf) or App ID";
            case GitHubAppKeyPathName when !Path.IsPathFullyQualified(value.Trim().Trim('"')):
                return "not an absolute path: give the full path of the App's .pem file";
            case GitHubAppKeyPathName when !File.Exists(value.Trim().Trim('"')):
                return "no file there: download the App's private key (.pem) and give its path";
        }

        if (IsConnectionName(name) && !IsReference(value))
        {
            var target = SqlTarget.TryParse(value);
            if (target is null)
            {
                return "not a connection string the hosts can read (Server=...;Database=...;...)";
            }

            if (target.Database.Length == 0)
            {
                return "names no Database";
            }
        }

        return null;
    }

    /// <summary>How a value is shown: a reference as it stands, a secret by its length, a connection string by its target.</summary>
    public static string Show(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        if (IsReference(value))
        {
            return value;
        }

        if (IsSecretName(name))
        {
            return $"set, {value.Length} characters";
        }

        if (IsConnectionName(name))
        {
            return SqlTarget.TryParse(value)?.Describe() ?? $"set, {value.Length} characters";
        }

        return value.Length > 70 ? value[..67] + "..." : value;
    }

    /// <summary>
    /// A random value for a key or a password: URL-safe base64, so it survives a connection string, cmd's set, a shell and
    /// YAML untouched. 48 bytes make the 64 characters of a signing key; a password takes 18 (24 characters).
    /// </summary>
    public static string Random(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 16);
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The value the setup generates for <paramref name="name"/>: a signing key for the control plane's, a password otherwise.</summary>
    public static string GenerateFor(string name)
        => string.Equals(name, "ControlPlane__Jwt__SigningKey", StringComparison.Ordinal) ? Random(48) : Random(18);

    /// <summary>
    /// A password SQL Server's policy accepts for <c>sa</c>: at least eight characters from three of upper case, lower case,
    /// digits and symbols. The random part may lack a class, so one of each is appended.
    /// </summary>
    public static string SqlServerPassword() => Random(18) + "-Aa1";

    /// <summary>
    /// A connection string value quoted the way ADO.NET reads it when it holds a ;, a quote, an = or surrounding spaces: in
    /// single quotes, each one inside doubled. Single quotes, because dev.bat's cmd set cannot carry a double quote next to
    /// a cmd operator.
    /// </summary>
    public static string QuoteConnectionValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.AsSpan().IndexOfAny(";\"'=") >= 0 || value != value.Trim()
            ? "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'"
            : value;
    }

    [GeneratedRegex(@"^\$\{(env|keyvault):[^}]+\}$", RegexOptions.CultureInvariant)]
    private static partial Regex WholeReference();

    [GeneratedRegex("(SECRET|PASSWORD|TOKEN|SIGNINGKEY|APIKEY|API_KEY|PRIVATE_KEY|PRIVATEKEY)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretName();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubAppId();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex PartitionId();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex GroupEmail();
}
