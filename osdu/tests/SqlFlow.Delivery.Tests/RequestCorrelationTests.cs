using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Removals;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Setup;
using SqlFlow.Delivery.Storage;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The correlation id every OSDU request carries (osdu/docs/reference/concepts/request-correlation.md): a run's, made of the
/// code of the repository document above its flow (osdu/docs/reference/flow/repository.md), the flow's operation code and
/// the run's id; an operation of the product's own, with a new id each time; and one of its own for a request sent outside
/// both. What a document may declare of it, and where a flow finds its repository document, are checked as a run and
/// <c>sqlflow validate</c> read them.
/// </summary>
public sealed class RequestCorrelationTests : IDisposable
{
    private static readonly DeliveryDocumentLoader Loader = new();

    private static readonly Guid RunId = Guid.Parse("0192f3a1-7c4e-7b2a-9d10-5e8f2c6a4b31");

    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (var folder in _folders)
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("osdu-delivery-wl-dim-0192f3a1-7c4e-7b2a-9d10-5e8f2c6a4b31", "wl", "dim")]
    [InlineData("osdu-delivery-dim-0192f3a1-7c4e-7b2a-9d10-5e8f2c6a4b31", null, "dim")]
    [InlineData("osdu-delivery-explore-0192f3a1-7c4e-7b2a-9d10-5e8f2c6a4b31", null, "explore")]
    [InlineData("osdu-delivery-0192f3a1-7c4e-7b2a-9d10-5e8f2c6a4b31", null, null)]
    [InlineData("0192f3a1-7c4e-7b2a-9d10-5e8f2c6a4b31", null, null)]
    [InlineData("  0192F3A1-7C4E-7B2A-9D10-5E8F2C6A4B31  ", null, null)]
    [InlineData("409 from storage (correlation-id osdu-delivery-wl-dim-0192f3a1-7c4e-7b2a-9d10-5e8f2c6a4b31)", "wl", "dim")]
    public void A_value_copied_from_a_log_names_what_it_carries(string text, string? repository, string? operation)
        => Assert.Equal(new OsduCorrelationValue(repository, operation, RunId), OsduCorrelation.Find(text));

    [Fact]
    public void An_id_that_opens_like_a_code_is_read_as_an_id()
    {
        var id = Guid.Parse("abcdef12-beef-4b2a-9d10-5e8f2c6a4b31");

        Assert.Equal(new OsduCorrelationValue(null, null, id), OsduCorrelation.Find($"osdu-delivery-{id:D}"));
        Assert.Equal(new OsduCorrelationValue(null, "dim", id), OsduCorrelation.Find($"osdu-delivery-dim-{id:D}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("welldb-welllog-05-dimensions")]
    [InlineData("osdu-delivery-wl-dim-")]
    public void A_text_with_no_id_names_nothing(string? text) => Assert.Null(OsduCorrelation.Find(text));

    [Fact]
    public void A_runs_requests_carry_its_repository_its_operation_and_its_id_and_an_operation_inside_it_is_the_runs()
    {
        using (var run = OsduCorrelation.ForRun("wl", "dim", RunId))
        {
            Assert.Equal($"osdu-delivery-wl-dim-{RunId:D}", run.Id);
            Assert.Equal(run.Id, OsduCorrelation.Value);
            using (var read = OsduCorrelation.Begin("read"))
            {
                Assert.Equal(run.Id, read.Id);
            }

            // A reading kept for every later ask is no part of the run that started it.
            using (var detached = OsduCorrelation.Detach("schema"))
            {
                AssertBuiltIn("schema", detached.Id);
                Assert.Equal(detached.Id, OsduCorrelation.Current);
            }

            Assert.Equal(run.Id, OsduCorrelation.Current);
        }

        Assert.Null(OsduCorrelation.Current);

        // A flow in no repository that names a code leaves the segment out.
        using var bare = OsduCorrelation.ForRun(null, FlowTelemetry.Delivery, RunId);
        Assert.Equal($"osdu-delivery-dlv-{RunId:D}", bare.Id);
    }

    [Fact]
    public void An_operation_of_the_products_own_carries_a_new_id_each_time_and_a_request_outside_any_one_of_its_own()
    {
        string first;
        using (var explore = OsduCorrelation.Begin("explore"))
        {
            first = explore.Id;
            AssertBuiltIn("explore", first);
        }

        using (var again = OsduCorrelation.Begin("explore"))
        {
            AssertBuiltIn("explore", again.Id);
            Assert.NotEqual(first, again.Id);
        }

        var loose = OsduCorrelation.Value;
        Assert.StartsWith("osdu-delivery-", loose, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(loose["osdu-delivery-".Length..], "D", out _), loose);
        Assert.NotEqual(loose, OsduCorrelation.Value);
        Assert.Null(OsduCorrelation.Current);
    }

    [Theory]
    [InlineData("WL")]
    [InlineData("w-l")]
    [InlineData("1wl")]
    [InlineData("")]
    [InlineData("dim ")]
    [InlineData("abcdefghijklmnopq")]
    public void A_code_is_a_lower_case_letter_then_lower_case_letters_and_digits_with_no_hyphen(string code)
    {
        Assert.False(OsduCorrelation.IsCode(code));
        var refused = Assert.Throws<ArgumentException>(() => OsduCorrelation.ForRun(code, "dim", RunId));
        Assert.StartsWith($"'{code}' is not a code: {OsduCorrelation.CodeRule}.", refused.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => OsduCorrelation.ForRun("wl", code, RunId));
        Assert.Throws<ArgumentException>(() => OsduCorrelation.Begin(code));
        Assert.True(OsduCorrelation.IsCode("abcdefghijklmnop"));
        Assert.True(OsduCorrelation.IsCode("wl2"));
    }

    [Fact]
    public void The_setup_checks_sign_in_carries_the_value_the_module_composes()
    {
        Assert.Equal(OsduCorrelation.HeaderName, OsduSignIn.CorrelationHeader);
        AssertBuiltIn("signin", OsduSignIn.CorrelationValue());
        Assert.NotEqual(OsduSignIn.CorrelationValue(), OsduSignIn.CorrelationValue());
    }

    [Fact]
    public void A_flow_takes_the_nearest_repository_document_and_never_one_above_its_repositorys_root()
    {
        var root = Folder();
        Write(root, RepositoryDefinition.FileName, Document("outer"));
        var repo = Path.Combine(root, "estate");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Write(repo, RepositoryDefinition.FileName, Document("wl"));
        Write(repo, "welllog/" + RepositoryDefinition.AlternateFileName, Document("wlh"));

        Assert.Equal("wl", CodeFor(Path.Combine(repo, "wellbore", "flows", "wellbore.yaml")));
        Assert.Equal("wlh", CodeFor(Path.Combine(repo, "welllog", "flows", "log.yaml")));

        // A repository without a document of its own takes none, however near the next one above it is.
        var bare = Path.Combine(root, "bare");
        File.WriteAllText(Directory.CreateDirectory(bare).FullName + "/.git", "gitdir: ../.git/worktrees/bare");
        Assert.Null(RepositoryDocuments.Find(Path.Combine(bare, "flows", "x.yaml"), Loader));

        // The name is matched exactly, as a node on Linux matches it.
        var cased = Path.Combine(root, "cased");
        Directory.CreateDirectory(Path.Combine(cased, ".git"));
        Write(cased, "OSDU-Delivery.yaml", Document("wl"));
        Assert.Null(RepositoryDocuments.Find(Path.Combine(cased, "x.yaml"), Loader));

        // Both names in one folder is two documents, and a flow cannot tell which is meant.
        Write(repo, RepositoryDefinition.AlternateFileName, Document("wl"));
        var both = Assert.Throws<FlowValidationException>(() => RepositoryDocuments.Find(Path.Combine(repo, "x.yaml"), Loader));
        Assert.Equal($"{repo} holds both osdu-delivery.yaml and osdu-delivery.yml; a folder holds one repository document. Remove one.", both.Message);

        string CodeFor(string flowFile) => RepositoryDocuments.Find(flowFile, Loader)!.Repository.Code;
    }

    [Theory]
    [InlineData("documentType: repository\ndescription: The well logs.\n", "osdu-delivery.yaml: code is required: the repository's code, which every run of its flows carries in its correlation id.")]
    [InlineData("documentType: repository\ncode: WL\n", "osdu-delivery.yaml: code 'WL' is not a repository code: a lower-case letter, then lower-case letters and digits, at most 16 in all and no hyphen, so the correlation id splits on its hyphens.")]
    [InlineData("documentType: repository\ncode: wl\nname: wl\n", "osdu-delivery.yaml: invalid YAML at line 3, column 1 - Property 'name' not found on type 'SqlFlow.Delivery.Documents.RepositoryYaml'.")]
    [InlineData("documentType: dictionary\ncode: wl\n", "osdu-delivery.yaml: expected 'documentType: repository', found 'dictionary'.")]
    public void A_repository_document_names_its_code_and_nothing_else(string yaml, string message)
        => Assert.Equal(message, Assert.Throws<FlowValidationException>(() => Loader.ParseRepository(yaml, "osdu-delivery.yaml")).Message);

    [Fact]
    public void A_repository_document_is_read_under_its_own_type_and_only_under_its_name()
    {
        var parsed = Loader.ParseRepository("documentType: repository\ncode: wl\ndescription: '  The well logs. '\n", "estate/osdu-delivery.yaml");
        Assert.Equal(("wl", "The well logs."), (parsed.Code, parsed.Description));
        Assert.Equal("wl", new RepositoryDocumentKind(Loader).ParseCompanion(Document("wl"), "estate/osdu-delivery.yml"));

        var misnamed = Assert.Throws<FlowValidationException>(() => Loader.ParseRepository(Document("wl"), "estate/repository.yaml"));
        Assert.Equal(
            "estate/repository.yaml: a repository document is found by its name, walking up from each flow's folder; name the file osdu-delivery.yaml.",
            misnamed.Message);
    }

    public static TheoryData<string, string> Kinds => new()
    {
        { FlowDefinition.FlowTypeName, FlowTelemetry.Delivery },
        { CacheDefinition.FlowTypeName, FlowTelemetry.Cache },
        { RetrievalDefinition.FlowTypeName, FlowTelemetry.Retrieval },
        { AssertionFlowDefinition.FlowTypeName, FlowTelemetry.Assertion },
        { DimensionFlowDefinition.FlowTypeName, FlowTelemetry.Dimension },
        { InventoryFlowDefinition.FlowTypeName, FlowTelemetry.Inventory },
        { RemovalFlowDefinition.FlowTypeName, FlowTelemetry.Removal },
        { DownloadFlowDefinition.FlowTypeName, FlowTelemetry.Download },
    };

    public static TheoryData<string> KindNames => new(FlowTelemetry.KindOperations.Keys);

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_kind_takes_its_own_operation_code_unless_its_flow_names_one(string kind, string code)
    {
        Assert.Equal(code, TelemetryOf(kind, Flows[kind]));
        Assert.Equal(code, FlowTelemetry.KindOperation(kind));
        Assert.Equal("whdr", TelemetryOf(kind, Flows[kind] + "\ntelemetry:\n  operation: whdr\n"));
        Assert.Equal(code, TelemetryOf(kind, Flows[kind] + "\ntelemetry: {}\n"));

        var refused = Assert.Throws<FlowValidationException>(() => TelemetryOf(kind, Flows[kind] + "\ntelemetry: { operation: Header-Log }\n"));
        Assert.Equal($"f.yaml: telemetry.operation 'Header-Log' is not an operation code: {OsduCorrelation.CodeRule}. Leave it out to take the kind's own, '{code}'.", refused.Message);
        var unknown = Assert.Throws<FlowValidationException>(() => TelemetryOf(kind, Flows[kind] + "\ntelemetry: { code: whdr }\n"));
        Assert.Contains("Property 'code' not found on type 'SqlFlow.Delivery.Documents.TelemetryYaml'", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_interface_of_a_source_runs_under_the_documents_operation_code()
    {
        var source = Loader.ParseSource("""
            flowType: delivery
            name: estate
            telemetry: { operation: whdr }
            source:
              connection: ${env:OSDU_DATA_DB}
              lastModified: update_date
              work: work
            target:
              endpoint: http://localhost
            interfaces:
              logs:
                record: { object: OsduData.silver.WellLog, key: [log_id] }
                mapping: WellLog@1.4.0
              wells:
                record: { object: OsduData.silver.Well, key: [well_id] }
                mapping: Well@1.0.0
            """, "f.yaml");

        Assert.Equal("whdr", source.TelemetryOperation);
        Assert.All(source.Interfaces, i => Assert.Equal("whdr", i.TelemetryOperation));
    }

    [Theory]
    [InlineData(FlowDefinition.FlowTypeName, "target:\n  endpoint: http://localhost\n", "target.headers")]
    [InlineData(FlowDefinition.FlowTypeName, "target:\n  endpoint: http://localhost\n", "target.airflow.headers")]
    [InlineData(CacheDefinition.FlowTypeName, "source:\n  endpoint: http://localhost\n", "source.headers")]
    [InlineData(RetrievalDefinition.FlowTypeName, "source:\n  endpoint: http://localhost\n", "source.headers")]
    [InlineData(AssertionFlowDefinition.FlowTypeName, "source:\n  endpoint: http://localhost\n", "source.headers")]
    [InlineData(DimensionFlowDefinition.FlowTypeName, "source:\n  endpoint: http://localhost\n", "source.headers")]
    [InlineData(InventoryFlowDefinition.FlowTypeName, "source:\n  endpoint: http://localhost\n", "source.headers")]
    [InlineData(DownloadFlowDefinition.FlowTypeName, "source:\n  endpoint: http://localhost\n", "source.headers")]
    public void A_flow_never_declares_the_correlation_id_its_runs_set(string kind, string block, string key)
    {
        var header = key == "target.airflow.headers"
            ? "  airflow:\n    endpoint: http://localhost/airflow\n    headers: { Correlation-Id: mine }\n"
            : "  headers: { Correlation-Id: mine }\n";
        var yaml = Flows[kind].Replace("  headers: { data-partition-id: dev }\n", string.Empty, StringComparison.Ordinal);
        Assert.Contains(block, yaml, StringComparison.Ordinal);
        var refused = Assert.Throws<FlowValidationException>(() => TelemetryOf(kind, yaml.Replace(block, block + header, StringComparison.Ordinal)));
        Assert.Equal(
            $"f.yaml: {key} names 'correlation-id', which every request of a run carries as the run sets it: osdu-delivery, the code of the repository (osdu-delivery.yaml), the flow's operation code (telemetry.operation) and the run's id. Remove the header.",
            refused.Message);
    }

    [Theory]
    [MemberData(nameof(KindNames))]
    public void Validate_refuses_a_flow_under_a_repository_document_a_run_could_not_read(string kind)
    {
        var repo = Repository("WL");
        var flowFile = Write(repo, $"flows/{kind}.yaml", Flows[kind]);

        var problems = KindOf(kind).Parse(Flows[kind], flowFile).CheckOffline(flowFile);

        Assert.Contains($"../osdu-delivery.yaml: code 'WL' is not a repository code: {OsduCorrelation.CodeRule}.", problems);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Every_kinds_run_says_what_its_requests_carry_before_it_does_anything_else(string kind, string code)
    {
        var flowFile = Write(Repository("wl"), $"flows/{kind}.yaml", Flows[kind]);
        var lines = new List<string>();

        // The run is asked for a partition its flow does not serve, so it stops before it reaches OSDU or a database.
        var result = await RunAsync(kind, flowFile, lines);

        Assert.False(result.Success);
        Assert.Equal(RunId, result.RunId);
        Assert.Contains(lines, line => line.Contains($"OSDU requests of this run carry correlation-id osdu-delivery-wl-{code}-{RunId:D}", StringComparison.Ordinal));
        Assert.Null(OsduCorrelation.Current);
    }

    [Theory]
    [MemberData(nameof(KindNames))]
    public async Task A_run_under_a_repository_document_it_cannot_read_fails_saying_why(string kind)
    {
        var flowFile = Write(Repository("WL"), $"flows/{kind}.yaml", Flows[kind]);

        var result = await RunAsync(kind, flowFile, []);

        Assert.False(result.Success);
        Assert.Contains("code 'WL' is not a repository code", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_operation_of_the_product_carries_its_own_code_whatever_repository_its_flow_is_in()
    {
        var repo = Repository("wl");
        Write(repo, "flows/explore.yaml", Flows[FlowDefinition.FlowTypeName].Replace("name: wl-logs", "name: explore", StringComparison.Ordinal));
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, "{\"results\":[],\"totalCount\":0}");
        var operation = new ExploreOperation(Engine(), handler, allowLoopback: true);

        var answers = new List<JsonNode>();
        for (var i = 0; i < 2; i++)
        {
            answers.Add(JsonNode.Parse(await operation.ExecuteAsync(new ComputeTaskPayload
            {
                Operation = ExploreOperation.OperationName,
                SourceRef = "explore",
                Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["repoRoot"] = repo,
                    ["relativePath"] = "flows/explore.yaml",
                    [DeliveryOperation.PartitionArgument] = "dev",
                    [ExploreOperation.ActionArgument] = ExploreOperation.SearchAction,
                    [ExploreOperation.SearchArgument] = new ExplorerSearch { Text = "master-data--Wellbore:WB" }.ToJson(),
                },
            }, CancellationToken.None))!);
        }

        Assert.Equal(2, handler.Calls.Count);
        var sent = handler.Calls.Select(c => c.Headers[OsduCorrelation.HeaderName]).ToList();
        Assert.All(sent, value => AssertBuiltIn("explore", value));
        Assert.NotEqual(sent[0], sent[1]);
        Assert.Equal(sent, answers.Select(a => a["correlationId"]!.GetValue<string>()));
    }

    /// <summary>A minimal flow of each kind, in the dev partition where the kind names partitions.</summary>
    private static readonly IReadOnlyDictionary<string, string> Flows = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [FlowDefinition.FlowTypeName] = """
            flowType: delivery
            name: wl-logs
            partitions: [dev]
            source:
              connection: ${env:OSDU_DATA_DB}
              record: { object: OsduData.silver.WellLog, key: [log_id] }
              lastModified: update_date
              work: work
            render:
              mapping: WellLog@1.4.0
            target:
              endpoint: http://localhost
              protocol: storage
            """,
        [CacheDefinition.FlowTypeName] = """
            flowType: cache
            name: wl-reference
            partitions: [dev]
            source:
              endpoint: http://localhost
            types:
              - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
                fields: [data.Code]
            """,
        [RetrievalDefinition.FlowTypeName] = """
            flowType: retrieval
            name: wl-out
            source:
              endpoint: http://localhost
              headers: { data-partition-id: dev }
              kind: "osdu:wks:master-data--Wellbore:1.0.0"
            target:
              location: ./out
            """,
        [AssertionFlowDefinition.FlowTypeName] = """
            flowType: assertion
            name: wl-assertions
            partitions: [dev]
            source:
              endpoint: http://localhost
            tests:
              - name: one
                kind: "osdu:wks:master-data--Wellbore:1.3.0"
                assert:
                  - count: { atLeast: 1 }
            """,
        [DimensionFlowDefinition.FlowTypeName] = """
            flowType: dimension
            name: wl-dimensions
            partitions: [dev]
            source:
              endpoint: http://localhost
            dimensions:
              - name: LogSource
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.Source
            """,
        [InventoryFlowDefinition.FlowTypeName] = """
            flowType: inventory
            name: wl-inventory
            partitions: [dev]
            source:
              endpoint: http://localhost
            inventories:
              - name: WellLogs
                kind: "*:*:work-product-component--WellLog:*"
            """,
        [RemovalFlowDefinition.FlowTypeName] = """
            flowType: removal
            name: wl-removal
            partitions: [dev]
            removals:
              - flow: wl-logs
                keys:
                  type: csv
                  location: ../data/wellbore
                  options: { delimiter: ",", header: true }
            """,
        [DownloadFlowDefinition.FlowTypeName] = """
            flowType: download
            name: wl-download
            partitions: [dev]
            source:
              endpoint: http://localhost
            target:
              location: abfss://lake@welldbstorage.dfs.core.windows.net/osdu/records
            downloads:
              - name: WellLogs
                kind: "*:*:work-product-component--WellLog:*"
            """,
    };

    private static IFlowDocumentKind KindOf(string kind) => kind switch
    {
        FlowDefinition.FlowTypeName => new DeliveryFlowKind(Loader),
        CacheDefinition.FlowTypeName => new CacheFlowKind(Loader),
        RetrievalDefinition.FlowTypeName => new RetrievalFlowKind(Loader),
        AssertionFlowDefinition.FlowTypeName => new AssertionFlowKind(Loader),
        DimensionFlowDefinition.FlowTypeName => new DimensionFlowKind(Loader),
        InventoryFlowDefinition.FlowTypeName => new InventoryFlowKind(Loader),
        RemovalFlowDefinition.FlowTypeName => new RemovalFlowKind(Loader),
        DownloadFlowDefinition.FlowTypeName => new DownloadFlowKind(Loader),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a flow kind of the module"),
    };

    private static string TelemetryOf(string kind, string yaml) => kind switch
    {
        FlowDefinition.FlowTypeName => Loader.ParseSource(yaml, "f.yaml").TelemetryOperation,
        CacheDefinition.FlowTypeName => Loader.ParseCache(yaml, "f.yaml").TelemetryOperation,
        RetrievalDefinition.FlowTypeName => Loader.ParseRetrieval(yaml, "f.yaml").TelemetryOperation,
        AssertionFlowDefinition.FlowTypeName => Loader.ParseAssertion(yaml, "f.yaml").TelemetryOperation,
        DimensionFlowDefinition.FlowTypeName => Loader.ParseDimension(yaml, "f.yaml").TelemetryOperation,
        InventoryFlowDefinition.FlowTypeName => Loader.ParseInventory(yaml, "f.yaml").TelemetryOperation,
        RemovalFlowDefinition.FlowTypeName => Loader.ParseRemoval(yaml, "f.yaml").TelemetryOperation,
        DownloadFlowDefinition.FlowTypeName => Loader.ParseDownload(yaml, "f.yaml").TelemetryOperation,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a flow kind of the module"),
    };

    /// <summary>One run of the flow of <paramref name="kind"/> in <paramref name="flowFile"/>, as its executor runs it, asked for a partition it does not serve.</summary>
    private static async Task<DocumentExecutionResult> RunAsync(string kind, string flowFile, List<string> lines)
    {
        var secrets = new SecretResolver([new EnvSecretProvider()]);
        using var provider = new ServiceCollection()
            .AddSingleton(Engine())
            .AddSingleton(new RemovalKeySources([], new ConnectionResolver(new NullDataSourceStore(), secrets, []), new CompositeConnectionFactory([]), new SourceProviderRegistry(), secrets))
            .BuildServiceProvider();
        IFlowDocumentExecutor executor = kind switch
        {
            FlowDefinition.FlowTypeName => new DeliveryExecutor(provider),
            CacheDefinition.FlowTypeName => new CacheExecutor(provider),
            RetrievalDefinition.FlowTypeName => new RetrievalExecutor(provider),
            AssertionFlowDefinition.FlowTypeName => new AssertionExecutor(provider),
            DimensionFlowDefinition.FlowTypeName => new DimensionExecutor(provider),
            InventoryFlowDefinition.FlowTypeName => new InventoryExecutor(provider),
            RemovalFlowDefinition.FlowTypeName => new RemovalExecutor(provider),
            DownloadFlowDefinition.FlowTypeName => new DownloadExecutor(provider),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a flow kind of the module"),
        };

        var document = KindOf(kind).Parse(File.ReadAllText(flowFile), flowFile);
        var options = new DocumentExecutionOptions
        {
            RunId = RunId,
            Echo = line =>
            {
                lock (lines)
                {
                    lines.Add(line);
                }
            },
            Parameters = new RunParameters { Values = new Dictionary<string, string>(StringComparer.Ordinal) { [PartitionNames.RunValue] = "nope" } },
        };
        return await executor.ExecuteAsync(document, flowFile, options, CancellationToken.None);
    }

    /// <summary>The engine as a host without the module's database composes it.</summary>
    private static EngineContext Engine()
    {
        var stores = Samples.Stores();
        return new EngineContext(
            new DeliveryDocumentLoader(),
            new MemoryIngestionTables(),
            new StoragePayloadFiles(stores),
            stores,
            new SecretResolver([new EnvSecretProvider()]),
            Ledger: null,
            TimeProvider.System,
            NullLoggerFactory.Instance,
            new DefaultProtocolFactory(),
            CompositeDeliveryListener.Empty);
    }

    /// <summary>A repository (a folder holding <c>.git</c>) whose repository document names <paramref name="code"/>.</summary>
    private string Repository(string code)
    {
        var repo = Folder();
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Write(repo, RepositoryDefinition.FileName, Document(code));
        return repo;
    }

    private static string Document(string code) => $"documentType: repository\ncode: {code}\n";

    private static string Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ReplaceLineEndings("\n"));
        return path;
    }

    private string Folder()
    {
        var folder = Samples.NewTempDirectory();
        _folders.Add(folder);
        return folder;
    }

    /// <summary>A value an operation of the product's own carries: <c>osdu-delivery-&lt;operation&gt;-&lt;id&gt;</c>.</summary>
    private static void AssertBuiltIn(string operation, string value)
    {
        var prefix = $"osdu-delivery-{operation}-";
        Assert.StartsWith(prefix, value, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(value[prefix.Length..], "D", out _), value);
    }
}
