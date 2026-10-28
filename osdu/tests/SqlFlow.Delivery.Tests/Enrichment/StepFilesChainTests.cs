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
/// The datasets a step's files are registered as, as the chain renders them (osdu/docs/reference/flow/enrich-files.md):
/// each under an id made from the record's id and the step, and a row's key for a step taking many rows, the same
/// on every render whatever the files hold; linked from the record before anything is sent, after what the record links
/// already; and the parts a plan resolves from them, whose hashes the record's hash takes in, so new files plan the record
/// again though its document is the same.
/// </summary>
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class StepFilesChainTests
{
    private const string DatasetType = "dataset--File.Generic";

    private static readonly MappingDefinition DatasetFragment = Fragment("""
        data:
          Name: { $from: file_name }
          Description: { $base: data.Name }
        """, name: "LasDataset");

    private static EnrichFiles Files(IReadOnlyList<string>? key = null, string link = EnrichFiles.DefaultLink) => new()
    {
        Payload = new FlowPayload { Root = "../data/las", LocationColumn = "las_folder", Pattern = "*.las", HashColumn = "las_hash" },
        Key = key ?? [],
        Link = link,
    };

    private static ResolvedStep FilesStep(string name = "lasfile", EnrichRows rows = EnrichRows.One, EnrichFiles? files = null, MappingDefinition? fragment = null, MappingDefinition? datasetFragment = null)
    {
        var declared = files ?? Files(rows == EnrichRows.Many ? ["file_name"] : null);
        declared = datasetFragment is null ? declared : declared with { Mapping = datasetFragment.Reference };
        return new ResolvedStep(
            new EnrichStep { Name = name, Mapping = fragment?.Reference, Files = declared, ReadsRows = true, Rows = rows },
            fragment,
            TestSchema.Build(),
            fragment is null ? null : Renderer(fragment))
        {
            Files = new ResolvedFiles(declared, DatasetType, datasetFragment, datasetFragment is null ? null : Renderer(datasetFragment)),
        };
    }

    private static RecordChain Chain(params ResolvedStep[] steps) => new(Renderer(Owner()), steps);

    private static IReadOnlyDictionary<string, string?> File(string name, string hash = "h1", string folder = "log-a")
        => Row(("file_name", name), ("las_hash", hash), ("las_folder", folder));

    private static string IdOf(RenderResult result, string suffix) => WorkflowValues.DerivedDatasetId(result.TargetId!, DatasetType, suffix);

    private static List<string> Links(RenderResult result, string path = "data.Datasets")
        => ((JsonArray)At(result.Document, path)!).Select(n => n!.GetValue<string>()).ToList();

    [Fact]
    public void A_step_taking_one_row_registers_one_dataset_its_name_names_and_the_record_links_it()
    {
        var result = Chain(FilesStep()).Render(Record("a", 1, ("lasfile", [File("a.las")])));

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        var id = IdOf(result, "enrich.lasfile");
        Assert.Equal([id + ":"], Links(result));
        var contribution = Assert.Single(result.Enrichment);
        var dataset = Assert.Single(contribution.Datasets);
        Assert.Equal((id, EnrichFiles.DefaultKind, 0), (dataset.Id, dataset.Kind, dataset.Row));
        Assert.Null(contribution.Mapping);
        Assert.Equal(["data.Datasets"], contribution.Effects.Select(e => e.Path));
        Assert.Contains(".", id[(id.LastIndexOf('-') + 1)..], StringComparison.Ordinal);
    }

    [Fact]
    public void A_dataset_keeps_its_id_whatever_its_files_hold_so_the_record_names_the_same_link()
    {
        var first = Chain(FilesStep()).Render(Record("a", 1, ("lasfile", [File("a.las", "h1", "log-a")])));
        var changed = Chain(FilesStep()).Render(Record("a", 1, ("lasfile", [File("b.las", "h2", "log-b")])));

        Assert.Equal(Links(first), Links(changed));
        Assert.Equal(first.MetadataHash, changed.MetadataHash);
        Assert.Equal(first.Enrichment[0].Datasets[0].Id, changed.Enrichment[0].Datasets[0].Id);
    }

    [Fact]
    public void A_step_taking_many_rows_registers_a_dataset_per_row_under_its_key_and_an_added_row_leaves_the_others_ids()
    {
        var step = FilesStep(rows: EnrichRows.Many);
        var one = Chain(step).Render(Record("a", 1, ("lasfile", [File("a.las")])));
        var two = Chain(step).Render(Record("a", 1, ("lasfile", [File("a.las"), File("b.las")])));

        var a = IdOf(one, $"enrich.lasfile.{DeliveryKey.Derive("lasfile", ["a.las"]).Value:N}");
        var b = IdOf(two, $"enrich.lasfile.{DeliveryKey.Derive("lasfile", ["b.las"]).Value:N}");
        Assert.Equal([a + ":"], Links(one));
        Assert.Equal([a + ":", b + ":"], Links(two));
        Assert.Equal([0, 1], two.Enrichment[0].Datasets.Select(d => d.Row));
    }

    [Fact]
    public void Two_rows_with_the_same_key_name_one_dataset_and_hold_the_record()
    {
        var result = Chain(FilesStep(rows: EnrichRows.Many)).Render(Record("a", 1, ("lasfile", [File("a.las", "h1"), File("a.las", "h2")])));

        Assert.True(result.IsHeld);
        Assert.Contains(result.Holds, h => h.StartsWith("enrich.lasfile: rows 1 and 2 of the step's dataset name the same dataset", StringComparison.Ordinal));
    }

    [Fact]
    public void A_row_whose_key_column_is_empty_holds_the_record_naming_the_column()
    {
        var result = Chain(FilesStep(rows: EnrichRows.Many)).Render(Record("a", 1, ("lasfile", [Row(("file_name", "  "), ("las_hash", "h"), ("las_folder", "f"))])));

        Assert.True(result.IsHeld);
        Assert.Contains(result.Holds, h => h.Contains("files.key column 'file_name' is empty in row 1", StringComparison.Ordinal));
    }

    [Fact]
    public void A_record_without_a_row_of_the_step_links_nothing_and_hashes_as_the_mapping_renders_it()
    {
        var plain = Renderer(Owner()).Render(Record("a", 1));
        var result = Chain(FilesStep()).Render(Record("a", 1, ("lasfile", [])));

        Assert.Equal(plain.MetadataHash, result.MetadataHash);
        Assert.Empty(Assert.Single(result.Enrichment).Datasets);
    }

    [Fact]
    public void The_links_go_after_what_the_record_lists_already_and_a_link_it_lists_is_not_added_twice()
    {
        var owner = new RecordChain(Renderer(Owner(data: "Aliases: [\"first:\"]")), [FilesStep(files: Files(link: "data.Aliases"))]);
        var result = owner.Render(Record("a", 1, ("lasfile", [File("a.las")])));
        var id = IdOf(result, "enrich.lasfile");
        Assert.Equal(["first:", id + ":"], Links(result, "data.Aliases"));

        var listed = new RecordChain(Renderer(Owner(data: $"Aliases: [\"{id}:\"]")), [FilesStep(files: Files(link: "data.Aliases"))]);
        Assert.Equal([id + ":"], Links(listed.Render(Record("a", 1, ("lasfile", [File("a.las")]))), "data.Aliases"));
    }

    [Fact]
    public void The_dataset_fragment_renders_each_dataset_record_from_its_row_and_the_record_before_it()
    {
        var result = Chain(FilesStep(datasetFragment: DatasetFragment)).Render(Record("a", 1, ("lasfile", [File("a.las")])));

        var dataset = Assert.Single(result.Enrichment[0].Datasets);
        Assert.Equal("a.las", dataset.Record["data"]!["Name"]!.GetValue<string>());
        Assert.Equal("a", dataset.Record["data"]!["Description"]!.GetValue<string>());
    }

    [Fact]
    public void A_step_with_a_fragment_and_files_adds_its_paths_and_its_links()
    {
        var grade = Fragment("""
            data:
              Symbol: { $from: file_name }
            """, name: "Grade");
        var result = Chain(FilesStep(fragment: grade)).Render(Record("a", 1, ("lasfile", [File("a.las")])));

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal("a.las", result.Document["data"]!["Symbol"]!.GetValue<string>());
        Assert.Single(Links(result));
        Assert.Equal(["data.Symbol", "data.Datasets"], result.Enrichment[0].Effects.Select(e => e.Path));
    }

    [Fact]
    public void A_step_whose_fragment_holds_registers_nothing_and_holds_the_record()
    {
        var strict = Fragment("""
            data:
              Symbol: { $from: grade }
            """, name: "Strict");
        var result = Chain(FilesStep(fragment: strict)).Render(Record("a", 1, ("lasfile", [File("a.las")])));

        Assert.True(result.IsHeld);
        Assert.Empty(result.Enrichment[0].Datasets);
        Assert.Null(At(result.Document, "data.Datasets"));
    }

    /// <summary>A flow with one step registering files, as a plan resolves its parts, read from a file under <paramref name="folder"/>.</summary>
    private static FlowDefinition Flow(string folder) => new DeliveryDocumentLoader().ParseFlow("""
        flowType: delivery
        name: demo
        source:
          connection: ${env:OSDU_DATA_DB}
          record:
            object: OsduData.silver.Thing
            key: [name]
          work: work
        render:
          mapping: Thing@1.0.0
          parameters: { dataPartition: dev }
        target:
          endpoint: https://example.org/api
          headers: { data-partition-id: dev }
          protocol: storage
        enrich:
          lasfile:
            dataset:
              object: OsduData.silver.ThingFile
              join: { name: name }
            files:
              root: ../data/las
              locationColumn: las_folder
              pattern: "*.las"
              hashColumn: las_hash
              contentType: text/plain
        """, Path.Combine(folder, "flows", "demo.yaml"));

    private static (RenderResult Render, string? Hold) Resolve(params IReadOnlyDictionary<string, string?>[] rows) => Resolve(null, false, rows);

    private static (RenderResult Render, string? Hold) Resolve(RecordState? state, bool hasPayload, params IReadOnlyDictionary<string, string?>[] rows)
    {
        var folder = Path.Combine(Path.GetTempPath(), "enrich-parts");
        var flow = Flow(folder);
        var step = FilesStep(files: flow.Enrich![0].Files) with { Step = flow.Enrich[0] };
        var record = Record("a", 1, ("lasfile", rows));
        var (render, hold, _) = EnrichParts.Resolve(flow, new Dictionary<string, string>(StringComparer.Ordinal), record, Chain(step).Render(record), [step], state, hasPayload);
        return (render, hold);
    }

    /// <summary>The record as the ledger holds it once <paramref name="delivered"/> landed: its hash, and the parts its delivery kept.</summary>
    private static RecordState Landed(RenderResult delivered, string? metadataHash = "", string? payloadHash = null)
    {
        var kept = new JsonObject { [EnrichParts.DatasetsValue] = EnrichParts.DatasetsText(delivered.StepParts.Select(p => p.DatasetId)) };
        foreach (var part in delivered.StepParts)
        {
            kept[EnrichParts.StateKey(part.DatasetId)] = part.Hash;
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
    public void A_plan_resolves_each_dataset_where_its_row_says_and_its_files_hash_moves_the_records_hash()
    {
        var (first, hold) = Resolve(File("a.las", "h1", "log-a"));
        Assert.Null(hold);
        var part = Assert.Single(first.StepParts);
        Assert.Equal(("lasfile", "h1", "text/plain"), (part.Step, part.FilesHash, part.ContentType));
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
        var earlier = Landed(delivered, metadataHash: EnrichParts.Fold("{\"other\":true}", delivered.StepParts.Select(p => (p.DatasetId, p.Hash))));
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
        Assert.Contains("enrich.lasfile: enrich.lasfile.files takes its folder from column 'las_folder'", noFolder, StringComparison.Ordinal);

        var (_, noHash) = Resolve(Row(("file_name", "a.las"), ("las_folder", "log-a")));
        Assert.Contains("column 'las_hash', which row 1 of the step's dataset leaves empty", noHash, StringComparison.Ordinal);

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
    public void A_target_state_lists_the_datasets_its_steps_registered_and_one_it_cannot_read_lists_none()
    {
        var state = new Dictionary<string, string>(StringComparer.Ordinal) { [EnrichParts.DatasetsValue] = EnrichParts.DatasetsText(["b", "a", "b"]) };
        Assert.Equal(["a", "b"], EnrichParts.Datasets(state));
        Assert.Empty(EnrichParts.Datasets(new Dictionary<string, string>(StringComparer.Ordinal) { [EnrichParts.DatasetsValue] = "not json" }));
        Assert.Empty(EnrichParts.Datasets(null));
    }
}
