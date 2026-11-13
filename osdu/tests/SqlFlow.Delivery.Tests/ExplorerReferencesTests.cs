using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The explorer's Referenced by (osdu/docs/reference/concepts/explorer.md): the types whose records name records of a
/// type, read from what the partition's Schema service holds. Every kind it lists is read once, bundled with the
/// schemas it refers to, each of those read once in a pass, and walked for the places its records name another record,
/// through references, branches, forms and items, a pattern alone marking a place where no relationship is declared. A
/// pass after the first reads only what may have changed; a kind that cannot be read is listed with why; a pass that
/// fails says why and is not retried by every ask; and while a pass reads, an ask sees where it stands and the reading
/// before it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ExplorerReferencesTests : IDisposable
{
    private const string Wellbore10 = "osdu:wks:master-data--Wellbore:1.0.0";
    private const string Wellbore11 = "osdu:wks:master-data--Wellbore:1.1.0";
    private const string CustomWellbore = "eq:custom:master-data--Wellbore:1.0.0";
    private const string Well10 = "osdu:wks:master-data--Well:1.0.0";
    private const string WellLog10 = "osdu:wks:work-product-component--WellLog:1.0.0";
    private const string TrajectoryType10 = "osdu:wks:reference-data--TrajectoryType:1.0.0";
    private const string Facility = "osdu:wks:AbstractFacility:1.0.0";
    private const string VerticalMeasurement = "osdu:wks:AbstractFacilityVerticalMeasurement:1.0.0";
    private const string MetaItem = "osdu:wks:AbstractMetaItem:1.0.0";
    private const string Unused = "osdu:wks:AbstractUnused:1.0.0";

    private readonly FakeOsduPlatform _platform = new();
    private readonly TestClock _clock = new();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }

        _platform.Dispose();
    }

    [Fact]
    public async Task The_types_naming_a_type_are_read_from_every_kind_the_service_holds_with_the_versions_that_do_and_those_that_do_not()
    {
        Partition();
        var index = Index();

        await index.Ensure(Open(), refresh: false)!;

        var trajectory = ExplorerReferences.Of("reference-data--TrajectoryType", index.View());
        Assert.Equal(ExplorerReferences.Ready, trajectory.State);
        Assert.Equal((10, 6, 0), (trajectory.Listed, trajectory.Kinds, trajectory.UnreadCount));
        var wellbore = Assert.Single(trajectory.Types);
        Assert.Equal("master-data--Wellbore", wellbore.EntityType);
        // By authority, then the oldest version first; a schema in development is listed with its status.
        Assert.Equal([(CustomWellbore, "DEVELOPMENT"), (Wellbore11, "PUBLISHED")], wellbore.Kinds.Select(k => (k.Kind, k.Status)));
        Assert.All(wellbore.Kinds, k => Assert.Equal([new ExplorerReferencePlace("data.TrajectoryTypeID", false)], k.Places));
        Assert.Equal([Wellbore10], wellbore.Without.Select(k => k.Kind));
        Assert.Empty(trajectory.AnyOfGroup);

        // Through every abstract schema, every form of meta's items, and a pattern alone where no relationship is declared;
        // master data first, then work products.
        var unit = ExplorerReferences.Of("reference-data--UnitOfMeasure", index.View());
        Assert.Equal(["master-data--Well", "master-data--Wellbore", "work-product-component--WellLog"], unit.Types.Select(t => t.EntityType));
        Assert.Equal(
            ["meta[].unitOfMeasureID", "data.VerticalMeasurements[].VerticalMeasurementUnitOfMeasureID"],
            unit.Types[0].Kinds.Single().Places.Select(p => p.At));
        Assert.Equal([CustomWellbore, Wellbore10, Wellbore11], unit.Types[1].Kinds.Select(k => k.Kind));
        Assert.Equal([new ExplorerReferencePlace("data.LocalUnit", true)], unit.Types[1].Kinds[0].Places);
        Assert.Empty(unit.Types[1].Without);
        Assert.Equal([new ExplorerReferencePlace("data.Curves[].CurveUnit", true)], unit.Types[2].Kinds.Single().Places);

        // A place naming any record of the type's group is answered apart, since it may hold one of the type.
        var file = ExplorerReferences.Of("dataset--File.Generic", index.View());
        Assert.Empty(file.Types);
        var any = Assert.Single(file.AnyOfGroup);
        Assert.Equal(("work-product-component--WellLog", "data.Datasets[]"), (any.EntityType, any.Kinds.Single().Places.Single().At));

        var log = ExplorerReferences.Of("master-data--Wellbore", index.View());
        Assert.Equal(("work-product-component--WellLog", "data.WellboreID"), (log.Types.Single().EntityType, log.Types.Single().Kinds.Single().Places.Single().At));

        // Each kind read once, each abstract schema read once however many kinds refer to it, and one no kind refers to
        // never read.
        foreach (var kind in new[] { Wellbore10, Wellbore11, CustomWellbore, Well10, WellLog10, TrajectoryType10, Facility, VerticalMeasurement, MetaItem })
        {
            Assert.Equal(1, _platform.SchemaReads(kind));
        }

        Assert.Equal(0, _platform.SchemaReads(Unused));

        // Every status and scope listed, and every call the Schema service's own.
        Assert.Equal(6, _platform.SchemaListings().Count);
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Schema);
    }

    [Fact]
    public async Task The_kinds_are_listed_without_reading_a_schema_the_partitions_own_naming_no_group_among_them_and_kept_until_due()
    {
        const string Completion = "acme:wells:Completion:1.0.0";
        Partition();
        _platform.Schemas[Completion] = Kind(data: Data(("WellboreID", Rel("master-data", "Wellbore"))));
        _platform.SchemaInfos[Completion] = ("DEVELOPMENT", "INTERNAL");
        var index = Index();

        await index.EnsureKinds(Open(), refresh: false)!;

        var view = index.KindsView();
        Assert.Equal(SchemaKindsView.Ready, view.State);
        // Every kind records can be of, the abstract building blocks left out, each in its status and scope; no schema read.
        Assert.Equal(
            [Completion, CustomWellbore, Well10, Wellbore10, Wellbore11, TrajectoryType10, WellLog10],
            view.Last!.Kinds.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("DEVELOPMENT", "INTERNAL"), (view.Last.Kinds[Completion].Status, view.Last.Kinds[Completion].Scope));
        Assert.Equal(11, view.Last.Listed);
        Assert.All(_platform.Schemas.Keys, id => Assert.Equal(0, _platform.SchemaReads(id)));
        Assert.Equal(6, _platform.SchemaListings().Count);

        // A listing stands until it is due, or a reader asks for one.
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(index.EnsureKinds(Open(), refresh: false));
        _platform.Schemas["acme:wells:SurveyRun:1.0.0"] = Kind(data: Data());
        await index.EnsureKinds(Open(), refresh: true)!;
        Assert.Contains("acme:wells:SurveyRun:1.0.0", index.KindsView().Last!.Kinds.Keys);
        _clock.Advance(PartitionSchemaIndex.KindsMaxAge + TimeSpan.FromSeconds(1));
        Assert.NotNull(index.EnsureKinds(Open(), refresh: false));

        // The pass reading every schema reads the partition's own kind too, so it is among the types naming a wellbore.
        await index.Ensure(Open(), refresh: false)!;
        var wellbore = ExplorerReferences.Of("master-data--Wellbore", index.View());
        Assert.Contains(wellbore.Types, t => t.EntityType == "Completion" && t.Kinds.Single().Kind == Completion);
    }

    [Fact]
    public async Task A_listing_of_the_kinds_the_service_refuses_says_why_and_is_not_retried_by_every_ask()
    {
        Partition();
        foreach (var status in SchemaServiceReader.Statuses)
        {
            foreach (var scope in SchemaServiceReader.Scopes)
            {
                _platform.SchemaListingRefusals[$"{status} {scope}"] = HttpStatusCode.Forbidden;
            }
        }

        var index = Index();
        await index.EnsureKinds(Open(), refresh: false)!;

        var view = index.KindsView();
        Assert.Equal((SchemaKindsView.Failed, (SchemaKindsListing?)null), (view.State, view.Last));
        Assert.False(string.IsNullOrEmpty(view.Problem));
        Assert.Null(index.EnsureKinds(Open(), refresh: false));
        Assert.Equal(SchemaKindsView.Failed, ExplorerSchemas.Of(view).State);
    }

    [Fact]
    public async Task A_pass_after_the_first_reads_only_the_kinds_added_and_those_in_development_and_drops_those_no_longer_listed()
    {
        Partition();
        var index = Index();
        await index.Ensure(Open(), refresh: false)!;
        var first = index.View().Last!.ReadUtc;

        // Nothing is due: an ask answers from what was read, and reads nothing.
        Assert.Null(index.Ensure(Open(), refresh: false));

        _platform.Schemas["osdu:wks:master-data--Wellbore:1.2.0"] = Kind(data: Data(("TrajectoryTypeID", Rel("reference-data", "TrajectoryType"))));
        _platform.Schemas[CustomWellbore] = Kind(data: Data(("TrajectoryTypeID", Rel("reference-data", "TrajectoryType")), ("UnitID", Rel("reference-data", "UnitOfMeasure"))));
        _platform.Schemas.Remove(Well10);
        _clock.Advance(TimeSpan.FromMinutes(1));

        await index.Ensure(Open(), refresh: true)!;

        Assert.Equal(1, _platform.SchemaReads("osdu:wks:master-data--Wellbore:1.2.0"));
        Assert.Equal(2, _platform.SchemaReads(CustomWellbore));
        foreach (var settled in new[] { Wellbore10, Wellbore11, WellLog10, TrajectoryType10, Facility })
        {
            Assert.Equal(1, _platform.SchemaReads(settled));
        }

        var view = index.View();
        Assert.True(view.Last!.ReadUtc > first);
        Assert.False(view.Last.Kinds.ContainsKey(Well10));
        var unit = ExplorerReferences.Of("reference-data--UnitOfMeasure", view);
        Assert.Equal(["data.UnitID"], unit.Types.Single(t => t.EntityType == "master-data--Wellbore").Kinds[0].Places.Select(p => p.At));
        Assert.DoesNotContain(unit.Types, t => t.EntityType == "master-data--Well");
        Assert.Equal(
            [CustomWellbore, Wellbore11, "osdu:wks:master-data--Wellbore:1.2.0"],
            ExplorerReferences.Of("reference-data--TrajectoryType", view).Types.Single().Kinds.Select(k => k.Kind));

        // A reading older than its age is brought up to date by the next ask.
        _clock.Advance(PartitionSchemaIndex.MaxAge + TimeSpan.FromMinutes(1));
        Assert.NotNull(index.Ensure(Open(), refresh: false));
    }

    [Fact]
    public async Task Every_page_of_a_listing_is_read_each_kind_once()
    {
        for (var i = 0; i < 230; i++)
        {
            _platform.Schemas[$"osdu:wks:reference-data--Code{i:D3}:1.0.0"] = Kind(data: Data(("ParentID", Rel("reference-data", "Parent"))));
        }

        var index = Index();
        await index.Ensure(Open(), refresh: false)!;

        var parent = ExplorerReferences.Of("reference-data--Parent", index.View());
        Assert.Equal(230, parent.Types.Count);
        Assert.Equal(230, parent.Kinds);
        var shared = _platform.SchemaListings().Where(u => u.Query.Contains("status=PUBLISHED", StringComparison.Ordinal) && u.Query.Contains("scope=SHARED", StringComparison.Ordinal)).ToList();
        Assert.Equal(["0", "100", "200"], shared.Select(u => System.Web.HttpUtility.ParseQueryString(u.Query)["offset"]));
        Assert.All(shared, u => Assert.Equal("100", System.Web.HttpUtility.ParseQueryString(u.Query)["limit"]));
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Schema);
    }

    [Fact]
    public async Task A_kind_that_cannot_be_read_is_listed_with_why_and_a_listing_refused_as_asked_is_noted()
    {
        Partition();
        _platform.SchemaFailures[TrajectoryType10] = HttpStatusCode.InternalServerError;
        _platform.Schemas.Remove(TrajectoryType10);
        _platform.SchemaListingRefusals["OBSOLETE INTERNAL"] = HttpStatusCode.BadRequest;
        var index = Index();

        await index.Ensure(Open(), refresh: false)!;

        var answer = ExplorerReferences.Of("reference-data--TrajectoryType", index.View());
        Assert.Equal(ExplorerReferences.Ready, answer.State);
        Assert.Equal(1, answer.UnreadCount);
        Assert.Equal(TrajectoryType10, answer.Unread.Single().Kind);
        Assert.Contains("500", answer.Unread.Single().Why, StringComparison.Ordinal);
        Assert.Contains(answer.Notes, n => n.Contains("refused to list its obsolete internal schemas", StringComparison.Ordinal));
        Assert.Single(answer.Types);

        // The next pass reads again what could not be read.
        _platform.SchemaFailures.Remove(TrajectoryType10);
        _platform.Schemas[TrajectoryType10] = Kind(data: Data(("Code", new JsonObject { ["type"] = "string" })));
        await index.Ensure(Open(), refresh: true)!;
        Assert.Equal(0, ExplorerReferences.Of("reference-data--TrajectoryType", index.View()).UnreadCount);
    }

    [Fact]
    public async Task A_pass_that_fails_says_why_and_another_starts_only_when_asked_for_or_after_a_while()
    {
        Partition();
        _platform.SchemaListingRefusals["PUBLISHED SHARED"] = HttpStatusCode.Forbidden;
        var index = Index();

        await index.Ensure(Open(), refresh: false)!;

        var failed = ExplorerReferences.Of("reference-data--TrajectoryType", index.View());
        Assert.Equal(ExplorerReferences.Failed, failed.State);
        Assert.Contains("403", failed.Problem, StringComparison.Ordinal);
        Assert.Null(failed.ReadUtc);

        // Every ask does not try again: the failure stands until a pass is asked for, or a while has passed.
        var listings = _platform.SchemaListings().Count;
        Assert.Null(index.Ensure(Open(), refresh: false));
        Assert.Equal(listings, _platform.SchemaListings().Count);

        await index.Ensure(Open(), refresh: true)!;
        Assert.Equal(ExplorerReferences.Failed, ExplorerReferences.Of("reference-data--TrajectoryType", index.View()).State);

        _platform.SchemaListingRefusals.Clear();
        _clock.Advance(PartitionSchemaIndex.RetryAfter + TimeSpan.FromSeconds(1));
        await index.Ensure(Open(), refresh: false)!;
        var ready = ExplorerReferences.Of("reference-data--TrajectoryType", index.View());
        Assert.Equal((ExplorerReferences.Ready, null), (ready.State, ready.Problem));
        Assert.Single(ready.Types);
    }

    [Fact]
    public async Task A_pass_that_cannot_read_most_kinds_stops_and_says_so()
    {
        for (var i = 0; i < PartitionSchemaIndex.MaxFailures + 10; i++)
        {
            _platform.SchemaFailures[$"osdu:wks:reference-data--Broken{i:D3}:1.0.0"] = HttpStatusCode.BadGateway;
        }

        var index = Index();
        await index.Ensure(Open(), refresh: false)!;

        var answer = ExplorerReferences.Of("reference-data--Parent", index.View());
        Assert.Equal(ExplorerReferences.Failed, answer.State);
        Assert.Contains("so the pass stopped", answer.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task While_a_pass_reads_an_ask_sees_where_it_stands_and_the_reading_before_it()
    {
        Partition();
        var gate = new GatedTransport(_platform);
        var index = Index();

        // The first pass, before anything is read: where it stands, and no types yet.
        var pass = index.Ensure(Open(gate), refresh: false)!;
        var reading = await StandingAsync(index);
        var answer = ExplorerReferences.Of("reference-data--TrajectoryType", index.View());
        Assert.Equal(ExplorerReferences.Reading, answer.State);
        Assert.Equal((false, 10, 6, 0), (reading.Listing, reading.Listed, reading.ToRead, reading.Read));
        Assert.Empty(answer.Types);
        Assert.Null(answer.ReadUtc);
        // Another ask joins the pass under way.
        Assert.Same(pass, index.Ensure(Open(gate), refresh: true));

        gate.Open();
        await pass;
        Assert.Equal(ExplorerReferences.Ready, ExplorerReferences.Of("reference-data--TrajectoryType", index.View()).State);

        // A pass bringing it up to date: the reading before it answers meanwhile.
        _platform.Schemas["osdu:wks:master-data--Wellbore:1.2.0"] = Kind(data: Data(("TrajectoryTypeID", Rel("reference-data", "TrajectoryType"))));
        gate.Close();
        var refresh = index.Ensure(Open(gate), refresh: true)!;
        await StandingAsync(index);
        var meanwhile = ExplorerReferences.Of("reference-data--TrajectoryType", index.View());
        Assert.Equal(ExplorerReferences.Reading, meanwhile.State);
        Assert.NotNull(meanwhile.Progress);
        Assert.Equal([CustomWellbore, Wellbore11], meanwhile.Types.Single().Kinds.Select(k => k.Kind));

        gate.Open();
        await refresh;
        Assert.Equal(3, ExplorerReferences.Of("reference-data--TrajectoryType", index.View()).Types.Single().Kinds.Count);
    }

    [Fact]
    public void Every_place_is_found_through_references_branches_forms_and_items_and_a_pattern_alone_marks_one()
    {
        var schema = new SchemaSnapshot("osdu:wks:master-data--Thing:1.0.0", JsonNode.Parse("""
            {
              "definitions": {
                "Node": {
                  "type": "object",
                  "properties": {
                    "ParentID": { "type": "string", "x-osdu-relationship": [{ "GroupType": "reference-data", "EntityType": "Node" }] },
                    "Children": { "type": "array", "items": { "$ref": "#/definitions/Node" } }
                  }
                }
              },
              "type": "object",
              "properties": {
                "id": { "type": "string", "pattern": "^[\\w\\-\\.]+:master-data\\-\\-Thing:[\\w\\-\\.\\:\\%]+$" },
                "data": {
                  "type": "object",
                  "properties": {
                    "Tree": { "$ref": "#/definitions/Node" },
                    "Ids": {
                      "type": "array",
                      "x-osdu-relationship": [{ "GroupType": "master-data", "EntityType": "Well" }],
                      "items": { "type": "string", "pattern": "^[\\w\\-\\.]+:master-data\\-\\-Wellbore:[\\w\\-\\.\\:\\%]+[0-9]*$" }
                    },
                    "Either": { "type": "string", "pattern": "^[\\w\\-\\.]+:(work-product-component\\-\\-WellLog|work-product-component\\-\\-WellboreTrajectory):[\\w\\-\\.\\:\\%]+[0-9]*$" },
                    "Code": { "type": "string", "pattern": "^[A-Z]{2}-[0-9]+$" },
                    "Tags": { "type": "object", "additionalProperties": { "type": "string", "x-osdu-relationship": [{ "GroupType": "master-data", "EntityType": "Field" }] } },
                    "Choice": {
                      "oneOf": [
                        { "type": "string", "x-osdu-relationship": [{ "GroupType": "master-data", "EntityType": "Well" }] },
                        { "type": "string", "x-osdu-relationship": [{ "GroupType": "master-data", "EntityType": "Wellbore" }] }
                      ]
                    },
                    "Files": { "type": "array", "items": { "type": "string", "x-osdu-relationship": [{ "GroupType": "dataset" }] } }
                  }
                }
              }
            }
            """)!.AsObject(), DateTimeOffset.UnixEpoch);

        var found = SchemaRelationships.Of(SchemaRules.Of(schema));

        Assert.False(found.Cut);
        Assert.Equal(
            [
                ("data.Tree.ParentID", "reference-data--Node", false),
                ("data.Ids", "master-data--Well", false),
                ("data.Either", "work-product-component--WellLog,work-product-component--WellboreTrajectory", true),
                ("data.Tags.*", "master-data--Field", false),
                ("data.Choice", "master-data--Well,master-data--Wellbore", false),
                ("data.Files[]", "dataset", false),
            ],
            found.Places.Select(p => (p.At, string.Join(',', p.Names), p.ByPattern)));
    }

    [Fact]
    public void An_osdu_schema_names_records_through_meta_its_abstract_schemas_its_choices_and_lists_inside_lists_each_place_declared()
    {
        // WellLog 1.5.0 as the Open Group's data definitions publish it (Fixtures/templates), bundled.
        var found = SchemaRelationships.Of(SchemaRules.Of(Samples.SampleTemplate("osdu:wks:work-product-component--WellLog:1.5.0")));
        var places = found.Places.ToDictionary(p => p.At, p => p.Names);

        Assert.False(found.Cut);
        Assert.Equal(["reference-data--UnitOfMeasure"], places["meta[].unitOfMeasureID"]);
        Assert.Equal(["master-data--Wellbore"], places["data.WellboreID"]);
        Assert.Equal(["reference-data--UnitOfMeasure"], places["data.Curves[].CurveUnit"]);
        Assert.Equal(["dataset"], places["data.Datasets[]"]);
        Assert.Equal(["master-data--Organisation"], places["data.TechnicalAssurances[].Reviewers[].OrganisationID"]);
        Assert.Contains("reference-data--GeoPoliticalEntityType", places["data.GeoContexts[].GeoTypeID"]);
        Assert.Equal(["master-data--Basin"], places["data.GeoContexts[].BasinID"]);
        // OSDU marks every reference it makes, and the record's own id is none.
        Assert.DoesNotContain(found.Places, p => p.ByPattern);
        Assert.False(places.ContainsKey("id"));
    }

    [Theory]
    [InlineData("reference-data--UnitOfMeasure", "reference-data--UnitOfMeasure")]
    [InlineData(" osdu:wks:reference-data--UnitOfMeasure:1.0.0 ", "reference-data--UnitOfMeasure")]
    [InlineData("*:*:master-data--Wellbore:*", "master-data--Wellbore")]
    [InlineData("*:*:master-data--*:*", null)]
    [InlineData("UnitOfMeasure", null)]
    [InlineData("osdu:wks:reference-data--UnitOfMeasure", null)]
    [InlineData("reference-data--Unit Of Measure", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_type_is_named_by_its_group_and_type_or_by_a_kind_naming_one(string? asked, string? named)
        => Assert.Equal(named, ExplorerReferences.EntityTypeOf(asked));

    [Fact]
    public void A_type_too_long_to_be_one_is_refused()
        => Assert.Null(ExplorerReferences.EntityTypeOf("reference-data--" + new string('A', ExplorerReferences.MaxTypeLength)));

    [Fact]
    public async Task The_task_answers_from_the_index_the_engine_keeps_and_a_host_without_one_reads_every_schema_for_the_ask()
    {
        Partition();
        var flowFile = await FlowFileAsync();
        using var indexes = new PartitionSchemaIndexes(_clock, NullLoggerFactory.Instance);
        var kept = new ExploreOperation(Samples.Engine(ledger: null, time: _clock) with { SchemaIndexes = indexes }, _platform, allowLoopback: true);

        var answered = JsonNode.Parse(await kept.ExecuteAsync(Ask(flowFile, new() { [ExploreOperation.TypeArgument] = "osdu:wks:reference-data--TrajectoryType:1.0.0" }), CancellationToken.None))!;
        Assert.Equal(("dev", "explorer-refs"), (answered["partition"]!.GetValue<string>(), answered["connection"]!.GetValue<string>()));
        var answer = answered["answer"]!;
        Assert.Equal(("reference-data--TrajectoryType", "ready"), (answer["entityType"]!.GetValue<string>(), answer["state"]!.GetValue<string>()));
        var type = answer["types"]!.AsArray().Single()!;
        Assert.Equal("master-data--Wellbore", type["entityType"]!.GetValue<string>());
        Assert.Equal(
            ["data.TrajectoryTypeID"],
            type["kinds"]!.AsArray()[1]!["places"]!.AsArray().Select(p => p!["at"]!.GetValue<string>()));
        Assert.Equal(Wellbore10, type["without"]!.AsArray().Single()!["kind"]!.GetValue<string>());
        Assert.Null(answer["problem"]);

        // The next ask, of another type, reads nothing: the engine kept the partition's schemas.
        var reads = _platform.SchemaReads(Wellbore10);
        var again = JsonNode.Parse(await kept.ExecuteAsync(Ask(flowFile, new() { [ExploreOperation.TypeArgument] = "reference-data--UnitOfMeasure" }), CancellationToken.None))!;
        Assert.Equal(3, again["answer"]!["types"]!.AsArray().Count);
        Assert.Equal(reads, _platform.SchemaReads(Wellbore10));
        Assert.All(_platform.Calls, call => Assert.Equal("dev", call.Headers["data-partition-id"]));

        // A host that keeps no index reads every schema for the ask, and answers once they are read.
        var bare = new ExploreOperation(Samples.Engine(ledger: null, time: _clock), _platform, allowLoopback: true);
        var once = JsonNode.Parse(await bare.ExecuteAsync(Ask(flowFile, new() { [ExploreOperation.TypeArgument] = "reference-data--TrajectoryType" }), CancellationToken.None))!;
        Assert.Equal("ready", once["answer"]!["state"]!.GetValue<string>());
        Assert.Equal(reads + 1, _platform.SchemaReads(Wellbore10));

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => kept.ExecuteAsync(Ask(flowFile, new() { [ExploreOperation.TypeArgument] = "UnitOfMeasure" }), CancellationToken.None));
        Assert.Contains("is not a type", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A partition of six kinds and four abstract schemas: two versions of Wellbore from OSDU (the newer naming a trajectory
    /// type), one of the partition's own in development, a Well, a WellLog naming a wellbore, its curves' units by a pattern
    /// alone and its datasets by group, and a trajectory type; the abstract schemas give a facility its vertical
    /// measurements and meta its forms, and one is referred to by none.
    /// </summary>
    private void Partition()
    {
        _platform.Schemas[MetaItem] = new JsonObject
        {
            ["oneOf"] = new JsonArray(
                Object(("kind", new JsonObject { ["const"] = "Unit" }), ("unitOfMeasureID", Rel("reference-data", "UnitOfMeasure"))),
                Object(("kind", new JsonObject { ["const"] = "CRS" }), ("coordinateReferenceSystemID", Rel("reference-data", "CoordinateReferenceSystem")))),
        };
        _platform.Schemas[VerticalMeasurement] = Object(
            ("VerticalMeasurementUnitOfMeasureID", Rel("reference-data", "UnitOfMeasure")),
            ("VerticalCRSID", Rel("reference-data", "CoordinateReferenceSystem")));
        _platform.Schemas[Facility] = Object(("VerticalMeasurements", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["$ref"] = VerticalMeasurement } }));
        _platform.Schemas[Unused] = Object(("Anything", Rel("reference-data", "TrajectoryType")));

        _platform.Schemas[Wellbore10] = Kind(meta: true, data: Facilitated(("WellID", Rel("master-data", "Well"))));
        _platform.Schemas[Wellbore11] = Kind(meta: true, data: Facilitated(("WellID", Rel("master-data", "Well")), ("TrajectoryTypeID", Rel("reference-data", "TrajectoryType"))));
        _platform.Schemas[CustomWellbore] = Kind(data: Data(("TrajectoryTypeID", Rel("reference-data", "TrajectoryType")), ("LocalUnit", PatternOnly("reference-data", "UnitOfMeasure"))));
        _platform.SchemaInfos[CustomWellbore] = ("DEVELOPMENT", "INTERNAL");
        _platform.Schemas[Well10] = Kind(meta: true, data: Facilitated());
        _platform.Schemas[WellLog10] = Kind(data: Data(
            ("WellboreID", Rel("master-data", "Wellbore")),
            ("Curves", new JsonObject { ["type"] = "array", ["items"] = Object(("CurveUnit", PatternOnly("reference-data", "UnitOfMeasure"))) }),
            ("Datasets", new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string", ["x-osdu-relationship"] = new JsonArray(new JsonObject { ["GroupType"] = "dataset" }) },
            })));
        _platform.Schemas[TrajectoryType10] = Kind(data: Data(("Code", new JsonObject { ["type"] = "string" })));
    }

    private static JsonObject Kind(JsonObject data, bool meta = false)
    {
        var properties = new JsonObject { ["id"] = new JsonObject { ["type"] = "string" }, ["kind"] = new JsonObject { ["type"] = "string" } };
        if (meta)
        {
            properties["meta"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["$ref"] = MetaItem } };
        }

        properties["data"] = data;
        return new JsonObject { ["$schema"] = "http://json-schema.org/draft-07/schema#", ["type"] = "object", ["properties"] = properties };
    }

    /// <summary>A record's data made of the facility's abstract schema and properties of its own, as OSDU composes it.</summary>
    private static JsonObject Facilitated(params (string Name, JsonNode Schema)[] own)
        => new() { ["allOf"] = new JsonArray(new JsonObject { ["$ref"] = Facility }, Object(own)) };

    private static JsonObject Data(params (string Name, JsonNode Schema)[] own) => Object(own);

    private static JsonObject Object(params (string Name, JsonNode Schema)[] properties)
    {
        var held = new JsonObject();
        foreach (var (name, schema) in properties)
        {
            held[name] = schema;
        }

        return new JsonObject { ["type"] = "object", ["properties"] = held };
    }

    /// <summary>A reference as OSDU writes one: the id pattern, and the relationship it declares.</summary>
    private static JsonObject Rel(string group, string entity) => new()
    {
        ["type"] = "string",
        ["pattern"] = $"^[\\w\\-\\.]+:{group}\\-\\-{entity}:[\\w\\-\\.\\:\\%]+[0-9]*$",
        ["x-osdu-relationship"] = new JsonArray(new JsonObject { ["GroupType"] = group, ["EntityType"] = entity }),
    };

    /// <summary>A reference a partition's own schema may write: the id pattern alone.</summary>
    private static JsonObject PatternOnly(string group, string entity) => new()
    {
        ["type"] = "string",
        ["pattern"] = $"^[\\w\\-\\.]+:{group}\\-\\-{entity}:[\\w\\-\\.\\:\\%]+[0-9]*$",
    };

    private PartitionSchemaIndex Index()
    {
        var index = new PartitionSchemaIndex("dev", _clock, NullLogger.Instance);
        _owned.Add(index);
        return index;
    }

    /// <summary>A connection to the fake platform for a pass, through <paramref name="transport"/> where a test gates it.</summary>
    private Func<CancellationToken, Task<SchemaIndexConnection>> Open(HttpMessageHandler? transport = null)
        => _ =>
        {
            var runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                new SecretResolver([new EnvSecretProvider()]), _clock, transport ?? _platform, allowLoopback: true);
            var client = new OsduHttpClient(
                runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
            return Task.FromResult(new SchemaIndexConnection(client, runtime));
        };

    /// <summary>Where the pass under way stands once it has listed the schemas and is held reading them.</summary>
    private static async Task<SchemaIndexProgress> StandingAsync(PartitionSchemaIndex index)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            if (index.View().Progress is { Listing: false } progress)
            {
                return progress;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The pass did not list the schemas in time.");
    }

    private static async Task<string> FlowFileAsync()
    {
        var root = Samples.NewTempDirectory();
        var flowFile = Path.Combine(root, "explorer-refs.yaml");
        await File.WriteAllTextAsync(flowFile, """
            flowType: delivery
            name: explorer-refs
            partitions:
              - dev
            source:
              connection: ${env:OSDU_DATA_DB}
              record: { object: OsduData.ing.Record, key: [record_id] }
              work: work
            render:
              mapping: Targeting@1.0.0
            target:
              endpoint: http://localhost
              protocol: storage
            """);
        return flowFile;
    }

    private static ComputeTaskPayload Ask(string flowFile, Dictionary<string, string> arguments)
    {
        arguments["flowFile"] = flowFile;
        arguments[ExploreOperation.ActionArgument] = ExploreOperation.ReferencedByAction;
        arguments[DeliveryOperation.PartitionArgument] = "dev";
        return new ComputeTaskPayload { Operation = ExploreOperation.OperationName, SourceRef = "explorer-refs", Arguments = arguments };
    }

    /// <summary>The fake platform behind a gate that holds every read of a schema by id until it is opened.</summary>
    private sealed class GatedTransport(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private volatile TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => _open.TrySetResult();

        public void Close() => _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/schema-service/v1/schema/", StringComparison.Ordinal))
            {
                await _open.Task.WaitAsync(cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
