using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Documents;

// A delivery flow's source block (osdu/docs/reference/flow/delivery.md, source): its tables, the files a child
// dataset's rows name and the system columns, and what the source must be for a run to read it: the objects,
// columns, filters and file locations it names, and the work location a run's batches go to.
internal static partial class FlowMapper
{
    /// <summary>The longest column name SQL Server accepts.</summary>
    private const int MaxColumnLength = 128;

    /// <summary>
    /// The files a child dataset's rows name (<c>source.datasets.&lt;name&gt;.files</c>, osdu/docs/reference/flow/enrich-records.md):
    /// where each row's files are, in the row's columns, and the column holding their content hash, which says when they go
    /// again. What they become (datasets of the record, or its bulk data) is the enrichment of the flow's mapping reading the
    /// rows to say.
    /// </summary>
    private static FlowPayload MapDatasetFiles(DatasetFilesYaml declared, string at, string source)
    {
        var files = new FlowPayload
        {
            Root = Require(declared.Root, at + ".root", source),
            LocationColumn = Require(declared.LocationColumn, at + ".locationColumn", source),
            Pattern = Optional(declared.Pattern) ?? FlowPayload.DefaultPattern,
            HashColumn = Optional(declared.HashColumn),
            ChunkCountColumn = Optional(declared.ChunkCountColumn),
            ContentType = Optional(declared.ContentType),
        };
        foreach (var column in new[] { files.LocationColumn, files.HashColumn, files.ChunkCountColumn }.OfType<string>())
        {
            CheckColumn(column, at, source);
        }

        return files;
    }

    private static FlowSource MapSource(FlowSourceYaml src, string source, KeyPaths paths)
    {
        var record = src.Record ?? throw Missing(paths.Record, source);
        var datasets = new Dictionary<string, FlowSourceDataset>(StringComparer.Ordinal);
        foreach (var (name, dataset) in src.Datasets ?? [])
        {
            var at = $"{paths.Datasets}.{name}";
            var declared = dataset ?? throw Missing(at + ".object", source);
            datasets[name.Trim()] = new FlowSourceDataset
            {
                Object = Require(declared.Object, at + ".object", source),
                Join = Trimmed(declared.Join),
                OrderBy = (declared.OrderBy ?? []).Select(c => c?.Trim() ?? string.Empty).ToList(),
                MaxRowsPerRecord = declared.MaxRowsPerRecord ?? FlowSourceDataset.DefaultMaxRowsPerRecord,
                Filter = Optional(declared.Filter),
                Files = declared.Files is { } files ? MapDatasetFiles(files, at + ".files", source) : null,
                Lookups = MapLookups(declared.Lookups, at + ".lookups", source),
            };
        }

        var payloads = new Dictionary<string, FlowPayload>(StringComparer.Ordinal);
        foreach (var (name, payload) in src.Payloads ?? [])
        {
            var at = paths.Payload(name);
            var declared = payload ?? throw Missing(at + ".root", source);
            payloads[name.Trim()] = new FlowPayload
            {
                Root = Require(declared.Root, at + ".root", source),
                LocationColumn = Optional(declared.LocationColumn),
                Pattern = Optional(declared.Pattern) ?? FlowPayload.DefaultPattern,
                HashColumn = Optional(declared.HashColumn),
                ChunkCountColumn = Optional(declared.ChunkCountColumn),
            };
        }

        return new FlowSource
        {
            Connection = Require(src.Connection, "source.connection", source),
            Record = new FlowSourceTable
            {
                Object = Require(record.Object, $"{paths.Record}.object", source),
                Key = (record.Key ?? []).Select(k => k?.Trim() ?? string.Empty).ToList(),
                PrimaryKey = Optional(record.PrimaryKey),
                Scope = Trimmed(record.Scope),
                Filter = Optional(record.Filter),
                MaxRecords = record.MaxRecords,
                Lookups = MapLookups(record.Lookups, $"{paths.Record}.lookups", source),
            },
            Datasets = datasets,
            Payloads = payloads,
            LastModified = Optional(src.LastModified),
            SystemColumns = MapSystemColumns(src.SystemColumns, source, paths),
            Incremental = new FlowIncremental
            {
                OverlapSeconds = src.Incremental?.OverlapSeconds ?? FlowIncremental.DefaultOverlapSeconds,
                PageSize = src.Incremental?.PageSize ?? FlowIncremental.DefaultPageSize,
                Isolation = ParseEnum(src.Incremental?.Isolation, SourceIsolation.Snapshot, paths.Shared("source.incremental.isolation"), source),
                CommandTimeoutSeconds = src.Incremental?.CommandTimeoutSeconds ?? 0,
            },
            Work = Require(src.Work, "source.work", source),
        };
    }

    /// <summary>System columns: a column the flow names, one it opts out of with <c>~</c>, and the default for one it leaves out.</summary>
    private static FlowSystemColumns MapSystemColumns(FlowSystemColumnsYaml? declared, string source, KeyPaths paths)
    {
        var defaults = new FlowSystemColumns();
        if (declared is null)
        {
            return defaults;
        }

        if (declared.HasUpdated && string.IsNullOrWhiteSpace(declared.Updated))
        {
            throw new FlowValidationException(
                $"{source}: {paths.Shared("source.systemColumns.updated")} cannot be opted out of: it is the column an incremental read windows on and a record's fingerprint is built from.");
        }

        return new FlowSystemColumns
        {
            Updated = declared.HasUpdated ? declared.Updated!.Trim() : defaults.Updated,
            FileName = declared.HasFileName ? Optional(declared.FileName) : defaults.FileName,
            FileNameDeclared = declared.HasFileName && !string.IsNullOrWhiteSpace(declared.FileName),
            RowNumber = declared.HasRowNumber ? Optional(declared.RowNumber) : defaults.RowNumber,
            RowNumberDeclared = declared.HasRowNumber && !string.IsNullOrWhiteSpace(declared.RowNumber),
            Deleted = declared.HasDeleted ? Optional(declared.Deleted) : defaults.Deleted,
            DeletedDeclared = declared.HasDeleted && !string.IsNullOrWhiteSpace(declared.Deleted),
            Inserted = declared.HasInserted ? Optional(declared.Inserted) : defaults.Inserted,
            InsertedDeclared = declared.HasInserted && !string.IsNullOrWhiteSpace(declared.Inserted),
        };
    }

    /// <summary>
    /// What a flow's source must be for a run to read it (osdu/docs/reference/flow/delivery.md, source). Every rule names
    /// the key it is about; the bindings to the tables' actual columns are checked when a run opens the source.
    /// </summary>
    private static void ValidateSource(FlowDefinition flow, string source, KeyPaths paths)
    {
        var src = flow.Source;
        IngestionConnection.CheckDeclared(src.Connection, source);
        CheckObject(src.Record.Object, $"{paths.Record}.object", source);

        if (src.Record.Key.Count == 0)
        {
            throw new FlowValidationException($"{source}: {paths.Record}.key must name the record table's key columns (the ingestion flow's load.keyColumns).");
        }

        var key = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in src.Record.Key)
        {
            CheckColumn(column, $"{paths.Record}.key", source);
            if (!key.Add(column))
            {
                throw new FlowValidationException($"{source}: {paths.Record}.key names column '{column}' more than once.");
            }
        }

        if (src.Record.PrimaryKey is { } primaryKey)
        {
            CheckColumn(primaryKey, $"{paths.Record}.primaryKey", source);
        }

        if (src.Record.MaxRecords is < 1 or > FlowSourceTable.MaxRecordsCeiling)
        {
            throw new FlowValidationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{source}: {paths.Record}.maxRecords is {src.Record.MaxRecords}; a run reads the first 1 to {FlowSourceTable.MaxRecordsCeiling:N0} records of its scope, or leave it out to read the scope whole."));
        }

        CheckFilter(src.Record.Filter, $"{paths.Record}.filter", source);
        CheckLookups(src.Record.Lookups, $"{paths.Record}.lookups", source);
        foreach (var (column, parameter) in src.Record.Scope)
        {
            CheckColumn(column, $"{paths.Record}.scope", source);
            if (!flow.Parameters.ContainsKey(parameter))
            {
                throw new FlowValidationException($"{source}: {paths.Record}.scope binds column '{column}' to parameter '{parameter}', which is not declared under parameters.");
            }
        }

        foreach (var (name, dataset) in src.Datasets)
        {
            var at = $"{paths.Datasets}.{name}";
            if (!DatasetName().IsMatch(name) || name.Length > MaxColumnLength || name.Equals(SourceDatasets.Record, StringComparison.OrdinalIgnoreCase))
            {
                throw new FlowValidationException(
                    $"{source}: {paths.Datasets} names a dataset '{name}'; a child dataset is letters, digits and '_', and '{SourceDatasets.Record}' names the record table itself.");
            }

            CheckObject(dataset.Object, at + ".object", source);
            if (dataset.Join.Count == 0)
            {
                throw new FlowValidationException($"{source}: {at}.join must join the child table's columns to the record table's key columns.");
            }

            foreach (var (child, recordColumn) in dataset.Join)
            {
                CheckColumn(child, at + ".join", source);
                if (!key.Contains(recordColumn))
                {
                    throw new FlowValidationException(
                        $"{source}: {at}.join joins child column '{child}' to record column '{recordColumn}', which is not a key column of {paths.Record}.key.");
                }
            }

            foreach (var column in src.Record.Key)
            {
                if (!dataset.Join.Values.Contains(column, StringComparer.OrdinalIgnoreCase))
                {
                    throw new FlowValidationException(
                        $"{source}: {at}.join does not cover key column '{column}' of the record table; every key column must be joined, or a child row could belong to several records.");
                }
            }

            foreach (var column in dataset.OrderBy)
            {
                CheckColumn(column, at + ".orderBy", source);
            }

            if (dataset.MaxRowsPerRecord is < 1 or > FlowSourceDataset.MaxRowsPerRecordCeiling)
            {
                throw new FlowValidationException(
                    string.Create(CultureInfo.InvariantCulture, $"{source}: {at}.maxRowsPerRecord must be between 1 and {FlowSourceDataset.MaxRowsPerRecordCeiling}."));
            }

            CheckFilter(dataset.Filter, at + ".filter", source);
            CheckLookups(dataset.Lookups, at + ".lookups", source);

            if (dataset.Files is { } files)
            {
                CheckLocation(flow, files.Root, at + ".files.root", source);
                if (files.Pattern.Contains('/', StringComparison.Ordinal) || files.Pattern.Contains('\\', StringComparison.Ordinal) || files.Pattern.Contains("..", StringComparison.Ordinal))
                {
                    throw new FlowValidationException($"{source}: {at}.files.pattern '{files.Pattern}' is a file name glob under each row's folder, without a path.");
                }
            }
        }

        foreach (var (name, payload) in src.Payloads)
        {
            var at = paths.Payload(name);
            CheckLocation(flow, payload.Root, at + ".root", source);
            foreach (var column in new[] { payload.LocationColumn, payload.HashColumn, payload.ChunkCountColumn }.OfType<string>())
            {
                CheckColumn(column, at, source);
            }

            if (payload.Pattern.Contains('/', StringComparison.Ordinal) || payload.Pattern.Contains('\\', StringComparison.Ordinal) || payload.Pattern.Contains("..", StringComparison.Ordinal))
            {
                throw new FlowValidationException($"{source}: {at}.pattern '{payload.Pattern}' is a file name glob under the record's payload folder, without a path.");
            }
        }

        if (PayloadParts.Of(flow) is { } parts)
        {
            ValidateParts(flow, parts, source, paths);
        }
        else if (DeliveryProtocols.CarriesPayload(flow.Target.Protocol))
        {
            // The bulk data an enrichment of the flow's mapping names is the payload set bulk once the flow is bound to its
            // mapping, which checks it then (osdu/docs/reference/flow/enrich-bulk.md).
            var selected = flow.Target.ProtocolOptions.Payload;
            var enrichmentBulk = selected == PayloadParts.Bulk && src.Payloads.Count == 0 && flow.Target.Protocol == DeliveryProtocol.Ddms;
            if (selected is not null && !src.Payloads.ContainsKey(selected) && !enrichmentBulk)
            {
                throw new FlowValidationException($"{source}: target.protocolOptions.payload '{selected}' is not declared under source.payloads.");
            }

            if (selected is null && src.Payloads.Count > 1)
            {
                throw new FlowValidationException(
                    $"{source}: source.payloads declares {src.Payloads.Count} payloads; target.protocolOptions.payload must name the one the {flow.Target.Protocol} protocol streams.");
            }

            var payloadName = enrichmentBulk ? null : selected ?? src.Payloads.Keys.FirstOrDefault();
            if (payloadName is not null)
            {
                RequirePartColumns(flow, payloadName, source, paths);
            }
            else if (flow.Target.Protocol == DeliveryProtocol.Dataset)
            {
                throw new FlowValidationException($"{source}: the dataset route stores and registers each record's files, and {paths.Payload(FilesPayload)} declares none.");
            }
        }

        if (src.LastModified is { } lastModified)
        {
            CheckColumn(lastModified, paths.Shared("source.lastModified"), source);
        }

        foreach (var column in new[] { src.SystemColumns.Updated, src.SystemColumns.FileName, src.SystemColumns.RowNumber, src.SystemColumns.Deleted, src.SystemColumns.Inserted }.OfType<string>())
        {
            CheckColumn(column, paths.Shared("source.systemColumns"), source);
        }

        if (src.Incremental.OverlapSeconds is < 0 or > FlowIncremental.MaxOverlapSeconds)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {paths.Shared("source.incremental.overlapSeconds")} must be between 0 and {FlowIncremental.MaxOverlapSeconds}."));
        }

        if (src.Incremental.PageSize is < 1 or > FlowIncremental.MaxPageSize)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {paths.Shared("source.incremental.pageSize")} must be between 1 and {FlowIncremental.MaxPageSize}."));
        }

        if (src.Incremental.CommandTimeoutSeconds < 0)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("source.incremental.commandTimeoutSeconds")} must not be negative (0 lets a read run as long as the run does).");
        }

        CheckWorkLocation(flow, src.Work, source);
    }

    /// <summary>
    /// The work location: as <see cref="CheckLocation"/> checks a location, except that it may open with a reference
    /// (<c>${env:LANDING_URL}/.work/welllog/{logSource}</c>), which the run resolves with its own values when it opens, so the
    /// work batches go to the storage the deployment configures, every node of a fan-out reaches them, and the flow moves
    /// between deployments unchanged (osdu/docs/reference/flow/delivery.md, source). A reference anywhere else in it would
    /// write under a folder named after the value, so it is refused.
    /// </summary>
    private static void CheckWorkLocation(FlowDefinition flow, string work, string source)
    {
        if (!work.Contains("${", StringComparison.Ordinal))
        {
            CheckLocation(flow, work, "source.work", source);
            return;
        }

        var opening = WorkReference().Match(work);
        var rest = opening.Success ? work[opening.Length..] : work;
        if (!opening.Success || rest.Contains("${", StringComparison.Ordinal))
        {
            throw new FlowValidationException(
                $"{source}: source.work '{work}' names a reference other than at its start. A work location that names one is the reference's value with a path after it, such as ${{env:LANDING_URL}}/.work/welllog/{{logSource}}.");
        }

        foreach (var token in Tokens(rest))
        {
            if (!flow.Parameters.ContainsKey(token))
            {
                throw new FlowValidationException($"{source}: source.work uses '{{{token}}}', which is not declared under parameters.");
            }
        }
    }

    /// <summary>A work location that opens with one <c>${scheme:locator}</c> reference.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\$\{[A-Za-z]+:[^}]+\}")]
    private static partial System.Text.RegularExpressions.Regex WorkReference();

    internal static void CheckObject(string declared, string key, string source)
    {
        if (!SourceObjectName.TryParse(declared, out _, out var problem))
        {
            throw new FlowValidationException($"{source}: {key} '{declared}' must be a three-part name [database].[schema].[table]: {problem}.");
        }
    }

    internal static void CheckColumn(string column, string key, string source)
    {
        if (string.IsNullOrWhiteSpace(column) || column.Length > MaxColumnLength || column.Any(char.IsControl)
            || column.Contains('[', StringComparison.Ordinal) || column.Contains(']', StringComparison.Ordinal) || column.Trim().Length != column.Length)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {key} names a column '{column}'; a column name is 1 to {MaxColumnLength} characters, without brackets, control characters or surrounding space."));
        }
    }

    /// <summary>A predicate a table's reads append (<see cref="SourceFilter"/>), when the flow declares one.</summary>
    private static void CheckFilter(string? filter, string key, string source)
    {
        if (filter is not null && SourceFilter.Problem(filter) is { } problem)
        {
            throw new FlowValidationException($"{source}: {key} is '{SourceFilter.Shown(filter)}'; {problem}.");
        }
    }

    /// <summary>
    /// A file location the flow declares (its work root, a payload root): written out, and its <c>{name}</c> tokens are
    /// declared parameters (<see cref="RefuseLocationReference"/>).
    /// </summary>
    private static void CheckLocation(FlowDefinition flow, string location, string key, string source)
    {
        RefuseLocationReference(location, key, source);
        foreach (var token in Tokens(location))
        {
            if (!flow.Parameters.ContainsKey(token))
            {
                throw new FlowValidationException($"{source}: {key} uses '{{{token}}}', which is not declared under parameters.");
            }
        }
    }

    /// <summary>
    /// Refuses a <c>${...}</c> reference in a file location of any kind's flow (a delivery flow's payload roots, a retrieval
    /// flow's target): such a location is substituted from the run's parameters and read as written, so a run would read or
    /// write under a folder named after the reference. What varies per run is a <c>{parameter}</c> token instead. A delivery
    /// flow's work location is the one that may open with a reference (<see cref="CheckWorkLocation"/>).
    /// </summary>
    internal static void RefuseLocationReference(string location, string key, string source)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location.Contains("${", StringComparison.Ordinal))
        {
            throw new FlowValidationException(
                $"{source}: {key} '{location}' holds a ${{...}} reference, and a location is not resolved from one: write the path or storage URI itself, and vary it per run or environment with a {{parameter}} token declared under parameters.");
        }
    }

    private static IReadOnlyDictionary<string, string> Trimmed(Dictionary<string, string>? declared)
        => (declared ?? []).ToDictionary(kv => kv.Key.Trim(), kv => kv.Value?.Trim() ?? string.Empty, StringComparer.Ordinal);
}
