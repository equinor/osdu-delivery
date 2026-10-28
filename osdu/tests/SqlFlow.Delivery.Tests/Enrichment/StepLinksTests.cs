using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Preview;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A list an update carries forward from the record OSDU holds (<c>target.protocolOptions.preserveDataKeys</c>) that a step of
/// <c>enrich:</c> links its datasets in (osdu/docs/reference/flow/enrich-files.md, Preserved keys): OSDU's items stay in OSDU's
/// order, other systems' among them; the step's links the document has are added, each once; those the last delivery wrote and
/// the document no longer has are taken off; and a list the steps link nothing in, now or before, is copied whole as before.
/// </summary>
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class StepLinksTests
{
    private const string Foreign = "dev:dataset--File.Generic:from-elsewhere:";
    private const string Las = "dev:dataset--File.Generic:log-1-enrich.lasfile";
    private const string Dlis = "dev:dataset--File.Generic:log-1-enrich.dlis";
    private const string Old = "dev:dataset--File.Generic:log-1-enrich.old";

    private static JsonObject Record(params string[] datasets) => new()
    {
        ["data"] = new JsonObject
        {
            ["Name"] = "log 1",
            ["Datasets"] = new JsonArray(datasets.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()),
        },
    };

    /// <summary>The list the update writes, once <paramref name="stored"/>'s preserved <c>Datasets</c> is carried into <paramref name="document"/>.</summary>
    private static List<string> Written(JsonObject stored, JsonObject document, StepLinks links)
    {
        RecordWriter.Preserve(stored, document, ["Datasets"], links);
        return document["data"]!["Datasets"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
    }

    [Fact]
    public void A_steps_link_is_added_after_the_links_OSDU_holds_and_what_else_the_document_renders_there_is_not()
    {
        var document = Record("dev:dataset--File.Generic:from-the-mapping:", EnrichParts.Link(Las));

        Assert.Equal([Foreign, EnrichParts.Link(Las)], Written(Record(Foreign), document, StepLinks.Of([Las], [])));
    }

    [Fact]
    public void A_link_the_last_delivery_wrote_that_the_document_no_longer_has_is_taken_off_and_other_systems_links_stay()
    {
        var document = new JsonObject { ["data"] = new JsonObject { ["Name"] = "log 1" } };

        Assert.Equal([Foreign], Written(Record(EnrichParts.Link(Las), Foreign), document, StepLinks.Of([], [Las])));
    }

    [Fact]
    public void A_link_OSDU_holds_already_keeps_its_place_and_none_is_listed_twice()
    {
        var stored = Record(EnrichParts.Link(Las), Foreign, EnrichParts.Link(Las));
        var document = Record(EnrichParts.Link(Dlis), EnrichParts.Link(Las));

        Assert.Equal(
            [EnrichParts.Link(Las), Foreign, EnrichParts.Link(Dlis)],
            Written(stored, document, StepLinks.Of([Las, Dlis], [Las])));
    }

    [Fact]
    public void A_list_the_steps_link_nothing_in_now_or_before_is_copied_whole_as_before()
    {
        Assert.Equal([Foreign], Written(Record(Foreign), Record("dev:dataset--File.Generic:rendered:"), StepLinks.None));

        // Steps whose links are in another list leave this one as OSDU holds it.
        Assert.Equal([Foreign], Written(Record(Foreign), Record("dev:dataset--File.Generic:rendered:"), StepLinks.Of([Las], [Dlis])));
    }

    [Fact]
    public void A_record_OSDU_holds_without_the_list_keeps_the_one_rendered()
    {
        var stored = new JsonObject { ["data"] = new JsonObject { ["Name"] = "log 1" } };

        Assert.Equal([EnrichParts.Link(Las)], Written(stored, Record(EnrichParts.Link(Las)), StepLinks.Of([Las], [])));
    }

    [Fact]
    public void A_preserved_value_OSDU_holds_that_is_not_a_list_is_copied_whole()
    {
        var stored = new JsonObject { ["data"] = new JsonObject { ["Datasets"] = "not a list" } };
        var document = Record(EnrichParts.Link(Las));

        RecordWriter.Preserve(stored, document, ["Datasets"], StepLinks.Of([Las], [Las]));

        Assert.Equal("not a list", document["data"]!["Datasets"]!.GetValue<string>());
    }

    [Fact]
    public void A_deliverys_step_links_are_its_registered_and_kept_datasets_now_and_those_its_last_delivery_wrote()
    {
        var work = new DeliveryWork
        {
            Key = DeliveryKey.Derive("step-links", ["log-1"]),
            TargetId = "dev:work-product-component--WellLog:log-1",
            Document = Record(EnrichParts.Link(Las), EnrichParts.Link(Dlis)),
            DeliverMetadata = true,
            DeliverPayload = false,
            StepParts = [new WorkStepPart(new EnrichPart("lasfile", Las, EnrichFiles.DefaultKind, "mem://las/*", "h1", "{}", null), new MemoryFiles(("a.las", "~Version")))],
            StepKeptDatasets = [Dlis],
            TargetState = new Dictionary<string, string>(StringComparer.Ordinal) { [EnrichParts.DatasetsValue] = EnrichParts.DatasetsText([Old, Las]) },
        };

        var written = Written(Record(EnrichParts.Link(Old), Foreign), work.Document, StepLinks.Of(work));

        Assert.Equal([Foreign, EnrichParts.Link(Las), EnrichParts.Link(Dlis)], written);
    }

    [Fact]
    public void The_preview_says_a_preserved_list_a_step_links_its_datasets_in_merges()
    {
        var flow = Samples.Targeting(new FlowTarget
        {
            Endpoint = FakeOsduPlatform.Endpoint,
            Protocol = DeliveryProtocol.Storage,
            ProtocolOptions = new ProtocolOptions { PreserveDataKeys = ["Datasets", "Aliases"] },
        }) with
        {
            Enrich =
            [
                new EnrichStep
                {
                    Name = "lasfile",
                    ReadsRows = true,
                    Files = new EnrichFiles { Payload = new FlowPayload { Root = "../data/las", LocationColumn = "las_folder", HashColumn = "las_hash" } },
                },
            ],
        };

        var notes = RouteRehearsal.Rehearse(flow, Record(EnrichParts.Link(Las)), "dev:work-product-component--WellLog:log-1", [], null).Notes;

        Assert.Contains(notes, n => n.StartsWith("An update copies data.Datasets, data.Aliases forward from the record OSDU holds", StringComparison.Ordinal));
        Assert.Contains("data.Datasets merges instead: the update keeps what OSDU holds there, adds the links the steps of enrich: render now and takes off those they no longer do.", notes);
    }
}
