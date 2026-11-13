using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Delivery.Source;

/// <summary>
/// The connection a flow reads its ingestion tables over. As declared in a flow document it holds references only: a whole
/// <c>${keyvault:vault/secret}</c> or <c>${env:NAME}</c> reference, or a SQL Server connection string whose password, when
/// it has one, is such a reference. It is resolved on the node that runs the flow, opened with an application name that
/// names the flow, and never written to a log or an error.
/// </summary>
public static partial class IngestionConnection
{
    /// <summary>The application name the source database's sessions carry, followed by the flow's name.</summary>
    public const string ApplicationName = "OSDU Delivery";

    private const string Placeholder = "__reference__";

    /// <summary>Refuses a declared connection that holds a literal secret or is not a SQL Server connection string.</summary>
    /// <param name="declared">The connection as the document declares it.</param>
    /// <param name="source">The document, for messages.</param>
    /// <param name="field">The document path of the connection, for messages.</param>
    public static void CheckDeclared(string declared, string source, string field = "source.connection")
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        var text = declared.Trim();
        if (WholeReference().IsMatch(text))
        {
            return;
        }

        // A reference inside the string is resolved when the run connects; for the check it stands for itself.
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(ReferenceToken().Replace(text, Placeholder));
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            throw new FlowValidationException(
                $"{source}: {field} is neither a ${{keyvault:vault/secret}} or ${{env:NAME}} reference nor a SQL Server connection string: {SecretHygiene.RedactedMessage(ex.Message)}");
        }

        if (!string.IsNullOrEmpty(builder.Password) && !builder.Password.Equals(Placeholder, StringComparison.Ordinal))
        {
            throw new FlowValidationException(
                $"{source}: {field} carries a literal password. A flow document holds references only: put the connection string, or its password, behind ${{keyvault:vault/secret}} or ${{env:NAME}}, or connect with Azure AD (Authentication=Active Directory Default).");
        }

        if (string.IsNullOrWhiteSpace(builder.DataSource))
        {
            throw new FlowValidationException($"{source}: {field} names no server (Data Source); give the connection string's server, or a reference to the whole connection string.");
        }
    }

    /// <summary>
    /// The connection string a read opens: the resolved one, with an application name that names the flow (unless the string
    /// names its own) and a 30 second connect timeout where the driver's 15 second default stands.
    /// </summary>
    public static string Canonicalize(string resolved, string flowName)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(resolved);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            throw new DeliveryException($"Flow '{flowName}': the source connection string, as its reference resolved, is malformed: {SecretHygiene.RedactedMessage(ex.Message)}");
        }

        if (!builder.ShouldSerialize("Application Name"))
        {
            var name = $"{ApplicationName}/{flowName}";
            builder.ApplicationName = name.Length <= 128 ? name : name[..128];
        }

        if (builder.ConnectTimeout == 15)
        {
            builder.ConnectTimeout = 30;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// Resolves the flow's declared connection through <paramref name="secrets"/> and opens it, retrying the open on the
    /// transient errors Azure SQL raises while a database scales or fails over. The caller owns the connection.
    /// </summary>
    public static async Task<SqlConnection> OpenAsync(string declared, string flowName, ISecretResolver secrets, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(declared);
        ArgumentNullException.ThrowIfNull(secrets);
        string resolved;
        try
        {
            resolved = await secrets.ResolveAsync(declared, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or InvalidOperationException or ArgumentException)
        {
            throw new DeliveryException($"Flow '{flowName}': the source connection reference could not be resolved on this node: {SecretHygiene.RedactedMessage(ex.Message)}", ex);
        }

        return await OpenResolvedAsync(Canonicalize(resolved, flowName), flowName, "source", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a connection string already resolved and canonicalized, retrying the open on the transient errors Azure SQL
    /// raises while a database scales or fails over; <paramref name="what"/> names the database in a failure ("source").
    /// The caller owns the connection.
    /// </summary>
    public static async Task<SqlConnection> OpenResolvedAsync(string connectionString, string flowName, string what, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        var connection = new SqlConnection(connectionString)
        {
            RetryLogicProvider = SqlConfigurableRetryFactory.CreateExponentialRetryProvider(new SqlRetryLogicOption
            {
                NumberOfTries = 4,
                DeltaTime = TimeSpan.FromSeconds(2),
                MaxTimeInterval = TimeSpan.FromSeconds(30),
            }),
        };

        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch (SqlException ex)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new DeliveryException($"Flow '{flowName}': the {what} database could not be opened (SQL error {ex.Number}): {SecretHygiene.RedactedMessage(ex.Message)}", ex);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0 && ex.InnerExceptions[^1] is SqlException last)
        {
            // The retry logic gave up on an error it retries: it throws every attempt's, and the last says why.
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new DeliveryException(
                $"Flow '{flowName}': the {what} database could not be opened after {ex.InnerExceptions.Count} attempts (SQL error {last.Number}): {SecretHygiene.RedactedMessage(last.Message)}",
                ex);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    [GeneratedRegex(@"^\$\{[a-zA-Z]+:[^}]+\}$")]
    private static partial Regex WholeReference();

    [GeneratedRegex(@"\$\{[a-zA-Z]+:[^}]+\}")]
    private static partial Regex ReferenceToken();
}
