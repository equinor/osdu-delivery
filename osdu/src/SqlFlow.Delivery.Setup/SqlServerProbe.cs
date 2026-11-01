using Microsoft.Data.SqlClient;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Delivery.Setup;

/// <summary>
/// Reads the SQL Servers a local run uses, with the client the hosts use, and on the setup's request creates a database or
/// allows snapshot isolation in one. A server that answered is read once per sign-in for as long as the probe lives (one
/// that did not is asked again, since it may be starting). Every read stands
/// alone: one the login may not make leaves the others, and says why. A database's name only ever reaches SQL as a
/// parameter, quoted by the server (QUOTENAME).
/// </summary>
internal sealed class SqlServerProbe : ISqlServerProbe
{
    private readonly Dictionary<string, ServerFacts> _servers = new(StringComparer.Ordinal);

    public async Task<ServerFacts> ProbeServerAsync(SqlTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_servers.TryGetValue(target.ServerKey, out var known))
        {
            return known;
        }

        ServerFacts facts;
        try
        {
            await using var connection = new SqlConnection(target.For("master"));
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)),
                       CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
                       CAST(ISNULL(SERVERPROPERTY('IsFullTextInstalled'), 0) AS int),
                       ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE'), 0),
                       SUSER_SNAME()
                """;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            var version = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            _ = int.TryParse(version.Split('.')[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var major);
            facts = new ServerFacts(
                true, 0, null, version, major, reader.IsDBNull(1) ? string.Empty : reader.GetString(1), reader.GetInt32(2) == 1,
                reader.GetInt32(3) == 1, reader.IsDBNull(4) ? string.Empty : reader.GetString(4));
        }
        catch (SqlException ex)
        {
            facts = ServerFacts.Failed(ex.Number, FirstLine(ex));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A sign-in mode with no provider here, Windows authentication off Windows, a name the client cannot resolve.
            facts = ServerFacts.Failed(0, FirstLine(ex));
        }

        if (facts.Answered)
        {
            _servers[target.ServerKey] = facts;
        }

        return facts;
    }

    public async Task<DatabaseFacts> ProbeDatabaseAsync(SqlTarget target, string database, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        try
        {
            await using var connection = new SqlConnection(target.For("master"));
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT d.state_desc, CAST(d.snapshot_isolation_state AS int), ISNULL(HAS_DBACCESS(d.name), 0)
                FROM sys.databases AS d WHERE d.name = @name
                """;
            command.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 128) { Value = database });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return new DatabaseFacts(false, false, string.Empty, false, false, null);
            }

            var state = reader.GetString(0);
            return new DatabaseFacts(true, state == "ONLINE", state, reader.GetInt32(2) == 1, reader.GetInt32(1) == 1, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DatabaseFacts.Unread(FirstLine(ex));
        }
    }

    public async Task<CatalogFacts> ReadCatalogAsync(SqlTarget target, string? adminName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        SqlConnection connection;
        try
        {
            connection = new SqlConnection(target.For(target.Database));
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CatalogFacts.Unread($"could not open {target.Database}: {FirstLine(ex)}");
        }

        await using (connection.ConfigureAwait(false))
        {
            var problems = new List<string>();
            int tables;
            bool hasCatalog, hasModule, hasRepos, hasConfig;
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT (SELECT COUNT(*) FROM sys.tables),
                           CASE WHEN OBJECT_ID(N'catalog.[User]') IS NULL THEN 0 ELSE 1 END,
                           CASE WHEN OBJECT_ID(N'osdu.[Partition]') IS NULL THEN 0 ELSE 1 END,
                           CASE WHEN OBJECT_ID(N'catalog.RepoSource') IS NULL THEN 0 ELSE 1 END,
                           CASE WHEN OBJECT_ID(N'osdu.ConfigProperty') IS NULL THEN 0 ELSE 1 END
                    """;
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await reader.ReadAsync(ct).ConfigureAwait(false);
                tables = reader.GetInt32(0);
                hasCatalog = reader.GetInt32(1) == 1;
                hasModule = reader.GetInt32(2) == 1;
                hasRepos = reader.GetInt32(3) == 1;
                hasConfig = reader.GetInt32(4) == 1;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return CatalogFacts.Unread($"could not read {target.Database}: {FirstLine(ex)}");
            }

            int? users = null;
            var adminExists = false;
            if (hasCatalog)
            {
                try
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT COUNT(*), ISNULL(SUM(CASE WHEN Username = @admin THEN 1 ELSE 0 END), 0) FROM catalog.[User]";
                    command.Parameters.Add(new SqlParameter("@admin", System.Data.SqlDbType.NVarChar, 256) { Value = adminName ?? string.Empty });
                    await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    await reader.ReadAsync(ct).ConfigureAwait(false);
                    users = reader.GetInt32(0);
                    adminExists = reader.GetInt32(1) > 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    problems.Add($"could not read catalog.[User]: {FirstLine(ex)}");
                }
            }

            var partitions = new List<PartitionFact>();
            if (hasModule)
            {
                try
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT Name, IsDefault FROM osdu.[Partition] ORDER BY Name";
                    await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        partitions.Add(new PartitionFact(reader.GetString(0), reader.GetBoolean(1)));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    problems.Add($"could not read osdu.[Partition]: {FirstLine(ex)}");
                }
            }

            var repositories = new List<RepositoryFact>();
            if (hasRepos)
            {
                try
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT Name, RemoteUrl, LocalPath, Branch, Enabled, LastError FROM catalog.RepoSource ORDER BY Name";
                    await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        repositories.Add(new RepositoryFact(
                            reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1), reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                            reader.IsDBNull(3) ? string.Empty : reader.GetString(3), reader.GetBoolean(4), reader.IsDBNull(5) ? string.Empty : reader.GetString(5)));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    problems.Add($"could not read catalog.RepoSource: {FirstLine(ex)}");
                }
            }

            var configNames = new List<string>();
            if (hasConfig)
            {
                try
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT DISTINCT Name FROM osdu.ConfigProperty";
                    await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        configNames.Add(reader.GetString(0));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    problems.Add($"could not read osdu.ConfigProperty: {FirstLine(ex)}");
                }
            }

            return new CatalogFacts(hasCatalog, tables, users, adminExists, hasModule, partitions, repositories, configNames, problems);
        }
    }

    public async Task CreateDatabaseAsync(SqlTarget target, string database, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        await ExecuteOnMasterAsync(target, """
            DECLARE @create nvarchar(400) = N'CREATE DATABASE ' + QUOTENAME(@name);
            IF DB_ID(@name) IS NULL EXEC (@create);
            DECLARE @snapshot nvarchar(400) = N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET ALLOW_SNAPSHOT_ISOLATION ON';
            EXEC (@snapshot);
            """, database, ct).ConfigureAwait(false);
    }

    public async Task DropDatabaseAsync(SqlTarget target, string database, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        await ExecuteOnMasterAsync(target, """
            DECLARE @drop nvarchar(600) = N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@name);
            IF DB_ID(@name) IS NOT NULL EXEC (@drop);
            """, database, ct).ConfigureAwait(false);
    }

    public async Task AllowSnapshotIsolationAsync(SqlTarget target, string database, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        await ExecuteOnMasterAsync(target, """
            DECLARE @snapshot nvarchar(400) = N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET ALLOW_SNAPSHOT_ISOLATION ON';
            EXEC (@snapshot);
            """, database, ct).ConfigureAwait(false);
    }

    private static async Task ExecuteOnMasterAsync(SqlTarget target, string sql, string database, CancellationToken ct)
    {
        await using var connection = new SqlConnection(target.For("master"));
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        // Creating a database waits on the server's storage; a minute and a half is plenty for an empty one.
        command.CommandTimeout = 90;
        command.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 128) { Value = database });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>An error's first line, redacted: a connection error may quote what it was given.</summary>
    private static string FirstLine(Exception ex)
    {
        var message = SecretHygiene.RedactedMessage(ex);
        var end = message.IndexOfAny(['\r', '\n']);
        return (end < 0 ? message : message[..end]).Trim();
    }
}
