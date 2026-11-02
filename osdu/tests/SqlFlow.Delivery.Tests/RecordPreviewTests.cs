using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Preview;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The record preview without a ledger: which row a key or the scope picks, what an operator is told when none can be
/// picked, the document and files of the sample well logs, what each route adds to the document it sends, and the bounds
/// that keep an answer small. The ledger's side (what the next run would do with a delivered record, keys the ledger
/// resolves) is <see cref="RecordPreviewLedgerTests"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RecordPreviewTests : IDisposable
{
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

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

    private async Task<(MemoryIngestionTables Tables, FlowRuntime Runtime)> RuntimeAsync(
        Func<FlowDefinition, FlowDefinition>? adjust = null, MemoryIngestionTables? tables = null, IReadOnlyDictionary<string, string>? values = null)
    {
        tables ??= await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var engine = Samples.Engine(ledger: null, _clock, sources: tables);
        var flow = Samples.LocalFlow(_root);
        var runtime = await FlowRuntime.CreateAsync(engine, adjust is null ? flow : adjust(flow), values ?? SampleEstate.Values);
        return (tables, runtime);
    }

    private static string SourceKeyOf(SampleLog log) => SourceKey.Display(SampleWellLogs.System, [log.SourceProject, log.LogId]);

    [Fact]
    public async Task Without_a_key_the_scopes_first_row_renders_as_a_first_delivery_would_and_nothing_is_written()
    {
        var (tables, runtime) = await RuntimeAsync();
        using (runtime)
        {
            var preview = await new RecordPreviewer(runtime).PreviewAsync(null);

            Assert.True(preview.Found, preview.Reason);
            Assert.Equal(PreviewKeyForms.First, preview.Asked.How);
            Assert.Equal(SampleEstate.Logs().Count, preview.Asked.ScopeRecords);
            Assert.Equal(0, preview.Asked.PassedOver);
            Assert.Equal(["source_project", "log_id"], preview.Asked.KeyColumns);

            // The table pages by its identity primary key, so the first row is the one the estate added first.
            var first = SampleEstate.Logs()[0];
            Assert.Equal(SourceKeyOf(first), preview.Source!.SourceKey);
            Assert.Equal(SampleEstate.Key(0).Value, preview.Source.DeliveryKey);
            Assert.Equal(SampleEstate.FileName, preview.Source.OriginFile);
            Assert.Equal(1, preview.Source.OriginRow);
            Assert.Equal(first.Curves.Count, preview.Source.Datasets["curves"].Total);

            // Without a ledger the run's view is a first delivery's.
            Assert.Equal("create", preview.Decision!.Action);
            Assert.Null(preview.Decision.Ledger);

            var document = preview.Document!;
            Assert.False(document.Held, string.Join("; ", document.Holds));
            Assert.Equal(TargetId.Compose("dev", "work-product-component--WellLog", SampleEstate.Key(0)), document.TargetId);
            Assert.Equal(document.TargetId, document.Rendered!["id"]!.GetValue<string>());
            Assert.StartsWith("osdu:wks:work-product-component--WellLog:", document.Kind, StringComparison.Ordinal);

            // The values the parameters rendered with are said, as they reached the record: the partition is the id's.
            var parameters = preview.Inputs.Parameters!;
            Assert.Equal(runtime.Mapping.Context.Parameters, parameters);
            Assert.Equal("dev", parameters["dataPartition"]);
            Assert.Null(document.Sent);
            Assert.Null(document.Omitted);
            Assert.NotEmpty(document.Searches);

            // The payload the ddms route streams: the log's one parquet chunk, measured by its footer.
            var part = Assert.Single(preview.Payload);
            Assert.Null(part.Role);
            Assert.Equal(1, part.TotalFiles);
            var file = Assert.Single(part.Files);
            Assert.EndsWith(".parquet", file.Name, StringComparison.Ordinal);
            Assert.Equal(first.Depths.Count, file.Parquet!.Rows);
            Assert.Contains(first.Curves.First(c => c.CurveId != SampleWellLogs.IndexCurveId).CurveId, file.Parquet.ColumnNames);
            Assert.Null(file.FooterProblem);

            Assert.Equal("ddms", preview.Route.Protocol);
            Assert.NotNull(preview.Route.Ddms);
            Assert.Equal(["DDMS", "DDMS"], preview.Steps.Select(s => s.Service));
            Assert.Equal([1, 2], preview.Steps.Select(s => s.Order));

            // Nothing was planned into a work location, and the tables were only read.
            Assert.False(Directory.Exists(Path.Combine(_root, "work")) && Directory.EnumerateFileSystemEntries(Path.Combine(_root, "work")).Any());
            Assert.All(tables.Selections, s => Assert.Equal(SourceSelectionKind.Full, s.Kind));
        }
    }

    [Fact]
    public async Task A_source_key_whose_part_holds_a_slash_finds_its_row_among_the_ways_the_text_splits()
    {
        var (_, runtime) = await RuntimeAsync();
        using (runtime)
        {
            var log = SampleEstate.Logs().First(l => l.LogId.Contains('/', StringComparison.Ordinal));
            var preview = await new RecordPreviewer(runtime).PreviewAsync(SourceKeyOf(log));

            Assert.True(preview.Found, preview.Reason);
            Assert.Equal(PreviewKeyForms.SourceKey, preview.Asked.How);
            Assert.Equal([log.SourceProject, log.LogId], preview.Source!.KeyParts);
            Assert.Equal(log.Key.Value, preview.Source.DeliveryKey);
        }
    }

    [Theory]
    [InlineData("json")]
    [InlineData("bars")]
    [InlineData("bare")]
    public async Task Each_form_of_the_key_an_operator_holds_names_the_same_row(string form)
    {
        var (_, runtime) = await RuntimeAsync();
        using (runtime)
        {
            var log = SampleEstate.Logs()[1];
            var text = form switch
            {
                "json" => JsonSerializer.Serialize(new[] { log.SourceProject, log.LogId }),
                "bars" => $"{log.SourceProject} | {log.LogId}",
                _ => $"{log.SourceProject}/{log.LogId}",
            };

            var preview = await new RecordPreviewer(runtime).PreviewAsync($"  {text}  ");

            Assert.True(preview.Found, preview.Reason);
            Assert.Equal(form == "json" ? PreviewKeyForms.KeyParts : PreviewKeyForms.SourceKey, preview.Asked.How);
            Assert.Equal(log.Key.Value, preview.Source!.DeliveryKey);
        }
    }

    [Theory]
    [InlineData("[\"only one part\"]", "keyed by 2")]
    [InlineData("[\"a\", null]", "not one of text parts")]
    [InlineData("welldb:", "names the source system and no key")]
    [InlineData("no-separator-at-all", "does not split into that many")]
    [InlineData("dev:work-product-component--WellLog:0123456789abcdef0123456789abcdef", "No record of this flow is delivered as")]
    [InlineData("PROJECT_A/no such log", "holds no row")]
    public async Task A_key_that_names_no_row_is_an_answer_that_says_why(string key, string why)
    {
        var (_, runtime) = await RuntimeAsync();
        using (runtime)
        {
            var preview = await new RecordPreviewer(runtime).PreviewAsync(key);

            Assert.False(preview.Found);
            Assert.Contains(why, preview.Reason, StringComparison.Ordinal);
            Assert.Null(preview.Source);
            Assert.Null(preview.Document);
            Assert.Equal(key.Trim(), preview.Asked.Key);
        }
    }

    [Fact]
    public async Task A_key_with_a_control_character_or_of_any_length_is_refused_before_a_read()
    {
        var (tables, runtime) = await RuntimeAsync();
        using (runtime)
        {
            var tab = await new RecordPreviewer(runtime).PreviewAsync("PROJECT\tA/1");
            var long_ = await new RecordPreviewer(runtime).PreviewAsync(new string('x', PreviewKeys.MaxKeyChars + 1));

            Assert.False(tab.Found);
            Assert.Contains("control character", tab.Reason, StringComparison.Ordinal);
            Assert.False(long_.Found);
            Assert.Contains("characters long", long_.Reason, StringComparison.Ordinal);
            Assert.Empty(tables.Selections);
        }
    }

    [Fact]
    public async Task The_first_record_passes_over_deleted_and_keyless_rows_and_names_them()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        tables.Records[0].DeletedUtc = Now.AddMinutes(-1);
        tables.Records[1].Row["log_id"] = "   ";
        var (_, runtime) = await RuntimeAsync(tables: tables);
        using (runtime)
        {
            var preview = await new RecordPreviewer(runtime).PreviewAsync(null);

            Assert.True(preview.Found, preview.Reason);
            Assert.Equal(2, preview.Asked.PassedOver);
            Assert.Contains(preview.Asked.PassedOverWhy, w => w.Contains("marked the row deleted", StringComparison.Ordinal));
            Assert.Contains(preview.Asked.PassedOverWhy, w => w.Contains("a key part is empty", StringComparison.Ordinal));
            Assert.Equal(SampleEstate.Key(2).Value, preview.Source!.DeliveryKey);
        }
    }

    [Fact]
    public async Task A_key_names_a_deleted_row_and_the_preview_says_it_renders_no_document()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        tables.Records[0].DeletedUtc = Now.AddMinutes(-1);
        var (_, runtime) = await RuntimeAsync(tables: tables);
        using (runtime)
        {
            var log = SampleEstate.Logs()[0];
            var preview = await new RecordPreviewer(runtime).PreviewAsync(JsonSerializer.Serialize(new[] { log.SourceProject, log.LogId }));

            Assert.True(preview.Found, preview.Reason);
            Assert.Null(preview.Document);
            Assert.Contains("deleted", preview.NoDocument, StringComparison.Ordinal);
            Assert.Equal("hold", preview.Decision!.Action);
            Assert.Empty(preview.Steps);
        }
    }

    [Fact]
    public async Task An_empty_scope_or_a_scope_of_rows_that_cannot_render_is_an_answer()
    {
        // A log source no row of the table carries: the scope those values name is empty.
        var (_, runtime) = await RuntimeAsync(values: new Dictionary<string, string>(StringComparer.Ordinal) { ["logSource"] = "NO_SUCH_SOURCE" });
        using (runtime)
        {
            var preview = await new RecordPreviewer(runtime).PreviewAsync(null);
            Assert.False(preview.Found);
            Assert.Contains("holds no row in the flow's scope for logSource=NO_SUCH_SOURCE", preview.Reason, StringComparison.Ordinal);
            Assert.Equal(0, preview.Asked.ScopeRecords);
            Assert.Equal("NO_SUCH_SOURCE", preview.Asked.Values["logSource"]);
        }

        var deleted = await SampleEstate.BuildAsync(Path.Combine(_root, "deleted"), Now.AddMinutes(-5), time: _clock);
        foreach (var record in deleted.Records)
        {
            record.DeletedUtc = Now.AddMinutes(-1);
        }

        var (_, all) = await RuntimeAsync(tables: deleted);
        using (all)
        {
            var preview = await new RecordPreviewer(all).PreviewAsync(null);
            Assert.False(preview.Found);
            Assert.Contains("is deleted, keyless or held by the source", preview.Reason, StringComparison.Ordinal);
            Assert.Equal(SampleEstate.Logs().Count, preview.Asked.PassedOver);
        }
    }

    [Fact]
    public async Task Passing_over_stops_at_its_bound_and_asks_for_a_key()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        foreach (var record in tables.Records)
        {
            record.DeletedUtc = Now.AddMinutes(-1);
        }

        var (_, runtime) = await RuntimeAsync(tables: tables);
        using (runtime)
        {
            var preview = await new RecordPreviewer(runtime, new RecordPreviewLimits { MaxPassedOver = 2 }).PreviewAsync(null);

            Assert.False(preview.Found);
            Assert.Equal(2, preview.Asked.PassedOver);
            Assert.Contains("name a record by its key", preview.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_document_over_the_bound_is_described_by_its_size_and_hash_rather_than_returned()
    {
        var (_, runtime) = await RuntimeAsync();
        using (runtime)
        {
            var preview = await new RecordPreviewer(runtime, new RecordPreviewLimits { MaxDocumentChars = 100 }).PreviewAsync(null);

            var document = preview.Document!;
            Assert.Null(document.Rendered);
            Assert.Null(document.Sent);
            Assert.True(document.Characters > 100);
            Assert.Contains(document.MetadataHash, document.Omitted, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Rows_payload_listings_and_parquet_columns_are_bounded()
    {
        var (_, runtime) = await RuntimeAsync();
        using (runtime)
        {
            var limits = new RecordPreviewLimits { MaxChildRows = 2, MaxCellChars = 3, MaxColumnNames = 1, MaxFooters = 0 };
            var preview = await new RecordPreviewer(runtime, limits).PreviewAsync(null);

            var curves = preview.Source!.Datasets["curves"];
            Assert.Equal(2, curves.Rows.Count);
            Assert.True(curves.Truncated);
            Assert.Contains(preview.Source.Row.Values, v => v is not null && v.Contains("... (", StringComparison.Ordinal));
            var file = Assert.Single(Assert.Single(preview.Payload).Files);
            Assert.Null(file.Parquet);
            Assert.Contains("first 0 parquet files", file.FooterProblem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_file_route_sends_the_record_with_a_placeholder_for_each_dataset_id_the_file_service_mints()
    {
        var (_, runtime) = await RuntimeAsync(flow => flow with { Target = flow.Target with { Protocol = DeliveryProtocol.File } });
        using (runtime)
        {
            var preview = await new RecordPreviewer(runtime).PreviewAsync(null);

            var document = preview.Document!;
            var file = Assert.Single(Assert.Single(preview.Payload).Files);
            var datasets = Assert.IsType<JsonArray>(document.Sent!["data"]!["Datasets"]);
            Assert.Equal($"<dataset id the File service returns for {file.Name}>:", datasets[^1]!.GetValue<string>());
            var placeholder = Assert.Single(document.Placeholders);
            Assert.Equal($"data.Datasets[{datasets.Count - 1}]", placeholder.Path);

            // The rendered document is what the ledger hashes; the route's additions are in the sent form only.
            Assert.DoesNotContain("<dataset id", document.Rendered!.ToJsonString(), StringComparison.Ordinal);

            Assert.Equal(["File", "Landing zone", "File", "Storage"], preview.Steps.Select(s => s.Service));
            Assert.StartsWith("GET " + FileUploads.DefaultUploadUrlPath, preview.Steps[0].Request, StringComparison.Ordinal);
            var registration = preview.Steps[2].Body!;
            Assert.Equal("osdu:wks:dataset--File.Generic:1.0.0", registration["kind"]!.GetValue<string>());
            Assert.Equal(document.Rendered["acl"]!.ToJsonString(), registration["acl"]!.ToJsonString());
            Assert.Equal(file.Name, registration["data"]!["Name"]!.GetValue<string>());
            Assert.StartsWith("PUT " + OsduRecordProtocol.DefaultRecordPath, preview.Steps[3].Request, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_dataset_route_names_the_dataset_its_files_go_in_by_the_id_it_derives_from_the_records()
    {
        var (_, runtime) = await RuntimeAsync(flow => flow with { Target = flow.Target with { Protocol = DeliveryProtocol.Dataset } });
        using (runtime)
        {
            var preview = await new RecordPreviewer(runtime).PreviewAsync(null);

            var document = preview.Document!;
            var expected = OsduDatasetProtocol.DatasetIdFor(runtime.Flow.Target.ProtocolOptions, document.TargetId!);
            Assert.Equal(expected + ":", document.Sent!["data"]!["Datasets"]![0]!.GetValue<string>());
            Assert.Empty(document.Placeholders);
            Assert.Contains(preview.Steps, s => s.What.Contains(expected, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task The_storage_route_sends_the_rendered_document_as_it_is()
    {
        var (_, runtime) = await RuntimeAsync(flow => flow with
        {
            Target = flow.Target with
            {
                Protocol = DeliveryProtocol.Storage,
                ProtocolOptions = flow.Target.ProtocolOptions with { PreserveDataKeys = ["ExtensionProperties"] },
            },
        });
        using (runtime)
        {
            var preview = await new RecordPreviewer(runtime).PreviewAsync(null);

            Assert.Null(preview.Document!.Sent);
            var step = Assert.Single(preview.Steps);
            Assert.Equal("Storage", step.Service);
            Assert.Equal("PUT " + OsduRecordProtocol.DefaultRecordPath, step.Request);
            Assert.Contains(preview.Notes, n => n.Contains("data.ExtensionProperties", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Fit_leaves_out_child_rows_then_documents_then_rows_and_says_so()
    {
        var preview = new RecordPreview
        {
            Flow = "f",
            FlowId = Guid.NewGuid(),
            Asked = new PreviewAsked { How = PreviewKeyForms.First, KeyColumns = ["k"] },
            Found = true,
            Inputs = new PreviewInputs("M@1.0.0", "a:b:c--D:1.0.0", "1", null, null, "0123456789abcdef"),
            Route = new PreviewRoute("storage", null, null),
            Source = new PreviewSource
            {
                SourceKey = "s:k",
                KeyParts = ["k"],
                Row = new Dictionary<string, string?> { ["k"] = new string('r', 1000) },
                Datasets = new Dictionary<string, SourceRowsView>
                {
                    ["child"] = new([new Dictionary<string, string?> { ["v"] = new string('c', 5000) }], 1, false),
                },
            },
            Document = new PreviewDocument { Kind = "a:b:c--D:1.0.0", MetadataHash = "h", Characters = 3000, Rendered = new JsonObject { ["big"] = new string('d', 3000) } },
            PreviewedUtc = DateTime.UnixEpoch,
        };
        int Measure(RecordPreview p) => RecordPreviewJson.Write(p).Length;
        var full = Measure(preview);

        Assert.Same(preview, RecordPreviewer.Fit(preview, full, Measure));

        var withoutChildren = RecordPreviewer.Fit(preview, full - 1000, Measure);
        Assert.Empty(withoutChildren.Source!.Datasets);
        Assert.NotNull(withoutChildren.Document!.Rendered);
        Assert.NotNull(withoutChildren.Source.Omitted);

        var withoutDocument = RecordPreviewer.Fit(preview, full - 6000, Measure);
        Assert.Null(withoutDocument.Document!.Rendered);
        Assert.NotNull(withoutDocument.Document.Omitted);
        Assert.NotEmpty(withoutDocument.Source!.Row);

        var bare = RecordPreviewer.Fit(preview, full - 8000, Measure);
        Assert.Empty(bare.Source!.Row);

        Assert.Throws<DeliveryException>(() => RecordPreviewer.Fit(preview, 10, Measure));
    }
}
