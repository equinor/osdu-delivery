using System.Globalization;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Engine.Checks;

/// <summary>
/// The rows a value check found wrong with an attribute, as an Excel workbook (osdu/docs/reference/concepts/gui.md#mappings):
/// a list to fix them from, with nothing left to look up. The <c>Issues</c> sheet is a row for each issue as the check meets
/// it: the record, what it came to and why, then where to fix it (the file, the row and the column, and what the column holds
/// there), then the base data behind it, the record's key and the source columns the attribute reads with what this row holds
/// in them (for an item of a repeated array, its child row). Colour connects the two: amber is what to fix, on the columns
/// that say where and on the cell itself among the base data. <c>Summary</c> adds the issues up by reason, the largest first,
/// and <c>About</c> says what was checked, with what, and what each column and colour means. Give <see cref="Add"/> to the
/// check as its <c>each</c>.
/// </summary>
/// <remarks>
/// The sheet streams into the output as the check runs, so the most it holds is the file itself, and that is bounded: once
/// the file has reached the bytes it may take, or a sheet has no row left, an issue is counted and not listed, and the About
/// sheet says how many went. Every count stays exact, so a file cut short still says what it is short of. A workbook disposed
/// before <see cref="Complete"/> is not a workbook; its caller discards what was written.
/// </remarks>
public sealed class ValueIssuesWorkbook : IDisposable
{
    /// <summary>The sheet with a row for each issue.</summary>
    public const string IssuesSheet = "Issues";

    /// <summary>The sheet adding the issues up by reason.</summary>
    public const string SummarySheet = "Summary";

    /// <summary>The sheet saying what was checked.</summary>
    public const string AboutSheet = RecordExport.AboutSheet;

    /// <summary>The rows a sheet holds under its header.</summary>
    internal const int MaxRows = 1_048_575;

    /// <summary>The values of a child dataset a value of the record names, which reads all of its rows.</summary>
    private const int MaxChildValues = 10;

    private static readonly IReadOnlyList<XlsxColumn> LeadingColumns =
    [
        new("Record", 36), new("Item", 7), new("Outcome", 16), new("Reason", 70),
        new("Source file", 40, XlsxTint.Amber), new("Source row", 11, XlsxTint.Amber), new("Source column", 34, XlsxTint.Amber), new("Source value", 34, XlsxTint.Amber),
        new("Value written", 34),
    ];

    private static readonly IReadOnlyList<XlsxColumn> TrailingColumns =
    [
        new("Rule", 26), new("Attribute", 46), new("Inside", 30), new("Source key", 36), new("Delivery key", 38),
    ];

    private static readonly IReadOnlyList<XlsxColumn> SummaryColumns =
    [
        new("Attribute", 46), new("Inside", 30), new("Outcome", 16), new("Rule", 26), new("On failure", 12), new("Reason", 80),
        new("Issues", 9), new("Rows", 9), new("Values", 60),
    ];

    private static readonly IReadOnlyList<(string Column, string Holds, XlsxTint Tint)> Notes =
    [
        ("Amber", "What to fix: the columns saying where, and the cell itself among the base data. An amber cell can be filtered on by its colour.", XlsxTint.Amber),
        ("Blue", "The source columns the attribute reads: what this row holds in them.", XlsxTint.Blue),
        ("Grey", "The record's key columns, which find the row in the source.", XlsxTint.Grey),
        ("Record", "The record's label from its mapping, or its source key when the mapping gives none.", XlsxTint.None),
        ("Item", "For an item of a repeated array, the child row it was written from, counting from 1.", XlsxTint.None),
        ("Outcome", "Held: the record is not delivered. Invalid: a value breaking a rule of the template is sent. Left out: the record is sent without the attribute. Assertion failed: a value fails an assertion of the mapping, and On failure on the Summary sheet says what that does.", XlsxTint.None),
        ("Reason", "Why, as this record states it.", XlsxTint.None),
        ("Source file, Source row", "The file the row was landed from and its row there: for an item of a repeated array, those of its child row.", XlsxTint.None),
        ("Source column, Source value", "The column to fix, named as the mapping names it, and what the row holds in it: the column the reason names, else the one holding the value, else the attribute's only column, else those it reads that are empty. Empty when none of these says which.", XlsxTint.None),
        ("Value written", "The value written to the attribute, or the value that caused the reason, cut at 300 characters.", XlsxTint.None),
        ("dataset.<column>", "The base data, named as the mapping names it. A column of a child dataset (dataset.<child>.<column>) is read from the item's child row.", XlsxTint.None),
        ("Rule", "For an invalid value, the template rule it breaks; for an assertion failed, the assertion.", XlsxTint.None),
        ("Attribute", "The attribute checked, as a template path.", XlsxTint.None),
        ("Inside", "Where the reason is, when it is a property inside the value written to the attribute.", XlsxTint.None),
        ("Source key, Delivery key", "The record's key in the source, and in the ledger when every part of its key has a value.", XlsxTint.None),
        ("On failure", "On the Summary sheet, what a failure of the assertion does: hold, report or omit.", XlsxTint.None),
        ("Values", "On the Summary sheet, the values behind the reason, the most frequent first.", XlsxTint.None),
    ];

    private readonly MemoryStream output;
    private readonly long maxBytes;
    private readonly ValueIssuesBaseData baseData;
    private readonly XlsxWorkbook workbook;
    private readonly XlsxSheet issues;
    private long found;

    /// <summary>
    /// A workbook written to <paramref name="output"/>, which is left open, listing issues with the source columns of
    /// <paramref name="baseData"/> until the file reaches <paramref name="maxBytes"/>.
    /// </summary>
    public ValueIssuesWorkbook(MemoryStream output, long maxBytes, ValueIssuesBaseData baseData)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(baseData);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        this.output = output;
        this.maxBytes = maxBytes;
        this.baseData = baseData;
        workbook = new XlsxWorkbook(output);
        issues = workbook.AddSheet(
            IssuesSheet,
            [
                .. LeadingColumns,
                .. baseData.Keys.Select(c => BaseColumn(c, XlsxTint.Grey)),
                .. baseData.Reads.Select(c => BaseColumn(c, XlsxTint.Blue)),
                .. TrailingColumns,
            ]);
    }

    /// <summary>Every issue met so far.</summary>
    public long Found => found;

    /// <summary>The issues the Issues sheet holds.</summary>
    public long Listed => issues.Rows;

    /// <summary>Counts one issue, and lists it while the file has room.</summary>
    public void Add(ValueCheckOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        found++;
        if (issues.Rows >= MaxRows || output.Length >= maxBytes)
        {
            return;
        }

        var record = occurrence.Record;
        var toFix = record.Columns ?? [];
        var fixValues = toFix.Select(name => Shown(CellNamed(name, occurrence))).ToList();
        issues.AddRow(
        [
            XlsxCell.Text(string.IsNullOrEmpty(record.Label) ? record.SourceKey : record.Label),
            XlsxCell.Number(record.Item),
            XlsxCell.Text(OutcomeWord(occurrence.Outcome)),
            XlsxCell.Text(occurrence.Message),
            XlsxCell.Text(record.File),
            XlsxCell.Number(record.Row),
            XlsxCell.Text(string.Join(", ", toFix)),
            XlsxCell.Text(string.Join("; ", fixValues)),
            XlsxCell.Text(occurrence.Value),
            .. baseData.Keys.Select(column => XlsxCell.Text(BaseValue(column, occurrence))),
            .. baseData.Reads.Select(column => toFix.Contains(column.ToString(), StringComparer.OrdinalIgnoreCase)
                ? XlsxCell.Text(Shown(BaseValue(column, occurrence)), tint: XlsxTint.Amber)
                : XlsxCell.Text(BaseValue(column, occurrence))),
            XlsxCell.Text(occurrence.Rule),
            XlsxCell.Text(occurrence.Target),
            XlsxCell.Text(occurrence.At == occurrence.Target ? null : occurrence.At),
            XlsxCell.Text(record.SourceKey),
            XlsxCell.Text(record.DeliveryKey?.ToString("D")),
        ]);
    }

    /// <summary>Writes what <paramref name="check"/> adds up to and closes the file. Nothing can be added after.</summary>
    public void Complete(ValueCheck check, FlowDefinition flow, DateTime exportedUtc)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(flow);
        WriteSummary(check);
        WriteAbout(check, flow, exportedUtc);
        workbook.Complete();
    }

    public void Dispose() => workbook.Dispose();

    /// <summary>An outcome in the words the data check's line says it in.</summary>
    internal static string OutcomeWord(string outcome) => outcome switch
    {
        "held" => "Held",
        "invalid" => "Invalid",
        "empty" => "Left out",
        "asserted" => "Assertion failed",
        _ => outcome,
    };

    private static XlsxColumn BaseColumn(DatasetColumn column, XlsxTint tint)
        => new(column.ToString(), Math.Clamp(column.ToString().Length + 4, 14, 40), tint);

    /// <summary>A value as a cell to fix says it: one with nothing in it still has to read as the cell it is.</summary>
    private static string Shown(string? value)
        => value is null ? "(empty)" : value.Trim().Length == 0 ? "(blank)" : value;

    private string? CellNamed(string name, ValueCheckOccurrence occurrence)
        => baseData.Reads.FirstOrDefault(column => string.Equals(column.ToString(), name, StringComparison.OrdinalIgnoreCase)) is { } column ? BaseValue(column, occurrence) : null;

    /// <summary>
    /// What the row holds in a source column: for an item, the child row it was written from; for a value of the record that
    /// reads a child dataset, what the dataset's rows hold, since it reads them all.
    /// </summary>
    private static string? BaseValue(DatasetColumn column, ValueCheckOccurrence occurrence)
    {
        if (occurrence.Source is not { } source)
        {
            return null;
        }

        if (column.Child is not { } child)
        {
            return source.Row.GetString(column.Column);
        }

        if (occurrence.Record.Item is not null && string.Equals(occurrence.Child, child, StringComparison.OrdinalIgnoreCase))
        {
            return ValueTally.CellOf(column, source, occurrence.Record.Item, child);
        }

        var held = source.ScopeRows(child).Select(row => row.GetString(column.Column)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        return held.Count == 0
            ? null
            : string.Join("; ", held.Take(MaxChildValues)) + (held.Count > MaxChildValues ? string.Create(CultureInfo.InvariantCulture, $"; and {held.Count - MaxChildValues} more") : string.Empty);
    }

    private void WriteSummary(ValueCheck check)
    {
        var lines = check.Variables
            .SelectMany(v => v.Findings
                .Where(f => f.Outcome != "notApplicable")
                .Select(f => new SummaryLine(v.Target, f.At == v.Target ? null : f.At, OutcomeWord(f.Outcome), f.Rule, f.OnFail, f.Message, f.Count, f.Rows, ValuesText(f))))
            .Concat(check.Variables
                .Where(v => v.Unlisted > 0)
                .Select(v => new SummaryLine(v.Target, null, "Not listed", null, null, "More occurrences, under reasons the check does not name one by one", v.Unlisted, null, null)));

        var summary = workbook.AddSheet(SummarySheet, SummaryColumns);
        foreach (var line in lines.OrderByDescending(l => l.Issues))
        {
            summary.AddRow(
                XlsxCell.Text(line.Attribute),
                XlsxCell.Text(line.Inside),
                XlsxCell.Text(line.Outcome),
                XlsxCell.Text(line.Rule),
                XlsxCell.Text(line.OnFail),
                XlsxCell.Text(line.Reason),
                XlsxCell.Number(line.Issues),
                XlsxCell.Number(line.Rows),
                XlsxCell.Text(line.Values));
        }
    }

    private void WriteAbout(ValueCheck check, FlowDefinition flow, DateTime exportedUtc)
    {
        var rows = check.Rows;
        var scope = check.Asked.Values.Count == 0 ? null : string.Join(", ", check.Asked.Values.Select(v => $"{v.Key}={v.Value}"));
        var read = rows.Complete
            ? "the whole scope"
            : string.Create(CultureInfo.InvariantCulture, $"the first {rows.Read:N0}; the scope holds about {rows.ScopeRecords ?? rows.Read:N0}");
        var listed = issues.Rows == found
            ? "every one"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"the first {issues.Rows:N0}: the workbook holds no more. Every count here is exact. Check fewer rows to list the rest, or use 'sqlflow values --rows', which lists every one.");

        List<(string Field, XlsxCell Value)> facts =
        [
            ("Attribute", XlsxCell.Text(check.Asked.Targets.Count == 0 ? "every attribute of the mapping" : string.Join(", ", check.Asked.Targets))),
            ("Flow", XlsxCell.Text(flow.Name)),
            ("Interface", XlsxCell.Text(flow.Interface)),
            ("Partition", XlsxCell.Text(flow.Partition)),
            ("Mapping", XlsxCell.Text(check.Inputs.Mapping)),
            ("Kind", XlsxCell.Text(check.Inputs.Kind)),
            ("Template version", XlsxCell.Text(check.Inputs.TemplateVersion)),
            ("Cache version", XlsxCell.Text(check.Inputs.CacheVersion)),
            ("Record table", XlsxCell.Text(flow.Source.Record.Object)),
            .. baseData.Reads.Select(column => column.Child).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(child => ($"Table of {child}", XlsxCell.Text(flow.Source.Datasets.TryGetValue(child, out var dataset) ? dataset.Object : null))),
            ("Scope", XlsxCell.Text(scope)),
            ("Rows checked", XlsxCell.Number(rows.Checked)),
            ("Rows read", XlsxCell.Text(read)),
            ("Issues", XlsxCell.Number(found)),
            ("Listed", XlsxCell.Text(listed)),
            ("Exported (UTC)", XlsxCell.Instant(exportedUtc)),
        ];
        facts.AddRange(check.Issues.Select(issue => ("Preflight warning", XlsxCell.Text(issue))));

        var about = workbook.AddSheet(AboutSheet, [new XlsxColumn("Field", 22), new XlsxColumn("Value", 110)], header: false);
        foreach (var (field, value) in facts.Where(f => !f.Value.IsEmpty))
        {
            about.AddRow(XlsxCell.Text(field, bold: true), value);
        }

        about.AddRow();
        about.AddRow(XlsxCell.Text("Column", bold: true), XlsxCell.Text("What it holds", bold: true));
        foreach (var (column, holds, tint) in Notes)
        {
            about.AddRow(XlsxCell.Text(column, tint: tint), XlsxCell.Text(holds));
        }
    }

    /// <summary>The values behind a finding, the most frequent first, and how many more there are.</summary>
    private static string? ValuesText(ValueCheckFinding finding)
    {
        if (finding.Values.Count == 0)
        {
            return null;
        }

        var shown = string.Join(", ", finding.Values.Select(v => string.Create(CultureInfo.InvariantCulture, $"{v.Value} ({v.Count:N0})")));
        return finding.OtherValues > 0 ? string.Create(CultureInfo.InvariantCulture, $"{shown}, and {finding.OtherValues:N0} others") : shown;
    }

    private sealed record SummaryLine(string? Attribute, string? Inside, string Outcome, string? Rule, string? OnFail, string Reason, long Issues, long? Rows, string? Values);
}

/// <summary>
/// The source columns a workbook lists for the attributes it is of: the record's key, which finds the row, and each column the
/// entries it evaluates read. A key column an entry reads is listed among what it reads.
/// </summary>
public sealed record ValueIssuesBaseData(IReadOnlyList<DatasetColumn> Keys, IReadOnlyList<DatasetColumn> Reads)
{
    /// <summary>The columns of the entries <paramref name="selection"/> evaluates, as a check evaluates them.</summary>
    public static ValueIssuesBaseData Of(FlowDefinition flow, MappingDefinition mapping, EntrySelection selection)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(selection);
        var reads = mapping.Entries
            .Where(e => e.IsRepeater ? selection.Covers(e.Target.Text) : selection.Includes(e.Target.Text))
            .SelectMany(e => e.Columns)
            .DistinctBy(column => column.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var keys = flow.Source.Record.Key
            .Select(key => new DatasetColumn(null, key))
            .Where(key => !reads.Exists(column => string.Equals(column.ToString(), key.ToString(), StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return new ValueIssuesBaseData(keys, reads);
    }
}
