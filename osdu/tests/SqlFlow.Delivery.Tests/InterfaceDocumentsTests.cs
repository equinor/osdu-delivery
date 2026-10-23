using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A delivery flow document as a source (osdu/docs/reference/flow/interfaces.md): one document listing the
/// interfaces a source delivers, each with its own ledger identity and a route that follows from what it declares, the
/// source's shared blocks laid under every interface, and every document written before interfaces existed read as a
/// source of one interface whose identity has not moved.
/// </summary>
public sealed class InterfaceDocumentsTests
{
    private readonly DeliveryDocumentLoader _loader = new();

    /// <summary>A source of <paramref name="interfaces"/> (a YAML block indented two spaces) and the shared blocks around it.</summary>
    private static string Source(string interfaces, string shared = "", string target = "") => ($$"""
        flowType: delivery
        name: petrel
        description: The Petrel project store.
        parameters:
          project: { required: true }
        source:
          connection: ${env:PETREL_DB}
          work: ../.work/{project}
          lastModified: update_date
          systemColumns:
            deleted: ~
          incremental:
            pageSize: 500
        render:
          parameters:
            dataPartition: dev
            region: north
        target:
          endpoint: ${env:OSDU_URL}
          headers:
            data-partition-id: dev
          protocolOptions:
            batchSize: 50
            uploadHeaders:
              x-ms-blob-type: BlockBlob
        {{target}}
        reliability:
          retry: { attempts: 5 }
        parallelism:
          concurrency: 4
        failWhen:
          failedPercent: 30
        {{shared}}
        interfaces:
        {{interfaces}}
        """).ReplaceLineEndings("\n");

    /// <summary>One interface, ending with a line break so another can follow it.</summary>
    private const string Wells = """
          wells:
            record: { object: Petrel.ing.Well, key: [uwi], primaryKey: RecId }
            mapping: Well@1.2.0

        """;

    [Fact]
    public void A_source_lists_its_interfaces_in_document_order_each_with_a_ledger_of_its_own()
    {
        var source = _loader.ParseSource(Source(Wells + """
              documents:
                record: { object: Petrel.ing.Document, key: [doc_id] }
                files: { root: ../data/documents, locationColumn: doc_folder, hashColumn: doc_hash }
                mapping: Document@1.0.0
                after: [wells]
              logs:
                record: { object: Petrel.ing.WellLog, key: [uwi, log_id], primaryKey: RecId }
                bulk: { root: ../data/curves, locationColumn: curve_folder, hashColumn: curve_hash }
                mapping: WellLog@1.4.0
                protocolOptions: { ddmsRoot: /api/os-wellbore-ddms }
                after: [wells]
            """), "petrel.yaml");

        Assert.True(source.DeclaresInterfaces);
        Assert.Equal("petrel", source.Name);
        Assert.Equal("petrel.yaml", source.SourcePath);
        Assert.Equal(["wells", "documents", "logs"], source.Names);
        Assert.Equal(SourceDefinition.DefaultParallelInterfaces, source.ParallelInterfaces);

        // Each interface keeps its own ledger, named by the flow and the interface.
        foreach (var flow in source.Interfaces)
        {
            Assert.Equal("petrel", flow.Name);
            Assert.Equal($"petrel/{flow.Interface}", flow.Label);
            Assert.Equal(flow.Label, flow.LedgerName);
            Assert.Equal(FlowId.Of($"petrel/{flow.Interface}"), flow.Id);
        }

        Assert.Equal(3, source.Interfaces.Select(i => i.Id).Distinct().Count());

        // The route follows from what each interface's records carry.
        var wells = source.Interface("wells");
        Assert.Equal(DeliveryProtocol.Storage, wells.Target.Protocol);
        Assert.Null(wells.Target.ProtocolOptions.Payload);
        Assert.Contains("storage service", wells.RouteReason, StringComparison.Ordinal);
        var documents = source.Interface("DOCUMENTS");
        Assert.Equal(DeliveryProtocol.File, documents.Target.Protocol);
        Assert.Equal("files", documents.Target.ProtocolOptions.Payload);
        Assert.Equal("../data/documents", documents.Source.Payloads["files"].Root);
        Assert.Contains("file service", documents.RouteReason, StringComparison.Ordinal);
        var logs = source.Interface("logs");
        Assert.Equal(DeliveryProtocol.Ddms, logs.Target.Protocol);
        Assert.Equal("bulk", logs.Target.ProtocolOptions.Payload);
        Assert.Equal("/api/os-wellbore-ddms", logs.Target.ProtocolOptions.DdmsRoot);
        Assert.Equal(["wells"], logs.After);
        Assert.Equal("storage", DeliveryProtocols.Name(wells.Target.Protocol));
        Assert.Equal("ddms", DeliveryProtocols.Name(logs.Target.Protocol));

        // What the source declares is every interface's, and what differs is the interface's own.
        Assert.All(source.Interfaces, flow =>
        {
            Assert.Equal("${env:PETREL_DB}", flow.Source.Connection);
            Assert.Equal("../.work/{project}", flow.Source.Work);
            Assert.Equal("update_date", flow.Source.LastModified);
            Assert.Null(flow.Source.SystemColumns.Deleted);
            Assert.Equal(500, flow.Source.Incremental.PageSize);
            Assert.Equal("dev", flow.Render.Parameters["dataPartition"]);
            Assert.Equal(4, flow.Parallelism.Concurrency);
            Assert.Equal(5, flow.Reliability.Retry.Attempts);
            Assert.Equal(30, flow.FailWhen.FailedPercent);
            Assert.Equal(50, flow.Target.ProtocolOptions.BatchSize);
            Assert.True(flow.Parameters["project"].Required);
            Assert.Equal("dev", flow.Target.Headers["data-partition-id"]);
        });
        Assert.Equal("The Petrel project store.", source.Description);
        Assert.Null(wells.Description);
    }

    [Fact]
    public void What_one_interface_makes_of_the_shared_blocks_never_reaches_another()
    {
        // The wells take the storage route, which drops the source's DDMS root from their own view; the logs, read after
        // them, still find it.
        var source = _loader.ParseSource(Source(Wells + """
              logs:
                record: { object: Petrel.ing.WellLog, key: [uwi, log_id] }
                bulk: { root: ../data/curves, locationColumn: curve_folder, hashColumn: curve_hash }
                mapping: WellLog@1.4.0
                protocolOptions: { uploadHeaders: { x-logs: only } }
            """).Replace("    batchSize: 50\n", "    batchSize: 50\n    ddmsRoot: /api/os-wellbore-ddms\n", StringComparison.Ordinal), "petrel.yaml");

        Assert.Null(source.Interface("wells").Target.ProtocolOptions.DdmsRoot);
        Assert.Equal("/api/os-wellbore-ddms", source.Interface("logs").Target.ProtocolOptions.DdmsRoot);
        Assert.False(source.Interface("wells").Target.ProtocolOptions.UploadHeaders.ContainsKey("x-logs"));
        Assert.Equal("only", source.Interface("logs").Target.ProtocolOptions.UploadHeaders["x-logs"]);
        Assert.Null(source.Interface("wells").Target.ProtocolOptions.Payload);
        Assert.Equal("bulk", source.Interface("logs").Target.ProtocolOptions.Payload);
    }

    [Fact]
    public void An_interface_overrides_the_shared_blocks_key_by_key()
    {
        var source = _loader.ParseSource(Source("""
              wells:
                description: Wells, from the well header table.
                record: { object: Petrel.ing.Well, key: [uwi] }
                mapping: Well@1.2.0
                lastModified: modified_at
                systemColumns: { fileName: ~, deleted: DeletedAt }
                incremental: { overlapSeconds: 60 }
                render: { cacheVersion: pinned-7, parameters: { region: south, source: petrel } }
                change: { onUnchanged: deliver }
                reliability: { retry: { baseDelayMs: 100 } }
                parallelism: { concurrency: 2 }
                failWhen: { consecutiveFailures: 10, outageFailures: 0 }
                protocolOptions: { batchSize: 10, uploadHeaders: { x-extra: yes }, preserveDataKeys: [Datasets] }
                verify: { reconcile: true }
            """), "petrel.yaml");

        var wells = source.First;
        Assert.Equal("Wells, from the well header table.", wells.Description);
        Assert.Equal("modified_at", wells.Source.LastModified);
        Assert.Null(wells.Source.SystemColumns.FileName);
        Assert.Equal("DeletedAt", wells.Source.SystemColumns.Deleted);
        Assert.True(wells.Source.SystemColumns.DeletedDeclared);
        Assert.Equal(FlowSystemColumns.DefaultUpdated, wells.Source.SystemColumns.Updated);
        Assert.Equal(60, wells.Source.Incremental.OverlapSeconds);
        Assert.Equal(500, wells.Source.Incremental.PageSize);
        Assert.Equal("pinned-7", wells.Render.CacheVersion);
        Assert.Equal("south", wells.Render.Parameters["region"]);
        Assert.Equal("dev", wells.Render.Parameters["dataPartition"]);
        Assert.Equal("petrel", wells.Render.Parameters["source"]);
        Assert.Equal(UnchangedAction.Deliver, wells.Change.OnUnchanged);
        Assert.Equal(2, wells.Parallelism.Concurrency);
        Assert.Equal(5, wells.Reliability.Retry.Attempts);
        Assert.Equal(100, wells.Reliability.Retry.BaseDelayMs);
        Assert.Equal(30, wells.FailWhen.FailedPercent);
        Assert.Equal(10, wells.FailWhen.ConsecutiveFailures);
        Assert.Equal(0, wells.FailWhen.OutageFailures);
        Assert.Equal(10, wells.Target.ProtocolOptions.BatchSize);
        Assert.Equal("BlockBlob", wells.Target.ProtocolOptions.UploadHeaders["x-ms-blob-type"]);
        Assert.Equal("yes", wells.Target.ProtocolOptions.UploadHeaders["x-extra"]);
        Assert.Equal(["Datasets"], wells.Target.ProtocolOptions.PreserveDataKeys);
        Assert.True(wells.Verify.Reconcile);
    }

    [Fact]
    public void A_document_in_the_single_form_is_a_source_of_one_interface_whose_identity_has_not_moved()
    {
        // The sample names one partition, which keeps the ledger the flow kept before it named it: a run binds to it
        // without being told, and its identity is the one the flow always had.
        var source = _loader.LoadSource(Samples.Flow).ForPartition(null);
        var flow = Assert.Single(source.Interfaces);
        Assert.False(source.DeclaresInterfaces);
        Assert.Null(flow.Interface);
        Assert.Null(flow.AdoptedLedger);
        Assert.Null(flow.RouteReason);
        Assert.Equal("welldb-welllog-03-header-delivery", flow.Label);
        Assert.Equal("welldb-welllog-03-header-delivery", flow.LedgerName);
        Assert.Equal(FlowId.Of("welldb-welllog-03-header-delivery"), flow.Id);
        Assert.Same(flow, source.Interface(null));
        Assert.Empty(source.Names);
        Assert.Equal(new FlowFailWhen(), flow.FailWhen);

        // The one-flow reading is the same flow, and the single form answers to no interface name.
        Assert.Equal(flow.Id, _loader.LoadFlow(Samples.Flow).ForPartition(Samples.SamplePartition).Id);
        Assert.Contains("declares no interfaces", Assert.Throws<DeliveryException>(() => source.Interface("welllogs")).Message, StringComparison.Ordinal);
        Assert.Contains("declares no interfaces", Assert.Throws<DeliveryException>(() => source.Select(["welllogs"])).Message, StringComparison.Ordinal);
        Assert.Same(source.Interfaces, source.Select([]));
    }

    [Fact]
    public void An_interface_adopts_an_existing_ledger_so_consolidating_flows_loses_no_history()
    {
        var source = _loader.ParseSource(Source(Wells + """
              logs:
                ledger: welldb-welllog-03-header-delivery
                record: { object: Petrel.ing.WellLog, key: [uwi, log_id] }
                mapping: WellLog@1.4.0
            """), "petrel.yaml");

        var logs = source.Interface("logs");
        Assert.Equal("welldb-welllog-03-header-delivery", logs.AdoptedLedger);
        Assert.Equal("welldb-welllog-03-header-delivery", logs.LedgerName);
        Assert.Equal("petrel/logs", logs.Label);
        Assert.Equal(FlowId.Of("welldb-welllog-03-header-delivery"), logs.Id);
        Assert.Same(logs, source.ByFlowId(FlowId.Of("welldb-welllog-03-header-delivery")));
        Assert.Null(source.ByFlowId(FlowId.Of("petrel/logs")));
    }

    [Fact]
    public void Reading_one_flow_out_of_a_source_names_the_interface_when_there_are_several()
    {
        var yaml = Source(Wells + """
              wellbores:
                record: { object: Petrel.ing.Wellbore, key: [uwbi] }
                mapping: Wellbore@1.0.0
            """);

        var refused = Assert.Throws<FlowValidationException>(() => _loader.ParseFlow(yaml, "petrel.yaml"));
        Assert.Contains("petrel.yaml: Flow 'petrel' delivers 2 interfaces (wells, wellbores); name the one", refused.Message, StringComparison.Ordinal);
        Assert.Equal("wellbores", _loader.ParseFlow(yaml, "petrel.yaml", "wellbores").Interface);
        Assert.Contains("has no interface 'logs'", Assert.Throws<FlowValidationException>(() => _loader.ParseFlow(yaml, "petrel.yaml", "logs")).Message, StringComparison.Ordinal);

        // A source of one interface reads as that interface without a name.
        Assert.Equal("wells", _loader.ParseFlow(Source(Wells), "petrel.yaml").Interface);
    }

    [Fact]
    public void A_selection_of_interfaces_is_in_document_order_and_names_only_interfaces_of_the_source()
    {
        var source = _loader.ParseSource(Source(Wells + """
              wellbores:
                record: { object: Petrel.ing.Wellbore, key: [uwbi] }
                mapping: Wellbore@1.0.0
              logs:
                record: { object: Petrel.ing.WellLog, key: [uwi, log_id] }
                mapping: WellLog@1.4.0
            """), "petrel.yaml");

        Assert.Equal(["wells", "logs"], source.Select(["LOGS", "wells"]).Select(i => i.Interface));
        var refused = Assert.Throws<DeliveryException>(() => source.Select(["logs", "seismic", "cores"]));
        Assert.Contains("no interface 'seismic', 'cores'", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("route: storage\nfiles: { root: ../d, locationColumn: f, hashColumn: h }", "route is storage, which writes the record alone, so interfaces.wells.files would never be sent")]
    [InlineData("route: file", "route is file, which uploads and registers each record's files, and the interface declares none under interfaces.wells.files")]
    [InlineData("route: fileAndDdms\nfiles: { root: ../d, locationColumn: f, hashColumn: h }", "route is fileAndDdms, which registers each record's files and writes its bulk data through its DDMS, and the interface declares no interfaces.wells.bulk")]
    [InlineData("route: manifestAndDdms", "route is manifestAndDdms, which writes each record's bulk data through its DDMS after the manifest, and the interface declares none under interfaces.wells.bulk")]
    [InlineData("route: dataset", "route is dataset, which stores each record's files and registers them through the dataset service, and the interface declares none under interfaces.wells.files")]
    [InlineData("route: dataset\nfiles: { root: ../d, locationColumn: f, hashColumn: h }\nbulk: { root: ../b, locationColumn: f, hashColumn: h }", "route is dataset, which writes no DDMS bulk data, so interfaces.wells.bulk would never be sent")]
    [InlineData("route: workflow", "route is workflow, and the interface declares no workflow under interfaces.wells.workflow")]
    [InlineData("route: storage\nworkflow: { stages: [{ workflow: eds_scheduler }] }", "interfaces.wells.route is storage, and interfaces.wells.workflow declares a workflow, which only the workflow route runs")]
    [InlineData("bulk: { root: ../b, locationColumn: f, hashColumn: h }\nworkflow: { stages: [{ workflow: eds_scheduler }] }", "the workflow route writes no DDMS bulk data, so interfaces.wells.bulk would never be sent")]
    [InlineData("route: teleport", "'interfaces.wells.route' value 'teleport' is not one of storage, file, dataset, manifest, ddms, fileAndDdms, manifestAndDdms, workflow")]
    public void A_route_that_cannot_deliver_what_an_interface_declares_is_refused(string declared, string expected)
    {
        var yaml = Source($$"""
              wells:
                record: { object: Petrel.ing.Well, key: [uwi] }
                mapping: Well@1.2.0
                {{declared.Replace("\n", "\n    ", StringComparison.Ordinal)}}
            """.ReplaceLineEndings("\n"));
        var refused = Assert.Throws<FlowValidationException>(() => _loader.ParseSource(yaml, "petrel.yaml"));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_named_route_is_taken_as_named()
    {
        var source = _loader.ParseSource(Source("""
              seismic:
                record: { object: Petrel.ing.Seismic, key: [survey] }
                files: { root: ../data/segy, locationColumn: segy_folder, hashColumn: segy_hash }
                mapping: SeismicTraceData@1.3.0
                route: manifest
              markers:
                record: { object: Petrel.ing.Marker, key: [uwbi, marker] }
                mapping: WellboreMarkerSet@1.2.0
                route: ddms
                protocolOptions: { ddmsRoot: /api/os-wellbore-ddms }
            """), "petrel.yaml");

        var seismic = source.Interface("seismic");
        Assert.Equal(DeliveryProtocol.Manifest, seismic.Target.Protocol);
        Assert.Equal("files", seismic.Target.ProtocolOptions.Payload);
        Assert.Contains("route names the manifest route", seismic.RouteReason, StringComparison.Ordinal);
        var markers = source.Interface("markers");
        Assert.Equal(DeliveryProtocol.Ddms, markers.Target.Protocol);
        Assert.Null(markers.Target.ProtocolOptions.Payload);
    }

    [Theory]
    [InlineData("  record: { object: Petrel.ing.Well, key: [uwi] }", "source.record (each interface's record table is interfaces.<name>.record)")]
    [InlineData("  datasets: { aliases: { object: Petrel.ing.Alias, join: { uwi: uwi } } }", "source.datasets (each interface's child tables")]
    [InlineData("  payloads: { files: { root: ../d } }", "source.payloads (an interface's files are interfaces.<name>.files")]
    public void What_belongs_to_an_interface_is_refused_at_the_source_level(string sourceKey, string expected)
    {
        var yaml = Source(Wells).Replace("  work: ../.work/{project}\n", $"  work: ../.work/{{project}}\n{sourceKey}\n", StringComparison.Ordinal);
        var refused = Assert.Throws<FlowValidationException>(() => _loader.ParseSource(yaml, "petrel.yaml"));
        Assert.Contains("the document declares interfaces, so these belong to an interface rather than the source", refused.Message, StringComparison.Ordinal);
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mapping_protocol_or_payload_named_for_the_whole_source_is_refused()
    {
        var yaml = Source(Wells, target: "  protocol: storage")
            .Replace("  parameters:\n    dataPartition: dev", "  mapping: Well@1.2.0\n  parameters:\n    dataPartition: dev", StringComparison.Ordinal)
            .Replace("    batchSize: 50\n", "    batchSize: 50\n    payload: files\n", StringComparison.Ordinal);
        var refused = Assert.Throws<FlowValidationException>(() => _loader.ParseSource(yaml, "petrel.yaml")).Message;
        Assert.Contains("render.mapping (each interface pins its own mapping as interfaces.<name>.mapping)", refused, StringComparison.Ordinal);
        Assert.Contains("target.protocol (each interface's route follows from what it declares", refused, StringComparison.Ordinal);
        Assert.Contains("target.protocolOptions.payload", refused, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("interfaces: {}", "interfaces lists no interface")]
    [InlineData("interfaces:\n  1wells: { mapping: Well@1.2.0 }", "names an interface '1wells'")]
    [InlineData("interfaces:\n  well/s: { mapping: Well@1.2.0 }", "names an interface 'well/s'")]
    [InlineData("interfaces:\n  wells:", "interfaces.wells declares nothing")]
    [InlineData("interfaces:\n  wells: { record: { object: A.b.Well, key: [uwi] }, mapping: Well@1.2.0 }\n  WELLS: { record: { object: A.b.Well, key: [uwi] }, mapping: Well@1.2.0 }", "declares 'WELLS' more than once")]
    [InlineData("interfaces:\n  wells: { record: { object: A.b.Well, key: [uwi] }, mapping: Well }", "interfaces.wells.mapping 'Well' must be pinned as 'Name@version'")]
    [InlineData("interfaces:\n  wells: { record: { object: A.b.Well, key: [uwi] } }", "'interfaces.wells.mapping' is required")]
    [InlineData("interfaces:\n  wells: { mapping: Well@1.2.0 }", "'interfaces.wells.record' is required")]
    [InlineData("interfaces:\n  wells: { record: { object: A.b.Well }, mapping: Well@1.2.0 }", "interfaces.wells.record.key must name the record table's key columns")]
    [InlineData("interfaces:\n  wells: { record: { object: A.b.Well, key: [uwi] }, mapping: Well@1.2.0, parallelism: { interfaces: 2 } }", "interfaces.wells.parallelism.interfaces is the source's setting")]
    [InlineData("interfaces:\n  wells: { record: { object: A.b.Well, key: [uwi] }, mapping: Well@1.2.0, reliability: { parallelInterfaces: 2 } }", "interfaces.wells.reliability.parallelInterfaces is not a setting here any more: how many interfaces run at once is the source's setting, parallelism.interfaces")]
    [InlineData("interfaces:\n  wells: { record: { object: A.b.Well, key: [uwi] }, mapping: Well@1.2.0, reliability: { fanOut: 2 } }", "interfaces.wells.reliability.fanOut is now interfaces.wells.parallelism.fanOut")]
    [InlineData("interfaces:\n  wells: { record: { object: A.b.Well, key: [uwi] }, mapping: Well@1.2.0, failWhen: { failedPercent: 0 } }", "failWhen.failedPercent of interface 'wells' must be above 0 and at most 100")]
    [InlineData("interfaces:\n  wells: { record: { object: A.b.Well, key: [uwi] }, mapping: Well@1.2.0, failWhen: { consecutiveFailures: 0 } }", "failWhen.consecutiveFailures of interface 'wells' must be at least 1")]
    [InlineData("interfaces:\n  logs: { record: { object: A.b.Log, key: [id] }, mapping: WellLog@1.4.0, bulk: { root: ../c, locationColumn: f, hashColumn: h } }", "interfaces.logs is delivered through a DDMS")]
    [InlineData("interfaces:\n  wells: { record: { object: A.b.Well, key: [uwi] }, mapping: Well@1.2.0, after: [cores] }", "interfaces.wells.after names 'cores', which is not an interface of this document; it declares wells")]
    [InlineData("interfaces:\n  wells: { record: { object: A.b.Well, key: [uwi] }, mapping: Well@1.2.0, after: [WELLS] }", "interfaces.wells.after names the interface itself")]
    [InlineData("interfaces:\n  wells: { record: { object: A.b.Well, key: [uwi] }, mapping: Well@1.2.0 }\n  logs: { record: { object: A.b.Log, key: [id] }, mapping: WellLog@1.4.0, after: [wells, Wells] }", "interfaces.logs.after names 'Wells' more than once")]
    [InlineData("interfaces:\n  a: { record: { object: A.b.A, key: [id] }, mapping: A@1.0.0, after: [c] }\n  b: { record: { object: A.b.B, key: [id] }, mapping: B@1.0.0, after: [a] }\n  c: { record: { object: A.b.C, key: [id] }, mapping: C@1.0.0, after: [b] }", "the interfaces a -> c -> b -> a wait for each other through after:")]
    [InlineData("interfaces:\n  a: { ledger: shared, record: { object: A.b.A, key: [id] }, mapping: A@1.0.0 }\n  b: { ledger: SHARED, record: { object: A.b.B, key: [id] }, mapping: B@1.0.0 }", "the interfaces a, b would keep the same ledger ('shared')")]
    [InlineData("interfaces:\n  a: { ledger: petrel/b, record: { object: A.b.A, key: [id] }, mapping: A@1.0.0 }\n  b: { record: { object: A.b.B, key: [id] }, mapping: B@1.0.0 }", "the interfaces a, b would keep the same ledger ('petrel/b')")]
    [InlineData("interfaces:\n  a: { ledger: '  ', record: { object: A.b.A, key: [id] }, mapping: A@1.0.0 }", "interfaces.a.ledger must name the flow whose ledger the interface adopts")]
    public void An_interface_that_cannot_be_delivered_is_refused_naming_its_key(string interfaces, string expected)
    {
        var yaml = Source(string.Empty).Replace("interfaces:\n", interfaces + "\n", StringComparison.Ordinal);
        var refused = Assert.Throws<FlowValidationException>(() => _loader.ParseSource(yaml, "petrel.yaml"));
        Assert.StartsWith("petrel.yaml: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void How_many_interfaces_run_at_once_is_the_source_s_setting_only()
    {
        var parallel = _loader.ParseSource(Source(Wells).Replace("  concurrency: 4\n", "  concurrency: 4\n  interfaces: 7\n", StringComparison.Ordinal), "petrel.yaml");
        Assert.Equal(7, parallel.ParallelInterfaces);

        var tooMany = Source(Wells).Replace("  concurrency: 4\n", "  concurrency: 4\n  interfaces: 33\n", StringComparison.Ordinal);
        Assert.Contains("parallelism.interfaces must be between 1 and 32", Assert.Throws<FlowValidationException>(() => _loader.ParseSource(tooMany, "petrel.yaml")).Message, StringComparison.Ordinal);

        var single = File.ReadAllText(Samples.WellboreFlowFile).ReplaceLineEndings("\n").Replace("  concurrency: 8\n", "  concurrency: 8\n  interfaces: 2\n", StringComparison.Ordinal);
        Assert.Contains("the document declares no interfaces", Assert.Throws<FlowValidationException>(() => _loader.ParseSource(single, "wellbore.yaml")).Message, StringComparison.Ordinal);

        var cache = """
            flowType: cache
            name: searched
            source:
              endpoint: https://osdu.example.com
              headers: { data-partition-id: dev }
            types:
              - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
                fields: [data.Code, data.Name]
            reliability:
              parallelInterfaces: 2
            """;
        Assert.Contains(
            "cache.yaml: reliability.parallelInterfaces is not a setting here any more: a delivery flow writes it as parallelism.interfaces, and a cache flow has no use for it. Remove it.",
            Assert.Throws<FlowValidationException>(() => _loader.ParseCache(cache, "cache.yaml")).Message,
            StringComparison.Ordinal);

        // A cache flow runs nothing at once that a setting governs, so it has no parallelism block at all.
        var parallelism = cache
            .Replace("reliability:", "parallelism:", StringComparison.Ordinal)
            .Replace("parallelInterfaces:", "interfaces:", StringComparison.Ordinal);
        Assert.Contains("Property 'parallelism' not found", Assert.Throws<FlowValidationException>(() => _loader.ParseCache(parallelism, "cache.yaml")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_the_ledger_cannot_record_is_refused_before_anything_runs()
    {
        var longName = new string('n', 190);
        var yaml = Source("""
              interface-with-a-long-name:
                record: { object: Petrel.ing.Well, key: [uwi] }
                mapping: Well@1.2.0
            """).Replace("name: petrel\n", $"name: {longName}\n", StringComparison.Ordinal);
        var refused = Assert.Throws<FlowValidationException>(() => _loader.ParseSource(yaml, "petrel.yaml"));
        Assert.Contains("characters; the ledger records a flow under at most 200", refused.Message, StringComparison.Ordinal);

        var single = File.ReadAllText(Samples.WellboreFlowFile).ReplaceLineEndings("\n").Replace("name: wells-wellbore-03-header-delivery\n", $"name: {new string('w', 201)}\n", StringComparison.Ordinal);
        Assert.Contains("name is 201 characters", Assert.Throws<FlowValidationException>(() => _loader.ParseSource(single, "wellbore.yaml")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_keeps_its_interfaces_in_the_order_the_document_writes_them()
    {
        var names = Enumerable.Range(0, 40).Select(i => $"i{(i * 7919) % 97:D2}x{i}").ToList();
        var interfaces = string.Concat(names.Select(n => $"  {n}:\n    record: {{ object: Petrel.ing.T{n}, key: [id] }}\n    mapping: {n}@1.0.0\n"));
        var source = _loader.ParseSource(Source(string.Empty).Replace("interfaces:\n", "interfaces:\n" + interfaces, StringComparison.Ordinal), "petrel.yaml");
        Assert.Equal(names, source.Names);
    }

    [Fact]
    public void A_source_whose_document_is_a_single_flow_becomes_a_source_of_it()
    {
        var flow = _loader.LoadFlow(Samples.WellboreFlowFile);
        var source = SourceDefinition.Of(flow);
        Assert.Same(flow, source.First);
        Assert.Equal(flow.Name, source.Name);
        Assert.False(source.DeclaresInterfaces);

        var interfaceFlow = _loader.ParseFlow(Source(Wells), "petrel.yaml");
        Assert.Throws<ArgumentException>(() => SourceDefinition.Of(interfaceFlow));
    }
}
