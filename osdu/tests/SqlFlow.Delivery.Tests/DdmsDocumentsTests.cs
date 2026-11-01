using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The DDMSs a flow document declares under <c>target.ddms</c> (osdu/docs/reference/flow/ddms.md, target.ddms), the
/// route type <c>target.protocol</c> names, and every declaration the loader refuses.
/// </summary>
public sealed class DdmsDocumentsTests
{
    private readonly DeliveryDocumentLoader _loader = new();

    /// <summary>A flow in the single form; <paramref name="ddms"/> is YAML indented two spaces, under target.</summary>
    private static string Single(string ddms, string protocol = "ddms", string options = "") => ($$"""
        flowType: delivery
        name: logs
        source:
          connection: ${env:LOGS_DB}
          record: { object: Logs.ing.WellLog, key: [log_id] }
          work: work
        render:
          mapping: WellLog@1.4.0
        target:
          endpoint: https://osdu.example.com
          headers: { data-partition-id: dev }
          protocol: {{protocol}}
          protocolOptions: { batchSize: 10{{options}} }
        {{ddms}}
        """).ReplaceLineEndings("\n");

    /// <summary>A source with a storage interface and a ddms one; <paramref name="ddms"/> is YAML indented two spaces, under target.</summary>
    private static string Source(string ddms, string logs = "bulk: { root: curves, locationColumn: folder, hashColumn: curve_hash }") => ($$"""
        flowType: delivery
        name: estate
        source:
          connection: ${env:ESTATE_DB}
          work: work
        target:
          endpoint: https://osdu.example.com
          headers: { data-partition-id: dev }
        {{ddms}}
        interfaces:
          wellbores:
            record: { object: Estate.ing.Wellbore, key: [uwi] }
            mapping: Wellbore@1.0.0
          logs:
            record: { object: Estate.ing.WellLog, key: [log_id] }
            {{logs}}
            mapping: WellLog@1.4.0
        """).ReplaceLineEndings("\n");

    [Fact]
    public void A_flow_declares_each_ddms_with_its_root_shape_and_collections()
    {
        var flow = _loader.ParseFlow(Single("""
              ddms:
                petro:
                  root: /petro/
                  collections:
                    work-product-component--WellLog: { path: logs, bulk: true, columns: curveIds }
                    master-data--Wellbore: { path: wellbores }
                wellbore:
                  root: /api/os-wellbore-ddms
                  shape: wellbore-ddms-v3
                  collections:
                    work-product-component--WellboreTrajectory: { path: wellboretrajectories, bulk: true, columns: trajectoryStations }
            """), "logs.yaml");

        Assert.Equal(DeliveryProtocol.Ddms, flow.Target.Protocol);
        Assert.Equal(["petro", "wellbore"], flow.Target.Ddms.Select(d => d.Name));
        var petro = flow.Target.Ddms[0];
        Assert.Equal("/petro", petro.Root);
        Assert.Equal(DdmsShape.WellboreDdmsV3, petro.Shape);
        Assert.Null(petro.Registration);
        Assert.Equal(
            ["work-product-component--WellLog:logs:True:CurveIds", "master-data--Wellbore:wellbores:False:Unchecked"],
            petro.Collections.Select(c => $"{c.EntityType}:{c.Segment}:{c.Bulk}:{c.Columns}"));
        Assert.Equal(DdmsBulkColumns.TrajectoryStations, flow.Target.Ddms[1].CollectionFor("work-product-component--WellboreTrajectory")!.Columns);
    }

    [Fact]
    public void A_ddms_that_lists_no_collections_serves_those_of_its_shape()
    {
        var flow = _loader.ParseFlow(Single("""
              ddms:
                wellbore:
                  root: /api/os-wellbore-ddms
            """), "logs.yaml");

        Assert.Same(DdmsCatalog.WellboreDdmsCollections, flow.Target.Ddms.Single().Collections);

        // Declared with no body, a DDMS is the endpoint itself, with its shape's collections.
        var bare = _loader.ParseFlow(Single("""
              ddms:
                wellbore:
            """), "logs.yaml");
        Assert.Null(bare.Target.Ddms.Single().Root);
        Assert.Same(DdmsCatalog.WellboreDdmsCollections, bare.Target.Ddms.Single().Collections);
    }

    [Fact]
    public void A_flow_declares_a_well_delivery_ddms_with_its_deployment_and_a_rafs_ddms_with_its_content()
    {
        var flow = _loader.ParseFlow(Single("""
              ddms:
                welldelivery:
                  root: /api/well-delivery
                  shape: wellDeliveryV1
                  mirror: false
                  provider: ibm
                  concurrency: 2
                  collections:
                    master-data--Well:
                    master-data--WellboreSegment: {}
                    master-data--Rig: { path: rig }
                rafs:
                  root: /api/rafs-ddms/
                  shape: rafsV2
                  collections:
                    work-product-component--SamplesAnalysis: { path: samplesanalysis, bulk: true, typedContent: true }
                    work-product-component--DepthShift: { path: depthshift, bulk: true }
                    master-data--Sample: { path: masterdata }
            """, options: ", contentSchemaVersion: '1.1'"), "logs.yaml");

        var wd = flow.Target.Ddms[0];
        Assert.Equal(DdmsShape.WellDeliveryV1, wd.Shape);
        Assert.True(wd.DeclaresCollections);
        Assert.Equal(new WellDeliverySettings { Mirror = false, Provider = DdmsProvider.Ibm, Concurrency = 2 }, wd.WellDelivery);
        Assert.Equal(
            ["master-data--Well:well", "master-data--WellboreSegment:wellboresegment", "master-data--Rig:rig"],
            wd.Collections.Select(c => $"{c.EntityType}:{c.Segment}"));
        Assert.All(wd.Collections, c => Assert.False(c.Bulk));

        var rafs = flow.Target.Ddms[1];
        Assert.Equal("/api/rafs-ddms", rafs.Root);
        Assert.Null(rafs.WellDelivery);
        Assert.Equal(
            ["work-product-component--SamplesAnalysis:samplesanalysis:True:True", "work-product-component--DepthShift:depthshift:True:False", "master-data--Sample:masterdata:False:False"],
            rafs.Collections.Select(c => $"{c.EntityType}:{c.Segment}:{c.Bulk}:{c.TypedContent}"));
        Assert.Equal("1.1", flow.Target.ProtocolOptions.ContentSchemaVersion);

        // Without collections or settings, each serves its shape's collections, and a Well Delivery deployment keeps a Storage copy.
        var defaults = _loader.ParseFlow(Single("""
              ddms:
                welldelivery: { root: /api/well-delivery, shape: wellDeliveryV1 }
                rafs: { root: /api/rafs-ddms, shape: rafsV2 }
            """), "logs.yaml");
        Assert.Same(DdmsCatalog.WellDeliveryCollections, defaults.Target.Ddms[0].Collections);
        Assert.False(defaults.Target.Ddms[0].DeclaresCollections);
        Assert.Equal(new WellDeliverySettings(), defaults.Target.Ddms[0].WellDelivery);
        Assert.Same(DdmsCatalog.RafsCollections, defaults.Target.Ddms[1].Collections);
        Assert.Equal(ProtocolOptions.DefaultContentSchemaVersion, defaults.Target.ProtocolOptions.ContentSchemaVersion);
    }

    [Fact]
    public void A_flow_declares_a_production_historian_with_its_query_service_and_its_waits()
    {
        var flow = _loader.ParseFlow(Single("""
              ddms:
                historian:
                  root: /api/pddms/ingest/v1/
                  shape: productionTimeSeriesV1
                  queryRoot: /api/timeseries/v1
                  settleSeconds: 0
                  pollSeconds: 60
                  maxRequestBytes: 4000000
            """), "logs.yaml");

        var historian = flow.Target.Ddms.Single();
        Assert.Equal(DdmsShape.ProductionTimeSeriesV1, historian.Shape);
        Assert.Equal("/api/pddms/ingest/v1", historian.Root);
        Assert.Same(DdmsCatalog.TimeSeriesCollections, historian.Collections);
        Assert.Null(historian.WellDelivery);
        Assert.Equal(
            new TimeSeriesSettings { QueryRoot = "/api/timeseries/v1", SettleSeconds = 0, PollSeconds = 60, MaxRequestBytesPerRequest = 4_000_000 },
            historian.TimeSeries);

        // Without settings, the query service is where its contract serves it, and a delivery reads its points back for a minute.
        var defaults = _loader.ParseFlow(Single("""
              ddms:
                historian: { root: /api/pddms/ingest/v1, shape: productionTimeSeriesV1 }
            """), "logs.yaml").Target.Ddms.Single().TimeSeries;
        Assert.Equal(new TimeSeriesSettings { QueryRoot = DdmsCatalog.UsualTimeSeriesQueryRoot }, defaults);
        Assert.Equal((60, 5, 8_000_000L), (defaults!.SettleSeconds, defaults.PollSeconds, defaults.MaxRequestBytesPerRequest));
        Assert.Null(_loader.ParseFlow(Single("""
              ddms:
                rafs: { root: /api/rafs-ddms, shape: rafsV2 }
            """), "logs.yaml").Target.Ddms.Single().TimeSeries);
    }

    [Fact]
    public void A_flow_declares_a_seismic_store_with_its_subproject_folder_and_object_store()
    {
        var flow = _loader.ParseFlow(Single("""
              ddms:
                seismic:
                  root: /api/seismic-store/v3/
                  shape: seismicStoreV3
                  tenant: ' dev '
                  subproject: seismic-raw
                  folder: /surveys/north/
                  provider: anthos
                  objectStore: https://minio.example.com:9000/
                  region: eu-west-1
                  chunkMiB: 8
                  readOnly: true
                  collections:
                    dataset--FileCollection.SEGY:
                    dataset--FileCollection.Bluware.OpenVDS: { path: vds, bulk: true }
            """), "logs.yaml");

        var seismic = flow.Target.Ddms.Single();
        Assert.Equal(DdmsShape.SeismicStoreV3, seismic.Shape);
        Assert.Equal("/api/seismic-store/v3", seismic.Root);
        Assert.True(seismic.DeclaresCollections);
        Assert.Equal(
            ["dataset--FileCollection.SEGY:segy:True", "dataset--FileCollection.Bluware.OpenVDS:vds:True"],
            seismic.Collections.Select(c => $"{c.EntityType}:{c.Segment}:{c.Bulk}"));
        Assert.Equal(
            new SeismicStoreSettings
            {
                Tenant = "dev",
                Subproject = "seismic-raw",
                Folder = "surveys/north",
                Provider = DdmsProvider.Anthos,
                ObjectStore = "https://minio.example.com:9000",
                Region = "eu-west-1",
                ChunkMiB = 8,
                ReadOnly = true,
            },
            seismic.SeismicStore);
        Assert.Null(seismic.WellDelivery);
        Assert.Null(seismic.TimeSeries);

        // Without settings beyond the subproject, the tenant is the partition, the files go whole where the service says,
        // in parts of 32 MiB, and the dataset types are the ones Seismic Store's clients know.
        var defaults = _loader.ParseFlow(Single("""
              ddms:
                seismic: { root: /api/seismic-store/v3, shape: seismicStoreV3, subproject: seismic, folder: '/' }
            """), "logs.yaml").Target.Ddms.Single();
        Assert.Equal(new SeismicStoreSettings { Subproject = "seismic" }, defaults.SeismicStore);
        Assert.Equal(("us-east-1", 32, false), (defaults.SeismicStore!.Region, defaults.SeismicStore.ChunkMiB, defaults.SeismicStore.ReadOnly));
        Assert.Same(DdmsCatalog.SeismicStoreCollections, defaults.Collections);
        Assert.Null(_loader.ParseFlow(Single("""
              ddms:
                rafs: { root: /api/rafs-ddms, shape: rafsV2 }
            """), "logs.yaml").Target.Ddms.Single().SeismicStore);

        // gc takes another Google endpoint, and azure and gc need none; chunkMiB 0 keeps a file whole on Azure.
        var gc = _loader.ParseFlow(Single("""
              ddms:
                seismic: { root: /seistore-svc/api/v3, shape: seismicStoreV3, subproject: seismic, provider: gc, objectStore: 'http://gcs.local', chunkMiB: 0 }
            """), "logs.yaml").Target.Ddms.Single().SeismicStore!;
        Assert.Equal((DdmsProvider.Gc, "http://gcs.local", 0), (gc.Provider!.Value, gc.ObjectStore, gc.ChunkMiB));
    }

    [Fact]
    public void A_flow_declares_a_reservoir_management_ddms_with_its_waits_and_the_header_collections_it_uses()
    {
        var flow = _loader.ParseFlow(Single("""
              ddms:
                rm:
                  root: /api/rm-ddms/
                  shape: reservoirManagement
                  settleSeconds: 0
                  pollSeconds: 10
                  collections:
                    work-product-component--PersistedCollection: { path: kr-synthesis }
                    work-product-component--AquiferInterpretation: { path: ' tank-datum ' }
                    work-product-component--ReservoirEstimatedVolumes:
                    master-data--FluidSystem: {}
            """), "logs.yaml");

        var rm = flow.Target.Ddms.Single();
        Assert.Equal((DdmsShape.ReservoirManagement, "/api/rm-ddms"), (rm.Shape, rm.Root));
        Assert.Equal(new ReservoirManagementSettings { SettleSeconds = 0, PollSeconds = 10 }, rm.ReservoirManagement);
        Assert.Equal(
            [
                "work-product-component--PersistedCollection:kr-synthesis:True",
                "work-product-component--AquiferInterpretation:tank-datum:True",
                "work-product-component--ReservoirEstimatedVolumes:estimated-volumes:True",
                "master-data--FluidSystem:pvt-properties:False",
            ],
            rm.Collections.Select(c => $"{c.EntityType}:{c.Segment}:{c.Bulk}"));
        Assert.Null(rm.TimeSeries);
        Assert.Null(rm.SeismicStore);

        // Without settings, the defaults; without collections, the service's own entity types.
        var defaults = _loader.ParseFlow(Single("""
              ddms:
                rm: { root: /api/rm-ddms, shape: reservoirManagement }
            """), "logs.yaml").Target.Ddms.Single();
        Assert.Equal(new ReservoirManagementSettings(), defaults.ReservoirManagement);
        Assert.Equal((60, 5), (defaults.ReservoirManagement!.SettleSeconds, defaults.ReservoirManagement.PollSeconds));
        Assert.Same(DdmsCatalog.ReservoirManagementCollections, defaults.Collections);
        Assert.Null(_loader.ParseFlow(Single("""
              ddms:
                h: { root: /api/pddms/ingest/v1, shape: productionTimeSeriesV1, settleSeconds: 5 }
            """), "logs.yaml").Target.Ddms.Single().ReservoirManagement);
    }

    [Fact]
    public void A_ddms_named_by_its_registration_leaves_what_it_does_not_declare_to_the_register_service()
    {
        var flow = _loader.ParseFlow(Single("""
              ddms:
                wellbore:
                  register: wellbore
            """, options: ", registerPath: '/api/register/v1/ddms/{id}'"), "logs.yaml");

        var wellbore = flow.Target.Ddms.Single();
        Assert.Equal("wellbore", wellbore.Registration);
        Assert.True(wellbore.AwaitsDiscovery);
        Assert.Null(wellbore.Root);
        Assert.Empty(wellbore.Collections);
        Assert.Equal("/api/register/v1/ddms/{id}", flow.Target.ProtocolOptions.RegisterPath);

        // With a root, the registration supplies the collections; with collections, the root.
        var rooted = _loader.ParseFlow(Single("""
              ddms:
                wellbore:
                  register: wellbore
                  root: /wdms
            """), "logs.yaml");
        Assert.Equal("/wdms", rooted.Target.Ddms.Single().Root);
        Assert.Empty(rooted.Target.Ddms.Single().Collections);
    }

    [Theory]
    [InlineData("ddms", DeliveryProtocol.Ddms)]
    [InlineData("Ddms", DeliveryProtocol.Ddms)]
    [InlineData("storage", DeliveryProtocol.Storage)]
    [InlineData("file", DeliveryProtocol.File)]
    [InlineData("manifest", DeliveryProtocol.Manifest)]
    [InlineData("dspdm", DeliveryProtocol.Dspdm)]
    [InlineData("etp", DeliveryProtocol.Etp)]
    public void The_protocol_names_a_route(string value, DeliveryProtocol expected)
        => Assert.Equal(expected, _loader.ParseFlow(Single(string.Empty, value), "logs.yaml").Target.Protocol);

    /// <summary>
    /// The names these routes carried before they took the names they carry everywhere else. Flow documents written then
    /// keep loading, so none of these may stop resolving without a deliberate break.
    /// </summary>
    [Theory]
    [InlineData("osduRecord", DeliveryProtocol.Storage)]
    [InlineData("osduWellLog", DeliveryProtocol.Ddms)]
    [InlineData("OSDUWELLLOG", DeliveryProtocol.Ddms)]
    [InlineData("osduFile", DeliveryProtocol.File)]
    [InlineData("osduManifest", DeliveryProtocol.Manifest)]
    [InlineData("osduDspdm", DeliveryProtocol.Dspdm)]
    [InlineData("osduEtp", DeliveryProtocol.Etp)]
    public void A_retired_protocol_name_still_names_the_route_it_named(string value, DeliveryProtocol expected)
        => Assert.Equal(expected, _loader.ParseFlow(Single(string.Empty, value), "logs.yaml").Target.Protocol);

    /// <summary>
    /// The rest of the retired names, whose routes need payloads or a workflow this flow does not declare. The name still
    /// resolves: what refuses the flow is the route itself, and it says so under the name that route carries now.
    /// </summary>
    [Theory]
    [InlineData("osduDataset", "dataset")]
    [InlineData("osduFileAndDdms", "fileAndDdms")]
    [InlineData("osduManifestAndDdms", "manifestAndDdms")]
    [InlineData("osduWorkflow", "workflow")]
    public void A_retired_protocol_name_of_a_route_this_flow_cannot_run_still_resolves(string value, string route)
    {
        var refused = Assert.Throws<FlowValidationException>(() => _loader.ParseFlow(Single(string.Empty, value), "logs.yaml"));
        Assert.Contains($"the {route} route", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("teleport")]
    [InlineData("1")]
    public void A_protocol_that_is_neither_is_refused(string value)
    {
        var refused = Assert.Throws<FlowValidationException>(() => _loader.ParseFlow(Single(string.Empty, value), "logs.yaml"));
        Assert.Equal(
            $"logs.yaml: 'target.protocol' value '{value}' is not one of storage, file, dataset, manifest, ddms, fileAndDdms, manifestAndDdms, workflow, dspdm, etp.",
            refused.Message);
    }

    public static TheoryData<string, string, string, string> RefusedDeclarations => new()
    {
        { "  ddms:\n    petro: { root: /petro }", "storage", "", "target.ddms declares the DDMSs the ddms route delivers to, and this flow's protocol is storage" },
        { "  ddms: {}", "ddms", "", "target.ddms declares no DDMS" },
        { "  ddms:\n    2petro: { root: /petro }", "ddms", "", "target.ddms names a DDMS '2petro'; a DDMS name is a letter followed by letters, digits, '_' and '-'" },
        { "  ddms:\n    petro: { root: /a, collections: { master-data--Well: { path: wells } } }\n    Petro: { root: /b, collections: { master-data--Wellbore: { path: wellbores } } }", "ddms", "", "target.ddms declares 'Petro' more than once (DDMS names are compared ignoring case)" },
        { "  ddms:\n    petro: { root: /petro, shape: etp }", "ddms", "", "'target.ddms.petro.shape' value 'etp' is not one of wellboreDdmsV3" },
        { "  ddms:\n    petro: { root: api/petro }", "ddms", "", "target.ddms.petro.root 'api/petro' must be a path under the endpoint starting with '/'" },
        { "  ddms:\n    petro: { root: 'https://petro.example.com/ddms' }", "ddms", "", "target.ddms.petro.root 'https://petro.example.com/ddms' must be a path" },
        { "  ddms:\n    petro: { root: '/petro/{id}' }", "ddms", "", "target.ddms.petro.root '/petro/{id}' must be a path" },
        { "  ddms:\n    petro: { root: / }", "ddms", "", "target.ddms.petro.root '/' must be a path" },
        { "  ddms:\n    petro: { root: /petro, collections: {} }", "ddms", "", "target.ddms.petro.collections lists no collection" },
        { "  ddms:\n    petro: { root: /petro, collections: { WellLog: { path: welllogs } } }", "ddms", "", "target.ddms.petro.collections names 'WellLog', which is not an OSDU entity type with its group" },
        { "  ddms:\n    petro: { root: /petro, collections: { master-data--Well: { path: a }, MASTER-DATA--WELL: { path: b } } }", "ddms", "", "target.ddms.petro.collections lists MASTER-DATA--WELL more than once" },
        { "  ddms:\n    petro:\n      root: /petro\n      collections:\n        master-data--Well:", "ddms", "", "target.ddms.petro.collections.master-data--Well declares nothing" },
        { "  ddms:\n    petro: { root: /petro, collections: { master-data--Well: { path: wells/x } } }", "ddms", "", "target.ddms.petro.collections.master-data--Well.path 'wells/x' must be the collection's path segment" },
        { "  ddms:\n    petro: { root: /petro, collections: { master-data--Well: { bulk: true } } }", "ddms", "", "target.ddms.petro.collections.master-data--Well.path '' must be the collection's path segment" },
        { "  ddms:\n    petro: { root: /petro, collections: { master-data--Well: { path: wells, columns: curveIds } } }", "ddms", "", "target.ddms.petro.collections.master-data--Well.columns says what bulk data columns are checked against, and the collection holds records alone (bulk is false)" },
        { "  ddms:\n    petro: { root: /petro, collections: { master-data--Well: { path: wells, bulk: true, columns: mnemonics } } }", "ddms", "", "'target.ddms.petro.collections.master-data--Well.columns' value 'mnemonics' is not one of" },
        { "  ddms:\n    a: { root: /a, collections: { work-product-component--WellLog: { path: logs } } }\n    b: { root: /b }", "ddms", "", "target.ddms serves work-product-component--WellLog from both 'a' and 'b'" },
        { "  ddms:\n    a: { collections: { work-product-component--WellLog: { path: logs } } }\n    b: { root: /b, collections: { master-data--Well: { path: wells } } }", "ddms", "", "target.ddms.a names no root, which makes the flow's endpoint that DDMS itself, so the flow reaches no other DDMS. Give every DDMS its root" },
        { "  ddms:\n    a: { collections: { work-product-component--WellLog: { path: logs } } }", "ddms", ", ddmsRoot: /api/os-wellbore-ddms", "target.ddms.a names no root, which makes the flow's endpoint that DDMS itself, so the flow reaches no other DDMS and target.protocolOptions.ddmsRoot places none under it" },
        { "  ddms:\n    a: { register: w }", "ddms", "", "target.ddms.a.register 'w' is not an id the Register service keeps a DDMS under" },
        { "  ddms:\n    a: { register: wellbore, root: /wdms, collections: { master-data--Well: { path: wells } } }", "ddms", "", "target.ddms.a declares its root and its collections, so its registration has nothing to supply" },
        { "  ddms:\n    a: { root: /wdms }", "ddms", ", registerPath: '/api/register/v1/ddms/{id}'", "target.protocolOptions.registerPath says where the Register service reads a DDMS registration, and no DDMS under target.ddms names one with register" },
        { "  ddms:\n    a: { register: wellbore }", "ddms", ", registerPath: /api/register/v1/ddms", "target.protocolOptions.registerPath '/api/register/v1/ddms' must be a path under the endpoint starting with '/', or an absolute http(s) URL, with {id} where the registration's id goes" },
        { "  ddms:\n    a: { register: wellbore }", "ddms", ", registerPath: 'register/{id}'", "target.protocolOptions.registerPath 'register/{id}' must be a path" },
        { "  ddms:\n    a: { rooot: /wdms }", "ddms", "", "invalid YAML" },
        { "  ddms:\n    wd: { root: /wd, shape: wellDeliveryV1, register: wd-1 }", "ddms", "", "target.ddms.wd.register reads a DDMS's collections from its Register service registration, which the route reads for DDMSs of the wellboreDdmsV3 shape" },
        { "  ddms:\n    r: { root: /r, shape: rafsV2, mirror: false, concurrency: 2 }", "ddms", "", "target.ddms.r.mirror, target.ddms.r.concurrency describe a Well Delivery DDMS deployment, and target.ddms.r has the rafsV2 shape" },
        { "  ddms:\n    wd: { root: /wd, shape: wellDeliveryV1, provider: anthos }", "ddms", "", "target.ddms.wd.provider 'anthos' is not a provider the Well Delivery DDMS runs on: azure, aws, gc or ibm" },
        { "  ddms:\n    wd: { root: /wd, shape: wellDeliveryV1, provider: moon }", "ddms", "", "'target.ddms.wd.provider' value 'moon' is not one of azure, aws, gc, anthos, ibm" },
        { "  ddms:\n    wd: { root: /wd, shape: wellDeliveryV1, concurrency: 0 }", "ddms", "", "target.ddms.wd.concurrency must be between 1 and 16" },
        { "  ddms:\n    wd: { root: /wd, shape: wellDeliveryV1, collections: { master-data--Well: { path: wells } } }", "ddms", "", "target.ddms.wd.collections.master-data--Well.path 'wells' is not the path the Well Delivery DDMS serves master-data--Well under" },
        { "  ddms:\n    wd: { root: /wd, shape: wellDeliveryV1, collections: { work-product-component--WellLog: { bulk: true } } }", "ddms", "", "target.ddms.wd.collections.work-product-component--WellLog describes bulk data, and the Well Delivery DDMS keeps records alone" },
        { "  ddms:\n    petro: { root: /petro, collections: { master-data--Well: { path: wells, bulk: true, typedContent: true } } }", "ddms", "", "target.ddms.petro.collections.master-data--Well.typedContent says a RAFS collection holds several content types, and target.ddms.petro has the wellboreDdmsV3 shape" },
        { "  ddms:\n    r: { root: /r, shape: rafsV2, collections: { master-data--Sample: { path: masterdata, typedContent: true } } }", "ddms", "", "target.ddms.r.collections.master-data--Sample.typedContent says what content the collection holds, and it holds records alone (bulk is false)" },
        { "  ddms:\n    r: { root: /r, shape: rafsV2, collections: { work-product-component--FluidModel: { path: fluidmodel, bulk: true, columns: curveIds } } }", "ddms", "", "target.ddms.r.collections.work-product-component--FluidModel.columns names the curve and station checks the Wellbore DDMS applies to its bulk data, and target.ddms.r has the rafsV2 shape" },
        { "  ddms:\n    r: { root: /r, shape: rafsV2 }", "ddms", ", contentSchemaVersion: v1", "target.protocolOptions.contentSchemaVersion 'v1' is not a content schema version such as 1.0.0" },
        { "  ddms:\n    h: { shape: productionTimeSeriesV1 }", "ddms", "", "target.ddms.h.root is required. The historian's records are written through Storage and its points through its ingestion service, so the flow's endpoint is the platform both are under; say where the ingestion service is under it (usually /api/pddms/ingest/v1)." },
        { "  ddms:\n    h: { shape: productionTimeSeriesV1, register: pddms }", "ddms", "", "target.ddms.h.register reads a DDMS's collections from its Register service registration, which the route reads for DDMSs of the wellboreDdmsV3 shape" },
        { "  ddms:\n    r: { root: /r, shape: rafsV2, queryRoot: /q, settleSeconds: 5 }", "ddms", "", "target.ddms.r.queryRoot, target.ddms.r.settleSeconds describe the Production DDMS historian, and target.ddms.r has the rafsV2 shape. Remove them, or declare shape: productionTimeSeriesV1." },
        { "  ddms:\n    w: { root: /w, pollSeconds: 5, maxRequestBytes: 20000 }", "ddms", "", "target.ddms.w.pollSeconds, target.ddms.w.maxRequestBytes describe the Production DDMS historian, and target.ddms.w has the wellboreDdmsV3 shape" },
        { "  ddms:\n    h: { root: /h, shape: productionTimeSeriesV1, settleSeconds: 3601 }", "ddms", "", "target.ddms.h.settleSeconds must be between 0 and 3600" },
        { "  ddms:\n    h: { root: /h, shape: productionTimeSeriesV1, settleSeconds: -1 }", "ddms", "", "target.ddms.h.settleSeconds must be between 0 and 3600" },
        { "  ddms:\n    h: { root: /h, shape: productionTimeSeriesV1, pollSeconds: 0 }", "ddms", "", "target.ddms.h.pollSeconds must be between 1 and 60." },
        { "  ddms:\n    h: { root: /h, shape: productionTimeSeriesV1, maxRequestBytes: 9999 }", "ddms", "", "target.ddms.h.maxRequestBytes must be between 10000 and 64000000" },
        { "  ddms:\n    h: { root: /h, shape: productionTimeSeriesV1, maxRequestBytes: 64000001 }", "ddms", "", "target.ddms.h.maxRequestBytes must be between 10000 and 64000000" },
        { "  ddms:\n    h: { root: /h, shape: productionTimeSeriesV1, queryRoot: api/query }", "ddms", "", "target.ddms.h.queryRoot 'api/query' must be a path under the endpoint starting with '/'" },
        { "  ddms:\n    h: { root: /h, shape: productionTimeSeriesV1, collections: { work-product-component--ProductionValues: { path: production-values, bulk: true } } }", "ddms", "", "target.ddms.h.collections lists collections, and the historian serves work-product-component--ProductionValues records alone, under production-values. Leave collections out." },
        { "  ddms:\n    s: { shape: seismicStoreV3, subproject: seismic }", "ddms", "", "target.ddms.s.root is required. A dataset's record is read and removed through Storage, so the flow's endpoint is the platform Seismic Store is under; say where Seismic Store is under it, version path included (usually /api/seismic-store/v3, or /seistore-svc/api/v3 on Azure)." },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3 }", "ddms", "", "target.ddms.s.subproject is required: the Seismic Store subproject the datasets are registered in, which an operator provisions." },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: ' ' }", "ddms", "", "target.ddms.s.subproject is required" },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: Seismic }", "ddms", "", "target.ddms.s.subproject 'Seismic' is not a Seismic Store subproject name: a lower-case letter, then lower-case letters, digits and '-', not ending in '-'." },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: seismic, tenant: 'open des' }", "ddms", "", "target.ddms.s.tenant 'open des' is not a Seismic Store tenant name (letters, digits, '_', '.' and '-'); on OSDU it is the data partition id." },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: seismic, folder: 'a//b' }", "ddms", "", "target.ddms.s.folder 'a//b' is not a Seismic Store folder: segments of letters, digits, '_', '.' and '-', separated by single slashes." },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: seismic, folder: 'north sea' }", "ddms", "", "target.ddms.s.folder 'north sea' is not a Seismic Store folder" },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: seismic, provider: aws }", "ddms", "", "target.ddms.s.provider 'aws' is not a provider Seismic Store v3 runs on: azure, gc, anthos or ibm." },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: seismic, provider: moon }", "ddms", "", "'target.ddms.s.provider' value 'moon' is not one of azure, aws, gc, anthos, ibm" },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: seismic, provider: ibm }", "ddms", "", "target.ddms.s.objectStore is required on ibm: Seismic Store issues a key triple there for an S3 store it does not name (osdu/specs/seismic-ddms/INTEGRATION.md section 4.3)." },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: seismic, objectStore: 'minio:9000' }", "ddms", "", "target.ddms.s.objectStore 'minio:9000' must be the object store's absolute http(s) address, without credentials, query or fragment (https://s3.example.com)." },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: seismic, objectStore: 'https://minio.example.com/?region=x' }", "ddms", "", "target.ddms.s.objectStore 'https://minio.example.com/?region=x' must be the object store's absolute http(s) address" },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: seismic, objectStore: 'https://someone@minio.example.com' }", "ddms", "", "target.ddms.s.objectStore 'https://someone@minio.example.com' must be the object store's absolute http(s) address" },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: seismic, objectStore: 'https://minio.example.com#x' }", "ddms", "", "target.ddms.s.objectStore 'https://minio.example.com#x' must be the object store's absolute http(s) address" },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: seismic, region: EU_WEST }", "ddms", "", "target.ddms.s.region 'EU_WEST' is not a region name (lower-case letters, digits and '-', such as us-east-1)." },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: seismic, chunkMiB: 257 }", "ddms", "", "target.ddms.s.chunkMiB must be between 0 and 256" },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: seismic, chunkMiB: -1 }", "ddms", "", "target.ddms.s.chunkMiB must be between 0 and 256" },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: seismic, register: sdms }", "ddms", "", "target.ddms.s.register reads a DDMS's collections from its Register service registration, which the route reads for DDMSs of the wellboreDdmsV3 shape" },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: seismic, mirror: false }", "ddms", "", "target.ddms.s.mirror describe a Well Delivery DDMS deployment, and target.ddms.s has the seismicStoreV3 shape" },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: seismic, settleSeconds: 5 }", "ddms", "", "target.ddms.s.settleSeconds describe the Production DDMS historian, and target.ddms.s has the seismicStoreV3 shape" },
        { "  ddms:\n    w: { root: /w, subproject: seismic, readOnly: true }", "ddms", "", "target.ddms.w.subproject, target.ddms.w.readOnly describe a Seismic Store, and target.ddms.w has the wellboreDdmsV3 shape. Remove them, or declare shape: seismicStoreV3." },
        { "  ddms:\n    r: { root: /r, shape: rafsV2, objectStore: 'https://s3.example.com', chunkMiB: 8 }", "ddms", "", "target.ddms.r.objectStore, target.ddms.r.chunkMiB describe a Seismic Store, and target.ddms.r has the rafsV2 shape" },
        { "  ddms:\n    wd: { root: /wd, shape: wellDeliveryV1, provider: ibm, tenant: dev }", "ddms", "", "target.ddms.wd.tenant describe a Seismic Store, and target.ddms.wd has the wellDeliveryV1 shape" },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: seismic, collections: { work-product-component--SeismicTraceData: { path: traces } } }", "ddms", "", "target.ddms.s.collections.work-product-component--SeismicTraceData names a type Seismic Store registers no dataset for; a Seismic Store dataset's record is a dataset--FileCollection.* record." },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: seismic, collections: { dataset--FileCollection.: {} } }", "ddms", "", "target.ddms.s.collections.dataset--FileCollection. names a type Seismic Store registers no dataset for" },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: seismic, collections: { dataset--FileCollection.SEGY: { bulk: false } } }", "ddms", "", "target.ddms.s.collections.dataset--FileCollection.SEGY says what a dataset keeps, and a Seismic Store dataset always keeps its files; remove bulk, columns and typedContent." },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: seismic, collections: { dataset--FileCollection.SEGY: { columns: curveIds } } }", "ddms", "", "target.ddms.s.collections.dataset--FileCollection.SEGY says what a dataset keeps" },
        { "  ddms:\n    s: { root: /s, shape: seismicStoreV3, subproject: seismic, collections: { dataset--FileCollection.SEGY: { path: 'a/b' } } }", "ddms", "", "target.ddms.s.collections.dataset--FileCollection.SEGY.path 'a/b' must be a name of letters, digits, '.', '_' and '-', at most 100 characters." },
        { "  ddms:\n    r: { shape: reservoirManagement }", "ddms", "", "target.ddms.r.root is required. A header record is written, read and removed through Storage, so the flow's endpoint is the platform the Reservoir Management DDMS is under; say where the service is under it (its project names no prefix, so it is the deployment's)." },
        { "  ddms:\n    r: { root: /r, shape: reservoirManagement, settleSeconds: 3601 }", "ddms", "", "target.ddms.r.settleSeconds must be between 0 and 3600: how long a delivery with rows waits for the service to take its header record, 0 asking once." },
        { "  ddms:\n    r: { root: /r, shape: reservoirManagement, pollSeconds: 0 }", "ddms", "", "target.ddms.r.pollSeconds must be between 1 and 60." },
        { "  ddms:\n    r: { root: /r, shape: reservoirManagement, queryRoot: /q, maxRequestBytes: 20000 }", "ddms", "", "target.ddms.r.queryRoot, target.ddms.r.maxRequestBytes describe the Production DDMS historian, and target.ddms.r has the reservoirManagement shape" },
        { "  ddms:\n    r: { root: /r, shape: reservoirManagement, provider: azure }", "ddms", "", "target.ddms.r.provider describe a Well Delivery DDMS deployment, and target.ddms.r has the reservoirManagement shape" },
        { "  ddms:\n    r: { root: /r, shape: reservoirManagement, subproject: seismic }", "ddms", "", "target.ddms.r.subproject describe a Seismic Store, and target.ddms.r has the reservoirManagement shape" },
        { "  ddms:\n    r: { root: /r, shape: reservoirManagement, register: rm-ddms }", "ddms", "", "target.ddms.r.register reads a DDMS's collections from its Register service registration, which the route reads for DDMSs of the wellboreDdmsV3 shape" },
        { "  ddms:\n    r: { root: /r, shape: reservoirManagement, collections: { work-product-component--Other: {} } }", "ddms", "", "target.ddms.r.collections.work-product-component--Other.path is required: the header collection of the Reservoir Management DDMS the records go to (estimated-volumes, pvt-properties, geological-labels, petro-properties, tank-datum, fluid-synthesis, kr-synthesis, phi-k-synthesis, forecast); work-product-component--Other is none of its own entity types." },
        { "  ddms:\n    r: { root: /r, shape: reservoirManagement, collections: { work-product-component--Other: { path: aquifer-datum } } }", "ddms", "", "target.ddms.r.collections.work-product-component--Other.path 'aquifer-datum' is not a header collection of the Reservoir Management DDMS: estimated-volumes, pvt-properties" },
        { "  ddms:\n    r: { root: /r, shape: reservoirManagement, collections: { work-product-component--ReservoirEstimatedVolumes: { bulk: false } } }", "ddms", "", "target.ddms.r.collections.work-product-component--ReservoirEstimatedVolumes says what the collection keeps, and the Reservoir Management DDMS's tables decide that (estimated-volumes keeps rows in estimated-volumes-det); remove bulk, columns and typedContent." },
        { "  ddms:\n    r: { root: /r, shape: reservoirManagement, collections: { master-data--FluidSystem: { columns: curveIds } } }", "ddms", "", "(pvt-properties keeps no rows); remove bulk, columns and typedContent." },
        { "  ddms:\n    h: { root: /h, shape: productionTimeSeriesV1 }\n    r: { root: /r, shape: reservoirManagement }", "ddms", "", "target.ddms serves work-product-component--ProductionValues from both 'h' and 'r'" },
        { string.Empty, "storage", ", uploadBlockMiB: 0", "target.protocolOptions.uploadBlockMiB must be 1 to 1024." },
        { string.Empty, "storage", ", uploadBlockMiB: 1025", "target.protocolOptions.uploadBlockMiB must be 1 to 1024." },
        { string.Empty, "storage", ", uploadConcurrency: 0", "target.protocolOptions.uploadConcurrency must be 1 to 32." },
        { string.Empty, "storage", ", uploadConcurrency: 33", "target.protocolOptions.uploadConcurrency must be 1 to 32." },
    };

    [Fact]
    public void A_flow_says_how_a_large_file_goes_and_needs_to_say_nothing()
    {
        var declared = _loader.ParseFlow(Single(string.Empty, "storage", ", uploadBlockMiB: 1, uploadConcurrency: 8, uploadServerCopy: false"), "logs.yaml").Target.ProtocolOptions;
        Assert.Equal((1, 8, false), (declared.UploadBlockMiB, declared.UploadConcurrency, declared.UploadServerCopy));

        var defaults = _loader.ParseFlow(Single(string.Empty, "storage"), "logs.yaml").Target.ProtocolOptions;
        Assert.Equal((32, 4, true), (defaults.UploadBlockMiB, defaults.UploadConcurrency, defaults.UploadServerCopy));
    }

    [Theory]
    [MemberData(nameof(RefusedDeclarations))]
    public void A_declaration_that_cannot_mean_what_it_says_is_refused(string ddms, string protocol, string options, string expected)
    {
        var refused = Assert.Throws<FlowValidationException>(() => _loader.ParseFlow(Single(ddms, protocol, options), "logs.yaml"));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
        Assert.StartsWith("logs.yaml: ", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_sends_its_ddms_interfaces_to_the_ddms_it_declares()
    {
        var source = _loader.ParseSource(Source("""
              ddms:
                wellbore:
                  root: /api/os-wellbore-ddms
            """), "estate.yaml");

        var logs = source.Interface("logs");
        Assert.Equal(DeliveryProtocol.Ddms, logs.Target.Protocol);
        Assert.Null(logs.Target.ProtocolOptions.DdmsRoot);
        Assert.Equal("/api/os-wellbore-ddms", logs.Target.Ddms.Single().Root);

        // The storage interface has no use for the DDMSs.
        Assert.Empty(source.Interface("wellbores").Target.Ddms);

        // A registration says where its DDMS is, so it needs no root.
        var registered = _loader.ParseSource(Source("""
              ddms:
                wellbore:
                  register: wellbore
            """), "estate.yaml");
        Assert.Equal("wellbore", registered.Interface("logs").Target.Ddms.Single().Registration);
    }

    [Theory]
    [InlineData("  ddms:\n    wellbore:\n      collections: { work-product-component--WellLog: { path: welllogs, bulk: true } }", "bulk: { root: curves, locationColumn: folder, hashColumn: curve_hash }", "target.ddms.wellbore.root is required. The source's endpoint is the platform its interfaces reach every service under")]
    [InlineData("", "bulk: { root: curves, locationColumn: folder, hashColumn: curve_hash }", "declare it under target.ddms with its root, or set target.protocolOptions.ddmsRoot or interfaces.logs.protocolOptions.ddmsRoot")]
    [InlineData("  ddms:\n    wellbore: { root: /api/os-wellbore-ddms }", "route: storage", "target.ddms declares the DDMSs the source's interfaces are delivered to, and no interface is delivered through one")]
    [InlineData("  ddms:\n    welldelivery: { shape: wellDeliveryV1 }", "route: ddms", "target.ddms.welldelivery.root is required. The source's endpoint is the platform its interfaces reach every service under, so say where the DDMS is under it (a DDMS of the wellDeliveryV1 shape is usually deployed under /api/well-delivery).")]
    public void A_source_that_does_not_say_where_its_ddms_is_or_declares_one_it_does_not_use_is_refused(string ddms, string logs, string expected)
    {
        var refused = Assert.Throws<FlowValidationException>(() => _loader.ParseSource(Source(ddms, logs), "estate.yaml"));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }
}
