using System.Globalization;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Model;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Maps a parsed removal document onto <see cref="RemovalFlowDefinition"/> and validates what YAML cannot
/// (osdu/docs/reference/flow/removal.md, Top-level keys): the partitions, the guard, and every removal's delivery flow, values
/// and key sources. What needs the delivery flow (that it exists, serves the partition, keys its records by the columns a key
/// source fills, and binds its scope) is checked when a run opens it, since a delivery flow is another document.
/// </summary>
internal static partial class RemovalMapper
{
    /// <summary>
    /// The options of a SQLFlow file reader a full key read refuses: the file lifecycle, which would move or delete the
    /// source's files, and the date windows, which read part of the source, so every key outside the window would look
    /// removed. A source of removed keys takes the windows, which only remove less.
    /// </summary>
    internal static IReadOnlyList<string> LifecycleOptions { get; } = ["copyToPath", "zipToPath", "srcDeleteIngested", "srcDeleteAtPath"];

    /// <summary>The date windows a full key read refuses (see <see cref="LifecycleOptions"/>).</summary>
    internal static IReadOnlyList<string> WindowOptions { get; } = ["initFromFileDate", "initToFileDate", "incrementalAfterDate"];

    /// <summary>The column a reader source's removal moment defaults to: the file's date.</summary>
    internal const string FileDateColumn = "FileDate_DW";

    public static RemovalFlowDefinition Map(RemovalFlowYaml y, string source)
    {
        var name = FlowMapper.Require(y.Name, "name", source);
        if (name.Length > Data.DeliveryLedger.MaxFlowNameLength)
        {
            throw new FlowValidationException(
                $"{source}: name is {name.Length} characters; a removal flow's name, which its runs are kept under, is at most {Data.DeliveryLedger.MaxFlowNameLength}.");
        }

        var partitions = MapPartitions(y.Partitions, source);
        var parameters = FlowParameterDeclarations.Map(y.Parameters, source);
        if (parameters.ContainsKey(PartitionNames.RunValue))
        {
            throw new FlowValidationException(
                $"{source}: parameters declares '{PartitionNames.RunValue}', which a removal flow keeps for the partition a run removes in. Rename the parameter.");
        }

        var entries = MapEntries(y.Removals, parameters, source);
        return new RemovalFlowDefinition
        {
            SourcePath = source == "<inline>" ? null : source,
            Name = name,
            Description = Optional(y.Description),
            Batch = Optional(y.Batch),
            TelemetryOperation = FlowMapper.MapTelemetry(y.Telemetry, RemovalFlowDefinition.FlowTypeName, source),
            Parameters = parameters,
            Partitions = partitions,
            Guard = MapGuard(y.Guard, source),
            Entries = entries,
        };
    }

    private static IReadOnlyList<string> MapPartitions(IReadOnlyList<string>? declared, string source)
    {
        if (declared is null)
        {
            return [];
        }

        var names = declared.Select((n, i) => PartitionNames.Check(n, string.Create(CultureInfo.InvariantCulture, $"{source}: partitions[{i}]"))).ToList();
        PartitionNames.CheckList(names, $"{source}: partitions");
        return names;
    }

    private static RemovalGuard MapGuard(RemovalGuardYaml? y, string source)
    {
        if (y is null)
        {
            return new RemovalGuard();
        }

        var percent = y.MaxPercent ?? RemovalGuard.DefaultMaxPercent;
        if (percent is < 0 or > 100)
        {
            throw new FlowValidationException(
                $"{source}: guard.maxPercent is {percent}; it is the share of the keys a removal compares that it may take out without an approval, from 0 to 100.");
        }

        if (y.MaxRecords is < 0)
        {
            throw new FlowValidationException(
                $"{source}: guard.maxRecords is {y.MaxRecords}; it is how many keys one removal may take out without an approval, 0 or more.");
        }

        return new RemovalGuard { MaxPercent = percent, MaxRecords = y.MaxRecords };
    }

    private static IReadOnlyList<RemovalEntry> MapEntries(IReadOnlyList<RemovalEntryYaml?>? declared, IReadOnlyDictionary<string, FlowParameter> parameters, string source)
    {
        if (declared is null || declared.Count == 0)
        {
            throw new FlowValidationException(
                $"{source}: removals names no removal. A removal flow names at least one: the delivery flow whose records it takes out, and where it reads the keys the source holds or the keys it removed.");
        }

        if (declared.Count > RemovalFlowDefinition.MaxEntries)
        {
            throw new FlowValidationException(
                $"{source}: removals names {declared.Count} removals; a flow names at most {RemovalFlowDefinition.MaxEntries}.");
        }

        var entries = new List<RemovalEntry>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var path = string.Create(CultureInfo.InvariantCulture, $"removals[{i}]");
            var y = declared[i] ?? throw new FlowValidationException($"{source}: {path} is empty.");
            var flow = FlowMapper.Require(y.Flow, $"{path}.flow", source);
            var @interface = Optional(y.Interface);
            if (@interface is not null && !SourceDefinition.IsInterfaceName(@interface))
            {
                throw new FlowValidationException(
                    $"{source}: {path}.interface '{@interface}' is not an interface name: a letter followed by letters, digits, '_' and '-'.");
            }

            var entryName = Optional(y.Name) ?? (@interface is null ? flow : $"{flow}/{@interface}");
            if (!RemovalNames.IsName(entryName))
            {
                throw new FlowValidationException(
                    $"{source}: {path} is named '{Shown(entryName)}', {entryName.Length} characters; a removal's name is {RemovalNames.Rule}.");
            }
            if (entries.Any(e => string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FlowValidationException(
                    $"{source}: {path} is named '{entryName}', which another removal of the flow is named already (ignoring case); name each removal once.");
            }

            var values = MapValues(y.Values, parameters, $"{path}.values", source);
            var keys = y.Keys is null ? null : MapSource(y.Keys, full: true, $"{path}.keys", parameters, source);
            var removed = y.Removed is null ? null : MapSource(y.Removed, full: false, $"{path}.removed", parameters, source);
            if (keys is null && removed is null)
            {
                throw new FlowValidationException(
                    $"{source}: {path} declares neither keys (the keys the source holds now) nor removed (the keys it lists as removed); a removal reads at least one.");
            }

            entries.Add(new RemovalEntry
            {
                Name = entryName,
                Flow = flow,
                Interface = @interface,
                Values = values,
                KeepReferenced = y.KeepReferenced ?? true,
                Keys = keys,
                Removed = removed,
                Path = path,
            });
        }

        return entries;
    }

    private static IReadOnlyDictionary<string, string> MapValues(
        Dictionary<string, RunValueYaml?>? declared, IReadOnlyDictionary<string, FlowParameter> parameters, string path, string source)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in declared ?? [])
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new FlowValidationException($"{source}: {path} names a parameter with an empty name.");
            }

            if (string.Equals(name.Trim(), PartitionNames.RunValue, StringComparison.Ordinal))
            {
                throw new FlowValidationException(
                    $"{source}: {path}.{PartitionNames.RunValue} is set; the partition is the run's, never a value of the delivery flow. Remove it.");
            }

            if (value is null || value.Items.Count == 0)
            {
                throw new FlowValidationException($"{source}: {path}.{name} has no value; give the delivery flow's parameter a value, or a list of values, or remove it.");
            }

            foreach (var item in value.Items)
            {
                CheckTokens(item, parameters, $"{path}.{name}", source);
            }

            values[name.Trim()] = value.Text;
        }

        return values;
    }

    private static RemovalKeySource MapSource(
        RemovalKeySourceYaml y, bool full, string path, IReadOnlyDictionary<string, FlowParameter> parameters, string source)
    {
        var reader = y.Type is not null || y.Location is not null || y.Options is not null;
        var table = y.Connection is not null || y.Provider is not null || y.Object is not null || y.Filter is not null;
        if (reader && table)
        {
            throw new FlowValidationException(
                $"{source}: {path} names both a source reader (type, location, options) and a source table (connection, provider, object, filter); a key source is one or the other.");
        }

        if (!reader && !table)
        {
            throw new FlowValidationException(
                $"{source}: {path} names no source: a SQLFlow source reader (type, location, options), or a table or view of a source database (connection, object).");
        }

        var columns = MapColumns(y.Columns, $"{path}.columns", source);
        RemovalKeyColumn? removedAt = null;
        if (y.RemovedAt is { } at)
        {
            if (full)
            {
                throw new FlowValidationException(
                    $"{source}: {path}.removedAt is set, and keys is the source's current key set, whose keys are removed when they are not there; only a source of removed keys (removed) says when a key was removed.");
            }

            removedAt = MapColumn(at, $"{path}.removedAt", source);
            if (removedAt.As is not null)
            {
                throw new FlowValidationException($"{source}: {path}.removedAt.as is set; the removal moment fills no key column. Remove it.");
            }
        }

        if (reader)
        {
            var type = FlowMapper.Require(y.Type, $"{path}.type", source);
            var options = new Dictionary<string, string?>(y.Options ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (var option in options.Keys)
            {
                if (LifecycleOptions.Contains(option, StringComparer.OrdinalIgnoreCase))
                {
                    throw new FlowValidationException(
                        $"{source}: {path}.options.{option} is set; reading keys never copies, archives or deletes a source's files. Remove it.");
                }

                if (full && WindowOptions.Contains(option, StringComparer.OrdinalIgnoreCase))
                {
                    throw new FlowValidationException(
                        $"{source}: {path}.options.{option} is set, and keys is the source's complete key set: a date window reads part of it, and every key outside the window would look removed. Remove it.");
                }

                if (option.Equals(SourceOptions.SettledFilesOnly, StringComparison.OrdinalIgnoreCase))
                {
                    throw new FlowValidationException(
                        $"{source}: {path}.options.{option} is the engine's to set. Remove it.");
                }
            }

            var location = Optional(y.Location);
            if (location is not null)
            {
                CheckTokens(location, parameters, $"{path}.location", source);
            }
            else if (!options.ContainsKey(SourceLocations.SrcPathOption))
            {
                throw new FlowValidationException(
                    $"{source}: {path} names no location (or options.{SourceLocations.SrcPathOption}): the folder, file, glob or URI the reader reads.");
            }

            return new RemovalKeySource
            {
                Reader = new RemovalReaderSource { Type = type.ToLowerInvariant(), Location = location, Options = options },
                Columns = columns,
                RemovedAt = removedAt,
                Path = path,
            };
        }

        var connection = FlowMapper.Require(y.Connection, $"{path}.connection", source);
        var provider = ParseProvider(y.Provider, $"{path}.provider", source);
        CheckConnection(connection, provider, $"{path}.connection", source);
        var @object = FlowMapper.Require(y.Object, $"{path}.object", source);
        try
        {
            _ = SqlFlow.Core.Ingestion.RelationalObject.Parse(@object);
        }
        catch (SqlFlowException ex)
        {
            throw new FlowValidationException($"{source}: {path}.object: {ex.Message}", ex);
        }

        var filter = Optional(y.Filter);
        if (filter is not null && !LeadingAnd().IsMatch(filter))
        {
            throw new FlowValidationException(
                $"{source}: {path}.filter is '{Shown(filter)}'; it is appended to the read's WHERE as written, so it carries its own leading AND (AND status = 'active').");
        }

        if (!full && removedAt is null)
        {
            throw new FlowValidationException(
                $"{source}: {path}.removedAt is required for a table source of removed keys: the column holding when each key was removed, which a key whose row came back after it is held against.");
        }

        return new RemovalKeySource
        {
            Table = new RemovalTableSource { Connection = connection, Provider = provider, ObjectName = @object, Filter = filter },
            Columns = columns,
            RemovedAt = removedAt,
            Path = path,
        };
    }

    private static IReadOnlyList<RemovalKeyColumn> MapColumns(IReadOnlyList<RemovalKeyColumnYaml?>? declared, string path, string source)
    {
        if (declared is null)
        {
            return [];
        }

        if (declared.Count == 0)
        {
            throw new FlowValidationException(
                $"{source}: {path} is empty. List the columns the record key is read from, or leave the key out to read each record key column from a column of the same name.");
        }

        var columns = new List<RemovalKeyColumn>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var where = string.Create(CultureInfo.InvariantCulture, $"{path}[{i}]");
            var column = MapColumn(declared[i] ?? throw new FlowValidationException($"{source}: {where} is empty."), where, source);
            if (columns.Any(c => string.Equals(c.Output, column.Output, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FlowValidationException(
                    $"{source}: {where} fills record key column '{column.Output}', which another column of the list fills already (ignoring case); each key column is read once.");
            }

            columns.Add(column);
        }

        return columns;
    }

    private static RemovalKeyColumn MapColumn(RemovalKeyColumnYaml y, string path, string source)
    {
        var name = FlowMapper.Require(y.Name, $"{path}.name", source);
        var expr = Optional(y.Expr);
        if (expr is not null && (expr.Contains(';', StringComparison.Ordinal) || expr.Contains("--", StringComparison.Ordinal) || expr.Contains("/*", StringComparison.Ordinal)
            || Statement().IsMatch(expr)))
        {
            throw new FlowValidationException(
                $"{source}: {path}.expr is '{Shown(expr)}'; it is one SQL expression over the column's value (@ColName), evaluated where the ledger is kept as the value is converted: no statement, query, comment or ';'.");
        }

        if (expr is not null && Variables().Matches(expr).FirstOrDefault(m => !m.Value.Equals("@ColName", StringComparison.OrdinalIgnoreCase)) is { } other)
        {
            throw new FlowValidationException(
                $"{source}: {path}.expr uses '{other.Value}'; an expression names the column's value @ColName, as SQLFlow's transform columns do (UPPER(@ColName)), and no other variable.");
        }

        return new RemovalKeyColumn { Name = name, As = Optional(y.As), Expr = expr };
    }

    /// <summary>The source system a table source is read with, by the provider names SQLFlow's documents take.</summary>
    private static DataSourceKind ParseProvider(string? provider, string field, string source)
        => provider?.Trim().ToLowerInvariant() switch
        {
            null or "" or "mssql" or "sqlserver" => DataSourceKind.MSSQL,
            "azdb" => DataSourceKind.AZDB,
            "mysql" => DataSourceKind.MySQL,
            "postgres" or "postgresql" => DataSourceKind.PostgreSQL,
            "oracle" => DataSourceKind.Oracle,
            _ => throw new FlowValidationException(
                $"{source}: '{field}' has unknown provider '{provider}'. Allowed: mssql, azdb, mysql, postgres, oracle."),
        };

    /// <summary>
    /// A SQL Server connection is checked as a delivery flow's is (a reference, or a connection string holding none); any other
    /// source system's is a whole reference, since its connection string is that system's to parse.
    /// </summary>
    private static void CheckConnection(string connection, DataSourceKind provider, string field, string source)
    {
        if (provider is DataSourceKind.MSSQL or DataSourceKind.AZDB)
        {
            IngestionConnection.CheckDeclared(connection, source, field);
            return;
        }

        if (!WholeReference().IsMatch(connection.Trim()))
        {
            throw new FlowValidationException(
                $"{source}: {field} is not a ${{keyvault:vault/secret}} or ${{env:NAME}} reference. A {provider} connection string is kept behind a reference, so a document never holds one.");
        }
    }

    private static void CheckTokens(string text, IReadOnlyDictionary<string, FlowParameter> parameters, string path, string source)
    {
        foreach (var token in FlowMapper.Tokens(text))
        {
            if (!parameters.ContainsKey(token) && !string.Equals(token, PartitionNames.RunValue, StringComparison.Ordinal))
            {
                throw new FlowValidationException($"{source}: {path} uses '{{{token}}}', which is not declared under parameters.");
            }
        }
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Shown(string value) => value.Length > 60 ? value[..60] + "..." : value;

    [GeneratedRegex(@"^\s*AND\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingAnd();

    /// <summary>A word that makes an expression a statement or a query, which a key's expression never is.</summary>
    [GeneratedRegex(@"\b(?:select|insert|update|delete|merge|exec|execute|drop|alter|create|truncate|grant|revoke|deny|declare|openrowset|opendatasource|openquery|waitfor|shutdown|dbcc|backup|restore)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Statement();

    [GeneratedRegex(@"^\$\{(?:env|keyvault):[^{}\s]+\}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WholeReference();

    /// <summary>A variable an expression names (<c>@name</c>, <c>@@name</c>), outside a string literal or not: an expression names only <c>@ColName</c>.</summary>
    [GeneratedRegex(@"@+[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex Variables();
}
