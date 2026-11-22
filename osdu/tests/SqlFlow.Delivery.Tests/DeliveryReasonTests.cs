using System.Text.Json.Nodes;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Storage;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Why a record was delivered (osdu/docs/reference/concepts/delivery-reasons.md): the paths of a
/// document and what changed between two renders, every kind of trigger alone and together, the words a plan says its
/// decisions in, and the forms the reason travels in.
/// </summary>
public sealed class DeliveryReasonTests
{
    private static readonly DateTime At = new(2026, 11, 17, 8, 0, 0, DateTimeKind.Utc);

    private const string OldFingerprint = "1f0c8b2e44d9a7c6b5e3f2a1908d7c6b5a4f3e2d1c0b9a8f7e6d5c4b3a291807";
    private const string NewFingerprint = "9a8b7c6d5e4f30211f0e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a6978";
    private const string OldCommit = "268b2527d1c04e2fa3b9c8d7e6f5a4b3c2d1e0f9";
    private const string NewCommit = "852db64a9c0e4f7b8d1e2a3b4c5d6e7f80912345";

    // --- The paths of a document ------------------------------------------------------------------------------------

    [Fact]
    public void A_documents_paths_descend_objects_and_take_an_array_or_an_empty_object_as_one_value()
    {
        var paths = RenderPaths.Of(Canonical("""
            {"acl":{"owners":["o@x"],"viewers":["v1@x","v2@x"]},"data":{"Curves":[{"Mnemonic":"GR"},{"Mnemonic":"DT"}],"Name":"C-8","Extra":{}},
             "tags":{"dra.lraID":"L1","nativeUID":"N1"},"kind":"osdu:wks:work-product-component--WellLog:1.4.0"}
            """));

        Assert.Equal(
            ["acl.owners", "acl.viewers", "data.Curves", "data.Extra", "data.Name", "kind", "tags.nativeUID", "tags['dra.lraID']"],
            paths.Paths);
    }

    [Fact]
    public void Two_renders_compare_path_by_path_naming_what_was_added_removed_and_changed_without_their_values()
    {
        var before = RenderPaths.Of(Canonical("""{"acl":{"viewers":["a@x"]},"data":{"Name":"C-8","Top":1},"tags":{"nativeUID":"N1"}}"""));
        var after = RenderPaths.Of(Canonical("""{"acl":{"viewers":["b@x"]},"data":{"Name":"C-8","Top":1},"tags":{"dra.lraID":"L1"}}"""));

        var changes = RenderPaths.Compare(before, after);

        Assert.True(changes.Compared);
        Assert.Equal(
            [new PathChange("acl.viewers", PathChangeKind.Changed), new PathChange("tags.nativeUID", PathChangeKind.Removed), new PathChange("tags['dra.lraID']", PathChangeKind.Added)],
            changes.Listed);
        Assert.Equal((1, 1, 1, 0), (changes.Added, changes.Removed, changes.Changed, changes.More));
    }

    [Fact]
    public void An_array_whose_items_move_is_one_changed_path()
    {
        var before = RenderPaths.Of(Canonical("""{"acl":{"viewers":["a@x","b@x"]}}"""));
        var after = RenderPaths.Of(Canonical("""{"acl":{"viewers":["b@x","a@x"]}}"""));

        Assert.Equal([new PathChange("acl.viewers", PathChangeKind.Changed)], RenderPaths.Compare(before, after).Listed);
        Assert.Equal(0, RenderPaths.Compare(before, before).Total);
    }

    [Fact]
    public void The_paths_a_reason_lists_are_capped_and_the_rest_counted()
    {
        var before = RenderPaths.Of(Canonical(Object(Enumerable.Range(0, 80).Select(i => ($"P{i:D3}", "1")))));
        var after = RenderPaths.Of(Canonical(Object(Enumerable.Range(0, 80).Select(i => ($"P{i:D3}", "2")))));

        var changes = RenderPaths.Compare(before, after);

        Assert.Equal(RenderPaths.MaxListed, changes.Listed.Count);
        Assert.Equal(80, changes.Changed);
        Assert.Equal(30, changes.More);
        Assert.Equal("data.P000", changes.Listed[0].Path);
    }

    [Fact]
    public void The_kept_form_reads_back_as_written_and_a_damaged_one_is_refused_with_why()
    {
        var paths = RenderPaths.Of(Canonical("""{"acl":{"viewers":["a"]},"data":{"Name":"x","Top":1.5},"tags":{"dra.lraID":"L","ä":"ü"}}"""));
        var stored = paths.Encode();

        var read = RenderPaths.Decode(stored, out var problem);

        Assert.Null(problem);
        Assert.NotNull(read);
        Assert.Equal(paths.Paths, read!.Paths);
        Assert.Equal(0, RenderPaths.Compare(paths, read).Total);
        Assert.Null(RenderPaths.Decode(stored[..^3], out var cut));
        Assert.Contains("cannot be read", cut, StringComparison.Ordinal);
        Assert.Null(RenderPaths.Decode([9, 0], out var format));
        Assert.Contains("format 9", format, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => RenderPaths.Of("[1,2]"));
    }

    [Fact]
    public void A_typical_record_keeps_about_twenty_five_bytes_a_path()
    {
        var document = Canonical(Object(Enumerable.Range(0, 60).Select(i => ($"Property{i:D2}", $"\"value {i}\""))));
        var stored = RenderPaths.Of(document).Encode();

        Assert.InRange(stored.Length, 60 * 10, 60 * 25);
    }

    // --- Nothing to compare against ---------------------------------------------------------------------------------

    [Fact]
    public void A_record_with_no_earlier_render_says_why_its_paths_are_not_compared()
    {
        var rendered = RenderPaths.Of(Canonical("""{"data":{"Name":"x"}}"""));

        Assert.Contains("new to the ledger", DeliveryReasons.Paths(null, rendered).NotCompared, StringComparison.Ordinal);
        Assert.Contains("holds no version", DeliveryReasons.Paths(Delivered() with { Status = RecordStatus.Deleted, TargetVersion = null }, rendered).NotCompared, StringComparison.Ordinal);
        Assert.Contains("delivered before the ledger kept them", DeliveryReasons.Paths(Delivered() with { RenderPaths = null }, rendered).NotCompared, StringComparison.Ordinal);
        Assert.Contains("cannot be read", DeliveryReasons.Paths(Delivered() with { RenderPaths = [1, 5, 0] }, rendered).NotCompared, StringComparison.Ordinal);
    }

    // --- Each trigger kind ------------------------------------------------------------------------------------------

    [Fact]
    public void A_record_new_to_the_ledger_is_a_first_delivery()
    {
        var reason = Reasons().For(null, Create(), Row(), Paths("""{"data":{"Name":"x"}}"""));

        Assert.Equal("create", reason.Action);
        Assert.Equal([ReasonKinds.FirstDelivery], Kinds(reason));
        Assert.Equal("first delivery", reason.Summary());
        Assert.False(reason.Paths!.Compared);
    }

    [Fact]
    public void A_record_only_ever_held_is_a_first_delivery_too()
    {
        var held = Delivered() with { Status = RecordStatus.Held, TargetVersion = null, LastDeliveredUtc = null, MetadataHash = null, RenderContext = null };

        var reason = Reasons().For(held, Create(), Row(), null);

        Assert.Equal([ReasonKinds.FirstDelivery], Kinds(reason));
        Assert.Contains("as held", reason.Triggers[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_restamped_row_is_a_source_change_with_its_moments_and_no_column_named()
    {
        var reason = Reasons().For(Delivered(), Update(), Row(fingerprint: "f2", rowUpdated: At.AddDays(1)), Paths(Doc()));

        var source = Assert.Single(reason.Triggers);
        Assert.Equal(ReasonKinds.SourceChanged, source.Kind);
        Assert.Equal("2026-11-17T08:00:00Z", source.From);
        Assert.Equal("2026-11-18T08:00:00Z", source.To);
        Assert.Contains("which columns changed is not kept", source.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_moved_business_version_is_a_source_change()
    {
        var reason = Reasons().For(Delivered() with { SourceModifiedUtc = At }, Update(), Row(modified: At.AddHours(3)), Paths(Doc()));

        var source = Assert.Single(reason.Triggers);
        Assert.Equal(ReasonKinds.SourceChanged, source.Kind);
        Assert.Contains("source.lastModified moved from 2026-11-17T08:00:00Z to 2026-11-17T11:00:00Z", source.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_moved_fingerprint_on_an_unmoved_row_is_a_child_or_lookup_change()
    {
        var reason = Reasons().For(Delivered(), Update(), Row(fingerprint: "f2"), Paths(Doc()));

        Assert.Equal([ReasonKinds.ChildOrLookupChanged], Kinds(reason));
        Assert.Equal("a child or lookup row changed; no path changed", reason.Summary());
    }

    [Fact]
    public void A_fingerprint_a_request_forgot_is_never_taken_for_a_source_change()
    {
        var redelivered = Delivered() with { SourceFingerprint = null, MetadataHash = null, PlanRequestedUtc = At.AddDays(2) };

        var reason = Reasons().For(redelivered, Update(), Row(fingerprint: "f2"), Paths(Doc()));

        Assert.Equal([ReasonKinds.Requested], Kinds(reason));
    }

    [Fact]
    public void A_mapping_edit_names_both_references_fingerprints_and_commits()
    {
        var reason = Reasons(Context(fingerprint: NewFingerprint), NewCommit).For(Delivered(), Update(), Row(), Paths(Doc()));

        var mapping = Assert.Single(reason.Triggers);
        Assert.Equal(ReasonKinds.MappingChanged, mapping.Kind);
        Assert.Equal(("WellLog@1.4.0", "WellLog@1.4.0"), (mapping.From, mapping.To));
        Assert.Equal((OldFingerprint, NewFingerprint), (mapping.FromFingerprint, mapping.ToFingerprint));
        Assert.Equal((OldCommit, NewCommit), (mapping.FromCommit, mapping.ToCommit));
        Assert.Equal("mapping WellLog@1.4.0 changed (852db64); no path changed", reason.Summary());
    }

    [Fact]
    public void A_mapping_of_another_version_is_named_from_and_to()
    {
        var reason = Reasons(Context(reference: "WellLog@1.5.0")).For(Delivered(), Update(), Row(), Paths(Doc()));

        Assert.StartsWith("mapping WellLog@1.4.0 changed to WellLog@1.5.0", reason.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_template_of_another_version_is_a_template_change()
    {
        var reason = Reasons(Context(schema: "osdu:wks:work-product-component--WellLog:1.5.0")).For(Delivered(), Update(), Row(), Paths(Doc()));

        var template = Assert.Single(reason.Triggers);
        Assert.Equal(ReasonKinds.TemplateChanged, template.Kind);
        Assert.Equal("osdu:wks:work-product-component--WellLog:1.5.0", template.To);
    }

    [Fact]
    public void Parameters_whose_values_moved_are_named_and_so_is_a_moved_restriction()
    {
        var context = Context(parameters: new() { ["dataPartition"] = "dev", ["legalTag"] = "dev-new" }) with
        {
            RestrictedAccess = new RenderRestriction(7, "pending-lra", ["data.restricted.viewers@x"], ["data.restricted.owners@x"]),
        };

        var reason = Reasons(context).For(Delivered(), Update(), Row(), Paths(Doc()));

        var parameters = Assert.Single(reason.Triggers);
        Assert.Equal(ReasonKinds.ParametersChanged, parameters.Kind);
        Assert.Equal(["legalTag", "restrictedAccess"], parameters.Names);
        Assert.DoesNotContain("dev-new", reason.ToJson().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_enrichment_added_or_edited_is_an_enrichment_change()
    {
        var curves = new RenderStep("curves", "Curves@1.0.0", "a1", "osdu:wks:work-product-component--WellLog:1.4.0", new Dictionary<string, string>(StringComparer.Ordinal));
        var before = Context() with { Steps = [curves] };
        var after = Context() with { Steps = [curves with { MappingFingerprint = "a2" }, curves with { Name = "tops", MappingReference = "Tops@1.0.0" }] };

        var reason = Reasons(after).For(Delivered(context: before.Canonical()), Update(), Row(), Paths(Doc()));

        var enrichment = Assert.Single(reason.Triggers);
        Assert.Equal(ReasonKinds.EnrichmentChanged, enrichment.Kind);
        Assert.Equal(["tops", "curves"], enrichment.Names);
        Assert.Contains("added: tops", enrichment.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_moved_cache_version_is_a_cache_change()
    {
        var reason = Reasons(Context(cache: "20261121T020000Z")).For(Delivered(), Update(), Row(), Paths(Doc()));

        var cache = Assert.Single(reason.Triggers);
        Assert.Equal(ReasonKinds.CacheChanged, cache.Kind);
        Assert.Equal(("20261117T020000Z", "20261121T020000Z"), (cache.From, cache.To));
    }

    [Fact]
    public void A_request_says_when_it_was_made_and_the_note_it_left()
    {
        var asked = Delivered() with { PlanRequestedUtc = At.AddDays(3), LastError = "redelivery of metadata requested by cache change 42" };

        var reason = Reasons().For(asked, Update(), Row(), Paths(Doc()));

        var request = Assert.Single(reason.Triggers);
        Assert.Equal(ReasonKinds.Requested, request.Kind);
        Assert.Equal(At.AddDays(3), request.At);
        Assert.Equal("the record's note: redelivery of metadata requested by cache change 42", request.Detail);
        Assert.Equal("asked to be planned again; no path changed", reason.Summary());
    }

    [Fact]
    public void A_request_an_intervention_made_names_it_and_who_asked()
    {
        var named = new ReasonTrigger { Kind = ReasonKinds.Requested, At = At, ActivityId = 91, Activity = "redeliver", Actor = "user:ada" };
        var reason = new DeliveryReason { Action = "update-metadata", Triggers = [named] };

        Assert.Equal("redeliver asked by user:ada", reason.Summary());
    }

    [Fact]
    public void A_difference_verification_found_is_drift()
    {
        var drifted = Delivered() with { LastVerifyOutcome = VerifyOutcome.Drifted, LastVerifiedUtc = At.AddDays(1) };

        var reason = Reasons().For(drifted, Update(), Row(), Paths(Doc()));

        Assert.Equal([ReasonKinds.Drift], Kinds(reason));
        Assert.Equal(At.AddDays(1), reason.Triggers[0].At);
    }

    [Fact]
    public void A_flow_that_sends_whatever_the_hashes_say_is_forced()
    {
        var always = new DeliveryReasons(Context(), null, new FlowChange { Detect = ChangeDetection.Always, OnUnchanged = UnchangedAction.Deliver });

        var reason = always.For(Delivered(), Update(), Row(), Paths(Doc()));

        var forced = Assert.Single(reason.Triggers);
        Assert.Equal(ReasonKinds.Forced, forced.Kind);
        Assert.Equal(["change.detect: always", "change.onUnchanged: deliver"], forced.Names);
    }

    [Fact]
    public void A_moved_payload_hash_is_a_payload_change_and_a_redelivery_marker_is_not()
    {
        var both = new ChangeDecision(PlannedAction.UpdatePayload, SkipTier.None, false, true, new ChangeBasis { Kind = ChangeBasisKind.Changed, PayloadChanged = true });

        var moved = Reasons().For(Delivered(), both, Row(payload: "p2"), null);
        var marked = Reasons().For(Delivered(payload: PayloadParts.RedeliverMarker(["files"])) with { PlanRequestedUtc = At }, both, Row(payload: "p2"), null);

        var payload = Assert.Single(moved.Triggers);
        Assert.Equal((ReasonKinds.PayloadChanged, "p1", "p2"), (payload.Kind, payload.From, payload.To));
        Assert.Null(moved.Paths);
        Assert.Equal([ReasonKinds.Requested], Kinds(marked));
    }

    [Fact]
    public void A_render_that_moved_with_no_compared_input_says_so_rather_than_guessing()
    {
        var reason = Reasons().For(Delivered(), Update(), Row(), Paths(Doc()));

        var other = Assert.Single(reason.Triggers);
        Assert.Equal(ReasonKinds.OtherInputChanged, other.Kind);
        Assert.Contains("a record it searches for in OSDU, its parent, or a child it links", other.Detail, StringComparison.Ordinal);
    }

    // --- Several at once, and the real case --------------------------------------------------------------------------

    [Fact]
    public void A_mapping_and_a_cache_change_together_are_both_listed_in_order_and_both_counted()
    {
        var reason = Reasons(Context(fingerprint: NewFingerprint, cache: "20261121T020000Z"), NewCommit).For(Delivered(), Update(), Row(), Paths(Doc()));

        Assert.Equal([ReasonKinds.MappingChanged, ReasonKinds.CacheChanged], Kinds(reason));
        Assert.Equal(TriggerKinds.MappingChanged | TriggerKinds.CacheChanged, reason.Kinds);
        Assert.Equal("mapping WellLog@1.4.0 changed (852db64), cache moved to 20261121T020000Z; no path changed", reason.Summary());
    }

    [Fact]
    public void Every_input_that_moved_is_listed_not_only_the_first()
    {
        var asked = Delivered() with { PlanRequestedUtc = At.AddDays(3), SourceModifiedUtc = At };

        var reason = Reasons(Context(fingerprint: NewFingerprint, parameters: new() { ["dataPartition"] = "dev", ["legalTag"] = "dev-new" }), NewCommit)
            .For(asked, Update(), Row(fingerprint: "f2", rowUpdated: At.AddDays(1), modified: At.AddDays(1)), Paths(Doc()));

        Assert.Equal([ReasonKinds.Requested, ReasonKinds.SourceChanged, ReasonKinds.MappingChanged, ReasonKinds.ParametersChanged], Kinds(reason));
    }

    [Fact]
    public void The_real_case_an_acl_viewers_edit_with_the_source_unchanged_is_a_mapping_change_naming_acl_viewers()
    {
        // A well log delivered again with its row, its fingerprint and its payload hash unchanged: the estate commit 852db64
        // changed the acl.viewers country-group rule of WellLog@1.4.0, and nothing else.
        var before = Doc("""["data.default.viewers@dev.example.com","data.country-no.viewers@dev.example.com"]""");
        var after = Doc("""["data.default.viewers@dev.example.com","data.country-no-restricted.viewers@dev.example.com"]""");
        var state = Delivered() with { RenderPaths = RenderPaths.Of(before).Encode() };

        var reason = Reasons(Context(fingerprint: NewFingerprint), NewCommit).For(state, Update(), Row(), RenderPaths.Of(after));

        var mapping = Assert.Single(reason.Triggers);
        Assert.Equal(ReasonKinds.MappingChanged, mapping.Kind);
        Assert.Equal((OldFingerprint, NewFingerprint), (mapping.FromFingerprint, mapping.ToFingerprint));
        Assert.Equal((OldCommit, NewCommit), (mapping.FromCommit, mapping.ToCommit));
        Assert.Equal([new PathChange("acl.viewers", PathChangeKind.Changed)], reason.Paths!.Listed);
        Assert.Equal("mapping WellLog@1.4.0 changed (852db64); acl.viewers changed", reason.Summary());
        Assert.DoesNotContain("country-no", reason.ToJson().ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_flows_records_share_a_context_comparison()
    {
        var reasons = Reasons(Context(fingerprint: NewFingerprint), NewCommit);
        var first = reasons.For(Delivered(), Update(), Row(), Paths(Doc()));
        var second = reasons.For(Delivered() with { RenderCommit = null }, Update(), Row(), Paths(Doc()));

        Assert.Equal(OldCommit, first.Triggers[0].FromCommit);
        Assert.Null(second.Triggers[0].FromCommit);
        Assert.Equal(first.Triggers[0].ToFingerprint, second.Triggers[0].ToFingerprint);
    }

    // --- The forms a reason travels in --------------------------------------------------------------------------------

    [Fact]
    public void A_reason_reads_back_from_the_json_its_attempt_keeps()
    {
        var state = Delivered() with { RenderPaths = RenderPaths.Of(Doc()).Encode(), PlanRequestedUtc = At };
        var reason = Reasons(Context(fingerprint: NewFingerprint, cache: "20261121T020000Z"), NewCommit)
            .For(state, Update(), Row(), RenderPaths.Of(Doc("""["x@x"]""")));

        var json = reason.ToJson();
        var read = DeliveryReason.FromJson(JsonNode.Parse(json.ToJsonString()));

        Assert.NotNull(read);
        Assert.Equal(reason.Summary(), read!.Summary());
        Assert.Equal(reason.Kinds, read.Kinds);
        Assert.Equal(reason.Paths!.Listed, read.Paths!.Listed);
        Assert.Equal(reason.Summary(), json["summary"]!.GetValue<string>());
        Assert.Equal(reason.Summary(), DeliveryReason.OfResult(AttemptResult.WithReason("""{"steps":[]}""", reason))!.Summary());
        Assert.Null(DeliveryReason.OfResult("""{"steps":[],"detail":"1 chunk(s)"}"""));
    }

    [Fact]
    public void A_work_item_carries_the_reason_the_paths_and_the_commit_and_an_older_line_carries_none()
    {
        var reason = Reasons().For(null, Create(), Row(), Paths(Doc()));
        var paths = Convert.ToBase64String(RenderPaths.Of(Doc()).Encode());
        var item = new WorkItem(Guid.NewGuid(), "dev:wpc--WellLog:1", Doc(), Reason: reason.ToJson().ToJsonString(), Paths: paths, Commit: NewCommit);

        var read = WorkBatchFile.Decode(WorkBatchFile.Encode(item));
        var older = WorkBatchFile.Decode(WorkBatchFile.Encode(item with { Reason = null, Paths = null, Commit = null }));

        Assert.Equal((item.Reason, item.Paths, item.Commit), (read.Reason, read.Paths, read.Commit));
        Assert.Equal((null, null, null), (older.Reason, older.Paths, older.Commit));
    }

    // --- Skip and decision words are unchanged ----------------------------------------------------------------------

    [Fact]
    public void The_words_a_plan_says_its_decisions_in_are_unchanged()
    {
        var change = new FlowChange();
        var queued = Delivered() with { Status = RecordStatus.Pending, PendingDocumentRef = "0:0:1", PendingMetadata = true, PendingMetadataHash = "m2" };

        Assert.Equal("not in ledger", ChangeDetector.Decide(null, "m1", null, false, change).Reason);
        Assert.Equal("ledger status held, never delivered", ChangeDetector.Decide(Delivered() with { Status = RecordStatus.Held, TargetVersion = null }, "m1", null, false, change).Reason);
        Assert.Equal("metadata and payload hashes unchanged", ChangeDetector.Decide(Delivered(), "m1", "p1", true, change).Reason);
        Assert.Equal("metadata and payload changed", ChangeDetector.Decide(Delivered(), "m2", "p2", true, change).Reason);
        Assert.Equal("metadata changed, payload unchanged", ChangeDetector.Decide(Delivered(), "m2", "p1", true, change).Reason);
        Assert.Equal("payload changed, metadata unchanged", ChangeDetector.Decide(Delivered(), "m1", "p2", true, change).Reason);
        Assert.Equal("metadata and payload hashes equal the version already queued", ChangeDetector.DecideWithQueue(queued, "m2", "p1", true, change).Reason);
        Assert.Equal(
            "metadata changed, payload unchanged against the version already queued, which this one replaces",
            ChangeDetector.DecideWithQueue(queued, "m3", "p1", true, change).Reason);
        Assert.Equal(SkipTier.ContentHash, ChangeDetector.Decide(Delivered(), "m1", "p1", true, change).SkipTier);
    }

    [Fact]
    public void A_decision_keeps_its_structured_basis_beside_the_words_a_plan_put_in_their_place()
    {
        var decision = ChangeDetector.Decide(Delivered(), "m2", "p2", true, new FlowChange());
        var told = decision with { Reason = "said otherwise" };

        Assert.Equal((ChangeBasisKind.Changed, true, true), (decision.Basis.Kind, decision.Basis.MetadataChanged, decision.Basis.PayloadChanged));
        Assert.Equal("said otherwise", told.Reason);
        Assert.Equal(decision.Basis, told.Basis);
    }

    // --- Helpers ----------------------------------------------------------------------------------------------------

    private static RenderContext Context(
        string reference = "WellLog@1.4.0", string? fingerprint = OldFingerprint, string cache = "20261117T020000Z",
        string schema = "osdu:wks:work-product-component--WellLog:1.4.0", Dictionary<string, string>? parameters = null) => new()
    {
        MappingReference = reference,
        MappingFingerprint = fingerprint,
        CacheScope = "dev",
        CacheVersion = cache,
        SchemaSnapshotVersion = schema,
        Parameters = parameters ?? new Dictionary<string, string>(StringComparer.Ordinal) { ["dataPartition"] = "dev", ["legalTag"] = "dev-old" },
    };

    private static DeliveryReasons Reasons(RenderContext? context = null, string? commit = null)
        => new(context ?? Context(), commit, new FlowChange());

    private static RecordState Delivered(string fingerprint = "f1", string payload = "p1", string? context = null) => new()
    {
        DeliveryKey = new DeliveryKey(Guid.NewGuid()),
        FlowId = Guid.NewGuid(),
        SourceKey = "acme:WB-0001/GR/1",
        MappingName = "WellLog",
        Status = RecordStatus.Delivered,
        SourceFingerprint = fingerprint,
        SourceUpdatedUtc = At,
        MetadataHash = "m1",
        PayloadHash = payload,
        RenderContext = context ?? Context().Canonical(),
        RenderCommit = OldCommit,
        RenderPaths = RenderPaths.Of(Doc()).Encode(),
        TargetVersion = 3,
        LastDeliveredUtc = At,
    };

    private static ReasonSource Row(string fingerprint = "f1", DateTime? rowUpdated = null, DateTime? modified = null, string? payload = "p1")
        => new(new SourceVersion(fingerprint, modified), rowUpdated ?? At, payload);

    private static ChangeDecision Create() => ChangeDetector.Decide(null, "m1", null, false, new FlowChange());

    private static ChangeDecision Update()
        => new(PlannedAction.UpdateMetadata, SkipTier.None, true, false, new ChangeBasis { Kind = ChangeBasisKind.Changed, MetadataChanged = true });

    private static RenderPaths Paths(string document) => RenderPaths.Of(document);

    private static string Doc(string viewers = """["data.default.viewers@dev.example.com"]""")
        => Canonical($$"""{"acl":{"owners":["data.default.owners@dev.example.com"],"viewers":{{viewers}}},"data":{"Name":"C-8 B"},"kind":"osdu:wks:work-product-component--WellLog:1.4.0"}""");

    private static string Object(IEnumerable<(string Name, string Value)> properties)
        => "{\"data\":{" + string.Join(",", properties.Select(p => $"\"{p.Name}\":{p.Value}")) + "}}";

    private static string Canonical(string json) => CanonicalJson.ToString(JsonNode.Parse(json));

    private static IReadOnlyList<string> Kinds(DeliveryReason reason) => [.. reason.Triggers.Select(t => t.Kind)];
}
