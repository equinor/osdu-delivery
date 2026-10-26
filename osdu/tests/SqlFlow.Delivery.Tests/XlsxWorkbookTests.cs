using ExcelDataReader;
using SqlFlow.Delivery.Storage;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The workbook a record export is written as (osdu/docs/reference/cli/records.md, records export): Office Open XML a strict
/// reader accepts, written to a stream that cannot seek as the API's buffer is, with typed cells that read back as written,
/// a header row that stays in view and carries the filter, and text a cell cannot hold as it is still opening as typed.
/// </summary>
public sealed class XlsxWorkbookTests
{
    private static readonly DateTime At = new(2026, 10, 26, 9, 30, 15, DateTimeKind.Utc);

    [Fact]
    public void A_workbook_is_valid_office_open_xml_with_typed_cells_a_frozen_header_and_a_filter()
    {
        var bytes = Write(workbook =>
        {
            var sheet = workbook.AddSheet("Records", [new XlsxColumn("Name", 20), new XlsxColumn("Count", 8), new XlsxColumn("When (UTC)", 20)]);
            sheet.AddRow(XlsxCell.Text("first"), XlsxCell.Number(3), XlsxCell.Instant(At));
            sheet.AddRow(XlsxCell.Text("second"), XlsxCell.Empty, XlsxCell.Instant(null));
            sheet.AddRow(XlsxCell.Text("third"));
            Assert.Equal(3, sheet.Rows);
            var about = workbook.AddSheet("About", [new XlsxColumn("Field", 10), new XlsxColumn("Value", 40)], header: false);
            about.AddRow(XlsxCell.Text("Records", bold: true), XlsxCell.Number(3));
            Assert.Equal(1, about.Rows);
        });

        using var read = new WorkbookReading(bytes);
        Assert.Equal(["Records", "About"], read.SheetNames);
        Assert.Equal(["Name", "Count", "When (UTC)"], read.Headers());
        var records = read.Sheet("Records");
        Assert.True(records.Cell(1, 1).Style.Font.Bold);
        Assert.Equal(1, records.SheetView.SplitRow);
        Assert.Equal("A1:C4", records.AutoFilter.Range.RangeAddress.ToString());

        var rows = read.Rows();
        Assert.Equal(["first", "second", "third"], rows.Select(r => r["Name"].GetText()));
        Assert.Equal(3d, rows[0]["Count"].GetNumber());
        Assert.Equal(At, DateTime.SpecifyKind(rows[0]["When (UTC)"].GetDateTime(), DateTimeKind.Utc));
        Assert.True(rows[1]["Count"].IsBlank);
        Assert.True(rows[1]["When (UTC)"].IsBlank);
        Assert.True(rows[2]["Count"].IsBlank);

        // A sheet with no header row has no frozen row and no filter; its bold cells are bold.
        var about = read.Sheet("About");
        Assert.Equal(0, about.SheetView.SplitRow);
        Assert.False(about.AutoFilter.IsEnabled);
        Assert.True(about.Cell(1, 1).Style.Font.Bold);
        Assert.Equal(3d, about.Cell(1, 2).Value.GetNumber());
    }

    [Fact]
    public void An_empty_sheet_still_filters_its_header_row()
    {
        var bytes = Write(workbook => workbook.AddSheet("Records", [new XlsxColumn("Name", 20), new XlsxColumn("Count", 8)]));

        using var read = new WorkbookReading(bytes);
        Assert.Empty(read.Rows());
        Assert.Equal("A1:B1", read.Sheet("Records").AutoFilter.Range.RangeAddress.ToString());
    }

    [Theory]
    [InlineData("a\u0001b\u001Fc")]
    [InlineData("_x0041_ is typed, not an escape")]
    [InlineData("_X0041_ too, and _x00 is no escape at all_")]
    [InlineData("a tab\tand a line\nfeed")]
    [InlineData("\U0001F6E2 a barrel, as a surrogate pair")]
    [InlineData("  leading and trailing spaces  ")]
    [InlineData("=SUM(A1:A9) is text, never a formula")]
    public void Text_reads_back_as_it_was_written(string text)
        => Assert.Equal(text, RoundTrip(text));

    [Fact]
    public void Line_endings_are_written_as_excel_writes_them_and_half_a_surrogate_pair_as_the_replacement_character()
    {
        Assert.Equal("one\ntwo\nthree", RoundTrip("one\r\ntwo\rthree"));
        Assert.Equal("a�b�", RoundTrip("a\uD83Db\uDC00"));
    }

    [Fact]
    public void Text_past_what_a_cell_holds_is_cut_saying_so_and_never_through_a_surrogate_pair()
    {
        var cut = RoundTrip(new string('a', 40_000));
        Assert.Equal(XlsxWorkbook.MaxCellText, cut.Length);
        Assert.EndsWith(XlsxWorkbook.Cut, cut, StringComparison.Ordinal);

        // A pair straddling the cut is dropped whole rather than split.
        var keep = XlsxWorkbook.MaxCellText - XlsxWorkbook.Cut.Length;
        var straddling = RoundTrip(new string('a', keep - 1) + "\U0001F6E2" + new string('b', 100));
        Assert.Equal(new string('a', keep - 1) + XlsxWorkbook.Cut, straddling);
    }

    [Theory]
    [InlineData(0L, true)]
    [InlineData(999_999_999_999_999L, true)]
    [InlineData(-999_999_999_999_999L, true)]
    [InlineData(1_000_000_000_000_000L, false)]
    [InlineData(1_729_012_345_678_901L, false)]
    [InlineData(long.MinValue, false)]
    [InlineData(long.MaxValue, false)]
    public void A_number_past_what_a_spreadsheet_keeps_exactly_is_written_as_text(long value, bool number)
    {
        var bytes = Write(workbook => workbook.AddSheet("Numbers", [new XlsxColumn("Value", 24)]).AddRow(XlsxCell.Number(value)));
        using var read = new WorkbookReading(bytes);
        var cell = Assert.Single(read.Rows("Numbers"))["Value"];
        Assert.Equal(number, cell.IsNumber);
        Assert.Equal(value.ToString(System.Globalization.CultureInfo.InvariantCulture), number ? ((long)cell.GetNumber()).ToString(System.Globalization.CultureInfo.InvariantCulture) : cell.GetText());
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(51, "AZ")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void Columns_are_lettered_as_excel_letters_them(int index, string name)
        => Assert.Equal(name, XlsxWorkbook.ColumnName(index));

    [Fact]
    public void A_workbook_refuses_what_it_could_not_write_as_a_workbook()
    {
        using var output = new MemoryStream();
        using var workbook = new XlsxWorkbook(output);
        XlsxColumn[] one = [new XlsxColumn("Name", 10)];
        Assert.Throws<ArgumentException>(() => workbook.AddSheet("a/b", one));
        Assert.Throws<ArgumentException>(() => workbook.AddSheet(new string('x', 32), one));
        Assert.Throws<ArgumentException>(() => workbook.AddSheet("Empty", []));
        var first = workbook.AddSheet("Records", one);
        Assert.Throws<ArgumentException>(() => first.AddRow(XlsxCell.Text("a"), XlsxCell.Text("b")));
        Assert.Throws<ArgumentException>(() => workbook.AddSheet("RECORDS", one));

        // A sheet takes rows until the workbook moves on.
        workbook.AddSheet("About", one);
        Assert.Throws<InvalidOperationException>(() => first.AddRow(XlsxCell.Text("late")));
        workbook.Complete();
        Assert.Throws<ObjectDisposedException>(() => workbook.AddSheet("More", one));
    }

    [Fact]
    public void A_workbook_with_no_sheet_cannot_complete()
    {
        using var output = new MemoryStream();
        using var workbook = new XlsxWorkbook(output);
        Assert.Throws<InvalidOperationException>(workbook.Complete);
    }

    /// <summary>
    /// <paramref name="text"/> written to a cell and read back by ExcelDataReader, the reader SQLFlow's own xlsx sources read
    /// with, which decodes the escapes a cell's text may carry as Excel does (ClosedXML leaves them as written).
    /// </summary>
    private static string RoundTrip(string text)
    {
        var bytes = Write(workbook => workbook.AddSheet("Text", [new XlsxColumn("Value", 40)]).AddRow(XlsxCell.Text(text)));
        WorkbookReading.AssertValid(bytes);

        // ExcelDataReader reads its configuration's code page even for a workbook, as SQLFlow's xlsx source registers it.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        using var stream = new MemoryStream(bytes);
        using var reader = ExcelReaderFactory.CreateOpenXmlReader(stream);
        Assert.True(reader.Read());
        Assert.Equal("Value", reader.GetString(0));
        Assert.True(reader.Read());
        var read = reader.GetString(0);
        Assert.False(reader.Read());
        return read;
    }

    /// <summary>A workbook filled by <paramref name="fill"/> and completed, written to a stream that cannot seek.</summary>
    private static byte[] Write(Action<XlsxWorkbook> fill)
    {
        using var buffer = new MemoryStream();
        using (var workbook = new XlsxWorkbook(new ForwardOnly(buffer)))
        {
            fill(workbook);
            workbook.Complete();
        }

        return buffer.ToArray();
    }

    /// <summary>A stream that only writes forward, as the response buffer the API writes a workbook to does.</summary>
    private sealed class ForwardOnly(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
