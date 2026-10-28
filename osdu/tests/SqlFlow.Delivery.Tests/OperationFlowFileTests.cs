using System.Net;
using System.Text.Json.Nodes;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Storage;
using SqlFlow.Node;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Where an operation of a flow reads the flow's file (osdu/docs/reference/concepts/architecture.md, What runs where): in the
/// folder the catalog recorded for its repository while the host sees it, and otherwise in the repository's synced commit
/// checked out again from its remote, so a redeployed control plane, whose checkouts went with its old container, answers a
/// preview or a value lookup as the host that synced it did; and why none is read when none can be. The remote is a real
/// local git repository and OSDU a fake, so nothing is reached over the network.
/// </summary>
public sealed class OperationFlowFileTests : IDisposable
{
    private const string FlowName = "explorer-route";
    private const string RelativePath = "flows/explorer-route.yaml";

    private const string Flow = """
        flowType: delivery
        name: explorer-route
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
          protocol: ddms
        """;

    private static readonly Guid Repo = Guid.NewGuid();
    private readonly List<string> _folders = [];
    private readonly SecretResolver _secrets = new([new EnvSecretProvider()]);

    public void Dispose()
    {
        foreach (var folder in _folders)
        {
            RepositoryFoldersTests.Delete(folder);
        }
    }

    [Fact]
    public async Task A_flow_file_whose_synced_folder_is_gone_is_read_from_the_synced_commit_checked_out_again()
    {
        var remote = Folder();
        string synced;
        using (var git = new Repository(Repository.Init(remote)))
        {
            synced = RepositoryFoldersTests.Commit(git, remote, RelativePath, Flow);
        }

        // The sync recorded a folder of a container a redeploy replaced, so this host holds none of it.
        var gone = Path.Combine(Folder(), "node-cache", "estate", synced);
        var checkouts = Folder();
        var folders = Folders(new RepositorySync("estate", gone, null, remote, synced, null, null), checkouts);
        var handler = Search();

        var answered = JsonNode.Parse(await new ExploreOperation(Engine(folders), handler, allowLoopback: true)
            .ExecuteAsync(Payload(gone), CancellationToken.None))!;

        Assert.Equal(("dev", FlowName), (answered["partition"]!.GetValue<string>(), answered["connection"]!.GetValue<string>()));
        Assert.Single(handler.Calls);

        // The commit is checked out in the cache a run's commit is checked out into, where the next operation finds it.
        var checkedOut = Assert.Single(Directory.EnumerateFiles(checkouts, "explorer-route.yaml", SearchOption.AllDirectories));
        Assert.Equal(Flow.ReplaceLineEndings(), (await File.ReadAllTextAsync(checkedOut)).ReplaceLineEndings());
    }

    [Fact]
    public async Task A_flow_file_is_read_in_the_folder_the_catalog_recorded_while_the_host_sees_it()
    {
        var recorded = Folder();
        var file = Path.Combine(recorded, RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, Flow);

        // The folder is on this host, so the catalog is not asked where else the repository may be had.
        var asked = 0;
        var folders = new CatalogRepositoryFolders(
            (_, _) =>
            {
                asked++;
                return Task.FromResult<RepositorySync?>(null);
            },
            _secrets,
            new GitMaterializer(Folder()));
        var handler = Search();

        var answered = JsonNode.Parse(await new ExploreOperation(Engine(folders), handler, allowLoopback: true)
            .ExecuteAsync(Payload(recorded), CancellationToken.None))!;

        Assert.Equal(FlowName, answered["connection"]!.GetValue<string>());
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task A_flow_file_that_cannot_be_had_on_the_host_says_why_and_nothing_is_read()
    {
        var gone = Path.Combine(Folder(), "gone");
        var handler = Search();

        // The catalog records no commit to check out again: the reason the repository cannot be had here is said.
        var uncommitted = new ExploreOperation(Engine(Folders(new RepositorySync("estate", gone, null, null, null, null, null))), handler, allowLoopback: true);
        var unseen = await Assert.ThrowsAsync<SqlFlowException>(() => uncommitted.ExecuteAsync(Payload(gone), CancellationToken.None));
        Assert.Equal(
            $"The flow file for '{FlowName}' cannot be read: repository 'estate' was synced from {gone}, which this host does not see, and it records no commit to check out from a remote; sync it again on this host.",
            unseen.Message);

        // A task that names no repository leaves nothing to check out.
        var unnamed = await Assert.ThrowsAsync<SqlFlowException>(() => uncommitted.ExecuteAsync(Payload(gone, namesRepository: false), CancellationToken.None));
        Assert.Equal(
            $"The flow file for '{FlowName}' cannot be read: {gone}, where its repository was synced, is not on this host, and the task names no repository ('repoId') to check out again.",
            unnamed.Message);

        // A folder that does not hold the file is a catalog that drifted from the repository's files.
        var empty = Folder();
        var drifted = await Assert.ThrowsAsync<SqlFlowException>(() => uncommitted.ExecuteAsync(Payload(empty), CancellationToken.None));
        Assert.Equal(
            $"The flow file for '{FlowName}' was not found where its repository was synced: {Path.GetFullPath(Path.Combine(empty, "flows", "explorer-route.yaml"))}. Re-sync the repository.",
            drifted.Message);

        Assert.Empty(handler.Calls);
    }

    private string Folder()
    {
        var folder = Samples.NewTempDirectory();
        _folders.Add(folder);
        return folder;
    }

    /// <summary>The folders as the catalog records <see cref="Repo"/> as <paramref name="sync"/>, checking commits out into <paramref name="checkouts"/>.</summary>
    private CatalogRepositoryFolders Folders(RepositorySync sync, string? checkouts = null)
        => new((id, _) => Task.FromResult<RepositorySync?>(id == Repo ? sync : null), _secrets, new GitMaterializer(checkouts ?? Folder()));

    /// <summary>OSDU's search service, answering every query with no records.</summary>
    private static FakeHttpHandler Search()
        => new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, "{\"results\":[],\"totalCount\":0}");

    /// <summary>
    /// A search through the flow, as the control plane hands it over: the folder the catalog recorded, the flow's path in it
    /// and, unless <paramref name="namesRepository"/> is false, the repository the flow is synced from.
    /// </summary>
    private static ComputeTaskPayload Payload(string recorded, bool namesRepository = true)
    {
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["repoRoot"] = recorded,
            ["relativePath"] = RelativePath,
            [DeliveryOperation.PartitionArgument] = "dev",
            [ExploreOperation.ActionArgument] = ExploreOperation.SearchAction,
            [ExploreOperation.SearchArgument] = new ExplorerSearch { Text = "master-data--Wellbore:WB" }.ToJson(),
        };
        if (namesRepository)
        {
            arguments[DeliveryOperation.RepositoryArgument] = Repo.ToString("D");
        }

        return new ComputeTaskPayload { Operation = ExploreOperation.OperationName, SourceRef = FlowName, Arguments = arguments };
    }

    /// <summary>The engine as a host without the module's database composes it, finding repositories through <paramref name="folders"/>.</summary>
    private static EngineContext Engine(IRepositoryFolders folders)
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
            CompositeDeliveryListener.Empty)
        {
            RepositoryFolders = folders,
        };
    }
}
