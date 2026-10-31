using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace SqlFlow.Delivery.Storage;

/// <summary>
/// An Excel workbook (Office Open XML, <c>.xlsx</c>) written as it is filled, so a sheet of tens of thousands of rows is
/// never held whole: each sheet streams into its own part of the archive as its rows are added, and the parts that list
/// the sheets are written by <see cref="Complete"/>. It writes what a working list needs: text, whole numbers and UTC
/// times in typed cells, a bold header row that stays in view and carries a filter, fixed column widths, and a tint for
/// the cells and columns a reader is to look at (<see cref="XlsxTint"/>). Strings are written inline, so no table of them
/// is kept.
/// </summary>
/// <remarks>
/// A text cell never runs as a formula: only a formula element runs, and none is written. A value a cell cannot hold as
/// it is still opens: a character XML refuses is written as Excel's <c>_xHHHH_</c> escape, text that looks like that
/// escape keeps its underscore escaped, and text past <see cref="MaxCellText"/> characters is cut, saying so.
/// A workbook disposed before <see cref="Complete"/> is not a workbook; its caller discards what was written.
/// </remarks>
public sealed class XlsxWorkbook : IDisposable
{
    /// <summary>The media type of a workbook.</summary>
    public const string MediaType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>The most characters Excel holds in one cell.</summary>
    public const int MaxCellText = 32_767;

    /// <summary>What ends a text cut at <see cref="MaxCellText"/>.</summary>
    internal const string Cut = " ... (cut: a cell holds at most 32767 characters)";

    internal const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>The cell styles <c>styles.xml</c> declares, by their index.</summary>
    internal const int PlainStyle = 0;
    internal const int BoldStyle = 1;
    internal const int InstantStyle = 2;

    /// <summary>The first of the tinted styles: each tint plain, then each tint in bold.</summary>
    private const int FirstTintedStyle = 3;

    /// <summary>The fill each tint is, as ARGB, in the order of <see cref="XlsxTint"/>.</summary>
    private static readonly string[] TintColors = ["FFFFD966", "FFBDD7EE", "FFD9D9D9"];

    internal static readonly XmlWriterSettings Settings = new()
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        NewLineHandling = NewLineHandling.None,
        CloseOutput = false,
    };

    private readonly ZipArchive archive;
    private readonly List<XlsxSheet> sheets = [];
    private XlsxSheet? open;
    private bool completed;

    /// <summary>A workbook written to <paramref name="output"/>, which is left open; it need not be seekable.</summary>
    public XlsxWorkbook(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
    }

    /// <summary>
    /// Starts a sheet named <paramref name="name"/> with <paramref name="columns"/>, finishing the one before it. With
    /// <paramref name="header"/>, its first row is the columns' headers, in bold, frozen in view, and the filter covers it
    /// and every row after.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not one Excel takes, or another sheet has it; or there are no columns.</exception>
    public XlsxSheet AddSheet(string name, IReadOnlyList<XlsxColumn> columns, bool header = true)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(columns);
        ObjectDisposedException.ThrowIf(completed, this);
        if (name.Length is 0 or > 31 || name.IndexOfAny(['\\', '/', '?', '*', '[', ']', ':']) >= 0 || name.StartsWith('\'') || name.EndsWith('\''))
        {
            throw new ArgumentException($"'{name}' is not a sheet name: 1 to 31 characters, none of \\ / ? * [ ] :, and no apostrophe at either end.", nameof(name));
        }

        if (sheets.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"The workbook already has a sheet named '{name}'.", nameof(name));
        }

        if (columns.Count == 0)
        {
            throw new ArgumentException("A sheet needs at least one column.", nameof(columns));
        }

        open?.Finish();
        var entry = archive.CreateEntry($"xl/worksheets/sheet{sheets.Count + 1}.xml", CompressionLevel.Optimal);
        open = new XlsxSheet(name, columns, header, entry.Open());
        sheets.Add(open);
        return open;
    }

    /// <summary>Finishes the last sheet and writes the parts that make the archive a workbook. Nothing can be added after.</summary>
    /// <exception cref="InvalidOperationException">The workbook has no sheet.</exception>
    public void Complete()
    {
        ObjectDisposedException.ThrowIf(completed, this);
        if (sheets.Count == 0)
        {
            throw new InvalidOperationException("A workbook needs at least one sheet.");
        }

        open?.Finish();
        open = null;
        Part("[Content_Types].xml", ContentTypes);
        Part("_rels/.rels", w =>
        {
            w.WriteStartElement("Relationships", PackageRelationships);
            Relationship(w, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml");
            w.WriteEndElement();
        });
        Part("xl/workbook.xml", Workbook);
        Part("xl/_rels/workbook.xml.rels", w =>
        {
            w.WriteStartElement("Relationships", PackageRelationships);
            for (var i = 0; i < sheets.Count; i++)
            {
                Relationship(w, Id(i), "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", $"worksheets/sheet{i + 1}.xml");
            }

            Relationship(w, "rIdStyles", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml");
            w.WriteEndElement();
        });
        Part("xl/styles.xml", Styles);
        completed = true;
        archive.Dispose();
    }

    /// <summary>Closes the archive. A workbook not completed first is not a workbook.</summary>
    public void Dispose()
    {
        if (completed)
        {
            return;
        }

        completed = true;
        open?.Abandon();
        archive.Dispose();
    }

    /// <summary>
    /// <paramref name="value"/> as a cell's text: line endings as Excel writes them (a line feed), cut at
    /// <see cref="MaxCellText"/> characters, and every character XML cannot carry written as Excel's <c>_xHHHH_</c> escape.
    /// </summary>
    internal static string CellText(string value)
    {
        var text = value.ReplaceLineEndings("\n");
        if (text.Length > MaxCellText)
        {
            var keep = MaxCellText - Cut.Length;
            if (char.IsHighSurrogate(text[keep - 1]))
            {
                keep--;
            }

            text = string.Concat(text.AsSpan(0, keep), Cut);
        }

        StringBuilder? escaped = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            string? replacement = null;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                escaped?.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                // Half a pair is no character at all; the replacement character says one was there.
                replacement = "�";
            }
            else if (!XmlConvert.IsXmlChar(c))
            {
                replacement = Escape(c);
            }
            else if (c == '_' && LooksEscaped(text, i))
            {
                // Text that reads as an escape would be read back as the character it names; its underscore is escaped instead.
                replacement = Escape('_');
            }

            if (replacement is null)
            {
                escaped?.Append(c);
                continue;
            }

            escaped ??= new StringBuilder(text.Length + 16).Append(text, 0, i);
            escaped.Append(replacement);
        }

        return escaped?.ToString() ?? text;
    }

    /// <summary>The letters of a column, from zero: A to Z, then AA.</summary>
    internal static string ColumnName(int index)
    {
        var name = new StringBuilder();
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            name.Insert(0, (char)('A' + ((n - 1) % 26)));
        }

        return name.ToString();
    }

    private static string Escape(char c) => string.Create(CultureInfo.InvariantCulture, $"_x{(int)c:X4}_");

    /// <summary>Whether the text at <paramref name="at"/> is <c>_xHHHH_</c>, which a reader would take for an escape.</summary>
    private static bool LooksEscaped(string text, int at)
        => at + 6 < text.Length
            && text[at + 1] is 'x' or 'X'
            && char.IsAsciiHexDigit(text[at + 2]) && char.IsAsciiHexDigit(text[at + 3]) && char.IsAsciiHexDigit(text[at + 4]) && char.IsAsciiHexDigit(text[at + 5])
            && text[at + 6] == '_';

    private static string Id(int sheet) => string.Create(CultureInfo.InvariantCulture, $"rId{sheet + 1}");

    private void Part(string name, Action<XmlWriter> write)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, Settings);
        writer.WriteStartDocument(standalone: true);
        write(writer);
        writer.WriteEndDocument();
    }

    private void ContentTypes(XmlWriter w)
    {
        const string types = "http://schemas.openxmlformats.org/package/2006/content-types";
        w.WriteStartElement("Types", types);
        Default(w, types, "rels", "application/vnd.openxmlformats-package.relationships+xml");
        Default(w, types, "xml", "application/xml");
        Override(w, types, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        Override(w, types, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
        for (var i = 0; i < sheets.Count; i++)
        {
            Override(w, types, $"/xl/worksheets/sheet{i + 1}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
        }

        w.WriteEndElement();

        static void Default(XmlWriter w, string ns, string extension, string type)
        {
            w.WriteStartElement("Default", ns);
            w.WriteAttributeString("Extension", extension);
            w.WriteAttributeString("ContentType", type);
            w.WriteEndElement();
        }

        static void Override(XmlWriter w, string ns, string part, string type)
        {
            w.WriteStartElement("Override", ns);
            w.WriteAttributeString("PartName", part);
            w.WriteAttributeString("ContentType", type);
            w.WriteEndElement();
        }
    }

    private void Workbook(XmlWriter w)
    {
        w.WriteStartElement("workbook", Main);
        w.WriteAttributeString("xmlns", "r", null, Relationships);
        w.WriteStartElement("sheets", Main);
        for (var i = 0; i < sheets.Count; i++)
        {
            w.WriteStartElement("sheet", Main);
            w.WriteAttributeString("name", sheets[i].Name);
            w.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("id", Relationships, Id(i));
            w.WriteEndElement();
        }

        w.WriteEndElement();

        // A sheet's filter is named as Excel names one it makes, so the filter buttons work in every reader.
        var filtered = sheets.Select((s, i) => (Sheet: s, Index: i)).Where(s => s.Sheet.FilterRange is not null).ToList();
        if (filtered.Count > 0)
        {
            w.WriteStartElement("definedNames", Main);
            foreach (var (sheet, index) in filtered)
            {
                w.WriteStartElement("definedName", Main);
                w.WriteAttributeString("name", "_xlnm._FilterDatabase");
                w.WriteAttributeString("localSheetId", index.ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("hidden", "1");
                w.WriteString($"'{sheet.Name.Replace("'", "''", StringComparison.Ordinal)}'!{sheet.FilterRange}");
                w.WriteEndElement();
            }

            w.WriteEndElement();
        }

        w.WriteEndElement();
    }

    private static void Styles(XmlWriter w)
    {
        w.WriteStartElement("styleSheet", Main);
        w.WriteStartElement("numFmts", Main);
        w.WriteAttributeString("count", "1");
        w.WriteStartElement("numFmt", Main);
        w.WriteAttributeString("numFmtId", "164");
        w.WriteAttributeString("formatCode", "yyyy-mm-dd hh:mm:ss");
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("fonts", Main);
        w.WriteAttributeString("count", "2");
        Font(w, bold: false);
        Font(w, bold: true);
        w.WriteEndElement();

        w.WriteStartElement("fills", Main);
        w.WriteAttributeString("count", (2 + TintColors.Length).ToString(CultureInfo.InvariantCulture));
        Fill(w, "none");
        Fill(w, "gray125");
        foreach (var color in TintColors)
        {
            Tint(w, color);
        }

        w.WriteEndElement();

        w.WriteStartElement("borders", Main);
        w.WriteAttributeString("count", "1");
        w.WriteStartElement("border", Main);
        foreach (var side in new[] { "left", "right", "top", "bottom", "diagonal" })
        {
            w.WriteElementString(side, Main, string.Empty);
        }

        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("cellStyleXfs", Main);
        w.WriteAttributeString("count", "1");
        Format(w, numberFormat: 0, font: 0, inStyle: false);
        w.WriteEndElement();

        // In the order of PlainStyle, BoldStyle and InstantStyle, then each tint plain and each tint in bold (TintedStyle).
        w.WriteStartElement("cellXfs", Main);
        w.WriteAttributeString("count", (FirstTintedStyle + (TintColors.Length * 2)).ToString(CultureInfo.InvariantCulture));
        Format(w, numberFormat: 0, font: 0, inStyle: true);
        Format(w, numberFormat: 0, font: 1, inStyle: true);
        Format(w, numberFormat: 164, font: 0, inStyle: true);
        foreach (var font in new[] { 0, 1 })
        {
            for (var tint = 0; tint < TintColors.Length; tint++)
            {
                Format(w, numberFormat: 0, font, inStyle: true, fill: 2 + tint);
            }
        }

        w.WriteEndElement();

        w.WriteStartElement("cellStyles", Main);
        w.WriteAttributeString("count", "1");
        w.WriteStartElement("cellStyle", Main);
        w.WriteAttributeString("name", "Normal");
        w.WriteAttributeString("xfId", "0");
        w.WriteAttributeString("builtinId", "0");
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();

        static void Font(XmlWriter w, bool bold)
        {
            w.WriteStartElement("font", Main);
            if (bold)
            {
                w.WriteElementString("b", Main, string.Empty);
            }

            w.WriteStartElement("sz", Main);
            w.WriteAttributeString("val", "11");
            w.WriteEndElement();
            w.WriteStartElement("name", Main);
            w.WriteAttributeString("val", "Calibri");
            w.WriteEndElement();
            w.WriteStartElement("family", Main);
            w.WriteAttributeString("val", "2");
            w.WriteEndElement();
            w.WriteEndElement();
        }

        static void Fill(XmlWriter w, string pattern)
        {
            w.WriteStartElement("fill", Main);
            w.WriteStartElement("patternFill", Main);
            w.WriteAttributeString("patternType", pattern);
            w.WriteEndElement();
            w.WriteEndElement();
        }

        static void Tint(XmlWriter w, string color)
        {
            w.WriteStartElement("fill", Main);
            w.WriteStartElement("patternFill", Main);
            w.WriteAttributeString("patternType", "solid");
            w.WriteStartElement("fgColor", Main);
            w.WriteAttributeString("rgb", color);
            w.WriteEndElement();
            w.WriteStartElement("bgColor", Main);
            w.WriteAttributeString("indexed", "64");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
        }

        static void Format(XmlWriter w, int numberFormat, int font, bool inStyle, int fill = 0)
        {
            w.WriteStartElement("xf", Main);
            w.WriteAttributeString("numFmtId", numberFormat.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("fontId", font.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("fillId", fill.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("borderId", "0");
            if (inStyle)
            {
                w.WriteAttributeString("xfId", "0");
                if (font != 0)
                {
                    w.WriteAttributeString("applyFont", "1");
                }

                if (numberFormat != 0)
                {
                    w.WriteAttributeString("applyNumberFormat", "1");
                }

                if (fill != 0)
                {
                    w.WriteAttributeString("applyFill", "1");
                }
            }

            w.WriteEndElement();
        }
    }

    /// <summary>The style of text in <paramref name="tint"/>, in bold when asked.</summary>
    internal static int TintedStyle(XlsxTint tint, bool bold)
        => tint == XlsxTint.None
            ? (bold ? BoldStyle : PlainStyle)
            : FirstTintedStyle + (int)tint - 1 + (bold ? TintColors.Length : 0);

    private static void Relationship(XmlWriter w, string id, string type, string target)
    {
        w.WriteStartElement("Relationship", PackageRelationships);
        w.WriteAttributeString("Id", id);
        w.WriteAttributeString("Type", type);
        w.WriteAttributeString("Target", target);
        w.WriteEndElement();
    }

}

/// <summary>A sheet of the workbook, open for rows until the workbook starts the next or completes.</summary>
public sealed class XlsxSheet
{
    private readonly IReadOnlyList<XlsxColumn> columns;
    private readonly bool header;
    private readonly Stream stream;
    private readonly XmlWriter writer;
    private int row;
    private bool finished;

    internal XlsxSheet(string name, IReadOnlyList<XlsxColumn> columns, bool header, Stream stream)
    {
        Name = name;
        this.columns = columns;
        this.header = header;
        this.stream = stream;
        writer = XmlWriter.Create(stream, XlsxWorkbook.Settings);
        writer.WriteStartDocument(standalone: true);
        writer.WriteStartElement("worksheet", XlsxWorkbook.Main);
        if (header)
        {
            // The header row stays in view however far the rows below it scroll.
            writer.WriteStartElement("sheetViews", XlsxWorkbook.Main);
            writer.WriteStartElement("sheetView", XlsxWorkbook.Main);
            writer.WriteAttributeString("workbookViewId", "0");
            writer.WriteStartElement("pane", XlsxWorkbook.Main);
            writer.WriteAttributeString("ySplit", "1");
            writer.WriteAttributeString("topLeftCell", "A2");
            writer.WriteAttributeString("activePane", "bottomLeft");
            writer.WriteAttributeString("state", "frozen");
            writer.WriteEndElement();
            writer.WriteStartElement("selection", XlsxWorkbook.Main);
            writer.WriteAttributeString("pane", "bottomLeft");
            writer.WriteAttributeString("activeCell", "A2");
            writer.WriteAttributeString("sqref", "A2");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteStartElement("cols", XlsxWorkbook.Main);
        for (var i = 0; i < columns.Count; i++)
        {
            var number = (i + 1).ToString(CultureInfo.InvariantCulture);
            writer.WriteStartElement("col", XlsxWorkbook.Main);
            writer.WriteAttributeString("min", number);
            writer.WriteAttributeString("max", number);
            writer.WriteAttributeString("width", columns[i].Width.ToString("0.##", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("customWidth", "1");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteStartElement("sheetData", XlsxWorkbook.Main);
        if (header)
        {
            Write(columns.Select(c => XlsxCell.Text(c.Header, bold: true, tint: c.Tint)).ToList());
        }
    }

    /// <summary>The sheet's name, as its tab shows it.</summary>
    public string Name { get; }

    /// <summary>The rows added, the header row left out.</summary>
    public int Rows => header ? row - 1 : row;

    /// <summary>The cells the filter covers, as a reference (<c>$A$1:$F$20</c>); null for a sheet with no header row, or before it is finished.</summary>
    internal string? FilterRange { get; private set; }

    /// <summary>Adds a row: one cell for each column, from the first; columns past the last cell are left empty.</summary>
    /// <exception cref="ArgumentException">More cells than the sheet has columns.</exception>
    /// <exception cref="InvalidOperationException">The workbook has moved on to another sheet, or completed.</exception>
    public void AddRow(params IReadOnlyList<XlsxCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (finished)
        {
            throw new InvalidOperationException($"The sheet '{Name}' is finished: the workbook has started another sheet or completed.");
        }

        if (cells.Count > columns.Count)
        {
            throw new ArgumentException($"The sheet '{Name}' has {columns.Count} columns, and the row has {cells.Count} cells.", nameof(cells));
        }

        Write(cells);
    }

    internal void Finish()
    {
        if (finished)
        {
            return;
        }

        finished = true;
        writer.WriteEndElement();
        if (header)
        {
            var last = XlsxWorkbook.ColumnName(columns.Count - 1);
            var bottom = Math.Max(row, 1).ToString(CultureInfo.InvariantCulture);
            writer.WriteStartElement("autoFilter", XlsxWorkbook.Main);
            writer.WriteAttributeString("ref", $"A1:{last}{bottom}");
            writer.WriteEndElement();
            FilterRange = $"$A$1:${last}${bottom}";
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Dispose();
        stream.Dispose();
    }

    /// <summary>Closes the sheet's part without finishing it, for a workbook given up part way.</summary>
    internal void Abandon()
    {
        if (finished)
        {
            return;
        }

        finished = true;
        writer.Dispose();
        stream.Dispose();
    }

    private void Write(IReadOnlyList<XlsxCell> cells)
    {
        row++;
        var number = row.ToString(CultureInfo.InvariantCulture);
        writer.WriteStartElement("row", XlsxWorkbook.Main);
        writer.WriteAttributeString("r", number);
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (cell.IsEmpty)
            {
                continue;
            }

            writer.WriteStartElement("c", XlsxWorkbook.Main);
            writer.WriteAttributeString("r", XlsxWorkbook.ColumnName(i) + number);
            switch (cell.Kind)
            {
                case XlsxCellKind.Text:
                    if (cell.Style != XlsxWorkbook.PlainStyle)
                    {
                        writer.WriteAttributeString("s", cell.Style.ToString(CultureInfo.InvariantCulture));
                    }

                    writer.WriteAttributeString("t", "inlineStr");
                    writer.WriteStartElement("is", XlsxWorkbook.Main);
                    writer.WriteStartElement("t", XlsxWorkbook.Main);
                    writer.WriteAttributeString("xml", "space", null, "preserve");
                    writer.WriteString(XlsxWorkbook.CellText(cell.TextValue!));
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                    break;
                case XlsxCellKind.Instant:
                    writer.WriteAttributeString("s", XlsxWorkbook.InstantStyle.ToString(CultureInfo.InvariantCulture));
                    writer.WriteElementString("v", XlsxWorkbook.Main, cell.NumberValue.ToString("R", CultureInfo.InvariantCulture));
                    break;
                default:
                    writer.WriteElementString("v", XlsxWorkbook.Main, cell.NumberValue.ToString("R", CultureInfo.InvariantCulture));
                    break;
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }
}

/// <summary>A fill that makes a cell or a column stand out from the rest of a sheet.</summary>
public enum XlsxTint
{
    None,
    Amber,
    Blue,
    Grey,
}

/// <summary>A column of a sheet: its header, its width in characters of the default font, and the tint of its header.</summary>
public sealed record XlsxColumn(string Header, double Width, XlsxTint Tint = XlsxTint.None);

/// <summary>What a cell holds.</summary>
public enum XlsxCellKind
{
    Text,
    Number,
    Instant,
}

/// <summary>One cell of a row: text, a number or a UTC time, or nothing.</summary>
public readonly record struct XlsxCell
{
    private XlsxCell(XlsxCellKind kind, string? text, double number, int style, bool empty)
    {
        Kind = kind;
        TextValue = text;
        NumberValue = number;
        Style = style;
        IsEmpty = empty;
    }

    /// <summary>A cell left empty.</summary>
    public static XlsxCell Empty { get; } = new(XlsxCellKind.Text, null, 0, XlsxWorkbook.PlainStyle, empty: true);

    public XlsxCellKind Kind { get; }

    /// <summary>A text cell's text; null for any other.</summary>
    public string? TextValue { get; }

    /// <summary>A number cell's number, or an instant's serial day; zero for a text cell.</summary>
    public double NumberValue { get; }

    /// <summary>Whether the cell is left empty: no value was given.</summary>
    public bool IsEmpty { get; }

    internal int Style { get; }

    /// <summary>Text, in bold and in a tint when asked; empty for null or an empty string.</summary>
    public static XlsxCell Text(string? value, bool bold = false, XlsxTint tint = XlsxTint.None)
        => string.IsNullOrEmpty(value) ? Empty : new(XlsxCellKind.Text, value, 0, XlsxWorkbook.TintedStyle(tint, bold), empty: false);

    /// <summary>A whole number; empty for null. Excel holds 15 significant digits, so a larger identifier is written as text.</summary>
    public static XlsxCell Number(long? value) => value switch
    {
        null => Empty,
        > -1_000_000_000_000_000 and < 1_000_000_000_000_000 => new(XlsxCellKind.Number, null, value.Value, XlsxWorkbook.PlainStyle, empty: false),
        _ => Text(value.Value.ToString(CultureInfo.InvariantCulture)),
    };

    /// <summary>A UTC time, shown as <c>yyyy-mm-dd hh:mm:ss</c>; empty for null.</summary>
    public static XlsxCell Instant(DateTime? value) => value is { } at
        ? new(XlsxCellKind.Instant, null, DateTime.SpecifyKind(at, DateTimeKind.Unspecified).ToOADate(), XlsxWorkbook.InstantStyle, empty: false)
        : Empty;
}
