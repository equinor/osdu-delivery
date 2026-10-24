using System.Globalization;
using System.Text;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Engine.Inventories;

/// <summary>
/// Writes an inventory's ids, those of one finding or every one, to a stream as CSV with a header row (RFC 4180), a ledger page
/// at a time in the order the inventory took them in, so an export of millions of ids never holds more than one page
/// (osdu/docs/reference/cli/inventory.md, export). The API's download and the CLI's export both write through here, so
/// the two are the same file. A row is what OSDU serves of the id (its kind, version, who created and last changed it
/// and when), its finding and why, and what the ledgers hold of it (the ledger, the record's delivery key, status and
/// version, the artifact and its state).
/// </summary>
/// <remarks>
/// A cell a spreadsheet would run as a formula is written as text, as a dimension export writes it
/// (<see cref="DimensionExport"/>): an OSDU id or a user name never runs.
/// </remarks>
public static class InventoryExport
{
    /// <summary>The ids one ledger read writes.</summary>
    public const int Page = 1000;

    /// <summary>The media type of the export.</summary>
    public const string MediaType = "text/csv; charset=utf-8";

    /// <summary>How a CSV line ends (RFC 4180).</summary>
    private const string LineEnd = "\r\n";

    private static readonly string[] Columns =
    [
        "inventory_record_id", "id", "kind", "version", "finding", "finding_utc", "detail", "create_user", "create_time", "modify_user", "modify_time",
        "first_seen_utc", "changed_utc", "gone_utc", "ledger", "ledger_flow_id", "delivery_key", "ledger_status", "ledger_version", "artifact_id",
        "artifact_state",
    ];

    /// <summary>The file an export is saved as: the flow, the partition and the inventory, with the finding or <c>all</c>.</summary>
    public static string FileName(InventoryState inventory, string? finding)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var parts = new[] { inventory.FlowName, inventory.Partition, inventory.Name, finding ?? "all" };
        return string.Join('-', parts.Select(Safe)) + ".csv";
    }

    /// <summary>
    /// Writes every id of the inventory with <paramref name="finding"/> (every id when null) to <paramref name="output"/> as UTF-8,
    /// and returns how many it wrote. The stream is left open.
    /// </summary>
    public static async Task<long> WriteAsync(ILedger ledger, InventoryState inventory, string? finding, Stream output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);
        var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024, leaveOpen: true);
        await using (writer.ConfigureAwait(false))
        {
            var rows = await WriteAsync(ledger, inventory, finding, writer, ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
            return rows;
        }
    }

    /// <summary>
    /// Writes every id of the inventory with <paramref name="finding"/> (every id when null) to <paramref name="output"/>, each
    /// line ending in CRLF whatever the writer's own line ending is, and returns how many it wrote.
    /// </summary>
    /// <exception cref="DeliveryException"><paramref name="finding"/> is not a finding.</exception>
    public static async Task<long> WriteAsync(ILedger ledger, InventoryState inventory, string? finding, TextWriter output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(output);
        if (finding is not null && !InventoryFindings.IsKnown(finding))
        {
            throw new DeliveryException($"'{finding}' is not a finding; an inventory finds {string.Join(", ", InventoryFindings.All)}.");
        }

        await output.WriteAsync(DimensionExport.Csv(Columns) + LineEnd).ConfigureAwait(false);
        var ledgers = new Dictionary<Guid, string?>();
        long rows = 0;
        long? after = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await ledger.ListInventoryRecordsAsync(inventory.Partition, inventory.InventoryId, finding, after, Page, ct).ConfigureAwait(false);
            foreach (var record in page)
            {
                string? ledgerName = null;
                if (record.LedgerFlowId is { } flowId && !ledgers.TryGetValue(flowId, out ledgerName))
                {
                    ledgerName = (await ledger.GetLedgerAsync(flowId, ct).ConfigureAwait(false))?.LedgerName;
                    ledgers[flowId] = ledgerName;
                }

                var line = DimensionExport.Csv(
                    Number(record.InventoryRecordId), record.TargetId, record.Kind, Number(record.Version), record.Finding, Instant(record.FindingUtc), record.Detail,
                    record.CreateUser, Instant(record.CreateTime), record.ModifyUser, Instant(record.ModifyTime), Instant(record.FirstSeenUtc), Instant(record.ChangedUtc),
                    Instant(record.GoneUtc), ledgerName, record.LedgerFlowId?.ToString("D"), record.DeliveryKey?.ToString("D"), record.LedgerStatus,
                    Number(record.LedgerVersion), Number(record.ArtifactId), record.ArtifactState);
                await output.WriteAsync(line + LineEnd).ConfigureAwait(false);
                rows++;
            }

            if (page.Count < Page)
            {
                return rows;
            }

            after = page[^1].InventoryRecordId;
        }
    }

    /// <summary>The file an export of a selection is saved as: the inventory's name with the selection's number and the finding or <c>all</c>.</summary>
    public static string FileName(InventoryState inventory, long selectionId, string? finding)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var parts = new[] { inventory.FlowName, inventory.Partition, inventory.Name, "selection", selectionId.ToString(CultureInfo.InvariantCulture), finding ?? "all" };
        return string.Join('-', parts.Select(Safe)) + ".csv";
    }

    /// <summary>
    /// Writes the ids of a selection (osdu/docs/reference/flow/inventory-selections.md, Selecting ids by what OSDU holds) with
    /// <paramref name="finding"/> (every id of it when null) to <paramref name="output"/> as UTF-8: the columns an inventory's
    /// export writes, then the version the search index held when the match read it, and one column for each property the
    /// selection's conditions ask (<paramref name="columns"/>), its values joined by a semicolon and a space. Returns how many
    /// it wrote; the stream is left open.
    /// </summary>
    public static async Task<long> WriteSelectionAsync(
        ILedger ledger, InventoryState inventory, long selectionId, IReadOnlyList<string> columns, string? finding, Stream output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(output);
        if (finding is not null && !InventoryFindings.IsKnown(finding))
        {
            throw new DeliveryException($"'{finding}' is not a finding; an inventory finds {string.Join(", ", InventoryFindings.All)}.");
        }

        var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024, leaveOpen: true);
        await using (writer.ConfigureAwait(false))
        {
            await writer.WriteAsync(DimensionExport.Csv([.. Columns, "matched_version", .. columns]) + LineEnd).ConfigureAwait(false);
            var ledgers = new Dictionary<Guid, string?>();
            long rows = 0;
            long? after = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await ledger.ListInventorySelectionRecordsAsync(inventory.Partition, selectionId, finding, after, Page, ct).ConfigureAwait(false);
                foreach (var selected in page)
                {
                    var record = selected.Record;
                    string? ledgerName = null;
                    if (record.LedgerFlowId is { } flowId && !ledgers.TryGetValue(flowId, out ledgerName))
                    {
                        ledgerName = (await ledger.GetLedgerAsync(flowId, ct).ConfigureAwait(false))?.LedgerName;
                        ledgers[flowId] = ledgerName;
                    }

                    var held = InventorySelectionValues.Parse(selected.ValuesJson);
                    var line = DimensionExport.Csv(
                    [
                        Number(record.InventoryRecordId), record.TargetId, record.Kind, Number(record.Version), record.Finding, Instant(record.FindingUtc), record.Detail,
                        record.CreateUser, Instant(record.CreateTime), record.ModifyUser, Instant(record.ModifyTime), Instant(record.FirstSeenUtc), Instant(record.ChangedUtc),
                        Instant(record.GoneUtc), ledgerName, record.LedgerFlowId?.ToString("D"), record.DeliveryKey?.ToString("D"), record.LedgerStatus,
                        Number(record.LedgerVersion), Number(record.ArtifactId), record.ArtifactState, Number(selected.MatchedVersion),
                        .. columns.Select(c => held.TryGetValue(c, out var values) ? string.Join("; ", values) : null),
                    ]);
                    await writer.WriteAsync(line + LineEnd).ConfigureAwait(false);
                    rows++;
                }

                if (page.Count < Page)
                {
                    await writer.FlushAsync(ct).ConfigureAwait(false);
                    return rows;
                }

                after = page[^1].Record.InventoryRecordId;
            }
        }
    }

    private static string? Number(long? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string? Instant(DateTime? value) => value is { } at
        ? DateTime.SpecifyKind(at, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
        : null;

    /// <summary>A part of a file name: letters, digits, dots, dashes and underscores, anything else a dash.</summary>
    private static string Safe(string part)
    {
        var safe = new StringBuilder(part.Length);
        foreach (var c in part)
        {
            safe.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-');
        }

        return safe.Length == 0 ? "inventory" : safe.ToString();
    }
}
