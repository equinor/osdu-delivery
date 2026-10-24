using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The inventory flow document (<c>flowType: inventory</c>, osdu/docs/reference/flow/inventory.md): what it declares,
/// every rule the loader holds it to, each refusal naming the place in the document it is about, how a run binds it to a
/// partition and selects its inventories, the payload and operations it takes, and the lineage it contributes.
/// </summary>
public class InventoryDocumentTests
{
    private const string Head = """
        flowType: inventory
        name: welllog-inventory
        batch: reconciliation
        partitions: [dev, test]
        source:
          endpoint: ${env:OSDU_URL}
          auth:
            type: bearer
            secretRef: ${env:OSDU_TOKEN}

        """;

    private const string WellLogs = """
        inventories:
          - name: WellLogs
            kind: "*:*:work-product-component--WellLog:*"
        """;

    private static InventoryFlowDefinition Parse(string yaml) => new DeliveryDocumentLoader().ParseInventory(yaml, "flows/inventory.yaml");

    private static FlowValidationException Refused(string yaml) => Assert.Throws<FlowValidationException>(() => Parse(yaml));

    private static string Inventories(int count) => "inventories:\n" + string.Concat(Enumerable.Range(0, count).Select(i => $"  - {{ name: I{i}, kind: \"osdu:wks:master-data--Well:1.0.0\" }}\n"));

    [Fact]
    public void A_document_declares_its_inventories_owners_and_bound_with_every_service_path_defaulted()
    {
        var flow = Parse(Head + """
            parameters:
              country: { default: US }
            owners: [delivery-sp@contoso.com, 5f2c0e1a-app]
            maxMissingChecks: 2500
            inventories:
              - name: WellLogs
                description: Every well log.
                kind: "*:*:work-product-component--WellLog:*"
                versions: all
              - name: CountryWells
                kind: "osdu:wks:master-data--Well:1.*.*"
                query: 'data.Country:"{country}" AND data.Partition:"{partition}"'
            """);

        Assert.Equal(("welllog-inventory", "reconciliation"), (flow.Name, flow.Batch));
        Assert.Equal(["dev", "test"], flow.Partitions);
        Assert.Equal(["delivery-sp@contoso.com", "5f2c0e1a-app"], flow.Owners);
        Assert.Equal(2500, flow.MaxMissingChecks);
        Assert.Equal(InventoryRead.Search, flow.Source.Read);
        Assert.Equal(
            (InventorySource.DefaultQueryPath, InventorySource.DefaultSearchPath, InventorySource.DefaultRecordQueryPath, InventorySource.DefaultHeadersPath, InventorySource.DefaultVersionsPath, InventorySource.DefaultSchemaPath),
            (flow.Source.QueryPath, flow.Source.SearchPath, flow.Source.RecordQueryPath, flow.Source.HeadersPath, flow.Source.VersionsPath, flow.Source.SchemaPath));

        var logs = flow.Inventories[0];
        Assert.Equal(("Every well log.", InventoryVersions.All, (string?)null), (logs.Description, logs.Versions, logs.Query));
        Assert.Equal(("work-product-component--WellLog", true), (logs.EntityType, logs.CoversEntityType));
        var wells = flow.Inventories[1];
        Assert.Equal((InventoryVersions.Latest, "master-data--Well", false), (wells.Versions, wells.EntityType, wells.CoversEntityType));
        Assert.Equal("data.Country:\"{country}\" AND data.Partition:\"{partition}\"", wells.Query);
    }

    [Fact]
    public void Left_out_the_owners_are_inferred_the_bound_is_its_default_and_a_storage_read_takes_its_own_service_paths()
    {
        var flow = Parse(Head.Replace("  auth:", "  read: STORAGE\n  headersPath: /storage/headers/\n  versionsPath: /storage/versions\n  auth:", StringComparison.Ordinal) + WellLogs);

        Assert.Empty(flow.Owners);
        Assert.Equal(InventoryFlowDefinition.DefaultMaxMissingChecks, flow.MaxMissingChecks);
        Assert.Equal((InventoryRead.Storage, "/storage/headers", "/storage/versions"), (flow.Source.Read, flow.Source.HeadersPath, flow.Source.VersionsPath));
        Assert.Equal(0, Parse(Head + "maxMissingChecks: 0\n" + WellLogs).MaxMissingChecks);
        Assert.Equal(InventoryFlowDefinition.MaxMissingChecksCeiling, Parse(Head + "maxMissingChecks: 1000000\n" + WellLogs).MaxMissingChecks);
    }

    [Theory]
    [InlineData("source:\n  endpoint: http://x\n  read: index\n", "source.read is 'index'")]
    [InlineData("source:\n  endpoint: http://x\n  queryPath: api/search\n", "source.queryPath 'api/search' must be a path under the endpoint")]
    [InlineData("source:\n  endpoint: http://x\n  schemaPath: /schema?x=1\n", "source.schemaPath '/schema?x=1' must be a path")]
    [InlineData("source:\n  endpoint: http://x\n  versionsPath: /a b\n", "source.versionsPath '/a b' must be a path")]
    [InlineData("source:\n  endpoint: http://x\n  headers: { data-partition-id: dev }\n", "source.headers names 'data-partition-id', and the flow names its partitions")]
    [InlineData("parameters:\n  partition: { default: dev }\n", "parameters declares 'partition'")]
    [InlineData("maxMissingChecks: -1\n", "maxMissingChecks is -1")]
    [InlineData("maxMissingChecks: 1000001\n", "maxMissingChecks is 1000001")]
    [InlineData("owners: ['two words']\n", "owners[0] is not an identity")]
    [InlineData("owners: ['']\n", "owners[0] is not an identity")]
    [InlineData("owners: [a@x.com, A@X.COM]\n", "owners[1] 'A@X.COM' is listed twice")]
    [InlineData("parallelism: { recordsAtOnce: 0 }\n", "parallelism.recordsAtOnce must be at least 1")]
    [InlineData("parallelism: { concurrency: 2 }\n", "parallelism.concurrency is now parallelism.recordsAtOnce, as on a delivery flow: the version lists a build reads at once. Rename it.")]
    [InlineData("reliability: { concurrency: 2 }\n", "reliability.concurrency is now parallelism.recordsAtOnce")]
    [InlineData("reliability: { fanOut: 2 }\n", "reliability.fanOut is not a setting here any more: a delivery flow writes it as parallelism.nodes, and an inventory flow has no use for it")]
    [InlineData("parallelism: { fanOut: 2 }\n", "Property 'fanOut' not found")]
    public void A_flow_level_rule_broken_is_refused_naming_its_key(string fragment, string expected)
    {
        var head = fragment.StartsWith("source:", StringComparison.Ordinal)
            ? "flowType: inventory\nname: welllog-inventory\npartitions: [dev]\n"
            : Head;
        var message = Refused(head + fragment + WellLogs).Message;

        Assert.Contains("flows/inventory.yaml", message, StringComparison.Ordinal);
        Assert.Contains(expected, message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("  - name: 'two words'\n    kind: \"osdu:wks:master-data--Well:1.0.0\"\n", "inventories[0].name 'two words' is not an inventory name")]
    [InlineData("  - kind: \"osdu:wks:master-data--Well:1.0.0\"\n", "inventories[0].name")]
    [InlineData("  - name: Wells\n", "inventories[0].kind")]
    [InlineData("  - name: Wells\n    kind: master-data--Well\n", "kind 'master-data--Well' is not authority:source:entityType:version")]
    [InlineData("  - name: Wells\n    kind: \"osdu:wks:master-data--Well\"\n", "is not authority:source:entityType:version")]
    [InlineData("  - name: Wells\n    kind: \"osdu:wks:master-data--Well:1.0.0\"\n    versions: some\n", "inventories[0] 'Wells': versions is 'some'")]
    [InlineData("  - name: Wells\n    kind: \"osdu:wks:master-data--Well:1.0.0\"\n    query: 'data.Country:\"{country}\"'\n", "query uses '{country}', which is not declared under parameters")]
    [InlineData("  - name: Wells\n    kind: \"osdu:wks:master-data--Well:1.0.0\"\n  - name: WELLS\n    kind: \"osdu:wks:master-data--Wellbore:1.0.0\"\n", "inventories[1] is named 'WELLS', as an earlier inventory is")]
    public void An_inventory_rule_broken_is_refused_naming_the_inventory(string inventories, string expected)
        => Assert.Contains(expected, Refused(Head + "inventories:\n" + inventories).Message, StringComparison.Ordinal);

    [Fact]
    public void A_query_is_refused_where_storage_is_read_and_a_query_or_kind_too_long_for_its_column_is_refused()
    {
        var storage = Head.Replace("  auth:", "  read: storage\n  auth:", StringComparison.Ordinal);
        Assert.Contains(
            "query narrows a search, and the flow reads storage",
            Refused(storage + "inventories:\n  - name: Wells\n    kind: \"osdu:wks:master-data--Well:1.0.0\"\n    query: 'data.Country:NO'\n").Message,
            StringComparison.Ordinal);

        var longQuery = "data.Name:" + new string('a', Data.DeliveryModel.MaxInventoryQueryLength);
        Assert.Contains("an inventory keeps at most", Refused(Head + $"inventories:\n  - name: Wells\n    kind: \"osdu:wks:master-data--Well:1.0.0\"\n    query: '{longQuery}'\n").Message, StringComparison.Ordinal);

        var longKind = "osdu:wks:" + new string('w', Data.DeliveryModel.MaxInventoryKindLength) + ":1.0.0";
        Assert.Contains("is not authority:source:entityType:version", Refused(Head + $"inventories:\n  - name: Wells\n    kind: \"{longKind}\"\n").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flow_holds_between_one_and_a_hundred_inventories_and_names_at_most_fifty_owners()
    {
        Assert.Contains("inventories must list at least one inventory", Refused(Head).Message, StringComparison.Ordinal);
        Assert.Contains("inventories must list at least one inventory", Refused(Head + "inventories: []\n").Message, StringComparison.Ordinal);
        Assert.Equal(InventoryFlowDefinition.MaxInventories, Parse(Head + Inventories(InventoryFlowDefinition.MaxInventories)).Inventories.Count);
        Assert.Contains("one flow holds at most 100", Refused(Head + Inventories(InventoryFlowDefinition.MaxInventories + 1)).Message, StringComparison.Ordinal);

        var owners = "owners: [" + string.Join(", ", Enumerable.Range(0, InventoryFlowDefinition.MaxOwners + 1).Select(i => $"app{i}")) + "]\n";
        Assert.Contains("a flow names at most 50", Refused(Head + owners + WellLogs).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_the_document_does_not_know_is_refused_as_are_another_kinds_keys_and_another_flow_type()
    {
        Assert.Throws<FlowValidationException>(() => Parse(Head + "dimensions: []\n" + WellLogs));
        Assert.Throws<FlowValidationException>(() => Parse(Head.Replace("  auth:", "  aggregationSize: 5\n  auth:", StringComparison.Ordinal) + WellLogs));
        Assert.Throws<FlowValidationException>(() => Parse(Head + "inventories:\n  - name: Wells\n    kind: \"osdu:wks:master-data--Well:1.0.0\"\n    path: data.Name\n"));
        Assert.Contains("expected 'flowType: inventory', found 'dimension'", Refused(Head.Replace("flowType: inventory", "flowType: dimension", StringComparison.Ordinal) + WellLogs).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("*:*:work-product-component--WellLog:*", null, true)]
    [InlineData("osdu:*:work-product-component--WellLog:*", null, false)]
    [InlineData("*:wks:work-product-component--WellLog:*", null, false)]
    [InlineData("*:*:work-product-component--WellLog:1.0.0", null, false)]
    [InlineData("*:*:*:*", null, false)]
    [InlineData("*:*:work-product-component--WellLog:*", "data.Name:A", false)]
    public void An_inventory_covers_its_type_whole_only_when_it_names_the_type_and_leaves_every_other_segment_and_the_query_open(string kind, string? query, bool covers)
        => Assert.Equal(covers, new InventorySpec { Name = "I", Kind = kind, Query = query }.CoversEntityType);

    [Fact]
    public void A_flow_naming_its_partitions_binds_to_each_and_keeps_its_inventories_per_partition()
    {
        var flow = Parse(Head + WellLogs);

        var dev = flow.ForPartition("DEV");
        Assert.Equal(("dev", "dev"), (dev.Partition, dev.Source.Headers["data-partition-id"]));
        Assert.Equal("welllog-inventory@dev", dev.LedgerName);
        Assert.NotEqual(dev.LedgerId, flow.ForPartition("test").LedgerId);
        Assert.Equal(Identity.FlowId.Of("welllog-inventory", "dev"), dev.LedgerId);
        Assert.Throws<InvalidOperationException>(() => flow.LedgerId);
        Assert.Contains("does not read partition 'prod'", Assert.Throws<DeliveryException>(() => flow.ForPartition("prod")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_reads_one_partition_the_one_it_names_the_registrys_default_or_the_flows_only_one()
    {
        var two = Parse(Head + WellLogs);
        var registry = new RegisteredPartitions([new RegisteredPartition("dev", null, false), new RegisteredPartition("test", null, true)]);
        var none = new RegisteredPartitions([]);

        Assert.Equal("test", two.ForRun(null, registry).Partition);
        Assert.Equal("dev", two.ForRun("dev", registry).Partition);
        Assert.True(two.NeedsRegistry(null));
        Assert.False(two.NeedsRegistry("dev"));
        Assert.Contains("name the partition this run reads", Assert.Throws<DeliveryException>(() => two.ForRun(null, none)).Message, StringComparison.Ordinal);
        Assert.Contains("reads one partition per run", Assert.Throws<DeliveryException>(() => two.ForRun(PartitionNames.Every, registry)).Message, StringComparison.Ordinal);

        var one = Parse(Head.Replace("partitions: [dev, test]", "partitions: [dev]", StringComparison.Ordinal) + WellLogs);
        Assert.Equal("dev", one.ForRun(null, none).Partition);
        Assert.False(one.NeedsRegistry(null));

        var registered = Parse(Head.Replace("partitions: [dev, test]\n", "", StringComparison.Ordinal) + WellLogs);
        Assert.True(registered.FollowsRegistry);
        Assert.Equal("test", registered.ForRun(null, registry).Partition);
        Assert.Throws<DeliveryException>(() => registered.ForRun("prod", registry));
        Assert.Contains("No partition is registered", Assert.Throws<DeliveryException>(() => registered.ForRun(null, none)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flow_whose_partition_is_its_header_reads_that_one_and_cannot_be_bound_to_another()
    {
        var flow = Parse(Head.Replace("partitions: [dev, test]\n", "", StringComparison.Ordinal).Replace("  auth:", "  headers: { data-partition-id: dev }\n  auth:", StringComparison.Ordinal) + WellLogs);

        Assert.False(flow.Partitioned);
        Assert.Same(flow, flow.ForRun(null, new RegisteredPartitions([])));
        Assert.Equal(Identity.FlowId.Of("welllog-inventory"), flow.LedgerId);
        Assert.Equal("welllog-inventory", flow.LedgerName);
        Assert.Throws<DeliveryException>(() => flow.ForRun("test", new RegisteredPartitions([])));
        Assert.Throws<DeliveryException>(() => flow.ForPartition("dev"));
        Assert.Contains("is empty", Refused(Head.Replace("partitions: [dev, test]\n", "", StringComparison.Ordinal).Replace("  auth:", "  headers: { data-partition-id: ' ' }\n  auth:", StringComparison.Ordinal) + WellLogs).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_selects_inventories_by_name_in_document_order_and_refuses_one_the_flow_does_not_have()
    {
        var flow = Parse(Head + "inventories:\n  - { name: Wells, kind: \"osdu:wks:master-data--Well:1.0.0\" }\n  - { name: Logs, kind: \"osdu:wks:work-product-component--WellLog:1.0.0\" }\n");

        Assert.Equal(["Wells", "Logs"], flow.Select(["logs", "WELLS"]).Select(i => i.Name));
        Assert.Equal(2, flow.Select([]).Count);
        Assert.Equal("Logs", flow.Inventory(" logs ")!.Name);
        Assert.Null(flow.Inventory("Nope"));
        var refused = Assert.Throws<DeliveryException>(() => flow.Select(["Nope", "Logs"])).Message;
        Assert.Contains("has no inventory named 'Nope'; its inventories are Wells, Logs.", refused, StringComparison.Ordinal);
    }

    [Fact]
    public void The_payload_carries_the_inventories_a_run_builds_and_no_other_kind_takes_them()
    {
        var payload = DeliveryRunPayload.Parse("""{ "inventories": ["WellLogs", "Wells"] }""");
        Assert.Equal(["WellLogs", "Wells"], payload.Inventories);
        Assert.True(payload.SelectsInventories);
        Assert.Equal("""{"inventories":["WellLogs","Wells"]}""", payload.ToJson());
        Assert.False(payload.CarriesOnlyConfiguration);

        Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "inventories": ["not a name"] }"""));
        Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "inventories": ["A", "a"] }"""));
        Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "inventories": "WellLogs" }"""));
        Assert.Contains("only an inventory flow", Assert.Throws<SqlFlowException>(() => payload.Validate(DeliveryOperations.Deliver)).Message, StringComparison.Ordinal);

        var kind = new InventoryFlowKind(new DeliveryDocumentLoader());
        kind.ValidateParameters(new RunParameters { Payload = payload.ToJson() });
        kind.ValidateParameters(new RunParameters());
        Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters { Payload = """{ "tests": ["t"] }""" }));
        Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters { Payload = """{ "dimensions": ["d"] }""" }));
        Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters { Payload = """{ "recordKeys": ["k"] }""" }));
        Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters { FullLoad = true }));
        Assert.Throws<SqlFlowException>(() => new DimensionFlowKind(new DeliveryDocumentLoader()).ValidateParameters(new RunParameters { Payload = payload.ToJson() }));
        Assert.Throws<SqlFlowException>(() => new AssertionFlowKind(new DeliveryDocumentLoader()).ValidateParameters(new RunParameters { Payload = payload.ToJson() }));
        Assert.Throws<SqlFlowException>(() => new RetrievalFlowKind(new DeliveryDocumentLoader()).ValidateParameters(new RunParameters { Payload = payload.ToJson() }));
    }

    [Fact]
    public void A_flow_may_let_an_operator_remove_its_orphan_stale_and_forgotten_ids_soft_deleted_or_purged()
    {
        var reading = Parse(Head + WellLogs);
        Assert.Null(reading.Removal);
        Assert.Equal(
            (InventorySource.DefaultDeletePath, InventorySource.DefaultBulkDeletePath, InventorySource.DefaultPurgePath),
            (reading.Source.DeletePath, reading.Source.BulkDeletePath, reading.Source.PurgePath));

        var removing = Parse(Head + WellLogs + "\nremoval: { findings: [Orphan, stale] }\n");
        Assert.Equal(["orphan", "stale"], removing.Removal!.Findings);
        Assert.False(removing.Removal.Purge);
        Assert.True(removing.Removal.Allows("orphan"));
        Assert.False(removing.Removal.Allows("forgotten"));
        Assert.True(Parse(Head + WellLogs + "\nremoval: { findings: [forgotten], purge: true }\n").Removal!.Purge);

        var paths = Parse(Head.Replace("    secretRef: ${env:OSDU_TOKEN}\n", "    secretRef: ${env:OSDU_TOKEN}\n  deletePath: /storage/records/{id}:delete\n  bulkDeletePath: /storage/records/delete\n  purgePath: /storage/records/{id}\n", StringComparison.Ordinal) + WellLogs);
        Assert.Equal(("/storage/records/{id}:delete", "/storage/records/delete", "/storage/records/{id}"), (paths.Source.DeletePath, paths.Source.BulkDeletePath, paths.Source.PurgePath));

        Assert.Contains("removal.findings must name", Refused(Head + WellLogs + "\nremoval: { findings: [] }\n").Message, StringComparison.Ordinal);
        Assert.Contains("removal.findings[0] is 'tracked'", Refused(Head + WellLogs + "\nremoval: { findings: [tracked] }\n").Message, StringComparison.Ordinal);
        Assert.Contains("removal.findings[0] is 'foreign'", Refused(Head + WellLogs + "\nremoval: { findings: [foreign] }\n").Message, StringComparison.Ordinal);

        // Gone ids are removed from the inventory whatever the document says, so a document does not name them among what it removes from OSDU.
        Assert.Contains("removal.findings[0] is 'gone'", Refused(Head + WellLogs + "\nremoval: { findings: [gone] }\n").Message, StringComparison.Ordinal);
        Assert.Contains("removal.findings[1] 'orphan' is listed twice", Refused(Head + WellLogs + "\nremoval: { findings: [orphan, orphan] }\n").Message, StringComparison.Ordinal);
        Assert.Contains("source.purgePath", Refused(Head.Replace("    secretRef: ${env:OSDU_TOKEN}\n", "    secretRef: ${env:OSDU_TOKEN}\n  purgePath: /storage/records\n", StringComparison.Ordinal) + WellLogs).Message, StringComparison.Ordinal);
        Assert.Throws<FlowValidationException>(() => Parse(Head + WellLogs + "\nremoval: { findings: [orphan], everything: true }\n"));
    }

    [Fact]
    public void A_match_run_names_one_inventory_and_the_selection_it_fills_and_a_removal_may_take_the_ids_of_a_selection()
    {
        var match = DeliveryRunPayload.Parse("""{ "inventories": ["WellLogs"], "selection": 12 }""");
        Assert.Equal((12L, true), (match.SelectionId!.Value, match.SelectsInventories));
        Assert.Equal("""{"inventories":["WellLogs"],"selection":12}""", match.ToJson());
        Assert.Contains("payload selection must be the number", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "selection": 0 }""")).Message, StringComparison.Ordinal);
        Assert.Contains("payload selection must be the number", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "selection": "12" }""")).Message, StringComparison.Ordinal);

        var kind = new InventoryFlowKind(new DeliveryDocumentLoader());
        Assert.Contains(kind.Operations, o => o.Name == DeliveryOperations.Match && !o.WritesTarget);
        kind.ValidateParameters(new RunParameters { Operation = DeliveryOperations.Match, Payload = match.ToJson() });
        Assert.Contains("names the one inventory it matches", Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters
        {
            Operation = DeliveryOperations.Match, Payload = """{ "inventories": ["WellLogs", "Wells"], "selection": 12 }""",
        })).Message, StringComparison.Ordinal);
        Assert.Contains("names the one inventory it matches", Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters
        {
            Operation = DeliveryOperations.Match, Payload = """{ "inventories": ["WellLogs"] }""",
        })).Message, StringComparison.Ordinal);
        Assert.Contains("payload selection does not apply to the build operation", Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters { Payload = match.ToJson() })).Message, StringComparison.Ordinal);
        Assert.Contains("does not apply to the match operation", Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters
        {
            Operation = DeliveryOperations.Match, Payload = """{ "inventories": ["WellLogs"], "selection": 12, "confirm": "dev" }""",
        })).Message, StringComparison.Ordinal);
        Assert.Equal(DeliveryOperations.Match, InventoryFlowKind.Operation(new RunParameters { Operation = DeliveryOperations.Match }));

        // A removal takes the ids of one finding a selection holds, and names no ids of its own beside them.
        var selected = DeliveryRunPayload.Parse("""{ "inventories": ["WellLogs"], "confirm": "dev", "removal": { "finding": "orphan", "scope": "record", "expected": 212, "selection": 12 } }""").Removal!;
        Assert.Equal(("orphan", 212L, 12L, false), (selected.Finding, selected.Expected, selected.Selection!.Value, selected.NamesIds));
        Assert.Equal(12L, DeliveryRunPayload.Parse(new DeliveryRunPayload { Removal = selected }.ToJson()).Removal!.Selection);
        Assert.Contains("names both ids and a selection", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse(
            """{ "removal": { "finding": "orphan", "scope": "record", "expected": 1, "ids": ["a:b:c"], "selection": 12 } }""")).Message, StringComparison.Ordinal);
        Assert.Contains("a selection holds ids OSDU serves", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse(
            """{ "removal": { "finding": "gone", "scope": "inventory", "expected": 1, "selection": 12 } }""")).Message, StringComparison.Ordinal);
        Assert.Contains("removal.selection must be the number", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse(
            """{ "removal": { "finding": "orphan", "scope": "record", "expected": 1, "selection": -1 } }""")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_duplicates_run_names_one_inventory_and_the_check_it_fills_and_no_other_run_names_a_check()
    {
        var check = DeliveryRunPayload.Parse("""{ "inventories": ["WellLogs"], "duplicateCheck": 7 }""");
        Assert.Equal((7L, true), (check.DuplicateCheckId!.Value, check.SelectsInventories));
        Assert.Equal("""{"inventories":["WellLogs"],"duplicateCheck":7}""", check.ToJson());
        Assert.Contains("payload duplicateCheck must be the number", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "duplicateCheck": 0 }""")).Message, StringComparison.Ordinal);

        var kind = new InventoryFlowKind(new DeliveryDocumentLoader());
        kind.ValidateParameters(new RunParameters { Operation = DeliveryOperations.Duplicates, Payload = check.ToJson() });
        Assert.Equal(DeliveryOperations.Duplicates, InventoryFlowKind.Operation(new RunParameters { Operation = DeliveryOperations.Duplicates }));
        Assert.Contains("names the one inventory it checks (inventories) and the check it fills (duplicateCheck)", Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters
        {
            Operation = DeliveryOperations.Duplicates, Payload = """{ "inventories": ["WellLogs"] }""",
        })).Message, StringComparison.Ordinal);
        Assert.Contains("names the one inventory it checks", Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters
        {
            Operation = DeliveryOperations.Duplicates, Payload = """{ "inventories": ["WellLogs", "Wells"], "duplicateCheck": 7 }""",
        })).Message, StringComparison.Ordinal);
        Assert.Contains("payload duplicateCheck does not apply to the match operation", Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters
        {
            Operation = DeliveryOperations.Match, Payload = """{ "inventories": ["WellLogs"], "selection": 12, "duplicateCheck": 7 }""",
        })).Message, StringComparison.Ordinal);
        Assert.Contains("payload selection does not apply to the duplicates operation", Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters
        {
            Operation = DeliveryOperations.Duplicates, Payload = """{ "inventories": ["WellLogs"], "selection": 12, "duplicateCheck": 7 }""",
        })).Message, StringComparison.Ordinal);
        Assert.Contains("is not one of them", Assert.Throws<SqlFlowException>(() => InventoryFlowKind.Operation(new RunParameters { Operation = "dedupe" })).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_remove_run_names_one_inventory_what_it_removes_and_the_partition_it_confirms_and_no_other_run_takes_a_removal()
    {
        var payload = DeliveryRunPayload.Parse("""{ "inventories": ["WellLogs"], "confirm": "dev", "removal": { "finding": "orphan", "scope": "record", "expected": 2, "ids": ["dev:wpc--WellLog:a", "dev:wpc--WellLog:b"] } }""");
        Assert.Equal(("orphan", "record", 2L, true), (payload.Removal!.Finding, payload.Removal.Scope, payload.Removal.Expected, payload.Removal.NamesIds));
        Assert.Equal(["dev:wpc--WellLog:a", "dev:wpc--WellLog:b"], payload.Removal.Ids);
        var again = DeliveryRunPayload.Parse(payload.ToJson()).Removal!;
        Assert.Equal((payload.Removal.Finding, payload.Removal.Scope, payload.Removal.Expected), (again.Finding, again.Scope, again.Expected));
        Assert.Equal(payload.Removal.Ids, again.Ids);
        Assert.False(DeliveryRunPayload.Parse("""{ "removal": { "finding": "stale", "scope": "everything", "expected": 5000000 } }""").Removal!.NamesIds);

        Assert.Contains("removal.finding is 'foreign'", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "removal": { "finding": "foreign", "scope": "record", "expected": 1 } }""")).Message, StringComparison.Ordinal);
        Assert.Contains("removal.scope is 'history'", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "removal": { "finding": "orphan", "scope": "history", "expected": 1 } }""")).Message, StringComparison.Ordinal);
        Assert.Contains("removal.expected", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "removal": { "finding": "orphan", "scope": "record", "expected": 0 } }""")).Message, StringComparison.Ordinal);
        Assert.Contains("names 1 ids and expected says 2", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "removal": { "finding": "orphan", "scope": "record", "expected": 2, "ids": ["a:b:c"] } }""")).Message, StringComparison.Ordinal);
        Assert.Contains("more than once", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "removal": { "finding": "orphan", "scope": "record", "expected": 2, "ids": ["a:b:c", "a:b:c"] } }""")).Message, StringComparison.Ordinal);
        Assert.Contains("removal.why", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "removal": { "finding": "orphan", "scope": "record", "expected": 1, "why": "x" } }""")).Message, StringComparison.Ordinal);

        // Gone ids go from the inventory alone, and only they do: OSDU no longer serves them, and the next build lists any other again.
        var gone = DeliveryRunPayload.Parse("""{ "inventories": ["WellLogs"], "confirm": "dev", "removal": { "finding": "gone", "scope": "inventory", "expected": 60955 } }""").Removal!;
        Assert.Equal(("gone", "inventory", 60955L, false), (gone.Finding, gone.Scope, gone.Expected, gone.NamesIds));
        Assert.Contains("nothing of it in OSDU to remove", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "removal": { "finding": "gone", "scope": "record", "expected": 1 } }""")).Message, StringComparison.Ordinal);
        Assert.Contains("the next build would list it again", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "removal": { "finding": "orphan", "scope": "inventory", "expected": 1 } }""")).Message, StringComparison.Ordinal);

        var kind = new InventoryFlowKind(new DeliveryDocumentLoader());
        kind.ValidateParameters(new RunParameters { Operation = "remove", Payload = payload.ToJson() });
        Assert.Contains("nothing is removed without all three", Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters
        {
            Operation = "remove", Payload = """{ "inventories": ["WellLogs"], "removal": { "finding": "orphan", "scope": "record", "expected": 1 } }""",
        })).Message, StringComparison.Ordinal);
        Assert.Contains("does not apply to the build operation", Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters { Payload = payload.ToJson() })).Message, StringComparison.Ordinal);
        Assert.Contains("does not apply to the reconcile operation", Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters { Operation = "reconcile", Payload = """{ "confirm": "dev" }""" })).Message, StringComparison.Ordinal);
        var removalOnly = DeliveryRunPayload.Parse("""{ "removal": { "finding": "orphan", "scope": "record", "expected": 1 } }""");
        Assert.Contains("payload removal does not apply to a delivery flow", Assert.Throws<SqlFlowException>(() => removalOnly.Validate(DeliveryOperations.Deliver)).Message, StringComparison.Ordinal);
        Assert.Throws<SqlFlowException>(() => new DimensionFlowKind(new DeliveryDocumentLoader()).ValidateParameters(new RunParameters { Payload = """{ "removal": { "finding": "orphan", "scope": "record", "expected": 1 } }""" }));
        Assert.False(DeliveryRunPayload.Parse("""{ "removal": { "finding": "orphan", "scope": "record", "expected": 1 } }""").CarriesOnlyConfiguration);
    }

    [Theory]
    [InlineData(null, "build")]
    [InlineData("build", "build")]
    [InlineData("reconcile", "reconcile")]
    [InlineData("plan", "plan")]
    [InlineData("remove", "remove")]
    public void An_inventory_flow_builds_by_default_and_reconciles_plans_or_removes_when_asked(string? operation, string expected)
        => Assert.Equal(expected, InventoryFlowKind.Operation(new RunParameters { Operation = operation }));

    [Theory]
    [InlineData("deliver")]
    [InlineData("undo")]
    [InlineData("delete-ledger")]
    public void An_inventory_flow_runs_no_other_operation_and_a_delivery_flow_does_not_reconcile(string operation)
    {
        Assert.Throws<SqlFlowException>(() => InventoryFlowKind.Operation(new RunParameters { Operation = operation }));
        Assert.Contains("'reconcile' is not an operation of a delivery flow", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("{}").Validate(DeliveryOperations.Reconcile)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_kind_offers_build_reconcile_plan_remove_match_and_duplicates_and_neither_a_plan_a_match_nor_a_check_writes_the_inventories()
    {
        var kind = new InventoryFlowKind(new DeliveryDocumentLoader());

        Assert.Equal("inventory", kind.FlowType);
        Assert.Equal(
            [("build", true), ("reconcile", true), ("plan", false), ("remove", true), ("match", false), ("duplicates", false)],
            kind.Operations.Select(o => (o.Name, o.WritesTarget)));
        var document = Assert.IsType<InventoryFlowDocument>(kind.Parse(Head + WellLogs, "flows/inventory.yaml"));
        Assert.Equal(("welllog-inventory", "inventory", "reconciliation"), (document.Name, document.Kind, document.Batch));
        Assert.Equal(("${env:OSDU_URL}", InventoryFlowDocument.InventoriesTarget), (document.SourceReference, document.TargetReference));
        Assert.False(document.RequiresRepoTree);
    }

    [Fact]
    public void Lineage_reads_each_inventorys_kind_once_in_each_partition_and_writes_nothing()
    {
        var flow = Parse(Head + "inventories:\n  - { name: Logs, kind: \"*:*:work-product-component--WellLog:*\" }\n  - { name: Logs2, kind: \"*:*:work-product-component--WellLog:*\", versions: all }\n  - { name: Wells, kind: \"osdu:wks:master-data--Well:1.0.0\" }\n");

        var lineage = InventoryLineage.Describe(flow);

        Assert.Equal(
            ["osdu-type/dev/*:*:work-product-component--WellLog:*", "osdu-type/dev/osdu:wks:master-data--Well:1.0.0", "osdu-type/test/*:*:work-product-component--WellLog:*", "osdu-type/test/osdu:wks:master-data--Well:1.0.0"],
            lineage.Datasets.Select(d => $"{d.System}/{d.Namespace}/{d.Name}"));
        Assert.All(lineage.Datasets, d => Assert.Equal(LineageRelation.Reads, d.Relation));
        Assert.Empty(lineage.Warnings);

        var registered = InventoryLineage.Describe(Parse(Head.Replace("partitions: [dev, test]\n", "", StringComparison.Ordinal) + WellLogs));
        Assert.Equal([PartitionNames.Every], registered.Datasets.Select(d => d.Namespace));
        var bound = InventoryLineage.Describe(flow.ForPartition("test"));
        Assert.All(bound.Datasets, d => Assert.Equal("test", d.Namespace));
    }

    [Fact]
    public void Its_secrets_are_references_the_hygiene_check_inspects()
    {
        var flow = Parse(Head.Replace("partitions: [dev, test]\n", "", StringComparison.Ordinal).Replace("  auth:", "  headers: { data-partition-id: '${env:OSDU_PARTITION}', x-tenant: plain }\n  auth:", StringComparison.Ordinal) + WellLogs);

        var references = new InventoryFlowDocument { Flow = flow }.CredentialReferences.ToList();

        Assert.Equal(
            [
                new KeyValuePair<string, string>("source.auth.secretRef", "${env:OSDU_TOKEN}"),
                new KeyValuePair<string, string>("source.endpoint", "${env:OSDU_URL}"),
                new KeyValuePair<string, string>("source.headers.data-partition-id", "${env:OSDU_PARTITION}"),
            ],
            references);
    }
}
