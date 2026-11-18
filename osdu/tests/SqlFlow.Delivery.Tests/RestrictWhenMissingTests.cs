using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Checks;
using SqlFlow.Delivery.Engine.Reversals;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// <c>$restrictWhenMissing</c> and <c>restrictedAccess</c> (osdu/docs/reference/flow/mapping-restricted-access.md) without a
/// database: what the parser takes and refuses, what the render does with a missing value (the property left out, the ACL
/// replaced whole, what is missing said as a hold would say it) and with every other reason a node gives nothing, and the
/// pieces that carry the access from the render to the ledger: the render context, the work batch line, the enrichment
/// parts' hash, the attempt's result and detail, a write back, and the value check.
/// </summary>
public sealed class RestrictWhenMissingTests
{
    private static readonly RenderRestriction Unclassified = new(3, "unclassified", ["data.restricted.viewers@x"], ["data.restricted.owners@x"]);

    /// <summary>
    /// A mapping document over the test template naming <c>restrictedAccess</c>, with the record's own blocks; it declares the
    /// parameter <c>restriction</c>, which a <c>restrictedAccess</c> may read.
    /// </summary>
    private static string Document(string data = "", string record = "", string restrictedAccess = "unclassified")
    {
        const string Parameters = "parameters:\n  dataPartition: { required: true }\n";
        var yaml = TestSchema.MappingDocument(data, record: record);
        Assert.Contains(Parameters, yaml, StringComparison.Ordinal);
        yaml = yaml.Replace(Parameters, Parameters + "  restriction: { required: false }\n", StringComparison.Ordinal);
        return restrictedAccess.Length == 0 ? yaml : yaml.Replace("\nrecord:\n", $"\nrestrictedAccess: {restrictedAccess}\nrecord:\n", StringComparison.Ordinal);
    }

    private static MappingDefinition Mapping(string data = "", string record = "", string restrictedAccess = "unclassified")
        => new DeliveryDocumentLoader().ParseMapping(Document(data, record, restrictedAccess), "thing.yaml");

    private static string Refusal(string data = "", string record = "", string restrictedAccess = "unclassified")
        => Assert.Throws<FlowValidationException>(() => Mapping(data, record, restrictedAccess)).Message;

    private static MappingRenderer Renderer(MappingDefinition mapping, RenderRestriction? restriction = null)
        => new(mapping, TestSchema.Build(), TestSchema.References(), TestSchema.Context() with { RestrictedAccess = restriction ?? Unclassified });

    private static SourceRecord Row(params (string Column, string? Value)[] columns)
    {
        var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["name"] = "well-1", ["depth"] = "1" };
        foreach (var (column, value) in columns)
        {
            row[column] = value;
        }

        return new SourceRecord { Row = SourceRow.FromStrings(row), Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase) };
    }

    private const string ClassifiedTag = """
        tags:
          dra.securityClassification:
            $from: class
            $restrictWhenMissing: true
        """;

    [Fact]
    public void A_marked_node_and_the_restriction_it_goes_out_under_are_read_from_the_mapping()
    {
        var mapping = Mapping(record: ClassifiedTag, restrictedAccess: "\"{$param.restriction}\"");
        var node = Assert.Single(mapping.RestrictingNodes());
        Assert.Equal("osdu.tags['dra.securityClassification']", node.Target.Text);
        Assert.True(node.Required);
        Assert.Equal("{$param.restriction}", mapping.RestrictedAccess);
        Assert.Equal("unclassified", mapping.RestrictionName(name => name == "restriction" ? " unclassified " : null));

        // A parameter the flow supplies no value for names no restriction, and is refused rather than read as a name.
        var unsupplied = Assert.Throws<FlowValidationException>(() => mapping.RestrictionName(_ => null));
        Assert.Contains("restrictedAccess reads parameter 'restriction'", unsupplied.Message, StringComparison.Ordinal);

        // A mapping that marks nothing names nothing.
        Assert.Null(TestSchema.Mapping().RestrictionName(_ => "x"));
        Assert.Empty(TestSchema.Mapping().RestrictingNodes());
    }

    [Fact]
    public void A_mapping_names_a_restriction_exactly_when_a_node_is_marked_and_the_mark_goes_only_where_a_value_can_be_missing()
    {
        Assert.Contains("is marked $restrictWhenMissing, and the mapping names no restrictedAccess", Refusal(record: ClassifiedTag, restrictedAccess: ""), StringComparison.Ordinal);
        Assert.Contains("and no node is marked so", Refusal(), StringComparison.Ordinal);
        Assert.Contains("restrictedAccess is empty", Refusal(record: ClassifiedTag, restrictedAccess: "\"  \""), StringComparison.Ordinal);
        Assert.Contains("reads parameter 'nowhere', which the mapping does not declare", Refusal(record: ClassifiedTag, restrictedAccess: "\"{$param.nowhere}\""), StringComparison.Ordinal);

        Assert.Contains(
            "$restrictWhenMissing restricts the record when the value is missing, and $required: false leaves the property out with the record's own access; keep one of them.",
            Refusal("""
                Symbol:
                  $from: symbol
                  $required: false
                  $restrictWhenMissing: true
                """),
            StringComparison.Ordinal);
        Assert.Contains("a literal $value takes only $when and $description beside it", Refusal("""
            Symbol:
              $value: x
              $restrictWhenMissing: true
            """), StringComparison.Ordinal);
        Assert.Contains("a $forEach node writes an array of rows", Refusal("""
            Curves:
              $forEach: curves
              $restrictWhenMissing: true
              $item:
                CurveID: { $from: curve_id }
            """), StringComparison.Ordinal);
        Assert.Contains("$restrictWhenMissing", Refusal("""
            Symbol:
              $coalesce:
                - $from: symbol
                  $restrictWhenMissing: true
                - $from: alias
            """), StringComparison.Ordinal);

        // Written true on an item's property, a coalesce node or a lookup node, it is taken.
        var taken = Mapping("""
            Symbol:
              $coalesce:
                - $from: symbol
                - $from: alias
              $restrictWhenMissing: true
            Curves:
              $forEach: curves
              $item:
                CurveID:
                  $from: curve_id
                  $restrictWhenMissing: true
            """);
        Assert.Equal(["osdu.data.Symbol", "osdu.data.Curves[].CurveID"], taken.RestrictingNodes().Select(n => n.Target.Text));

        // false is the default, and names nothing to restrict for.
        Assert.Empty(TestSchema.Mapping("Symbol: { $from: symbol, $restrictWhenMissing: false }").RestrictingNodes());
    }

    [Fact]
    public void A_missing_value_leaves_the_property_out_and_puts_the_record_under_the_restrictions_groups_whole()
    {
        var renderer = Renderer(Mapping(record: ClassifiedTag));

        var restricted = renderer.Render(Row(("class", "  ")));
        Assert.False(restricted.IsHeld, string.Join("; ", restricted.Holds));
        Assert.Null(restricted.Document["tags"]?["dra.securityClassification"]);
        Assert.Equal(Unclassified, restricted.Restriction);
        Assert.Equal(["osdu.tags['dra.securityClassification']: dataset.class is empty"], restricted.Missing);
        Assert.Equal(
            """{"owners":["data.restricted.owners@x"],"viewers":["data.restricted.viewers@x"]}""",
            restricted.Document["acl"]!.ToJsonString());

        // The value there: the ACL the mapping gives, nothing missing, no restriction.
        var open = renderer.Render(Row(("class", "confidential")));
        Assert.False(open.IsHeld, string.Join("; ", open.Holds));
        Assert.Equal("confidential", open.Document["tags"]!["dra.securityClassification"]!.GetValue<string>());
        Assert.Null(open.Restriction);
        Assert.Empty(open.Missing);
        Assert.Equal("""{"owners":["owners@x"],"viewers":["viewers@x"]}""", open.Document["acl"]!.ToJsonString());

        // The ACL is part of the document, so a record that moves between the two is sent again; one that stays is not.
        Assert.NotEqual(restricted.MetadataHash, open.MetadataHash);
        Assert.Equal(restricted.MetadataHash, renderer.Render(Row(("class", null))).MetadataHash);
    }

    [Fact]
    public void A_render_given_no_restriction_holds_the_record_rather_than_sending_it_with_its_own_access()
    {
        var mapping = Mapping(record: ClassifiedTag);
        var renderer = new MappingRenderer(mapping, TestSchema.Build(), TestSchema.References(), TestSchema.Context());

        var held = renderer.Render(Row(("class", null)));
        Assert.True(held.IsHeld);
        Assert.Null(held.Restriction);
        var reason = Assert.Single(held.Holds);
        Assert.StartsWith("osdu.tags['dra.securityClassification']: dataset.class is empty, and the record goes out under restricted access ('unclassified')", reason, StringComparison.Ordinal);
        Assert.Contains("held rather than sent with the access its mapping gives", reason, StringComparison.Ordinal);

        // A record with the value needs no restriction to go out.
        Assert.False(renderer.Render(Row(("class", "confidential"))).IsHeld);
    }

    [Fact]
    public void Only_a_missing_value_restricts_a_mistake_still_holds_and_a_when_that_does_not_hold_restricts_nothing()
    {
        var renderer = Renderer(Mapping("""
            Count:
              $from: count
              $restrictWhenMissing: true
            Symbol:
              $from: symbol
              $when: kind = "curve"
              $restrictWhenMissing: true
            """));

        // A value that cannot be written holds the record, as it would with $required: false.
        var mistaken = renderer.Render(Row(("count", "many"), ("kind", "log")));
        Assert.True(mistaken.IsHeld);
        Assert.Contains(mistaken.Holds, h => h.StartsWith("osdu.data.Count", StringComparison.Ordinal));
        Assert.Empty(mistaken.Missing);

        // The mapping means no value for the row: the property is left out with the record's own access.
        var meant = renderer.Render(Row(("count", "2"), ("kind", "log")));
        Assert.False(meant.IsHeld, string.Join("; ", meant.Holds));
        Assert.Null(meant.Restriction);

        // Both missing where they apply: one restriction, each value said.
        var both = renderer.Render(Row(("count", null), ("kind", "curve")));
        Assert.False(both.IsHeld, string.Join("; ", both.Holds));
        Assert.Equal(["osdu.data.Count: dataset.count is empty", "osdu.data.Symbol: dataset.symbol is empty"], both.Missing);
        Assert.Equal(Unclassified, both.Restriction);
    }

    [Fact]
    public void A_coalesce_whose_alternatives_give_nothing_restricts_naming_why_each_gave_nothing()
    {
        var renderer = Renderer(Mapping("""
            Symbol:
              $coalesce:
                - $from: symbol
                - $cache: UnitOfMeasure.Code
                  $findBy: Name = unit_name
              $restrictWhenMissing: true
            """));

        var restricted = renderer.Render(Row(("symbol", ""), ("unit_name", "furlong")));
        Assert.False(restricted.IsHeld, string.Join("; ", restricted.Holds));
        var missing = Assert.Single(restricted.Missing);
        Assert.StartsWith("osdu.data.Symbol: none of the 2 alternatives of $coalesce gives a value: 1. dataset.symbol is empty; 2. ", missing, StringComparison.Ordinal);
        Assert.Contains("no UnitOfMeasure matches 'furlong' by Name", missing, StringComparison.Ordinal);
        Assert.DoesNotContain("required", missing, StringComparison.Ordinal);

        Assert.Equal("m", renderer.Render(Row(("symbol", ""), ("unit_name", "metre"))).Document["data"]!["Symbol"]!.GetValue<string>());
    }

    [Fact]
    public void Items_missing_the_same_value_say_it_once_and_a_cache_that_finds_nothing_restricts()
    {
        var mapping = Mapping("""
            Unit:
              $cache: UnitOfMeasure.id
              $findBy: Code = unit
              $restrictWhenMissing: true
            Curves:
              $forEach: curves
              $item:
                CurveID:
                  $from: curve_id
                  $restrictWhenMissing: true
                TopDepth: { $from: top }
            """);
        var renderer = Renderer(mapping);
        static SourceRow Curve(string? id, string top) => SourceRow.FromStrings(new Dictionary<string, string?> { ["curve_id"] = id, ["top"] = top });
        var record = Row(("unit", "furlong")) with
        {
            Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase)
            {
                ["curves"] = [Curve(null, "10"), Curve("", "20"), Curve("GR", "30")],
            },
        };

        // Two items without their id are written without it, and the reason they share is said once.
        var restricted = renderer.Render(record);
        Assert.False(restricted.IsHeld, string.Join("; ", restricted.Holds));
        Assert.Null(restricted.Document["data"]!["Unit"]);
        Assert.Equal(2, restricted.Missing.Count);
        Assert.StartsWith("osdu.data.Unit: ", restricted.Missing[0], StringComparison.Ordinal);
        Assert.Contains("furlong", restricted.Missing[0], StringComparison.Ordinal);
        Assert.StartsWith("osdu.data.Curves[].CurveID: ", restricted.Missing[1], StringComparison.Ordinal);
        var curves = restricted.Document["data"]!["Curves"]!.AsArray();
        Assert.Equal([null, null, "GR"], curves.Select(c => c!["CurveID"]?.GetValue<string>()));
        Assert.Equal(["10", "20", "30"], curves.Select(c => c!["TopDepth"]!.ToJsonString()));

        // Every item with its id, and a unit the cache holds: nothing restricts.
        var whole = renderer.Render(Row(("unit", "m")) with
        {
            Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase) { ["curves"] = [Curve("GR", "30")] },
        });
        Assert.Null(whole.Restriction);
        Assert.Empty(whole.Missing);
    }

    [Fact]
    public void The_render_context_carries_the_restriction_only_when_named_and_reads_it_back_whole()
    {
        var plain = TestSchema.Context();
        Assert.DoesNotContain("restrictedAccess", plain.Canonical(), StringComparison.Ordinal);

        var restricted = plain with { RestrictedAccess = Unclassified };
        Assert.Contains("\"restrictedAccess\":{", restricted.Canonical(), StringComparison.Ordinal);
        Assert.NotEqual(plain.Hash(), restricted.Hash());
        var parsed = RenderContext.Parse(restricted.Canonical());
        Assert.Equal(Unclassified.RestrictionId, parsed.RestrictedAccess!.RestrictionId);
        Assert.Equal(Unclassified.Name, parsed.RestrictedAccess.Name);
        Assert.Equal(Unclassified.Viewers, parsed.RestrictedAccess.Viewers);
        Assert.Equal(Unclassified.Owners, parsed.RestrictedAccess.Owners);
        Assert.Equal(restricted.Hash(), parsed.Hash());

        // A restriction that cannot be read whole is refused rather than guessed at.
        var broken = restricted.Canonical().Replace("\"owners\":[\"data.restricted.owners@x\"]", "\"owners\":[]", StringComparison.Ordinal);
        Assert.Contains("restrictedAccess is not an access restriction", Assert.Throws<DeliveryException>(() => RenderContext.Parse(broken)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_access_a_document_goes_out_under_travels_in_its_work_batch_line_and_a_line_that_is_not_one_is_refused()
    {
        Assert.Null(DocumentAccess.Own.Encode());
        Assert.Null(DocumentAccess.Own.MissingText());
        Assert.Null(DocumentAccess.Own.MissingHash());
        Assert.Same(DocumentAccess.Own, DocumentAccess.Decode(null));
        Assert.Same(DocumentAccess.Own, DocumentAccess.Decode(" "));

        var access = new DocumentAccess(3, "unclassified", ["osdu.data.A: dataset.a is empty", "osdu.data.B: dataset.b is empty"]);
        var decoded = DocumentAccess.Decode(access.Encode());
        Assert.Equal((access.RestrictionId, access.Restriction), (decoded.RestrictionId, decoded.Restriction));
        Assert.Equal(access.Missing, decoded.Missing);
        Assert.Equal("osdu.data.A: dataset.a is empty\nosdu.data.B: dataset.b is empty", access.MissingText());
        Assert.Equal(ProblemSignature.Of(access.MissingText()!), access.MissingHash());

        foreach (var malformed in new[] { "{", "[]", """{"restrictionId":3,"restriction":"x","missing":[]}""", """{"restriction":"x","missing":["a"]}""", """{"restrictionId":3,"restriction":" ","missing":["a"]}""", """{"restrictionId":3,"restriction":"x","missing":[1]}""" })
        {
            Assert.Throws<DeliveryException>(() => DocumentAccess.Decode(malformed));
        }
    }

    [Fact]
    public void What_is_missing_is_kept_within_its_column_whole_reasons_first_and_the_rest_counted()
    {
        var reasons = Enumerable.Range(0, 100).Select(i => $"osdu.data.P{i:D3}: dataset.column_{i:D3} is empty, which a long explanation follows").ToList();
        var text = new DocumentAccess(1, "r", reasons).MissingText()!;
        Assert.True(text.Length <= DocumentAccess.MaxMissingLength, text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var lines = text.Split('\n');
        var kept = lines.Length - 1;
        Assert.Equal(reasons.Take(kept), lines.Take(kept));
        Assert.Equal($"and {reasons.Count - kept} more value(s) missing", lines[^1]);

        // One reason longer than the column is kept as far as it fits, never splitting a character in two.
        var single = new DocumentAccess(1, "r", [new string('x', DocumentAccess.MaxMissingLength + 10)]).MissingText()!;
        Assert.Equal(DocumentAccess.MaxMissingLength, single.Length);
        var emoji = new string('x', DocumentAccess.MaxMissingLength - 1) + "\U0001F600" + "tail";
        var cut = new DocumentAccess(1, "r", [emoji, "another"]).MissingText()!;
        Assert.True(cut.Length <= DocumentAccess.MaxMissingLength);
        Assert.False(char.IsHighSurrogate(cut[cut.IndexOf('\n', StringComparison.Ordinal) - 1]));
        Assert.EndsWith("\nand 1 more value(s) missing", cut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_work_batch_line_keeps_the_access_its_document_goes_out_under()
    {
        var root = Samples.NewTempDirectory();
        try
        {
            var stores = Samples.Stores();
            var submission = Guid.NewGuid();
            var access = new DocumentAccess(3, "unclassified", ["osdu.data.A: dataset.a is empty"]).Encode();
            var restricted = new WorkItem(Guid.NewGuid(), "dev:x:1", "{\"id\":\"dev:x:1\"}", Restricted: access);
            var open = new WorkItem(Guid.NewGuid(), "dev:x:2", "{\"id\":\"dev:x:2\"}");
            DocumentRef first, second;
            await using (var writer = await WorkBatchWriter.OpenAsync(stores, root, submission, 0))
            {
                first = await writer.WriteAsync(restricted);
                second = await writer.WriteAsync(open);
            }

            Assert.Equal(access, (await WorkBatchFile.ReadOneAsync(stores, root, submission, first)).Restricted);
            Assert.Null((await WorkBatchFile.ReadOneAsync(stores, root, submission, second)).Restricted);
            Assert.Equal(3, DocumentAccess.Decode((await WorkBatchFile.ReadOneAsync(stores, root, submission, first)).Restricted).RestrictionId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void An_added_records_hash_moves_with_the_access_only_while_the_record_is_restricted()
    {
        var part = new EnrichPart("las", "dev:dataset--File.Generic:1", "osdu:wks:dataset--File.Generic:1.0.0", "las/1.las", "files", "{\"id\":\"dev:dataset--File.Generic:1\"}", "text/plain");

        // A record with its own access hashes its added records as it always did, so nothing is sent again for it.
        Assert.Equal(SqlFlow.Delivery.Hashing.ContentHash.Of(part.Kind + "\n" + part.Record), part.RecordHash);

        var restricted = part with { Access = EnrichPart.RestrictedAccess(Unclassified) };
        Assert.Equal("restricted:3", restricted.Access);
        Assert.NotEqual(part.RecordHash, restricted.RecordHash);
        Assert.NotEqual(part.Hash, restricted.Hash);
        Assert.NotEqual(restricted.RecordHash, (part with { Access = EnrichPart.RestrictedAccess(Unclassified with { RestrictionId = 4 }) }).RecordHash);
    }

    [Fact]
    public void A_marked_property_the_template_requires_is_refused_by_the_preflight_and_a_marked_one_does_not_fill_every_row()
    {
        var report = MappingCoverage.Of(
            new DeliveryDocumentLoader().ParseMapping(Document(data: "Symbol: { $from: symbol, $restrictWhenMissing: true }", restrictedAccess: "unclassified"), "thing.yaml"),
            OsduTemplate.From(TestSchema.Build()));
        Assert.NotEqual(CoverageState.Always, Assert.Single(report.Variables, v => v.Target == "osdu.data.Symbol").State);

        var yaml = Document(data: "Symbol: { $from: symbol }").Replace("    Depth: { $from: depth }\n", "    Depth: { $from: depth, $restrictWhenMissing: true }\n", StringComparison.Ordinal);
        var required = MappingCoverage.Of(new DeliveryDocumentLoader().ParseMapping(yaml, "thing.yaml"), OsduTemplate.From(TestSchema.Build()));
        var issue = Assert.Single(required.Issues, i => i.Severity == IssueSeverity.Error);
        Assert.Equal("osdu.data.Depth", issue.Target);
        Assert.Contains("is $restrictWhenMissing, which sends the record restricted without the value, but the template requires osdu.data.Depth", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_builder_writes_the_mark_and_the_restriction_back_as_it_read_them()
    {
        var original = Mapping("""
            Symbol:
              $from: symbol
              $restrictWhenMissing: true
            Unit:
              $cache: UnitOfMeasure.id
              $findBy: Code = unit
              $restrictWhenMissing: true
            """, record: ClassifiedTag);
        var draft = MappingBuilder.FromDefinition(original);
        Assert.Equal("unclassified", draft.RestrictedAccess);
        Assert.Empty(MappingBuilder.Incomplete(draft));

        var yaml = MappingBuilder.ToYaml(draft);
        Assert.Contains("restrictedAccess: unclassified", yaml, StringComparison.Ordinal);
        var reread = new DeliveryDocumentLoader().ParseMapping(yaml, "thing.yaml");
        Assert.Equal(original.RestrictingNodes().Select(n => n.Target.Text), reread.RestrictingNodes().Select(n => n.Target.Text));
        Assert.Equal(original.RestrictedAccess, reread.RestrictedAccess);
        Assert.Equal(yaml, MappingBuilder.ToYaml(MappingBuilder.FromDefinition(reread)));

        // A draft that marks a node and names no restriction, or names one and marks none, is incomplete.
        Assert.Contains(MappingBuilder.Incomplete(draft with { RestrictedAccess = null }), i => i.Message.Contains("restrictedAccess", StringComparison.Ordinal));
        var unmarked = MappingBuilder.FromDefinition(TestSchema.Mapping()) with { RestrictedAccess = "unclassified" };
        Assert.NotEmpty(MappingBuilder.Incomplete(unmarked));
    }

    [Fact]
    public void A_write_back_of_a_record_restricted_until_complete_goes_out_under_the_restriction_or_not_at_all()
    {
        var key = DeliveryKey.Derive("test", ["w1"]);
        var writeBack = new WriteBack(key, "dev:x:w1", 1, 2, null);
        var record = new RecordState { DeliveryKey = key, FlowId = Guid.NewGuid(), SourceKey = "w1", MappingName = "Thing" };
        var registered = new AccessRestrictionState(
            3, "dev", "unclassified", null, new RecordAcl(["data.restricted.viewers@x"], ["data.restricted.owners@x"]), DateTime.UnixEpoch, "admin", DateTime.UnixEpoch, "admin");

        Assert.Same(writeBack, VersionWriteBack.Guarded(writeBack, record, new Dictionary<int, AccessRestrictionState>()));

        var guarded = VersionWriteBack.Guarded(writeBack, record with { IncompleteRestrictionId = 3 }, new Dictionary<int, AccessRestrictionState> { [3] = registered });
        Assert.Equal("""{"viewers":["data.restricted.viewers@x"],"owners":["data.restricted.owners@x"]}""", guarded.Acl!.ToJsonString());
        Assert.Null(guarded.Refusal);

        var refused = VersionWriteBack.Guarded(writeBack, record with { IncompleteRestrictionId = 4 }, new Dictionary<int, AccessRestrictionState> { [3] = registered });
        Assert.Null(refused.Acl);
        Assert.Contains("access restriction 4, which is no longer registered", refused.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_try_that_sent_the_document_says_what_it_did_to_the_records_access()
    {
        var record = new RecordState { DeliveryKey = DeliveryKey.Derive("test", ["w1"]), FlowId = Guid.NewGuid(), SourceKey = "w1", MappingName = "Thing" };
        var restricted = new DocumentAccess(3, "unclassified", ["osdu.data.A: dataset.a is empty", "osdu.data.B: dataset.b is empty"]);

        // Nothing sent, or sent open as it always was: nothing to say.
        Assert.Null(DeliveryWorker.AccessDetail(record, null));
        Assert.Null(DeliveryWorker.AccessDetail(record, DocumentAccess.Own));
        Assert.Equal("{\"steps\":[]}", AttemptResult.WithAccess("{\"steps\":[]}", DocumentAccess.Own, null));
        Assert.Equal("{\"steps\":[]}", AttemptResult.WithAccess("{\"steps\":[]}", null, 3));

        Assert.Equal(
            "restricted until complete under access restriction 'unclassified': osdu.data.A: dataset.a is empty (and 1 more value(s) missing)",
            DeliveryWorker.AccessDetail(record, restricted));
        Assert.StartsWith("still restricted until complete", DeliveryWorker.AccessDetail(record with { IncompleteRestrictionId = 3 }, restricted), StringComparison.Ordinal);
        Assert.StartsWith("restricted until complete", DeliveryWorker.AccessDetail(record with { IncompleteRestrictionId = 2 }, restricted), StringComparison.Ordinal);
        Assert.StartsWith("given its own access back", DeliveryWorker.AccessDetail(record with { IncompleteRestrictionId = 3 }, DocumentAccess.Own), StringComparison.Ordinal);

        var sent = JsonNode.Parse(AttemptResult.WithAccess("{\"steps\":[]}", restricted, null)!)!["access"]!;
        Assert.Equal(
            """{"restricted":true,"restrictionId":3,"restriction":"unclassified","missing":["osdu.data.A: dataset.a is empty","osdu.data.B: dataset.b is empty"],"was":null}""",
            sent.ToJsonString());
        var opened = JsonNode.Parse(AttemptResult.WithAccess(null, DocumentAccess.Own, 3)!)!["access"]!;
        Assert.Equal("""{"restricted":false,"was":3}""", opened.ToJsonString());
    }

    [Fact]
    public void The_value_check_counts_a_row_a_marked_node_gives_no_value_as_restricted_without_failing_it()
    {
        var mapping = Mapping(record: ClassifiedTag);
        var renderer = Renderer(mapping);
        var tally = new ValueTally(mapping, TestSchema.Build(), EntrySelection.All, new ValueCheckRequest(), ValueCheckLimits.Default, each: null);
        foreach (var (name, written) in new[] { ("w-1", (string?)null), ("w-2", "confidential"), ("w-3", " ") })
        {
            var record = new SourceRecord
            {
                Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["name"] = name, ["depth"] = "1", ["class"] = written }),
                Origin = new SourceOrigin("wells.csv", long.Parse(name[2..], System.Globalization.CultureInfo.InvariantCulture), null),
            };
            tally.Add(record, renderer.Inspect(record, EntrySelection.All), renderer);
        }

        var variable = Assert.Single(tally.Variables(), v => v.Target == "osdu.tags['dra.securityClassification']");
        Assert.Equal((2, 1, 0, 0), (variable.Rows.Restricted, variable.Rows.Valid, variable.Rows.Empty, variable.Rows.Held));
        var finding = Assert.Single(variable.Findings);
        Assert.Equal(("restricted", 2), (finding.Outcome, finding.Count));
        Assert.Equal([1L, 3L], finding.Samples.Select(s => s.Row!.Value));

        var rows = tally.Rows(3, 3, complete: true);
        Assert.Equal((2, 0, 0, 1), (rows.WithRestricted, rows.WithHeld, rows.WithEmpty, rows.Clean));
    }
}
