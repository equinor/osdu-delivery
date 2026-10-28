using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Workflows;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Storage;
using Xunit;
using static SqlFlow.Delivery.Tests.EnrichmentSuite;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The records an enrichment adds, as the chain renders them (osdu/docs/reference/flow/enrich-records.md): each under an id
/// made from the record's id and the enrichment, and a row's key for one taking many rows, the same on every render whatever
/// the files hold; linked from the record by the mapping's <c>$link</c> before anything is sent, after what the record lists
/// already; one for the record when the enrichment reads no rows; of any kind; and the parts a plan resolves from them, whose
/// hashes the record's hash takes in, so new files plan the record again though its document is the same.
/// </summary>
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class StepFilesChainTests
{
    private const string DatasetType = "dataset--File.Generic";
    private const string DocumentKind = "osdu:wks:work-product-component--Document:1.0.0";
    private const string OneRow = "lasfile:\n  rows: one\n  mapping: LasDataset@1.0.0\n";
    private const string ManyRows = "lasfile:\n  rows: many\n  key: [file_name]\n  mapping: LasDataset@1.0.0\n";

    private static readonly MappingDefinition DatasetMapping = Fragment("""
        data:
          Name: { $from: file_name }
          Description: { $base: data.Name }
        """, name: "LasDataset", kind: DatasetKind);

    /// <summary>The test mapping linking the enrichment's records from <c>data.Aliases</c>, after <paramref name="before"/>.</summary>
    private static MappingDefinition Linking(string enrich = OneRow, string before = "")
        => Owner(data: $"Aliases: [{before}{{ $link: lasfile }}]", enrich: enrich);

    private static RecordChain Chain(MappingDefinition owner, params ResolvedStep[] steps) => new(Renderer(owner), steps);

    private static RecordChain Chain(params ResolvedStep[] steps) => Chain(Linking(), steps);

    private static IReadOnlyDictionary<string, string?> File(string name, string hash = "h1", string folder = "log-a")
        => Row(("file_name", name), ("las_hash", hash), ("las_folder", folder));

    private static string IdOf(RenderResult result, string suffix, string type = DatasetType) => WorkflowValues.DerivedDatasetId(result.TargetId!, type, suffix);

    private static List<string> Links(RenderResult result, string path = "data.Aliases")
        => At(result.Document, path) is JsonArray links ? links.Select(n => n!.GetValue<string>()).ToList() : [];

    [Fact]
    public void An_enrichment_taking_one_row_adds_one_record_its_name_names_and_the_record_links_it()
    {
        var result = Chain(RecordsStep(files: LasFiles())).Render(Record("a", 1, ("lasfile", [File("a.las")])));

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        var id = IdOf(result, "enrich.lasfile");
        Assert.Equal([id + ":"], Links(result));
        var contribution = Assert.Single(result.Enrichment);
        var added = Assert.Single(contribution.Records);
        Assert.Equal((id, DatasetKind, 0), (added.Id, added.Kind, added.Row));
        Assert.Null(contribution.Mapping);
        Assert.Empty(contribution.Effects);
        Assert.Contains(".", id[(id.LastIndexOf('-') + 1)..], StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_keeps_its_id_whatever_its_files_hold_so_the_record_names_the_same_link()
    {
        var first = Chain(RecordsStep(files: LasFiles())).Render(Record("a", 1, ("lasfile", [File("a.las", "h1", "log-a")])));
        var changed = Chain(RecordsStep(files: LasFiles())).Render(Record("a", 1, ("lasfile", [File("b.las", "h2", "log-b")])));

        Assert.Equal(Links(first), Links(changed));
        Assert.Equal(first.MetadataHash, changed.MetadataHash);
        Assert.Equal(first.Enrichment[0].Records[0].Id, changed.Enrichment[0].Records[0].Id);
    }

    [Fact]
    public void An_enrichment_taking_many_rows_adds_a_record_per_row_under_its_key_and_an_added_row_leaves_the_others_ids()
    {
        var chain = Chain(Linking(ManyRows), RecordsStep(rows: EnrichRows.Many, files: LasFiles()));
        var one = chain.Render(Record("a", 1, ("lasfile", [File("a.las")])));
        var two = chain.Render(Record("a", 1, ("lasfile", [File("a.las"), File("b.las")])));

        var a = IdOf(one, $"enrich.lasfile.{DeliveryKey.Derive("lasfile", ["a.las"]).Value:N}");
        var b = IdOf(two, $"enrich.lasfile.{DeliveryKey.Derive("lasfile", ["b.las"]).Value:N}");
        Assert.Equal([a + ":"], Links(one));
        Assert.Equal([a + ":", b + ":"], Links(two));
        Assert.Equal([0, 1], two.Enrichment[0].Records.Select(d => d.Row));
    }

    [Fact]
    public void Two_rows_with_the_same_key_name_one_record_and_hold_the_record()
    {
        var result = Chain(Linking(ManyRows), RecordsStep(rows: EnrichRows.Many, files: LasFiles()))
            .Render(Record("a", 1, ("lasfile", [File("a.las", "h1"), File("a.las", "h2")])));

        Assert.True(result.IsHeld);
        Assert.Contains(result.Holds, h => h.StartsWith("enrich.lasfile: rows 1 and 2 of the enrichment's dataset name the same record", StringComparison.Ordinal));
    }

    [Fact]
    public void A_row_whose_key_column_is_empty_holds_the_record_naming_the_column_and_is_not_linked()
    {
        var result = Chain(Linking(ManyRows), RecordsStep(rows: EnrichRows.Many, files: LasFiles()))
            .Render(Record("a", 1, ("lasfile", [Row(("file_name", "  "), ("las_hash", "h"), ("las_folder", "f"))])));

        Assert.True(result.IsHeld);
        Assert.Contains(result.Holds, h => h.Contains("key column 'file_name' is empty in row 1", StringComparison.Ordinal));
    }

    [Fact]
    public void A_record_without_a_row_of_the_enrichment_links_nothing_and_hashes_as_its_mapping_renders_it_alone()
    {
        var plain = Renderer(Linking()).Render(Record("a", 1));
        var result = Chain(RecordsStep(files: LasFiles())).Render(Record("a", 1, ("lasfile", [])));

        Assert.Equal(plain.MetadataHash, result.MetadataHash);
        Assert.Empty(Links(result));
        Assert.Empty(Assert.Single(result.Enrichment).Records);
    }

    [Fact]
    public void The_links_go_after_what_the_mapping_lists_and_a_link_it_lists_already_is_written_once()
    {
        var result = Chain(Linking(before: "\"first:\", "), RecordsStep(files: LasFiles())).Render(Record("a", 1, ("lasfile", [File("a.las")])));
        var id = IdOf(result, "enrich.lasfile");
        Assert.Equal(["first:", id + ":"], Links(result));

        var listed = Chain(Linking(before: $"\"{id}:\", "), RecordsStep(files: LasFiles())).Render(Record("a", 1, ("lasfile", [File("a.las")])));
        Assert.Equal([id + ":"], Links(listed));
    }

    [Fact]
    public void A_link_writing_one_value_writes_the_one_record_an_enrichment_taking_one_row_adds()
    {
        var owner = Owner(data: "Symbol: { $link: lasfile }", enrich: OneRow);

        var result = Chain(owner, RecordsStep(files: LasFiles())).Render(Record("a", 1, ("lasfile", [File("a.las")])));
        var none = Chain(owner, RecordsStep(files: LasFiles())).Render(Record("a", 1, ("lasfile", [])));

        Assert.Equal(IdOf(result, "enrich.lasfile") + ":", At(result.Document, "data.Symbol")!.GetValue<string>());
        Assert.False(none.IsHeld);
        Assert.Null(At(none.Document, "data.Symbol"));
    }

    [Fact]
    public void The_enrichment_mapping_renders_each_record_from_its_row_and_the_record_before_it()
    {
        var result = Chain(RecordsStep(files: LasFiles(), mapping: DatasetMapping)).Render(Record("a", 1, ("lasfile", [File("a.las")])));

        var added = Assert.Single(result.Enrichment[0].Records);
        Assert.Equal("a.las", added.Record["data"]!["Name"]!.GetValue<string>());
        Assert.Equal("a", added.Record["data"]!["Description"]!.GetValue<string>());
    }

    [Fact]
    public void An_enrichment_reading_no_rows_adds_one_record_for_the_record_rendered_from_what_it_holds()
    {
        var summary = Fragment("data:\n  Name: { $base: data.Name }", name: "Summary", kind: DocumentKind);
        var owner = Owner(data: "Aliases: [{ $link: summary }]", enrich: "summary:\n  mapping: Summary@1.0.0\n");

        var result = Chain(owner, RecordsStep(name: "summary", mapping: summary, kind: DocumentKind, readsRows: false)).Render(Record("a", 1));

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        var id = IdOf(result, "enrich.summary", "work-product-component--Document");
        Assert.Equal([id + ":"], Links(result));
        var added = Assert.Single(result.Enrichment[0].Records);
        Assert.Equal((id, DocumentKind, "a"), (added.Id, added.Kind, added.Record["data"]!["Name"]!.GetValue<string>()));
        Assert.Null(result.Enrichment[0].Rows);
    }

    [Fact]
    public void A_record_of_any_kind_is_added_under_an_id_of_its_own_entity_type()
    {
        var note = Fragment("data:\n  Name: { $from: file_name }", name: "LasDataset", kind: DocumentKind);

        var result = Chain(RecordsStep(mapping: note, kind: DocumentKind)).Render(Record("a", 1, ("lasfile", [File("a.las")])));

        var id = IdOf(result, "enrich.lasfile", "work-product-component--Document");
        Assert.Equal([id + ":"], Links(result));
        Assert.Equal((id, DocumentKind), (result.Enrichment[0].Records[0].Id, result.Enrichment[0].Records[0].Kind));
    }

    [Fact]
    public void An_enrichment_whose_mapping_holds_adds_nothing_and_holds_the_record()
    {
        var strict = Fragment("data:\n  Name: { $from: grade }", name: "LasDataset", kind: DatasetKind);
        var result = Chain(RecordsStep(files: LasFiles(), mapping: strict)).Render(Record("a", 1, ("lasfile", [File("a.las")])));

        Assert.True(result.IsHeld);
        Assert.Contains(result.Holds, h => h.StartsWith("enrich.lasfile: the record of row 1: ", StringComparison.Ordinal));
        Assert.Empty(result.Enrichment[0].Records);
    }

    /// <summary>A flow whose mapping's enrichment registers files, as a plan resolves its parts, read from a file under <paramref name="folder"/>.</summary>
    private static FlowDefinition Flow(string folder) => Bound(new DeliveryDocumentLoader().ParseFlow("""
        flowType: delivery
        name: demo
        source:
          connection: ${env:OSDU_DATA_DB}
          record:
            object: OsduData.silver.Thing
            key: [name]
          datasets:
            lasfile:
              object: OsduData.silver.ThingFile
              join: { name: name }
              files:
                root: ../data/las
                locationColumn: las_folder
                pattern: "*.las"
                hashColumn: las_hash
                contentType: text/plain
          work: work
        render:
          mapping: Thing@1.0.0
          parameters: { dataPartition: dev }
        target:
          endpoint: https://example.org/api
          headers: { data-partition-id: dev }
          protocol: storage
        """, Path.Combine(folder, "flows", "demo.yaml")), Linking(), DatasetMapping);

    private static (RenderResult Render, string? Hold) Resolve(params IReadOnlyDictionary<string, string?>[] rows) => Resolve(null, false, rows);

    private static (RenderResult Render, string? Hold) Resolve(RecordState? state, bool hasPayload, params IReadOnlyDictionary<string, string?>[] rows)
    {
        var folder = Path.Combine(Path.GetTempPath(), "enrich-parts");
        var flow = Flow(folder);
        var step = RecordsStep(files: flow.Enrich![0].Records!.Files) with { Step = flow.Enrich[0] };
        var record = Record("a", 1, ("lasfile", rows));
        var (render, hold, _) = EnrichParts.Resolve(flow, new Dictionary<string, string>(StringComparer.Ordinal), record, Chain(step).Render(record), [step], state, hasPayload);
        return (render, hold);
    }

    /// <summary>The record as the ledger holds it once <paramref name="delivered"/> landed: its hash, and the parts its delivery kept.</summary>
    private static RecordState Landed(RenderResult delivered, string? metadataHash = "", string? payloadHash = null)
    {
        var kept = new JsonObject { [EnrichParts.RegisteredValue] = EnrichParts.RegisteredText(delivered.StepParts.Select(p => p.RecordId)) };
        foreach (var part in delivered.StepParts)
        {
            kept[EnrichParts.StateKey(part.RecordId)] = part.Hash;
        }

        return new RecordState
        {
            DeliveryKey = DeliveryKey.Derive("test", ["a"]),
            FlowId = Guid.NewGuid(),
            SourceKey = "a",
            MappingName = "Thing",
            MetadataHash = metadataHash == string.Empty ? delivered.MetadataHash : metadataHash,
            PayloadHash = payloadHash,
            TargetVersion = 3,
            TargetStateJson = kept.ToJsonString(),
        };
    }

    [Fact]
    public void Binding_makes_the_enrichment_of_a_dataset_kind_whose_rows_name_files_register_them()
    {
        var step = Assert.Single(Flow(Path.Combine(Path.GetTempPath(), "enrich-parts")).Enrich!);

        var records = step.Records!;
        Assert.Equal((DatasetKind, "LasDataset@1.0.0", true, "text/plain"), (records.Kind, records.Mapping, records.RegistersFiles, records.ContentType));
        Assert.Equal(("las_folder", "*.las", "las_hash"), (records.Files!.LocationColumn, records.Files.Pattern, records.Files.HashColumn));
        Assert.Null(step.Mapping);
    }

    [Fact]
    public void A_plan_resolves_each_dataset_where_its_row_says_and_its_files_hash_moves_the_records_hash()
    {
        var (first, hold) = Resolve(File("a.las", "h1", "log-a"));
        Assert.Null(hold);
        var part = Assert.Single(first.StepParts);
        Assert.Equal(("lasfile", "h1", "text/plain", true), (part.Step, part.FilesHash, part.ContentType, part.RegistersFiles));
        Assert.EndsWith("log-a", PayloadLocation.Parse(part.Location).Folder.TrimEnd('/', '\\'), StringComparison.Ordinal);
        Assert.Equal("*.las", PayloadLocation.Parse(part.Location).Pattern);

        var (changed, _) = Resolve(File("a.las", "h2", "log-a"));
        Assert.Equal(first.Canonical, changed.Canonical);
        Assert.NotEqual(first.MetadataHash, changed.MetadataHash);
        Assert.NotEqual(Hashing.ContentHash.Of(first.Canonical), first.MetadataHash);
        Assert.NotEqual(first.StepParts[0].Hash, changed.StepParts[0].Hash);
        Assert.Equal(first.StepParts[0].RecordHash, changed.StepParts[0].RecordHash);
    }

    [Fact]
    public void A_plan_resolves_a_record_written_through_storage_with_no_files_and_its_hash_moves_with_what_it_renders()
    {
        var note = Fragment("data:\n  Name: { $from: file_name }", name: "LasDataset", kind: DocumentKind);
        var flow = Flow(Path.Combine(Path.GetTempPath(), "enrich-parts"));
        var step = RecordsStep(mapping: note, kind: DocumentKind) with { Step = flow.Enrich![0] with { Records = flow.Enrich[0].Records! with { Files = null, Kind = DocumentKind } } };

        (RenderResult Render, string? Hold) Plan(string name)
        {
            var record = Record("a", 1, ("lasfile", [Row(("file_name", name))]));
            var (render, hold, _) = EnrichParts.Resolve(flow, new Dictionary<string, string>(StringComparer.Ordinal), record, Chain(step).Render(record), [step], null, false);
            return (render, hold);
        }

        var (first, hold) = Plan("a.las");
        Assert.Null(hold);
        var part = Assert.Single(first.StepParts);
        Assert.Equal((false, string.Empty, string.Empty, (string?)null, DocumentKind), (part.RegistersFiles, part.Location, part.FilesHash, part.ContentType, part.Kind));
        Assert.Equal("a.las", EnrichParts.RecordOf(part)["data"]!["Name"]!.GetValue<string>());

        var (renamed, _) = Plan("b.las");
        Assert.Equal(first.Canonical, renamed.Canonical);
        Assert.NotEqual(first.MetadataHash, renamed.MetadataHash);
        Assert.NotEqual(first.StepParts[0].RecordHash, renamed.StepParts[0].RecordHash);
    }

    [Fact]
    public void The_hash_of_a_document_with_its_parts_is_the_same_in_any_order_and_the_documents_own_without_any()
    {
        Assert.Equal(EnrichParts.Fold("{}", [("a", "1"), ("b", "2")]), EnrichParts.Fold("{}", [("b", "2"), ("a", "1")]));
        Assert.NotEqual(EnrichParts.Fold("{}", [("a", "1")]), EnrichParts.Fold("{}", [("a", "2")]));
        Assert.Equal(Hashing.ContentHash.Of("{}"), EnrichParts.Fold("{}", []));
    }

    [Fact]
    public void A_record_never_delivered_sends_its_document_and_every_part()
    {
        var (render, _) = Resolve(File("a.las"));

        Assert.Equal((false, true), (render.StepWork!.DocumentUnchanged, render.StepWork.Forced));
    }

    [Fact]
    public void New_files_under_the_same_document_send_the_datasets_alone_and_leave_the_record_as_OSDU_holds_it()
    {
        var (delivered, _) = Resolve(File("a.las", "h1"));
        var (render, _) = Resolve(Landed(delivered), false, File("a.las", "h2"));

        Assert.Equal((true, false), (render.StepWork!.DocumentUnchanged, render.StepWork.Forced));
        Assert.NotEqual(delivered.MetadataHash, render.MetadataHash);
    }

    [Fact]
    public void A_changed_document_is_written_whatever_its_parts_did()
    {
        var (delivered, _) = Resolve(File("a.las", "h1"));
        var earlier = Landed(delivered, metadataHash: EnrichParts.Fold("{\"other\":true}", delivered.StepParts.Select(p => (p.RecordId, p.Hash))));
        var (render, _) = Resolve(earlier, false, File("a.las", "h1"));

        Assert.Equal((false, false), (render.StepWork!.DocumentUnchanged, render.StepWork.Forced));
    }

    [Fact]
    public void A_redelivery_sends_the_document_and_every_part_again()
    {
        var (delivered, _) = Resolve(File("a.las", "h1"));

        // A redelivery of the record or its metadata clears the hash it was delivered with.
        var (all, _) = Resolve(Landed(delivered, metadataHash: null), false, File("a.las", "h1"));
        Assert.Equal((false, true), (all.StepWork!.DocumentUnchanged, all.StepWork.Forced));

        // A redelivery of a payload's files names them on its hash; one of a whole payload clears it on a flow with a payload.
        var (files, _) = Resolve(Landed(delivered, payloadHash: PayloadParts.RedeliverMarker([PayloadParts.Files])), true, File("a.las", "h1"));
        Assert.Equal((true, true), (files.StepWork!.DocumentUnchanged, files.StepWork.Forced));
        var (payload, _) = Resolve(Landed(delivered, payloadHash: null), true, File("a.las", "h1"));
        Assert.True(payload.StepWork!.Forced);

        // A flow without a payload has no payload hash to clear: its parts go as their hashes say.
        var (none, _) = Resolve(Landed(delivered, payloadHash: null), false, File("a.las", "h2"));
        Assert.False(none.StepWork!.Forced);
    }

    [Fact]
    public void A_row_naming_no_folder_or_no_hash_holds_the_record_naming_the_column()
    {
        var (_, noFolder) = Resolve(Row(("file_name", "a.las"), ("las_hash", "h1"), ("las_folder", " ")));
        Assert.Contains("enrich.lasfile: source.datasets.lasfile.files takes its folder from column 'las_folder'", noFolder, StringComparison.Ordinal);

        var (_, noHash) = Resolve(Row(("file_name", "a.las"), ("las_folder", "log-a")));
        Assert.Contains("column 'las_hash', which row 1 of the enrichment's dataset leaves empty", noHash, StringComparison.Ordinal);

        var (_, climbs) = Resolve(Row(("file_name", "a.las"), ("las_hash", "h1"), ("las_folder", "../elsewhere")));
        Assert.Contains("climbs out of its folder", climbs, StringComparison.Ordinal);
    }

    [Fact]
    public void The_parts_go_into_a_work_batch_line_and_read_back_as_written()
    {
        var (delivered, _) = Resolve(File("a.las", "h1"));
        var (render, _) = Resolve(Landed(delivered), false, File("a.las", "h2"));
        var text = EnrichParts.Encode(render.StepWork);
        Assert.NotNull(text);
        var read = EnrichParts.Decode(text);
        Assert.Equal(render.StepParts, read.Parts);
        Assert.Equal((true, false), (read.DocumentUnchanged, read.Forced));
        Assert.Null(EnrichParts.Encode(null));
        Assert.Null(EnrichParts.Encode(EnrichWork.None));
        Assert.Empty(EnrichParts.Decode(null).Parts);

        var item = new WorkItem(Guid.NewGuid(), render.TargetId!, render.Canonical, null, text);
        var line = WorkBatchFile.Decode(WorkBatchFile.Encode(item));
        Assert.Equal(render.StepParts, EnrichParts.Decode(line.Enrich).Parts);
        Assert.Null(WorkBatchFile.Decode(WorkBatchFile.Encode(item with { Enrich = null })).Enrich);
    }

    [Fact]
    public void Parts_that_do_not_read_say_so_rather_than_registering_nothing()
    {
        Assert.Contains("cannot be read", Assert.Throws<DeliveryException>(() => EnrichParts.Decode("{\"parts\":[1")).Message, StringComparison.Ordinal);
        Assert.Contains("name no parts", Assert.Throws<DeliveryException>(() => EnrichParts.Decode("{\"other\":1}")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_target_state_lists_the_records_its_enrichments_added_and_one_it_cannot_read_lists_none()
    {
        var state = new Dictionary<string, string>(StringComparer.Ordinal) { [EnrichParts.RegisteredValue] = EnrichParts.RegisteredText(["b", "a", "b"]) };
        Assert.Equal("enrich.records", EnrichParts.RegisteredValue);
        Assert.Equal(["a", "b"], EnrichParts.Records(state));
        Assert.Empty(EnrichParts.Records(new Dictionary<string, string>(StringComparer.Ordinal) { [EnrichParts.RegisteredValue] = "not json" }));
        Assert.Empty(EnrichParts.Records(null));
    }
}
