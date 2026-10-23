using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Tests;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

public class YamlDocumentLoaderTests
{
    private const string Flow = """
        flowType: delivery
        name: demo
        parameters:
          logSource: { required: true }
        source:
          connection: ${env:OSDU_DATA_DB}
          record:
            object: OsduData.silver.WellLog
            key: [source_project, log_id]
            scope: { log_source: logSource }
          datasets:
            curves:
              object: OsduData.silver.WellLogCurve
              join: { source_project: source_project, log_id: log_id }
              orderBy: [curve_ordinal]
          payloads:
            curves:
              root: curves/{logSource}
              locationColumn: curve_folder
              pattern: "chunk_*.parquet"
              hashColumn: payload_hash
          lastModified: update_date
          work: work/{logSource}
        render:
          mapping: WellLog@1.4.0
          parameters: { dataPartition: dev }
        target:
          endpoint: https://example.org/facade-api
          headers: { data-partition-id: dev }
          protocol: ddms
          protocolOptions: { payload: curves, recordMethod: POST }
        reliability:
          retry: { attempts: 5, backoff: fixed }
        parallelism:
          concurrency: 2
        """;

    /// <summary>
    /// A flow takes its records from its ingestion tables and nowhere else: records reach them through the pre and
    /// ingestion flows, so a document still declaring where API-submitted records would land is refused by name.
    /// </summary>
    [Fact]
    public void A_submissions_block_is_refused_as_a_key_the_flow_does_not_have()
    {
        var flow = Flow.ReplaceLineEndings("\n");
        var submissions = flow.Replace(
            "  lastModified: update_date",
            "  submissions:\n    record:\n      preFlow: demo-pre\n      landing: landing/record\n  lastModified: update_date",
            StringComparison.Ordinal);
        Assert.NotEqual(flow, submissions);

        var ex = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseFlow(submissions, "flow.yaml"));
        Assert.StartsWith("flow.yaml:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("submissions", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flow_without_the_partition_header_serves_the_registry_and_leaves_the_data_partition_to_the_run()
    {
        var yaml = Flow.ReplaceLineEndings("\n").Replace("headers: { data-partition-id: dev }", "headers: { }", StringComparison.Ordinal);
        Assert.DoesNotContain("data-partition-id", yaml, StringComparison.Ordinal);

        // Without its header the flow serves every registered partition, so the partition it mints ids in is the run's.
        var ex = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseFlow(yaml, "inline.yaml"));
        Assert.Contains("sets 'dataPartition'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("serves every partition registered with the catalog", ex.Message, StringComparison.Ordinal);

        var flow = new DeliveryDocumentLoader().ParseFlow(
            yaml.Replace("  parameters: { dataPartition: dev }\n", string.Empty, StringComparison.Ordinal), "inline.yaml");
        Assert.True(flow.FollowsRegistry);
        Assert.True(flow.IsUnbound);
    }

    [Theory]
    [InlineData("datasetKind: osdu:wks:dataset--File.Generic:1.0")]
    [InlineData("manifestKind: osdu:wks:Manifest")]
    public void A_dataset_or_manifest_kind_the_target_would_refuse_is_rejected_when_read(string option)
    {
        var yaml = Flow.Replace("protocolOptions: { payload: curves, recordMethod: POST }", "protocolOptions: { payload: curves, recordMethod: POST, " + option + " }", StringComparison.Ordinal);
        var ex = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseFlow(yaml, "inline.yaml"));
        Assert.Contains("major.minor.patch", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(-1, false)]
    public void Only_a_session_threshold_that_cannot_overwrite_chunks_is_accepted(int threshold, bool accepted)
    {
        var yaml = Flow.Replace("protocolOptions: { payload: curves, recordMethod: POST }", "protocolOptions: { payload: curves, recordMethod: POST, sessionThresholdChunks: " + threshold.ToString(System.Globalization.CultureInfo.InvariantCulture) + " }", StringComparison.Ordinal);
        var loader = new DeliveryDocumentLoader();
        if (accepted)
        {
            Assert.Equal(threshold, loader.ParseFlow(yaml, "inline.yaml").Target.ProtocolOptions.SessionThresholdChunks);
        }
        else
        {
            var ex = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(yaml, "inline.yaml"));
            Assert.Contains("overwrite each other", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Loads_the_sample_flow_and_mapping()
    {
        var loader = new DeliveryDocumentLoader();
        var flow = loader.LoadFlow(Samples.Flow);
        Assert.Equal("welldb-welllog-03-header-delivery", flow.Name);
        Assert.Equal(DeliveryProtocol.Ddms, flow.Target.Protocol);
        Assert.Equal("WellLog", flow.Render.MappingName);
        Assert.Equal("1.4.0", flow.Render.MappingVersion);
        Assert.Equal(TargetAuthType.OAuth2ClientCredentials, flow.Target.Auth.Type);
        Assert.Equal(["Datasets", "DDMSDatasets", "ExtensionProperties"], flow.Target.ProtocolOptions.PreserveDataKeys);

        // The sample delivers what its own pre and ing flows load into the ingestion tables.
        Assert.Equal("OsduData.silver.WellLog", flow.Source.Record.Object);
        Assert.Equal(["source_project", "log_id"], flow.Source.Record.Key);
        Assert.Equal("RecId", flow.Source.Record.PrimaryKey);
        Assert.Equal("logSource", flow.Source.Record.Scope["log_source"]);
        Assert.Equal("OsduData.silver.WellLogCurve", flow.Source.Datasets["curves"].Object);
        Assert.Equal("source_project", flow.Source.Datasets["curves"].Join["source_project"]);
        Assert.Equal(["curve_ordinal"], flow.Source.Datasets["curves"].OrderBy);
        Assert.Equal("curve_folder", flow.Source.Payloads["curves"].LocationColumn);
        Assert.Equal("chunk_count", flow.Source.Payloads["curves"].ChunkCountColumn);

        var mapping = new MappingCatalog(Samples.Mappings, loader).Load("WellLog@1.4.0");
        Assert.Equal(new TemplateReference("osdu:wks:work-product-component--WellLog:1.4.0", "26a3c3441882db4f"), mapping.Template);
        Assert.Contains(mapping.Entries, e => e.Target.Text == "osdu.data.Curves" && e.IsRepeater && e.Source!.Child == "curves");
        Assert.Equal(["curves"], mapping.ChildDatasets);
        Assert.Equal(["{$param.legalTag}"], mapping.Envelope.LegalTags);
        Assert.Equal(["US"], mapping.Envelope.OtherRelevantDataCountries);
    }

    [Fact]
    public void Parses_inline_flow_with_defaults()
    {
        var flow = new DeliveryDocumentLoader().ParseFlow(Flow, "inline.yaml");
        Assert.Equal(2, flow.Parallelism.Concurrency);
        Assert.Equal(BackoffKind.Fixed, flow.Reliability.Retry.Backoff);
        Assert.Equal(5, flow.Reliability.Retry.Attempts);
        Assert.Equal(FlowSystemColumns.DefaultUpdated, flow.Source.SystemColumns.Updated);
        Assert.Equal(FlowSystemColumns.DefaultFileName, flow.Source.SystemColumns.FileName);
        Assert.Equal(FlowIncremental.DefaultOverlapSeconds, flow.Source.Incremental.OverlapSeconds);
        Assert.Equal(SourceIsolation.Snapshot, flow.Source.Incremental.Isolation);
        Assert.Equal("POST", flow.Target.ProtocolOptions.RecordMethod);
        Assert.Equal(ChangeDetection.RenderedHash, flow.Change.Detect);
    }

    /// <summary>
    /// What runs at once, and across the fleet, is a flow's parallelism, and reliability keeps what makes a request hold: a
    /// document that still writes a moved setting under reliability is refused and told the key it is written as now.
    /// </summary>
    [Theory]
    [InlineData("concurrency: 3", "concurrency", "parallelism.concurrency")]
    [InlineData("fanOut: 3", "fanOut", "parallelism.fanOut")]
    [InlineData("fanOutMinRecords: 3", "fanOutMinRecords", "parallelism.fanOutMinRecords")]
    [InlineData("renderParallelism: 3", "renderParallelism", "parallelism.renderers")]
    [InlineData("batchRecords: 3", "batchRecords", "parallelism.batchRecords")]
    [InlineData("batchSize: 3", "batchSize", "parallelism.batchSize")]
    [InlineData("parallelInterfaces: 3", "parallelInterfaces", "parallelism.interfaces")]
    public void A_setting_that_moved_to_parallelism_is_refused_under_reliability_naming_its_key_now(string setting, string was, string now)
    {
        var yaml = Flow.ReplaceLineEndings("\n").Replace(
            "  retry: { attempts: 5, backoff: fixed }\n", "  retry: { attempts: 5, backoff: fixed }\n  " + setting + "\n", StringComparison.Ordinal);

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseFlow(yaml, "flow.yaml"));

        Assert.Equal(
            $"flow.yaml: reliability.{was} is now {now}: what runs at once, and across the fleet, is the flow's parallelism, and reliability keeps what makes a request hold. Move it.",
            refused.Message);
    }

    [Fact]
    public void The_parallelism_block_says_what_runs_at_once()
    {
        var yaml = Flow.ReplaceLineEndings("\n").Replace(
            "  concurrency: 2", "  concurrency: 2\n  renderers: 3\n  batchRecords: 40\n  batchSize: 20\n  fanOutMinRecords: 5000", StringComparison.Ordinal);

        var parallelism = new DeliveryDocumentLoader().ParseFlow(yaml, "flow.yaml").Parallelism;

        Assert.Equal(new FlowParallelism { Concurrency = 2, Renderers = 3, BatchRecords = 40, BatchSize = 20, FanOutMinRecords = 5000 }, parallelism);
        Assert.Equal(3, parallelism.EffectiveRenderers);
    }

    [Theory]
    [InlineData("concurrency: 0", "parallelism.concurrency must be at least 1.")]
    [InlineData("renderers: -1", "parallelism.renderers must be between 0 and 256.")]
    [InlineData("renderers: 257", "parallelism.renderers must be between 0 and 256.")]
    [InlineData("batchRecords: 0", "parallelism.batchRecords must be between 1 and 100000.")]
    [InlineData("batchRecords: 100001", "parallelism.batchRecords must be between 1 and 100000.")]
    [InlineData("fanOut: -1", "parallelism.fanOut must be between 0 and 64.")]
    [InlineData("fanOut: 65", "parallelism.fanOut must be between 0 and 64.")]
    [InlineData("fanOutMinRecords: 0", "parallelism.fanOutMinRecords must be at least 1.")]
    public void A_parallelism_setting_out_of_range_is_refused_naming_its_key(string setting, string expected)
    {
        var yaml = Flow.ReplaceLineEndings("\n").Replace("  concurrency: 2", "  " + setting, StringComparison.Ordinal);

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseFlow(yaml, "flow.yaml"));

        Assert.Equal("flow.yaml: " + expected, refused.Message);
    }

    /// <summary>
    /// A flow fans out unless it says otherwise, and needs no identity primary key to: without one its read is planned on the
    /// coordinating run and its sending still fans out; with one the read is cut on it and planned across the fleet too.
    /// </summary>
    [Fact]
    public void A_flow_fans_out_by_default_and_needs_no_primary_key_to()
    {
        var loader = new DeliveryDocumentLoader();
        var flow = Flow.ReplaceLineEndings("\n");
        var parsed = loader.ParseFlow(flow, "f");
        Assert.Null(parsed.Source.Record.PrimaryKey);
        Assert.Equal(FlowParallelism.DefaultFanOut, parsed.Parallelism.FanOut);
        Assert.Equal(1000, parsed.Parallelism.FanOutMinRecords);

        var fanned = flow.Replace("  concurrency: 2", "  concurrency: 2\n  fanOut: 4", StringComparison.Ordinal);
        Assert.Equal(4, loader.ParseFlow(fanned, "f").Parallelism.FanOut);
        Assert.Equal(0, loader.ParseFlow(flow.Replace("  concurrency: 2", "  concurrency: 2\n  fanOut: 0", StringComparison.Ordinal), "f").Parallelism.FanOut);

        var keyed = fanned.Replace("    key: [source_project, log_id]\n", "    key: [source_project, log_id]\n    primaryKey: RecId\n", StringComparison.Ordinal);
        var parsedKeyed = loader.ParseFlow(keyed, "f");
        Assert.Equal(("RecId", 4), (parsedKeyed.Source.Record.PrimaryKey, parsedKeyed.Parallelism.FanOut));

        var bracketed = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(keyed.Replace("primaryKey: RecId", "primaryKey: \"[RecId]\"", StringComparison.Ordinal), "f"));
        Assert.Contains("source.record.primaryKey", bracketed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_keys_are_a_parse_error_with_the_path()
    {
        var ex = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseFlow(Flow.Replace("concurrency: 2", "concurency: 2", StringComparison.Ordinal), "flow.yaml"));
        Assert.StartsWith("flow.yaml:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("concurency", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Floating_mappings_and_unknown_protocols_are_rejected()
    {
        var loader = new DeliveryDocumentLoader();
        var floating = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(Flow.Replace("WellLog@1.4.0", "WellLog", StringComparison.Ordinal), "f"));
        Assert.Contains("pinned", floating.Message, StringComparison.Ordinal);
        Assert.Equal(DeliveryProtocol.Manifest, loader.ParseFlow(Flow.Replace("protocol: ddms", "protocol: manifest", StringComparison.Ordinal), "f").Target.Protocol);
        Assert.Equal(DeliveryProtocol.File, loader.ParseFlow(Flow.Replace("protocol: ddms", "protocol: file", StringComparison.Ordinal), "f").Target.Protocol);
        var unknownProtocol = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(Flow.Replace("protocol: ddms", "protocol: ftp", StringComparison.Ordinal), "f"));
        Assert.Contains("target.protocol", unknownProtocol.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Undeclared_location_tokens_and_a_payload_the_protocol_does_not_stream_are_rejected()
    {
        var loader = new DeliveryDocumentLoader();
        Assert.Throws<FlowValidationException>(() => loader.ParseFlow(Flow.Replace("work: work/{logSource}", "work: work/{other}", StringComparison.Ordinal), "f"));
        Assert.Throws<FlowValidationException>(() => loader.ParseFlow(Flow.Replace("root: curves/{logSource}", "root: curves/{other}", StringComparison.Ordinal), "f"));
        Assert.Throws<FlowValidationException>(() => loader.ParseFlow(Flow.Replace("payload: curves", "payload: grids", StringComparison.Ordinal), "f"));

        // A pattern matches file names inside the record's own payload folder, so it carries no path of its own.
        var path = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(Flow.Replace("pattern: \"chunk_*.parquet\"", "pattern: \"{deliveryKey}/chunk_*.parquet\"", StringComparison.Ordinal), "f"));
        Assert.Contains("pattern", path.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_last_modified_column_orders_the_versions_of_a_record()
    {
        var loader = new DeliveryDocumentLoader();
        Assert.Equal("update_date", loader.ParseFlow(Flow, "f").Source.LastModified);
        Assert.Equal("update_date", loader.LoadFlow(Samples.Flow).Source.LastModified);

        // A flow may decide changes by what it renders alone, and then no business version orders its rows.
        var unordered = Flow.ReplaceLineEndings("\n").Replace("  lastModified: update_date\n", string.Empty, StringComparison.Ordinal);
        Assert.Null(loader.ParseFlow(unordered, "f").Source.LastModified);

        Assert.Equal(ChangeDetection.LastModified, loader.ParseFlow(Flow + "\nchange: { payloadDetect: lastModified }", "f").Change.PayloadDetect);
        var detect = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(Flow + "\nchange: { detect: lastModified }", "f"));
        Assert.Contains("source.lastModified", detect.Message, StringComparison.Ordinal);

        // A metadata-only flow streams no files, so there is no payload watermark to take.
        var noPayload = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(
            (Flow.ReplaceLineEndings("\n") + "\nchange: { payloadDetect: lastModified }")
                .Replace("protocol: ddms", "protocol: storage", StringComparison.Ordinal)
                .Replace("  protocolOptions: { payload: curves, recordMethod: POST }\n", string.Empty, StringComparison.Ordinal),
            "f"));
        Assert.Contains("no payload files", noPayload.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("detect: renderedHash", null)]
    [InlineData("detect: always", null)]
    [InlineData("payloadDetect: contentHash", null)]
    [InlineData("payloadDetect: always", null)]
    [InlineData("detect: contentHash", "change.detect is contentHash, which is how a payload is compared")]
    [InlineData("payloadDetect: renderedHash", "change.payloadDetect is renderedHash, which is how a document is compared")]
    public void Each_change_key_takes_its_own_name_for_comparing_hashes(string change, string? refusal)
    {
        var loader = new DeliveryDocumentLoader();
        var yaml = Flow + "\nchange: { " + change + " }";
        if (refusal is null)
        {
            loader.ParseFlow(yaml, "f");
            return;
        }

        var refused = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(yaml, "f"));
        Assert.Contains(refusal, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mapping_parse_reads_the_number_modifier_and_its_separators()
    {
        var loader = new DeliveryDocumentLoader();
        Modifier Only(string modifiers) => Assert.Single(loader
            .ParseMapping(TestSchema.MappingDocument($"Weight: {{ $from: a, $modifiers: {modifiers} }}"), "m.yaml")
            .Entries.Single(e => e.Target.Text == "osdu.data.Weight").Modifiers);

        var plain = Only("[number]");
        Assert.Equal(ModifierKind.Number, plain.Kind);
        Assert.Equal(".", plain.DecimalSeparator);
        Assert.Null(plain.GroupSeparator);
        Assert.Equal("number", plain.ToString());

        var european = Only("[{ number: { decimal: \",\", group: \" \" } }]");
        Assert.Equal(",", european.DecimalSeparator);
        Assert.Equal(" ", european.GroupSeparator);
        Assert.Equal("number(decimal ',', group ' ')", european.ToString());
    }

    [Fact]
    public void Mapping_parse_reads_a_replace_with_no_value_and_otherwise_beside_its_table()
    {
        var loader = new DeliveryDocumentLoader();
        Modifier Only(string modifiers) => Assert.Single(loader
            .ParseMapping(TestSchema.MappingDocument($"Symbol:\n  $from: a\n  $modifiers:\n{modifiers}"), "m.yaml")
            .Entries.Single(e => e.Target.Text == "osdu.data.Symbol").Modifiers);

        var plain = Only("      - replace: { M: m, NONE: ~, 1.5: one and a half, true: yes }");
        Assert.Equal(ReplaceFallback.Keep, plain.Otherwise);
        Assert.Equal("m", plain.Replacements["M"]);
        Assert.Null(plain.Replacements["NONE"]);
        Assert.Equal("one and a half", plain.Replacements["1.5"]);
        Assert.Equal("yes", plain.Replacements["true"]);

        Assert.Equal(ReplaceFallback.Empty, Only("      - replace: { M: m }\n        otherwise: ~").Otherwise);
        Assert.Equal(ReplaceFallback.Empty, Only("      - replace: { M: m }\n        otherwise: \"  \"").Otherwise);
        var text = Only("      - otherwise: Unevaluated\n        replace: { M: m }").Otherwise;
        Assert.Equal(ReplaceFallbackKind.Text, text.Kind);
        Assert.Equal("Unevaluated", text.Text);
        Assert.Equal("~", Only("      - replace: { M: m }\n        otherwise: \"~\"").Otherwise.Text);
        Assert.Equal("replace(M: m, NONE: ~; otherwise ~)", Only("      - replace: { M: m, NONE: ~ }\n        otherwise: ~").ToString());

        string Refused(string modifiers) => Assert.Throws<FlowValidationException>(() => Only(modifiers)).Message;
        Assert.Contains("replace takes 'otherwise', 'match' and 'field' beside it, not 'fields'", Refused("      - replace: { M: m }\n        fields: Code"), StringComparison.Ordinal);
        Assert.Contains("'match' and 'field' choose the fields of a table read from the cache", Refused("      - replace: { M: m }\n        match: Code"), StringComparison.Ordinal);
        Assert.Contains("otherwise is one text", Refused("      - replace: { M: m }\n        otherwise: [a, b]"), StringComparison.Ordinal);
        Assert.Contains("which are the same value once surrounding spaces are removed", Refused("      - replace: { M: m, \" M \": metre }"), StringComparison.Ordinal);
        Assert.Contains("lists an empty incoming value", Refused("      - replace: { \"  \": x }"), StringComparison.Ordinal);
        Assert.Contains("to something that is not text", Refused("      - replace: { M: [m] }"), StringComparison.Ordinal);
        Assert.Contains("replace lists incoming values", Refused("      - replace: {}\n        otherwise: ~"), StringComparison.Ordinal);
        Assert.Contains("'replace' needs a table", Refused("      - replace"), StringComparison.Ordinal);
        Assert.Contains("Only replace takes a setting beside it", Refused("      - split: { separator: \",\", part: 1 }\n        otherwise: ~"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_yaml_anchor_repeats_a_node_exactly_as_writing_it_out_again_would()
    {
        var loader = new DeliveryDocumentLoader();
        var anchored = loader.ParseMapping(TestSchema.MappingDocument("""
            Unit:
              $from: unit
              $modifiers: &unit
                - trim
                - replace: { M: m, FT: ft }
                - ref
            Symbol:
              $from: unit
              $modifiers: *unit
              $when: &regular flag = "REGULAR"
            Description:
              $expr: upper(name)
              $when: *regular
            """), "anchored.yaml");
        var expanded = loader.ParseMapping(TestSchema.MappingDocument("""
            Unit:
              $from: unit
              $modifiers:
                - trim
                - replace: { M: m, FT: ft }
                - ref
            Symbol:
              $from: unit
              $modifiers:
                - trim
                - replace: { M: m, FT: ft }
                - ref
              $when: flag = "REGULAR"
            Description:
              $expr: upper(name)
              $when: flag = "REGULAR"
            """), "expanded.yaml");

        var json = new JsonSerializerOptions { WriteIndented = false };
        Assert.Equal(
            JsonSerializer.Serialize(MappingBuilder.FromDefinition(expanded), json),
            JsonSerializer.Serialize(MappingBuilder.FromDefinition(anchored), json));
    }

    [Fact]
    public void Mapping_parse_reads_sources_and_refuses_what_is_malformed()
    {
        var loader = new DeliveryDocumentLoader();
        var mapping = loader.ParseMapping(TestSchema.MappingYaml, "m.yaml");
        Assert.Equal(TestSchema.Template, mapping.Template);
        var unit = mapping.Entries.Single(e => e.Target.Text == "osdu.data.Unit");
        Assert.Equal(MappingSourceKind.Cache, unit.Source!.Kind);
        Assert.True(unit.Source.ReadsRecordId);
        Assert.Equal("Code", Assert.Single(unit.FindBy).Field);
        Assert.Equal(new DatasetColumn(null, "unit"), unit.FindBy[0].Column);
        Assert.True(mapping.Entries.Single(e => e.Target.Text == "osdu.data.Curves").IsRepeater);
        Assert.Equal(new DatasetColumn("curves", "curve_id"), mapping.Entries.Single(e => e.Target.Text == "osdu.data.Curves[].CurveID").Source!.Column);

        string Refused(string data) => Assert.Throws<FlowValidationException>(() => loader.ParseMapping(TestSchema.MappingDocument(data), "m.yaml")).Message;
        Assert.Contains("a $cache node needs $findBy", Refused("Unit: { $cache: UnitOfMeasure.id }"), StringComparison.Ordinal);
        Assert.Contains("reads its value with $from and $value", Refused("Symbol: { $from: a, $value: b }"), StringComparison.Ordinal);
        Assert.Contains("record.data.Symbol reads no value", Refused("Symbol: { $required: false }"), StringComparison.Ordinal);
        Assert.Contains("'column a' is not a column", Refused("Symbol: { $from: column a }"), StringComparison.Ordinal);
        Assert.Contains("a column of the dataset's own row is $dataset.a", Refused("Symbol: { $from: dataset.a }"), StringComparison.Ordinal);
        Assert.Contains("'Curves[].CurveID' is not a property name", Refused("\"Curves[].CurveID\": { $from: a }"), StringComparison.Ordinal);
        Assert.Contains("duplicate key", Refused("Name: { $from: other }"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lays out $item, the items of an array whose rows a $forEach node repeats", Refused("Curves: { $item: { CurveID: { $from: curve_id } } }"), StringComparison.Ordinal);
        Assert.Contains("'curves.curve_id' is not a column", Refused("Symbol: { $from: curves.curve_id }"), StringComparison.Ordinal);
        Assert.Contains("under the $forEach over curves a bare name reads its row, so write curve_id", Refused("Curves: { $forEach: curves, $item: { CurveID: { $from: curves.curve_id } } }"), StringComparison.Ordinal);
        Assert.Contains("is not a modifier", Refused("Symbol: { $from: a, $modifiers: [shout] }"), StringComparison.Ordinal);
        Assert.Contains("the date format 'MM.yyyy' has no day of the month", Refused("When: { $from: a, $modifiers: [{ date: MM.yyyy }] }"), StringComparison.Ordinal);
        Assert.Contains("reads a two-digit year", Refused("When: { $from: a, $modifiers: [{ date: dd.MM.yy }] }"), StringComparison.Ordinal);
        Assert.Contains("has no year", Refused("When: { $from: a, $modifiers: [{ date: dd.MM }] }"), StringComparison.Ordinal);
        Assert.Contains("is one letter", Refused("When: { $from: a, $modifiers: [{ date: d }] }"), StringComparison.Ordinal);
        Assert.Contains("never closed", Refused("When: { $from: a, $modifiers: [{ date: \"dd.MM.yyyy 'at\" }] }"), StringComparison.Ordinal);
        Assert.Contains("date takes the input format as text", Refused("When: { $from: a, $modifiers: [{ date: [dd.MM.yyyy] }] }"), StringComparison.Ordinal);
        Assert.Contains("number takes '.' or ',' as its decimal separator, not ';'", Refused("Weight: { $from: a, $modifiers: [{ number: { decimal: \";\" } }] }"), StringComparison.Ordinal);
        Assert.Contains("cannot use ',' both between digit groups and before the decimals", Refused("Weight: { $from: a, $modifiers: [{ number: { decimal: \",\", group: \",\" } }] }"), StringComparison.Ordinal);
        Assert.Contains("cannot use '.' both between digit groups", Refused("Weight: { $from: a, $modifiers: [{ number: { group: \".\" } }] }"), StringComparison.Ordinal);
        Assert.Contains("as its group separator, not '-'", Refused("Weight: { $from: a, $modifiers: [{ number: { group: \"-\" } }] }"), StringComparison.Ordinal);
        Assert.Contains("number takes 'decimal' and 'group', not 'thousands'", Refused("Weight: { $from: a, $modifiers: [{ number: { thousands: \",\" } }] }"), StringComparison.Ordinal);
        Assert.Contains("number takes its separators", Refused("Weight: { $from: a, $modifiers: [{ number: \",\" }] }"), StringComparison.Ordinal);
        Assert.Contains("is NaN or Infinity, which a JSON record cannot carry", Refused("Weight: .inf"), StringComparison.Ordinal);
        Assert.Contains("is NaN or Infinity, which a JSON record cannot carry", Refused("Aliases: [a, .nan]"), StringComparison.Ordinal);
        Assert.Contains("is NaN or Infinity, which a JSON record cannot carry", Refused("Nested: { $value: { Inner: -.inf } }"), StringComparison.Ordinal);
        Assert.Contains("split needs the part", Refused("Symbol: { $from: a, $modifiers: [{ split: { separator: x } }] }"), StringComparison.Ordinal);
        Assert.Contains("$when 'a equals b': 'equals' at character 3 is not expected after a complete expression", Refused("Symbol: { $from: a, $when: a equals b }"), StringComparison.Ordinal);
        Assert.Contains("$when 'a is b' is a condition as the mapping language no longer writes it; a condition is an expression now: $when: a = \"b\"", Refused("Symbol: { $from: a, $when: a is b }"), StringComparison.Ordinal);
        Assert.Contains("$when 'upper(a)' gives a value, and $when is a condition", Refused("Symbol: { $from: a, $when: upper(a) }"), StringComparison.Ordinal);
        Assert.Contains("$when '1 = 1' reads no column", Refused("Symbol: { $from: a, $when: 1 = 1 }"), StringComparison.Ordinal);
        Assert.Contains("$expr 'upper(\"x\")' reads no column, so every record gets the same value", Refused("Symbol: { $expr: upper(\"x\") }"), StringComparison.Ordinal);
        Assert.Contains("reads $param.missing in 'a & $param.missing', but the mapping declares no parameter 'missing'", Refused("Symbol: { $expr: a & $param.missing }"), StringComparison.Ordinal);
        Assert.Contains("this node computes its value with $expr", Refused("Symbol: { $expr: trim(a), $findBy: Code = a }"), StringComparison.Ordinal);
        Assert.Contains("a $forEach node takes $forEach, '$item', '$where', '$when'", Refused("Curves: { $forEach: curves, $findBy: x, $item: { CurveID: { $from: curve_id } } }"), StringComparison.Ordinal);
        Assert.Contains("$where 'curve_id' gives a value, and $where is a condition", Refused("Curves: { $forEach: curves, $where: curve_id, $item: { CurveID: { $from: curve_id } } }"), StringComparison.Ordinal);
        Assert.Contains("names 'cache.Wellbore.Code'", Refused("Unit: { $cache: UnitOfMeasure.id, $findBy: cache.Wellbore.Code = a }"), StringComparison.Ordinal);
        Assert.Contains("uses {$param.missing}", Refused("Symbol: \"{$param.missing}\""), StringComparison.Ordinal);
        Assert.Contains("a literal $value takes only $when and $description beside it", Refused("Symbol: { $value: b, $required: false }"), StringComparison.Ordinal);
        Assert.Contains(
            "a repeated array inside a repeated item is not supported",
            Refused("Curves: { $forEach: curves, $item: { Points: { $forEach: points, $item: { X: { $from: a } } } } }"),
            StringComparison.Ordinal);

        Assert.Contains("template.version", Assert.Throws<FlowValidationException>(() => loader.ParseMapping(TestSchema.MappingYaml.Replace(TestSchema.Build().Version, "latest", StringComparison.Ordinal), "m")).Message, StringComparison.Ordinal);
        Assert.Throws<FlowValidationException>(() => loader.ParseMapping(TestSchema.MappingYaml.Replace("dataPartition: { required: true }", "other: { required: true }", StringComparison.Ordinal), "m"));
        var wrongKind = Assert.Throws<FlowValidationException>(() => loader.ParseMapping(TestSchema.MappingYaml.Replace("documentType: mapping", "flowType: delivery", StringComparison.Ordinal), "m"));
        Assert.Contains("expected 'documentType: mapping'", wrongKind.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_record_tree_reads_the_mapping_language_by_its_marker_and_everything_else_as_the_record()
    {
        var loader = new DeliveryDocumentLoader();
        var mapping = loader.ParseMapping(
            TestSchema.MappingDocument(
                """
                # A property whose own name starts with '$' takes one more, and so does one inside a literal list's objects.
                Nested:
                  $$weird: { $from: a }
                  from: { $from: b }
                Aliases: [{ $$ref: x, value: 1 }]
                Symbol:
                  $value: { $ref: verbatim, $from: text }
                Unit:
                  $from: unit
                  $modifiers:
                    - id: "{$param.dataPartition}:reference-data--UnitOfMeasure:{value}-{$value}:"
                """,
                record: "tags: { $$from: literal }"),
            "m.yaml");

        // The record's own names, however much they look like the language, are properties; the language starts with '$'.
        Assert.Equal("osdu.data.Nested.$weird", mapping.Entries.Single(e => e.Location == "record.data.Nested.$$weird").Target.Text);
        Assert.Equal(new DatasetColumn(null, "b"), mapping.Entries.Single(e => e.Target.Text == "osdu.data.Nested.from").Source!.Column);
        Assert.Equal("""[{"$ref":"x","value":1}]""", mapping.Entries.Single(e => e.Target.Text == "osdu.data.Aliases").Static!.ToJsonString());
        Assert.Equal("""{"$ref":"verbatim","$from":"text"}""", mapping.Entries.Single(e => e.Target.Text == "osdu.data.Symbol").Static!.ToJsonString());
        Assert.Equal("literal", mapping.Entries.Single(e => e.Target.Text == "osdu.tags.$from").Static!.GetValue<string>());

        // In an id, a bare name is a column, even one called value; the entry's own value is {$value}.
        var id = mapping.Entries.Single(e => e.Target.Text == "osdu.data.Unit").Modifiers.Single().Id!;
        Assert.Equal([IdTokenKind.Parameter, IdTokenKind.Text, IdTokenKind.Dataset, IdTokenKind.Text, IdTokenKind.Value, IdTokenKind.Text], id.Tokens.Select(t => t.Kind));
        Assert.Equal(new DatasetColumn(null, "value"), id.Tokens[2].Column);

        string Refused(string data, string record = "") => Assert.Throws<FlowValidationException>(() => loader.ParseMapping(TestSchema.MappingDocument(data, record: record), "m.yaml")).Message;

        // A node's keys are all the language's; a word it does not have is named with the one it most likely meant.
        Assert.Contains("beside the property 'Other'; a node's keys all start with '$'", Refused("Symbol: { $from: a, Other: b }"), StringComparison.Ordinal);
        Assert.Contains("'$form' is not a word of the mapping language. Did you mean '$from'?", Refused("Symbol: { $form: a }"), StringComparison.Ordinal);
        Assert.Contains("'$colour' is not a word of the mapping language, which reads", Refused("Symbol: { $colour: a }"), StringComparison.Ordinal);
        Assert.Contains("A property whose name starts with '$' is written $$colour", Refused("Symbol: { $colour: a }"), StringComparison.Ordinal);
        Assert.Contains("record holds '$from'", Assert.Throws<FlowValidationException>(() => loader.ParseMapping(
            TestSchema.MappingDocument().ReplaceLineEndings("\n").Replace("record:\n", "record:\n  $from: x\n", StringComparison.Ordinal), "m.yaml")).Message, StringComparison.Ordinal);

        // What a list of values, an item and an id cannot hold, and the tokens of the vocabulary before the marker.
        Assert.Contains("record.data.Aliases[1] is a node of the mapping language, and the items of a list of objects are objects laid out as the record tree", Refused("Aliases: [{ Name: a }, { $from: a }]"), StringComparison.Ordinal);
        Assert.Contains("An array of values from rows is not supported yet", Refused("Curves: { $forEach: curves, $item: { $from: curve_id } }"), StringComparison.Ordinal);
        Assert.Contains(
            "{param.dataPartition} is not a column; the mapping's own tokens start with '$', so write {$param.dataPartition}",
            Refused("Unit: { $from: unit, $modifiers: [{ id: \"{param.dataPartition}:reference-data--UnitOfMeasure:{$value}:\" }] }"),
            StringComparison.Ordinal);
        Assert.Contains("a parameter is read as {$param.dataPartition}", Refused("Symbol: \"{param.dataPartition}-x\""), StringComparison.Ordinal);
        Assert.Contains("replace: $cache.UnitAlias", Refused("Symbol: { $from: a, $modifiers: [{ replace: cache.UnitAlias }] }"), StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_requires_file_name_and_document_to_agree()
    {
        var dir = Samples.NewTempDirectory();
        File.WriteAllText(Path.Combine(dir, "Thing@2.0.0.yaml"), TestSchema.MappingYaml);
        var catalog = new MappingCatalog(dir, new DeliveryDocumentLoader());
        var ex = Assert.Throws<FlowValidationException>(() => catalog.Load("Thing@2.0.0"));
        Assert.Contains("filed as", ex.Message, StringComparison.Ordinal);
        Assert.Throws<FlowValidationException>(() => catalog.Load("Thing@9.9.9"));
        Assert.Contains("Thing@2.0.0", catalog.List());
    }

    [Fact]
    public void Flow_parameters_resolve_defaults_and_reject_unknowns()
    {
        var flow = new DeliveryDocumentLoader().ParseFlow(Flow, "f");
        Assert.Throws<FlowValidationException>(() => FlowParameters.Resolve(flow, null));
        var values = FlowParameters.Resolve(flow, new Dictionary<string, string> { ["logSource"] = "COMPOSITE" });

        // A declared location resolves against the flow file, with its tokens filled in.
        var work = FlowParameters.WorkLocation(flow, values);
        Assert.True(Path.IsPathRooted(work));
        Assert.EndsWith(Path.Combine("work", "COMPOSITE"), work, StringComparison.Ordinal);

        // The scope predicate: the column the run filters on, carrying the value of the parameter bound to it.
        Assert.Equal("COMPOSITE", FlowParameters.ScopeValues(flow, values)["log_source"]);

        // A parameter value that would climb out of a declared location is refused, because those locations bound
        // what a run may read and write.
        var climbing = FlowParameters.Resolve(flow, new Dictionary<string, string> { ["logSource"] = "../etc" });
        Assert.Contains("must not contain", Assert.Throws<FlowValidationException>(() => FlowParameters.WorkLocation(flow, climbing)).Message, StringComparison.Ordinal);
        Assert.Throws<FlowValidationException>(() => FlowParameters.Resolve(flow, new Dictionary<string, string> { ["logSource"] = "x", ["nope"] = "y" }));
    }
}

/// <summary>
/// The identifiers the storage service polices on every record it accepts: the kind, and the partition the record
/// id is minted in. A value that fails either would fail every record of a run, so it is refused where it is read.
/// </summary>
public class OsduIdentifierValidationTests
{
    [Theory]
    [InlineData("test:wks:work-product-component--Thing:1")]
    [InlineData("test:wks:work-product-component--Thing:1.0")]
    [InlineData("test:wks:work product:1.0.0")]
    [InlineData("test:wks:work-product-component--Thing:1.0.x")]
    public void A_mapping_kind_the_storage_service_would_refuse_is_rejected_when_read(string kind)
    {
        var yaml = TestSchema.MappingYaml.Replace("kind: test:wks:work-product-component--Thing:1.0.0", "kind: " + kind, StringComparison.Ordinal);
        var ex = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(yaml, "m.yaml"));
        Assert.Contains("major.minor.patch", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("legaltags: [tag]", "legaltags: [tag, tag]", "record.legal.legaltags")]
    [InlineData("otherRelevantDataCountries: [US]", "otherRelevantDataCountries: [US, US]", "record.legal.otherRelevantDataCountries")]
    public void A_repeated_legal_entry_is_rejected_because_the_legal_lists_are_sets(string from, string to, string key)
    {
        var yaml = TestSchema.MappingYaml.Replace(from, to, StringComparison.Ordinal);
        var ex = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(yaml, "m.yaml"));
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
        Assert.Contains("more than once", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_well_formed_mapping_kind_is_accepted()
    {
        var mapping = new DeliveryDocumentLoader().ParseMapping(TestSchema.MappingYaml, "m.yaml");
        Assert.Equal("test:wks:work-product-component--Thing:1.0.0", mapping.Kind);
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("my-partition.eu_1")]
    public void A_data_partition_that_is_a_valid_id_segment_is_used(string partition)
    {
        var context = Context(partition);
        Assert.Equal(partition, context.DataPartition);
    }

    [Theory]
    [InlineData("open des")]
    [InlineData("dev/eu")]
    [InlineData("dev:eu")]
    public void A_data_partition_that_would_mint_invalid_record_ids_is_refused(string partition)
    {
        var ex = Assert.Throws<FlowValidationException>(() => Context(partition).DataPartition);
        Assert.Contains("not a valid OSDU id segment", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_retrieval_flow_without_the_partition_header_is_refused_when_read()
    {
        const string Retrieval = """
            flowType: retrieval
            name: wellbores-out
            source:
              endpoint: https://osdu.example.com
              headers: { }
              kind: "osdu:wks:master-data--Wellbore:1.0.0"
            target:
              location: ./out
            """;
        var ex = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseRetrieval(Retrieval, "r.yaml"));
        Assert.Contains("data-partition-id", ex.Message, StringComparison.Ordinal);

        var withHeader = Retrieval.Replace("headers: { }", "headers: { data-partition-id: dev }", StringComparison.Ordinal);
        Assert.Equal("wellbores-out", new DeliveryDocumentLoader().ParseRetrieval(withHeader, "r.yaml").Name);
    }

    private static SqlFlow.Delivery.Snapshots.RenderContext Context(string partition) => new()
    {
        MappingReference = "Thing@1.0.0",
        CacheVersion = "r1",
        SchemaSnapshotVersion = "s1",
        Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { [SqlFlow.Delivery.Snapshots.RenderContext.DataPartitionParameter] = partition },
    };
}
