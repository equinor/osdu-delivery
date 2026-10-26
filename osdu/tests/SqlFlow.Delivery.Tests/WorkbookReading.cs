using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using SqlFlow.Delivery.Engine;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A workbook a record export wrote, read back as a person reads it: the Records sheet's rows by their headers, and the
/// About sheet's facts and column notes. Read with ClosedXML, after the Open XML SDK's validator has found nothing wrong
/// with it, so the file is proven against readers other than its writer.
/// </summary>
public sealed class WorkbookReading : IDisposable
{
    private readonly XLWorkbook workbook;

    public WorkbookReading(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        AssertValid(bytes);
        workbook = new XLWorkbook(new MemoryStream(bytes));
    }

    public IReadOnlyList<string> SheetNames => workbook.Worksheets.Select(w => w.Name).ToList();

    /// <summary>The sheet's header row.</summary>
    public IReadOnlyList<string> Headers(string sheet = RecordExport.RecordsSheet)
    {
        var worksheet = workbook.Worksheet(sheet);
        var last = worksheet.LastColumnUsed()?.ColumnNumber() ?? 0;
        return Enumerable.Range(1, last).Select(c => worksheet.Cell(1, c).Value.GetText()).ToList();
    }

    /// <summary>Every row under the header, each cell's value by the header of its column.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, XLCellValue>> Rows(string sheet = RecordExport.RecordsSheet)
    {
        var worksheet = workbook.Worksheet(sheet);
        var headers = Headers(sheet);
        var last = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        return Enumerable.Range(2, Math.Max(0, last - 1))
            .Select(r => (IReadOnlyDictionary<string, XLCellValue>)headers
                .Select((h, i) => (h, worksheet.Cell(r, i + 1).Value))
                .ToDictionary(c => c.h, c => c.Value, StringComparer.Ordinal))
            .ToList();
    }

    /// <summary>The About sheet's facts, each by its field, down to the blank row before the column notes.</summary>
    public IReadOnlyDictionary<string, XLCellValue> About()
    {
        var sheet = workbook.Worksheet(RecordExport.AboutSheet);
        var facts = new Dictionary<string, XLCellValue>(StringComparer.Ordinal);
        for (var row = 1; !sheet.Cell(row, 1).Value.IsBlank; row++)
        {
            Assert.True(sheet.Cell(row, 1).Style.Font.Bold, $"The About sheet's field on row {row} is not in bold.");
            facts[sheet.Cell(row, 1).Value.GetText()] = sheet.Cell(row, 2).Value;
        }

        return facts;
    }

    /// <summary>The About sheet's column notes: what each column holds, by the column (or columns) it is about.</summary>
    public IReadOnlyDictionary<string, string> Notes()
    {
        var sheet = workbook.Worksheet(RecordExport.AboutSheet);
        var row = 1;
        while (!sheet.Cell(row, 1).Value.IsBlank)
        {
            row++;
        }

        Assert.Equal("Column", sheet.Cell(row + 1, 1).Value.GetText());
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        for (row += 2; !sheet.Cell(row, 1).Value.IsBlank; row++)
        {
            notes[sheet.Cell(row, 1).Value.GetText()] = sheet.Cell(row, 2).Value.GetText();
        }

        return notes;
    }

    /// <summary>The worksheet itself, for what a row cannot say: its frozen header, its filter, a cell's style.</summary>
    public IXLWorksheet Sheet(string name) => workbook.Worksheet(name);

    public void Dispose() => workbook.Dispose();

    /// <summary>Checks the file against the Office Open XML schemas: a file Excel would repair or refuse fails here, naming where.</summary>
    public static void AssertValid(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var stream = new MemoryStream(bytes);
        using var document = SpreadsheetDocument.Open(stream, isEditable: false);
        var errors = new OpenXmlValidator().Validate(document).Select(e => $"{e.Part?.Uri} {e.Path?.XPath}: {e.Description}").ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }
}
