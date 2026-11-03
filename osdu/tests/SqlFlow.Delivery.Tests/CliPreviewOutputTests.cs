using System.Text.Json.Nodes;
using SqlFlow.Delivery.Cli;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Checks;
using SqlFlow.Delivery.Engine.Preview;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What <c>sqlflow preview</c> and <c>sqlflow values</c> write: <c>--out</c> holds the preview's document whole whatever its
/// size, while the console bounds it as the GUI does and says where the whole of it is; and a flow of a source with
/// interfaces is named once, as a run names it (<c>flow/interface</c>), on the console and in the <c>--rows</c> file.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CliPreviewOutputTests : IDisposable
{
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file a failed assertion left open is removed with the temporary folder by the operating system.
        }
    }

    private async Task<(EngineContext Engine, FlowDefinition Flow)> EstateAsync(string? @interface = null)
    {
        var tables = await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime.AddMinutes(-5), time: _clock);
        var engine = Samples.Engine(ledger: null, _clock, sources: tables);
        var flow = Samples.LocalFlow(_root);
        return (engine, @interface is null ? flow : flow with { Interface = @interface });
    }

    [Fact]
    public async Task Out_holds_the_document_whole_while_the_console_leaves_out_one_over_its_bound_and_says_where_it_is()
    {
        var (engine, flow) = await EstateAsync();
        var previews = await DeliveryPreviewVerbs.PreviewsAsync(engine, [flow], SampleEstate.Values, null, whole: true, CancellationToken.None);
        var document = Assert.Single(previews).Document!;
        Assert.NotNull(document.Rendered);
        Assert.Null(document.Omitted);

        var written = await DeliveryPreviewVerbs.WriteAsync(Path.Combine(_root, "preview.json"), previews, CancellationToken.None);
        var file = JsonNode.Parse(await File.ReadAllTextAsync(written))!;
        Assert.NotNull(file["document"]?["rendered"]);
        Assert.Null(file["document"]?["omitted"]);
        Assert.Equal(document.Characters, file["document"]!["characters"]!.GetValue<int>());

        // The console holds the document to a bound (the 2,000,000 characters the GUI shows, 100 here), and names the file.
        var text = new StringWriter();
        DeliveryPreviewVerbs.Show(text, previews, json: false, written, maxDocumentChars: 100);
        Assert.Contains($"the whole preview is written to {written}", text.ToString(), StringComparison.Ordinal);
        Assert.Contains(document.MetadataHash, text.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("document as rendered", text.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("--out", text.ToString(), StringComparison.Ordinal);

        var json = new StringWriter();
        DeliveryPreviewVerbs.Show(json, previews, json: true, written, maxDocumentChars: 100);
        var shown = JsonNode.Parse(json.ToString())!;
        Assert.Null(shown["document"]?["rendered"]);
        Assert.Contains(written, shown["document"]!["omitted"]!.GetValue<string>(), StringComparison.Ordinal);

        // The file was written from the whole previews, not from what the console shows.
        Assert.NotNull(JsonNode.Parse(await File.ReadAllTextAsync(written))!["document"]?["rendered"]);
    }

    [Fact]
    public async Task A_document_within_the_bound_is_shown_with_the_file_it_was_written_to()
    {
        var (engine, flow) = await EstateAsync();
        var previews = await DeliveryPreviewVerbs.PreviewsAsync(engine, [flow], SampleEstate.Values, null, whole: true, CancellationToken.None);
        var written = await DeliveryPreviewVerbs.WriteAsync(Path.Combine(_root, "preview.json"), previews, CancellationToken.None);

        var text = new StringWriter();
        DeliveryPreviewVerbs.Show(text, previews, json: false, written, RecordPreviewLimits.Default.MaxDocumentChars);

        Assert.Contains("document as", text.ToString(), StringComparison.Ordinal);
        Assert.Contains($"written to {written}", text.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_out_a_document_over_the_bound_says_out_writes_it_whole()
    {
        var (engine, flow) = await EstateAsync();
        var previews = await DeliveryPreviewVerbs.PreviewsAsync(engine, [flow], SampleEstate.Values, null, whole: false, CancellationToken.None);

        var bounded = RecordPreviewer.Bound(Assert.Single(previews), 100);

        Assert.Null(bounded.Document!.Rendered);
        Assert.Null(bounded.Document.Sent);
        Assert.Contains("'sqlflow preview' writes it whole with --out", bounded.Document.Omitted, StringComparison.Ordinal);
        Assert.Same(previews[0], RecordPreviewer.Bound(previews[0], int.MaxValue));
    }

    [Fact]
    public async Task A_previewed_interface_is_named_once_as_a_run_names_it()
    {
        var (engine, flow) = await EstateAsync("header");
        var previews = await DeliveryPreviewVerbs.PreviewsAsync(engine, [flow], SampleEstate.Values, null, whole: false, CancellationToken.None);

        var text = new StringWriter();
        DeliveryPreviewVerbs.Show(text, previews, json: false, written: null, RecordPreviewLimits.Default.MaxDocumentChars);

        var first = text.ToString().Split('\n')[0];
        Assert.StartsWith($"OK  {flow.Name}/header: ", first, StringComparison.Ordinal);
        Assert.DoesNotContain("header / header", text.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_checked_interface_is_named_once_on_the_console_and_in_the_rows_file()
    {
        var (engine, flow) = await EstateAsync("header");
        var rows = new StringWriter();

        var checks = await DeliveryValueCheckVerbs.ChecksAsync(engine, [flow], SampleEstate.Values, new ValueCheckRequest(), rows, CancellationToken.None);

        var text = new StringWriter();
        DeliveryValueCheckVerbs.WriteText(text, Assert.Single(checks));
        Assert.Contains($" {flow.Name}/header: ", text.ToString().Split('\n')[0], StringComparison.Ordinal);
        Assert.DoesNotContain("header / header", text.ToString(), StringComparison.Ordinal);

        // A finding leads with what is wrong in plain words, then what it does and what to do, as the page says it.
        Assert.Contains("  `dataset.curves.curve_version` is empty\n", text.ToString().ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("effect  Left out: these records are delivered without `CurveVersion`.", text.ToString(), StringComparison.Ordinal);
        Assert.Contains("to fix  1. Fill `dataset.curves.curve_version` in the source for the records listed.", text.ToString(), StringComparison.Ordinal);

        // Every row the check fails or leaves a variable out of is a line of the file, under the flow's label alone.
        var lines = rows.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.StartsWith($"{flow.Name}/header,", line, StringComparison.Ordinal));
    }
}
