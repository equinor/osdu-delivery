using System.Globalization;
using System.Text;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// The records of a delivery flow that a listing filter matches, or the records a lookup across every flow reaches, as an
/// Excel workbook (osdu/docs/reference/cli/records.md, records export): a working list of what to fix where the records
/// come from. Each record is a row of the <c>Records</c> sheet with what the ledger holds of it (its status, the issue and
/// the problem that keep it blocked, the file and row it came from, its key's parts, its attempts and where it landed in
/// OSDU), and the <c>About</c> sheet says what was exported, how and when, and what each column holds. The flow's Records
/// tab, the Records page, the API and <c>sqlflow records export</c> write through here, so each gives the same file.
/// </summary>
/// <remarks>
/// A flow's export reads the keys the filter matches once, so a record that changes while the file is written is neither
/// lost nor written twice, then the records a page of keys at a time, twice: once to sort them and once to write them, so
/// the most a write holds is the keys and their sort values. It writes at most <see cref="Limit"/> records, the listing's
/// count limit, and refuses a filter matching more, as every action on a whole listing does.
/// </remarks>
public static class RecordExport
{
    /// <summary>The media type of the export.</summary>
    public const string MediaType = XlsxWorkbook.MediaType;

    /// <summary>The most records one flow's export writes: the listing's count limit.</summary>
    public const int Limit = RecordListing.CountLimit;

    /// <summary>The records one ledger read takes.</summary>
    public const int Page = 1000;

    /// <summary>The sheet holding the records.</summary>
    public const string RecordsSheet = "Records";

    /// <summary>The sheet saying what was exported.</summary>
    public const string AboutSheet = "About";

    private const string Sorted = "By record (its label, or its source key when it has none), then by source key.";

    /// <summary>The file a flow's export is saved as: the flow, its interface and partition, and the status the filter names.</summary>
    public static string FileName(FlowDefinition flow, RecordQuery query)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(query);
        return Name([flow.Name, flow.Interface, flow.Partition, "records", StatusText(query.Status)]);
    }

    /// <summary>The file a lookup's export is saved as: the partition, the flow it was narrowed to and the status it names.</summary>
    public static string FileName(RecordLookupExport lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        return Name(["records", lookup.Partition, lookup.FlowName, StatusText(lookup.Status)]);
    }

    /// <summary>
    /// Writes the records of <paramref name="flow"/>'s ledger that <paramref name="query"/> matches (its paging left aside) to
    /// <paramref name="output"/> as a workbook, and returns how many it wrote. The stream is left open.
    /// </summary>
    /// <exception cref="RecordQueryTooBroadException">The filter matches more than <see cref="Limit"/> records, or is a contains search too broad to run.</exception>
    /// <exception cref="DeliveryException">A record holds a key that is not a JSON array of strings.</exception>
    public static async Task<int> WriteAsync(ILedger ledger, FlowDefinition flow, RecordQuery query, DateTime exportedUtc, Stream output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(output);

        var flowId = flow.Id;
        var matched = await ledger.CountAsync(flowId, query, Limit + 1, ct).ConfigureAwait(false);
        if (!matched.Exact || matched.Count > Limit)
        {
            throw new RecordQueryTooBroadException(string.Create(
                CultureInfo.InvariantCulture,
                $"The filter matches more than {Limit} records as far as the listing counts, and an export writes at most {Limit} at once. Narrow it (a status, an issue, a submission, a run or a search) and export each part."));
        }

        var keys = await ledger.ListKeysAsync(flowId, query, Limit, ct).ConfigureAwait(false);
        var order = new List<(string Record, string SourceKey, DeliveryKey Key)>(keys.Count);
        foreach (var page in keys.Chunk(Page))
        {
            var read = await ledger.GetRecordsAsync(flowId, page, ct).ConfigureAwait(false);
            order.AddRange(page.Where(read.ContainsKey).Select(k => (Named(read[k]), read[k].SourceKey, k)));
        }

        order.Sort((a, b) => Compare(a.Record, a.SourceKey, a.Key, b.Record, b.SourceKey, b.Key));
        var kept = await ledger.GetLedgerAsync(flowId, ct).ConfigureAwait(false);
        var columns = new Layout(flow.Source.Record.Key, AcrossFlows: false, Matched: false);

        using var workbook = new XlsxWorkbook(output);
        var sheet = workbook.AddSheet(RecordsSheet, columns.Columns());
        foreach (var page in order.Chunk(Page))
        {
            var read = await ledger.GetRecordsAsync(flowId, page.Select(o => o.Key), ct).ConfigureAwait(false);
            foreach (var (_, _, key) in page)
            {
                // A record taken out of the ledger since its key was read is not written: the file is what the ledger holds.
                if (read.TryGetValue(key, out var record))
                {
                    sheet.AddRow(columns.Cells(record, hit: null, flow.Name));
                }
            }
        }

        WriteAbout(
            workbook,
            [
                ("Flow", XlsxCell.Text(kept?.FlowName ?? flow.Name)),
                ("Interface", XlsxCell.Text(flow.Interface)),
                ("Ledger", XlsxCell.Text(kept?.LedgerName)),
                ("Partition", XlsxCell.Text(kept?.Partition ?? flow.Partition)),
                ("Filter", XlsxCell.Text(Describe(query))),
                ("Records", XlsxCell.Number(sheet.Rows)),
                ("Sorted", XlsxCell.Text(Sorted)),
                ("Exported (UTC)", XlsxCell.Instant(exportedUtc)),
            ],
            columns);
        workbook.Complete();
        return sheet.Rows;
    }

    /// <summary>
    /// Writes the records a lookup across every flow reached (<paramref name="hits"/>, each with the flow that delivers it)
    /// to <paramref name="output"/> as a workbook, and returns how many it wrote. The stream is left open.
    /// </summary>
    public static int WriteLookup(RecordLookupExport lookup, IReadOnlyList<RecordExportHit> hits, DateTime exportedUtc, Stream output)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(output);

        var columns = new Layout([], AcrossFlows: true, Matched: lookup.Search is not null);
        var ordered = hits
            .OrderBy(h => h.FlowName is null)
            .ThenBy(h => h.FlowName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Interface ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Partition ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h, Comparer<RecordExportHit>.Create((a, b) => Compare(
                Named(a.Record), a.Record.SourceKey, a.Record.DeliveryKey, Named(b.Record), b.Record.SourceKey, b.Record.DeliveryKey)));

        using var workbook = new XlsxWorkbook(output);
        var sheet = workbook.AddSheet(RecordsSheet, columns.Columns());
        foreach (var hit in ordered)
        {
            sheet.AddRow(columns.Cells(hit.Record, hit, hit.FlowName));
        }

        var narrowed = Narrowing(lookup.Narrowing.Drifted, lookup.Narrowing.StepHeld);
        List<(string, XlsxCell)> about =
        [
            ("Lookup", XlsxCell.Text(lookup.Search is not { } search
                ? "none: the records the delivery system last took in or sent"
                : lookup.Mode == SearchMode.Contains
                    ? $"'{search}' anywhere in the source key, the label, the OSDU id or the file, or the delivery key"
                    : $"'{search}': the start of an identity, the source key, a word of the label, the OSDU id, the delivery key or the file")),
            ("Status", XlsxCell.Text(StatusText(lookup.Status) ?? "every status")),
            ("Flow", XlsxCell.Text(lookup.FlowName ?? "every flow")),
            ("Partition", XlsxCell.Text(lookup.Partition ?? "every partition")),
            ("Narrowed to", XlsxCell.Text(narrowed.Count == 0 ? null : string.Join("; ", narrowed))),
            ("Records", XlsxCell.Number(sheet.Rows)),
        ];
        if (lookup.More)
        {
            about.Add(("Reach", XlsxCell.Text(string.Create(
                CultureInfo.InvariantCulture,
                $"More records match than the {RecordListing.LookupCandidateLimit} a lookup reaches. Narrow it, or export a flow's Records tab, which writes up to {Limit} records of the flow."))));
        }

        about.Add(("Sorted", XlsxCell.Text("By flow, interface and partition, then by record (its label, or its source key when it has none) and source key.")));
        about.Add(("Exported (UTC)", XlsxCell.Instant(exportedUtc)));
        WriteAbout(workbook, about, columns);
        workbook.Complete();
        return sheet.Rows;
    }

    /// <summary>A listing filter in words, as the About sheet says what was exported.</summary>
    internal static string Describe(RecordQuery query)
    {
        var parts = new List<string>();
        if (query.Status is { } status)
        {
            parts.Add($"status {StatusText(status)}");
        }

        if (query.Search is { } search)
        {
            parts.Add(query.Mode == SearchMode.Contains ? $"'{search}' anywhere" : $"'{search}' at the start");
        }

        parts.AddRange(Narrowing(query.Drifted, query.StepHeld));
        if (query.Problem is { } problem)
        {
            parts.Add($"issue {ProblemSignature.Format(problem)}");
        }

        if (query.SubmissionId is { } submission)
        {
            parts.Add($"last planned by submission {submission:D}");
        }

        if (query.DeliveredBySubmissionId is { } delivered)
        {
            parts.Add($"delivered by submission {delivered:D}");
        }

        if (query.RunId is { } run)
        {
            parts.Add($"touched by run {run:D}");
        }

        if (query.EverDelivered is { } ever)
        {
            parts.Add(ever ? "delivered at least once" : "never delivered");
        }

        return parts.Count == 0 ? "none: every record of the ledger" : string.Join("; ", parts);
    }

    /// <summary>The drift and held-step filters in words, as a flow's listing and the lookup across flows both say them.</summary>
    private static List<string> Narrowing(bool drifted, bool stepHeld)
    {
        var parts = new List<string>();
        if (drifted)
        {
            parts.Add("drifted only");
        }

        if (stepHeld)
        {
            parts.Add("an enrichment set aside on its hold");
        }

        return parts;
    }

    private static void WriteAbout(XlsxWorkbook workbook, IReadOnlyList<(string Field, XlsxCell Value)> facts, Layout columns)
    {
        var about = workbook.AddSheet(AboutSheet, [new XlsxColumn("Field", 22), new XlsxColumn("Value", 110)], header: false);
        foreach (var (field, value) in facts.Where(f => !f.Value.IsEmpty))
        {
            about.AddRow(XlsxCell.Text(field, bold: true), value);
        }

        about.AddRow();
        about.AddRow(XlsxCell.Text("Column", bold: true), XlsxCell.Text("What it holds", bold: true));
        foreach (var (column, holds) in columns.Notes())
        {
            about.AddRow(XlsxCell.Text(column), XlsxCell.Text(holds));
        }
    }

    /// <summary>The record as a row names it: its label, or its source key when its mapping gives none.</summary>
    private static string Named(RecordState record) => string.IsNullOrEmpty(record.Label) ? record.SourceKey : record.Label;

    private static int Compare(string recordA, string sourceA, DeliveryKey keyA, string recordB, string sourceB, DeliveryKey keyB)
    {
        var order = StringComparer.OrdinalIgnoreCase.Compare(recordA, recordB);
        if (order == 0)
        {
            order = StringComparer.Ordinal.Compare(sourceA, sourceB);
        }

        return order != 0 ? order : keyA.Value.CompareTo(keyB.Value);
    }

    private static string? StatusText(RecordStatus? status) => status?.ToString().ToLowerInvariant();

    private static string Name(IEnumerable<string?> parts)
        => string.Join('-', parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Safe(p!))) + ".xlsx";

    /// <summary>A part of a file name: letters, digits, dots, dashes and underscores, anything else a dash.</summary>
    private static string Safe(string part)
    {
        var safe = new StringBuilder(part.Length);
        foreach (var c in part.Trim())
        {
            safe.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-');
        }

        return safe.ToString();
    }

    /// <summary>
    /// The columns of the Records sheet: a flow's own key columns for one flow's records, the flow, interface and partition
    /// of each for a lookup across flows, and what the lookup's term matched when it searched.
    /// </summary>
    private sealed record Layout(IReadOnlyList<string> KeyColumns, bool AcrossFlows, bool Matched)
    {
        public List<XlsxColumn> Columns()
        {
            List<XlsxColumn> columns = [new("Record", 44), new("Source key", 36)];
            if (AcrossFlows)
            {
                columns.AddRange([new("Flow", 36), new("Interface", 18), new("Partition", 12)]);
            }

            if (Matched)
            {
                columns.Add(new("Matched", 30));
            }

            columns.AddRange(KeyColumns.Select(k => new XlsxColumn(k, Math.Clamp(k.Length + 4, 12, 30))));
            columns.AddRange(
            [
                new("Status", 11), new("Blocked", 9), new("Issue", 18), new("Problem", 100), new("Source file", 40), new("Source row", 11),
                new("Identities", 30), new("Attempts", 10), new("Next attempt (UTC)", 20), new("Waiting for", 50), new("Document waiting", 18),
                new("Delivered (UTC)", 20), new("Verified (UTC)", 20), new("Verify", 10), new("OSDU id", 60), new("OSDU version", 18),
                new("Mapping", 24), new("Updated (UTC)", 20), new("Delivery key", 38),
            ]);
            return columns;
        }

        public List<XlsxCell> Cells(RecordState record, RecordExportHit? hit, string? flowName)
        {
            // The file and row the record's newest version came from: the queued version's while work waits, as the Records tab shows it.
            var origin = record.PendingSourceFileName is not null ? record.PendingOrigin : record.Origin;
            List<XlsxCell> cells = [XlsxCell.Text(Named(record)), XlsxCell.Text(record.SourceKey)];
            if (AcrossFlows)
            {
                cells.AddRange(
                [
                    XlsxCell.Text(hit?.FlowName ?? $"no longer synced: {record.FlowId:D}"),
                    XlsxCell.Text(hit?.Interface),
                    XlsxCell.Text(hit?.Partition ?? record.Partition),
                ]);
            }

            if (Matched)
            {
                cells.Add(XlsxCell.Text(hit is null ? null : string.Join("; ", hit.Matched)));
            }

            if (KeyColumns.Count > 0)
            {
                var parts = KeyParts(record, flowName);
                cells.AddRange(KeyColumns.Select((_, i) => XlsxCell.Text(i < parts.Count ? parts[i] : null)));
            }

            cells.AddRange(
            [
                XlsxCell.Text(StatusText(record.Status)),
                XlsxCell.Text(record.Blocked ? "yes" : null),
                XlsxCell.Text(record.ProblemHash is { } problem ? ProblemSignature.Format(problem) : null),
                XlsxCell.Text(record.LastError),
                XlsxCell.Text(origin.FileName),
                XlsxCell.Number(origin.RowNumber),
                XlsxCell.Text(record.Identities.Count == 0 ? null : string.Join("; ", record.Identities)),
                XlsxCell.Number(record.AttemptCount),
                XlsxCell.Instant(record.NextAttemptUtc),
                XlsxCell.Text(record.WaitingFor),
                XlsxCell.Text(record.PendingDocumentRef is null ? null : "yes"),
                XlsxCell.Instant(record.LastDeliveredUtc),
                XlsxCell.Instant(record.LastVerifiedUtc),
                XlsxCell.Text(record.LastVerifyOutcome?.ToString().ToLowerInvariant()),
                XlsxCell.Text(record.TargetId),
                // Sixteen digits: a spreadsheet number keeps fifteen, so the version is written as text.
                XlsxCell.Text(record.TargetVersion?.ToString(CultureInfo.InvariantCulture)),
                XlsxCell.Text(record.MappingName),
                XlsxCell.Instant(record.UpdatedUtc),
                XlsxCell.Text(record.DeliveryKey.Value.ToString("D")),
            ]);
            return cells;
        }

        public IEnumerable<(string Column, string Holds)> Notes()
        {
            yield return ("Record", "The record's label from its mapping, or its source key when the mapping gives none.");
            if (AcrossFlows)
            {
                yield return ("Flow, Interface, Partition", "The flow that delivers the record, the interface of it, and the partition its ledger is kept in.");
            }

            if (Matched)
            {
                yield return ("Matched", "The values of the record the lookup's term matched.");
            }

            if (KeyColumns.Count > 0)
            {
                yield return (string.Join(", ", KeyColumns), "The parts of the record's key, as the flow names its key columns: what finds the row in the source.");
            }

            yield return ("Status", "Where the record stands: pending, delivering, delivered, held, failed, deleted, reverted or waiting.");
            yield return ("Blocked", "yes while the record is kept from delivery until its row changes or it is released.");
            yield return ("Issue", "What every record refused for the same reason shares; the flow's Issues tab and 'sqlflow records issues' group the records by it.");
            yield return ("Problem", "The last error, or why the record is held.");
            yield return ("Source file, Source row", "The ingestion file and row the record's newest version came from: the queued version's while work waits.");
            yield return ("Identities", "The values of the columns the mapping declares as identities (a wellbore id, a well name).");
            yield return ("Next attempt (UTC)", "When a delivery that failed is tried again.");
            yield return ("Waiting for", "The OSDU id of the record this one waits for, while it waits.");
            yield return ("Document waiting", "yes while a rendered document waits to be sent.");
            yield return ("OSDU id, OSDU version", "Where the record is in OSDU, and the version the ledger last recorded; the version is text, since it has more digits than a spreadsheet number keeps.");
        }

        private static IReadOnlyList<string?> KeyParts(RecordState record, string? flowName)
        {
            if (record.SourceKeyJson is not { } json)
            {
                return [];
            }

            try
            {
                return KeyTuple.FromJson(json).Values;
            }
            catch (DeliveryException ex)
            {
                throw new DeliveryException(
                    $"The record {record.DeliveryKey.Value:D} of {flowName ?? record.FlowId.ToString("D")} holds a key that is not a JSON array of strings, so it cannot be exported: {ex.Message}",
                    ex);
            }
        }
    }
}

/// <summary>A record a lookup across every flow reached, with the flow that delivers it and the values the lookup's term matched.</summary>
public sealed record RecordExportHit(RecordState Record, string? FlowName, string? Interface, string? Partition, IReadOnlyList<string> Matched);

/// <summary>
/// What a lookup across every flow asked, for the About sheet and the file's name: the term (null for the newest records), the
/// status, the flow it was narrowed to, the partition it read, whether more records matched than the lookup reaches, how
/// the term matched, and the drift and held-step filters.
/// </summary>
public sealed record RecordLookupExport(
    string? Search, RecordStatus? Status, string? FlowName, string? Partition, bool More, SearchMode Mode = SearchMode.Prefix,
    LookupNarrowing Narrowing = default);
