using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ClosedXML.Excel;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Checks;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The workbook of the rows a value check found wrong with an attribute (<see cref="ValueIssuesWorkbook"/>): a row for every
/// issue with what is wrong, where it came from and the base data behind it, added up by reason, with what was checked; read
/// back as a person reads it, after the Open XML validator, so the file is proven against readers other than its writer. One
/// with more issues than fit says so, and keeps every count.
/// </summary>
public sealed class ValueIssuesWorkbookTests
{
    private const string Attribute = "osdu.data.NameAlias[].AliasName";

    private static readonly DateTime Exported = new(2026, 10, 30, 22, 52, 16, DateTimeKind.Utc);

    private static readonly Guid Delivery = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly ValueIssuesBaseData Base = new(
        [new(null, "curve")],
        [new(null, "project"), new("aliases", "alias_name"), new("aliases", "alias_type")]);

    private static readonly ValueCheckFinding Left = new()
    {
        Outcome = "asserted",
        At = Attribute,
        Rule = "alias-name-type-in-osdu",
        OnFail = "omit",
        Message = "fails \"alias-name-type-in-osdu\" (record); the value is left out",
        Count = 2,
        Rows = 1,
        Values = [new ValueCheckValue("DEPTH", 1), new ValueCheckValue("GR", 1)],
        OtherValues = 3,
    };

    private static readonly ValueCheckFinding Unit = new()
    {
        Outcome = "invalid",
        At = Attribute,
        Rule = "pattern",
        Message = "does not match the template's pattern",
        Count = 7,
        Rows = 4,
    };

    private static readonly ValueCheckFinding Meant = new()
    {
        Outcome = "notApplicable",
        At = Attribute,
        Message = "the $when does not hold",
        Count = 90,
        Rows = 90,
    };

    private static ValueCheck Check(ValueCheckRows? rows = null, IReadOnlyList<string>? preflight = null) => new()
    {
        Flow = "recall-logcurvetype-03-delivery",
        FlowId = Guid.NewGuid(),
        Inputs = new ValueCheckInputs("LogCurveType@1.1.0", "osdu:wks:reference-data--LogCurveType:1.1.0", "cb4614b0dc3c2871", "dev", "212"),
        Asked = new ValueCheckAsked { Targets = [Attribute], Values = new Dictionary<string, string>(StringComparer.Ordinal) { ["logSource"] = "COMPOSITE" } },
        Rows = rows ?? new ValueCheckRows { Read = 644, Checked = 644, Complete = true, Clean = 395, WithInvalid = 4, WithFailedAssertion = 1 },
        Variables = [new ValueCheckVariable { Target = Attribute, Rows = new ValueCheckCounts(), Findings = [Left, Unit, Meant], Unlisted = 3 }],
        Issues = preflight ?? [],
        StartedUtc = Exported,
        CheckedUtc = Exported,
    };

    private static SourceRecord Row() => new()
    {
        Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["curve"] = "GR", ["project"] = "VALHALL", ["not_read"] = "never listed" }),
        Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase)
        {
            ["aliases"] =
            [
                SourceRow.FromStrings(new Dictionary<string, string?> { ["alias_name"] = "first", ["alias_type"] = "Mnemonic" }),
                SourceRow.FromStrings(new Dictionary<string, string?> { ["alias_name"] = "DEPTH", ["alias_type"] = "NoSuchType" }),
                SourceRow.FromStrings(new Dictionary<string, string?> { ["alias_name"] = "third", ["alias_type"] = "Mnemonic" }),
            ],
        },
    };

    private static ValueCheckOccurrence Issue(
        string at = Attribute, string outcome = "asserted", string? rule = "alias-name-type-in-osdu", string message = "'NoSuchType' names no record the partition holds",
        string? value = "DEPTH", string key = "recall:GR", string? label = "Gamma ray", int? item = 2, string? child = "aliases", SourceRecord? source = null,
        IReadOnlyList<string>? toFix = null) => new()
        {
            Target = Attribute,
            At = at,
            Outcome = outcome,
            Rule = rule,
            Message = message,
            Value = value,
            Record = new ValueCheckSample { SourceKey = key, Label = label, DeliveryKey = Delivery, File = "aliases.csv", Row = 12, Item = item, Columns = toFix ?? ["dataset.aliases.alias_type"] },
            Source = source ?? Row(),
            Child = child,
        };

    private static FlowDefinition Flow() => Samples.LocalFlow(Samples.NewTempDirectory());

    private static byte[] Write(ValueCheck check, params ValueCheckOccurrence[] issues)
    {
        using var file = new MemoryStream();
        using (var workbook = new ValueIssuesWorkbook(file, ExportValueIssuesOperation.MaxWorkbookBytes, Base))
        {
            foreach (var issue in issues)
            {
                workbook.Add(issue);
            }

            workbook.Complete(check, Flow(), Exported);
        }

        return file.ToArray();
    }

    [Fact]
    public void Every_issue_is_a_row_with_where_it_came_from_what_is_wrong_and_the_base_data_behind_it()
    {
        using var read = new WorkbookReading(Write(Check(), Issue(), Issue(label: null, at: "osdu.data.NameAlias[].AliasName.Inner")));
        Assert.Equal([ValueIssuesWorkbook.IssuesSheet, ValueIssuesWorkbook.SummarySheet, ValueIssuesWorkbook.AboutSheet], read.SheetNames);

        // What is wrong and where to fix it lead, the base data follows, and the rest comes last.
        Assert.Equal(
            ["Record", "Item", "Outcome", "Reason", "Source file", "Source row", "Source column", "Source value", "Value written",
                "dataset.curve", "dataset.project", "dataset.aliases.alias_name", "dataset.aliases.alias_type",
                "Rule", "Attribute", "Inside", "Source key", "Delivery key"],
            read.Headers(ValueIssuesWorkbook.IssuesSheet));

        var rows = read.Rows(ValueIssuesWorkbook.IssuesSheet);
        Assert.Equal(2, rows.Count);
        var first = rows[0];
        Assert.Equal("Gamma ray", first["Record"].GetText());
        Assert.Equal("recall:GR", first["Source key"].GetText());
        Assert.Equal("aliases.csv", first["Source file"].GetText());
        Assert.Equal(12, first["Source row"].GetNumber());
        Assert.Equal(2, first["Item"].GetNumber());
        Assert.Equal("Assertion failed", first["Outcome"].GetText());
        Assert.Equal("'NoSuchType' names no record the partition holds", first["Reason"].GetText());
        Assert.Equal("DEPTH", first["Value written"].GetText());
        Assert.Equal("dataset.aliases.alias_type", first["Source column"].GetText());
        Assert.Equal("NoSuchType", first["Source value"].GetText());
        Assert.Equal("alias-name-type-in-osdu", first["Rule"].GetText());
        Assert.Equal(Attribute, first["Attribute"].GetText());
        Assert.True(first["Inside"].IsBlank, "An issue at the attribute itself leaves Inside empty.");
        Assert.Equal(Delivery.ToString("D"), first["Delivery key"].GetText());

        // The base data is this row's: the key and the attribute's columns, read from the item's own child row.
        Assert.Equal("GR", first["dataset.curve"].GetText());
        Assert.Equal("VALHALL", first["dataset.project"].GetText());
        Assert.Equal("DEPTH", first["dataset.aliases.alias_name"].GetText());
        Assert.Equal("NoSuchType", first["dataset.aliases.alias_type"].GetText());

        // A record with no label is named by its key, and a reason inside a value written whole says where.
        Assert.Equal("recall:GR", rows[1]["Record"].GetText());
        Assert.Equal("osdu.data.NameAlias[].AliasName.Inner", rows[1]["Inside"].GetText());

        // The list stays a working list: the header in view, and a filter on every column.
        var sheet = read.Sheet(ValueIssuesWorkbook.IssuesSheet);
        Assert.Equal(1, sheet.SheetView.SplitRow);
        Assert.Equal(3, sheet.AutoFilter.Range.LastRow()!.RowNumber());
    }

    [Fact]
    public void Amber_connects_the_reason_to_the_cell_to_fix_and_the_columns_are_told_apart_by_colour()
    {
        using var read = new WorkbookReading(Write(Check(), Issue(), Issue(toFix: [])));
        var sheet = read.Sheet(ValueIssuesWorkbook.IssuesSheet);
        var headers = read.Headers(ValueIssuesWorkbook.IssuesSheet).ToList();
        XLColor Fill(int row, string header) => sheet.Cell(row, headers.IndexOf(header) + 1).Style.Fill.BackgroundColor;
        var amber = XLColor.FromArgb(unchecked((int)0xFFFFD966));

        // The columns to read first, and the cell in the row that is at fault.
        Assert.Equal(amber, Fill(1, "Source file"));
        Assert.Equal(amber, Fill(1, "Source column"));
        Assert.Equal(amber, Fill(2, "dataset.aliases.alias_type"));

        // The rest of the row is not, and a key and what the attribute reads have headers of their own.
        Assert.NotEqual(amber, Fill(2, "dataset.aliases.alias_name"));
        Assert.Equal(XLColor.FromArgb(unchecked((int)0xFFD9D9D9)), Fill(1, "dataset.curve"));
        Assert.Equal(XLColor.FromArgb(unchecked((int)0xFFBDD7EE)), Fill(1, "dataset.project"));
        Assert.True(sheet.Cell(1, 1).Style.Font.Bold, "A header stays in bold.");

        // A row that cannot say which column has nothing amber: colour is never a guess.
        Assert.NotEqual(amber, Fill(3, "dataset.aliases.alias_type"));
        Assert.True(read.Rows(ValueIssuesWorkbook.IssuesSheet)[1]["Source column"].IsBlank);

        Assert.Equal(["Amber", "Blue", "Grey"], read.Notes().Keys.Take(3));
    }

    [Fact]
    public void A_cell_to_fix_with_nothing_in_it_says_so_and_stays_amber()
    {
        var source = Row();
        var rows = new WorkbookReading(Write(Check(), Issue(item: 2, toFix: ["dataset.aliases.no_such_column"], source: source), Issue(toFix: ["dataset.project"], source: new SourceRecord
        {
            Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["curve"] = "GR", ["project"] = "   " }),
        }))).Rows(ValueIssuesWorkbook.IssuesSheet);

        Assert.Equal("(empty)", rows[0]["Source value"].GetText());
        Assert.Equal("(blank)", rows[1]["Source value"].GetText());
        Assert.Equal("(blank)", rows[1]["dataset.project"].GetText());
    }

    [Fact]
    public void A_value_of_the_record_that_reads_a_child_dataset_shows_what_its_rows_hold()
    {
        var rows = new WorkbookReading(Write(Check(), Issue(item: null, child: null))).Rows(ValueIssuesWorkbook.IssuesSheet);
        Assert.Equal("first; DEPTH; third", Assert.Single(rows)["dataset.aliases.alias_name"].GetText());

        // An item of another child dataset says nothing of this one's rows, so it is read as the record reads them.
        var other = new WorkbookReading(Write(Check(), Issue(item: 2, child: "curves"))).Rows(ValueIssuesWorkbook.IssuesSheet);
        Assert.Equal("first; DEPTH; third", Assert.Single(other)["dataset.aliases.alias_name"].GetText());

        // A child row the record does not hold is an empty cell, not another row's value.
        var gone = new WorkbookReading(Write(Check(), Issue(item: 9))).Rows(ValueIssuesWorkbook.IssuesSheet);
        Assert.True(Assert.Single(gone)["dataset.aliases.alias_name"].IsBlank);
    }

    [Fact]
    public void The_summary_adds_the_issues_up_by_reason_the_largest_first_and_leaves_out_what_the_mapping_means_empty()
    {
        using var read = new WorkbookReading(Write(Check()));
        var summary = read.Rows(ValueIssuesWorkbook.SummarySheet);

        Assert.Equal(["Invalid", "Not listed", "Assertion failed"], summary.Select(r => r["Outcome"].GetText()));
        Assert.Equal([7d, 3d, 2d], summary.Select(r => r["Issues"].GetNumber()));

        var asserted = summary[^1];
        Assert.Equal(Attribute, asserted["Attribute"].GetText());
        Assert.Equal("alias-name-type-in-osdu", asserted["Rule"].GetText());
        Assert.Equal("omit", asserted["On failure"].GetText());
        Assert.Equal(1, asserted["Rows"].GetNumber());
        Assert.Equal("DEPTH (1), GR (1), and 3 others", asserted["Values"].GetText());
    }

    [Fact]
    public void The_about_sheet_says_what_was_checked_with_what_and_what_each_column_holds()
    {
        var rows = new ValueCheckRows { Read = 1000, Checked = 1000, Complete = false, ScopeRecords = 5000, Clean = 900, WithHeld = 3 };
        using var read = new WorkbookReading(Write(Check(rows, ["the mapping names a deprecated kind"]), Issue()));
        var about = read.About();

        Assert.Equal(Attribute, about["Attribute"].GetText());
        Assert.Equal("LogCurveType@1.1.0", about["Mapping"].GetText());
        Assert.Equal("osdu:wks:reference-data--LogCurveType:1.1.0", about["Kind"].GetText());
        Assert.Equal("cb4614b0dc3c2871", about["Template version"].GetText());
        Assert.Equal("212", about["Cache version"].GetText());
        Assert.Equal("logSource=COMPOSITE", about["Scope"].GetText());
        Assert.Equal(1000, about["Rows checked"].GetNumber());
        Assert.Equal("the first 1,000; the scope holds about 5,000", about["Rows read"].GetText());
        Assert.Equal(1, about["Issues"].GetNumber());
        Assert.Equal("every one", about["Listed"].GetText());
        Assert.Equal("the mapping names a deprecated kind", about["Preflight warning"].GetText());
        Assert.Equal(Exported, about["Exported (UTC)"].GetDateTime());

        Assert.Contains("The base data", read.Notes()["dataset.<column>"], StringComparison.Ordinal);
    }

    [Fact]
    public void An_attribute_with_no_issue_is_still_a_workbook_saying_so()
    {
        var clean = Check(new ValueCheckRows { Read = 10, Checked = 10, Complete = true, Clean = 10 }) with { Variables = [] };
        using var read = new WorkbookReading(Write(clean));

        Assert.Empty(read.Rows(ValueIssuesWorkbook.IssuesSheet));
        Assert.Empty(read.Rows(ValueIssuesWorkbook.SummarySheet));
        Assert.Equal(0, read.About()["Issues"].GetNumber());
        Assert.Equal("the whole scope", read.About()["Rows read"].GetText());
    }

    [Fact]
    public void An_issue_past_what_the_file_may_hold_is_counted_not_listed_and_the_about_sheet_says_how_many_went()
    {
        using var file = new MemoryStream();
        using (var workbook = new ValueIssuesWorkbook(file, 16 * 1024, Base))
        {
            for (var i = 0; i < 2000; i++)
            {
                workbook.Add(Issue(value: Convert.ToHexString(RandomNumberGenerator.GetBytes(120)), key: $"recall:GR-{i}"));
            }

            Assert.Equal(2000, workbook.Found);
            Assert.InRange(workbook.Listed, 1, 1999);
            workbook.Complete(Check(), Flow(), Exported);
        }

        using var read = new WorkbookReading(file.ToArray());
        var listed = read.Rows(ValueIssuesWorkbook.IssuesSheet).Count;
        Assert.InRange(listed, 1, 1999);
        Assert.Equal(2000, read.About()["Issues"].GetNumber());
        Assert.StartsWith(string.Create(CultureInfo.InvariantCulture, $"the first {listed:N0}: "), read.About()["Listed"].GetText(), StringComparison.Ordinal);
        Assert.Contains("Every count here is exact", read.About()["Listed"].GetText(), StringComparison.Ordinal);
        Assert.Contains("sqlflow values --rows", read.About()["Listed"].GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_workbook_with_values_that_do_not_compress_still_fits_what_a_node_task_may_answer_with()
    {
        // Random text is the worst the sheet can hold: the file reaches its bound with the fewest issues listed, so this is the
        // largest answer a node can be asked to hand back, and the size of its Base64 is the size of the task's result.
        using var file = new MemoryStream();
        using var workbook = new ValueIssuesWorkbook(file, ExportValueIssuesOperation.MaxWorkbookBytes, Base);
        var message = Convert.ToBase64String(RandomNumberGenerator.GetBytes(225));
        for (var i = 0; workbook.Found == workbook.Listed && i < 200_000; i++)
        {
            workbook.Add(Issue(value: Convert.ToBase64String(RandomNumberGenerator.GetBytes(225)), message: message + i.ToString(CultureInfo.InvariantCulture), key: $"recall:GR-{i}"));
        }

        Assert.True(workbook.Found > workbook.Listed, "The test wrote enough to reach the bound.");
        workbook.Complete(Check(), Flow(), Exported);

        var answer = JsonSerializer.Serialize(
            (object)new ValueIssuesExport { Flow = "a-flow", RowsChecked = 1, Issues = workbook.Found, Listed = workbook.Listed, CheckedUtc = Exported, Workbook = file.ToArray() },
            DeliveryOperation.JsonOptions);
        Assert.True(answer.Length < 8_000_000, $"The answer is {answer.Length} characters.");

        // The file comes back out of the answer whole, as the GUI reads it.
        using var parsed = JsonDocument.Parse(answer);
        var carried = Convert.FromBase64String(parsed.RootElement.GetProperty("workbook").GetString()!);
        Assert.Equal(file.ToArray(), carried);
        WorkbookReading.AssertValid(carried);
    }

    [Fact]
    public void The_base_data_is_the_records_key_and_the_columns_the_attribute_reads_and_no_other()
    {
        var mapping = TestSchema.Mapping("""
            When:
              $from: when
              $required: false
            """);
        var flow = Flow();
        Assert.True(TemplatePath.TryParse("osdu.data.When", out var when, out _));
        var one = EntrySelection.Of([when!]);

        var columns = ValueIssuesBaseData.Of(flow, mapping, one);

        Assert.Equal(["dataset.when"], columns.Reads.Select(c => c.ToString()));
        Assert.Equal(flow.Source.Record.Key.Select(k => $"dataset.{k}"), columns.Keys.Select(c => c.ToString()));
    }
}
