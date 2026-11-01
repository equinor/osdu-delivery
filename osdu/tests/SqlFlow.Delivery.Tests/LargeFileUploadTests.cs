using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Workflows;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using Xunit;
using static SqlFlow.Delivery.Engine.Protocols.ObjectBody;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The path a large file takes to a signed Azure location (osdu/docs/reference/flow/large-files.md), driven on files of a few
/// MiB through <c>uploadBlockMiB: 1</c>: a file above one block goes in blocks and a commit, and the one thing a size changes
/// is how many blocks there are. The store is the fake's block store, which answers as real Azure Storage did under a
/// <c>sp=cw</c> SAS; <see cref="LargeFileLiveTests"/> drives the same code against real storage accounts.
/// </summary>
public sealed class LargeFileUploadTests
{
    private const int MiB = 1024 * 1024;
    private const string DatasetKind = "osdu:wks:dataset--File.Generic:1.0.0";
    private const string DatasetId = "dev:dataset--File.Generic:log-7";
    private const string SourceUrl = "https://landing.blob.core.windows.net/logs/log.las";
    private const string SourceSignature = "?sv=2021-06-08&sr=b&sp=r&sig=source-secret";

    private static readonly SecretResolver Secrets = new([new EnvSecretProvider()]);

    /// <summary>Three and a half blocks at one MiB each, no two blocks alike.</summary>
    private static readonly byte[] Content = RandomNumberGenerator.GetBytes((3 * MiB) + (MiB / 2));

    private static readonly string ContentMd5 = Convert.ToBase64String(Md5Of(Content));

    private sealed class Rig : IDisposable
    {
        public Rig(FakeOsduPlatform? platform = null, ProtocolOptions? options = null, long ceiling = 0)
        {
            Platform = platform ?? new FakeOsduPlatform();
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                Secrets, TimeProvider.System, Platform, allowLoopback: true);
            Client = new OsduHttpClient(
                Runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
            Options = options ?? new ProtocolOptions { PayloadContentType = "text/plain", UploadBlockMiB = 1 };
            Ceiling = ceiling;
        }

        public FakeOsduPlatform Platform { get; }

        public HttpRuntime Runtime { get; }

        public OsduHttpClient Client { get; }

        public ProtocolOptions Options { get; }

        public long Ceiling { get; }

        public List<FakeHttpHandler.Request> PutBlocks => [.. Platform.Calls.Where(c => c.Uri.Query.Contains("comp=block&", StringComparison.Ordinal))];

        public List<FakeHttpHandler.Request> Commits => [.. Platform.Calls.Where(c => c.Uri.Query.Contains("comp=blocklist", StringComparison.Ordinal))];

        public List<FakeHttpHandler.Request> WholePuts
            => [.. Platform.Calls.Where(c => FakeOsduPlatform.ToSignedLocation(c) && c.Method == HttpMethod.Put && !c.Uri.Query.Contains("comp=", StringComparison.Ordinal))];

        public int Registrations => Platform.Calls.Count(c => c.Uri.AbsolutePath.EndsWith("/registerDataset", StringComparison.Ordinal));

        /// <summary>Delivers a dataset record; a failure the route throws reads as one it returns, as the worker takes them alike.</summary>
        public async Task<DeliveryOutcome> DeliverDatasetAsync(IPayloadSource files)
        {
            var protocol = new OsduDatasetProtocol(Client, Options, Samples.Logger<OsduDatasetProtocol>(), Ceiling);
            try
            {
                return await protocol.DeliverAsync(Work(FakeOsduPlatform.Record(DatasetId, DatasetKind), files));
            }
            catch (SqlFlow.Core.SqlFlowException ex)
            {
                return new DeliveryOutcome { MetadataDelivered = false, PayloadDelivered = false, Failure = ex };
            }
        }

        public void Dispose() => Runtime.Dispose();
    }

    private static DeliveryWork Work(JsonObject document, IPayloadSource? files, IReadOnlyList<WorkStepPart>? parts = null) => new()
    {
        Key = DeliveryKey.Derive("large-files", [document["id"]!.GetValue<string>()]),
        TargetId = document["id"]!.GetValue<string>(),
        Document = document,
        DeliverMetadata = true,
        DeliverPayload = files is not null,
        Payload = files,
        StepParts = parts ?? [],
        StepPartsForced = parts is not null,
        TargetState = new Dictionary<string, string>(StringComparer.Ordinal),
    };

    private static IReadOnlyDictionary<string, string> StorageStep(DeliveryOutcome outcome)
        => outcome.Steps.Single(s => s.Name.StartsWith("storage-", StringComparison.Ordinal)).Returned;

    private static PayloadCopySource CopySource(byte[]? md5 = null, string etag = "v1", long? length = null)
        => new(new Uri(SourceUrl + SourceSignature), length ?? Content.LongLength, etag, md5);

    private static LargeFile Copyable(FakeOsduPlatform platform, byte[]? md5 = null)
    {
        platform.CopySources[SourceUrl] = Content;
        return new LargeFile(Content, _ => CopySource(md5));
    }

    /// <summary>A file read forward only, as a file in Azure Storage is, counting how it is read.</summary>
    private sealed class LargeFile(byte[] bytes, Func<int, PayloadCopySource?>? copy = null) : IPayloadSource
    {
        private int _asked;

        public int Opens { get; private set; }

        public long BytesRead => _streams.Sum(s => s.BytesRead);

        private readonly List<ForwardOnly> _streams = [];

        public Task<IReadOnlyList<PayloadFile>> ListChunksAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PayloadFile>>([new PayloadFile(0, "mem://logs/log.las", bytes.LongLength)]);

        public Task<Stream> OpenAsync(PayloadFile file, CancellationToken ct = default)
        {
            Opens++;
            var stream = new ForwardOnly(bytes);
            _streams.Add(stream);
            return Task.FromResult<Stream>(stream);
        }

        public Task<PayloadCopySource?> CopySourceAsync(PayloadFile file, DateTimeOffset? until, CancellationToken ct = default)
            => Task.FromResult(copy?.Invoke(++_asked));
    }

    private sealed class ForwardOnly(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public long BytesRead { get; private set; }

        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }
    }

    [Fact]
    public async Task A_file_above_one_block_goes_in_blocks_and_a_commit_and_lands_whole()
    {
        using var rig = new Rig();
        var file = new LargeFile(Content);
        var outcome = await rig.DeliverDatasetAsync(file);

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(Content, rig.Platform.Staged["/staging/blob/f-1"]);
        Assert.Equal(4, rig.PutBlocks.Count);
        Assert.Single(rig.Commits);
        Assert.Empty(rig.WholePuts);

        // The blob carries what one PUT would have given it, its MD5 included, though no one request saw the whole file.
        Assert.Equal(("text/plain", ContentMd5), rig.Platform.Committed["/staging/blob/f-1"]);
        var step = StorageStep(outcome);
        Assert.Equal("blocks", step["transfer"]);
        Assert.Equal("4", step["blocks"]);
        Assert.Equal(Convert.ToHexStringLower(Md5Of(Content)), step["md5"]);
        Assert.Equal(Content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), rig.Platform.Records[DatasetId]["data"]!["DatasetProperties"]!["FileSourceInfo"]!["FileSize"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_size_of_a_file_changes_only_how_many_blocks_it_goes_in()
    {
        var sizes = new[] { MiB + 1, 2 * MiB, (5 * MiB) + 7 };
        foreach (var size in sizes)
        {
            var bytes = RandomNumberGenerator.GetBytes(size);
            using var rig = new Rig();
            var outcome = await rig.DeliverDatasetAsync(new LargeFile(bytes));

            Assert.True(outcome.Succeeded, outcome.Failure?.Message);
            Assert.Equal(bytes, rig.Platform.Staged["/staging/blob/f-1"]);
            Assert.Equal((size + MiB - 1) / MiB, rig.PutBlocks.Count);
        }
    }

    [Fact]
    public async Task A_file_of_one_block_or_less_goes_in_one_request_as_it_always_did()
    {
        var bytes = RandomNumberGenerator.GetBytes(MiB);
        using var rig = new Rig();
        var outcome = await rig.DeliverDatasetAsync(new LargeFile(bytes));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Single(rig.WholePuts);
        Assert.Empty(rig.PutBlocks);
        Assert.Equal("BlockBlob", rig.WholePuts[0].Headers["x-ms-blob-type"]);
        Assert.Equal("single", StorageStep(outcome)["transfer"]);
    }

    [Fact]
    public async Task One_put_of_more_than_azure_holds_is_refused_and_blocks_are_what_get_it_there()
    {
        // What every file above 5000 MiB met before: the store refuses the one PUT, and there is no second try that helps.
        var platform = new FakeOsduPlatform { MaxPutBlobBytes = 2 * MiB };
        using (var whole = new Rig(platform, new ProtocolOptions { PayloadContentType = "text/plain", UploadBlockMiB = 8 }))
        {
            var refused = await whole.DeliverDatasetAsync(new LargeFile(Content));
            Assert.False(refused.Succeeded);
            Assert.Contains("RequestBodyTooLarge", refused.Failure!.Message, StringComparison.Ordinal);
        }

        using var blocks = new Rig(new FakeOsduPlatform { MaxPutBlobBytes = 2 * MiB });
        var outcome = await blocks.DeliverDatasetAsync(new LargeFile(Content));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
    }

    [Fact]
    public async Task Blocks_are_named_as_the_store_requires_and_commit_in_order_however_they_arrive()
    {
        using var rig = new Rig();
        await rig.DeliverDatasetAsync(new LargeFile(Content));

        var ids = rig.PutBlocks
            .Select(c => System.Web.HttpUtility.ParseQueryString(c.Uri.Query)["blockid"]!)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(Enumerable.Range(0, 4).Select(BlockBlob.Id), ids);
        Assert.Single(ids.Select(id => id.Length).Distinct());
        Assert.Equal(Enumerable.Range(0, 4).Select(BlockBlob.Id), rig.Platform.CommittedBlocks["/staging/blob/f-1"]);
    }

    [Fact]
    public async Task No_request_of_an_upload_carries_the_flows_authorization()
    {
        using var rig = new Rig();
        await rig.DeliverDatasetAsync(new LargeFile(Content));

        Assert.All(rig.PutBlocks.Concat(rig.Commits), c =>
        {
            Assert.False(c.Headers.ContainsKey("Authorization"));
            Assert.Equal("2021-06-08", c.Headers["x-ms-version"]);
            Assert.False(c.Headers.ContainsKey("data-partition-id"));
        });
    }

    [Fact]
    public async Task A_forward_only_file_is_read_once_however_many_blocks_it_has()
    {
        using var rig = new Rig();
        var file = new LargeFile(Content);
        await rig.DeliverDatasetAsync(file);

        Assert.Equal(1, file.Opens);
        Assert.Equal(Content.LongLength, file.BytesRead);
    }

    [Fact]
    public async Task A_block_the_store_fails_is_sent_again_by_itself_and_the_upload_goes_on()
    {
        var platform = new FakeOsduPlatform { BlockFails = (index, attempt) => index == 2 && attempt <= 2 ? HttpStatusCode.InternalServerError : null };
        using var rig = new Rig(platform);
        var file = new LargeFile(Content);
        var outcome = await rig.DeliverDatasetAsync(file);

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(Content, platform.Staged["/staging/blob/f-1"]);
        Assert.Equal(6, rig.PutBlocks.Count);
        Assert.Equal(1, file.Opens);
    }

    [Fact]
    public async Task A_block_that_keeps_failing_ends_the_upload_and_nothing_is_committed_or_registered()
    {
        var platform = new FakeOsduPlatform { BlockFails = (index, _) => index == 1 ? HttpStatusCode.InternalServerError : null };
        using var rig = new Rig(platform);
        var outcome = await rig.DeliverDatasetAsync(new LargeFile(Content));

        Assert.False(outcome.Succeeded);
        Assert.Contains("HTTP 500", outcome.Failure!.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Commits);
        Assert.Equal(0, rig.Registrations);
        Assert.False(platform.Staged.ContainsKey("/staging/blob/f-1"));
    }

    [Fact]
    public async Task A_refusal_of_a_block_is_not_repeated()
    {
        var platform = new FakeOsduPlatform { BlockFails = (index, _) => index == 0 ? HttpStatusCode.Conflict : null };
        using var rig = new Rig(platform);
        var outcome = await rig.DeliverDatasetAsync(new LargeFile(Content));

        Assert.False(outcome.Succeeded);
        Assert.Single(rig.PutBlocks, c => c.Uri.Query.Contains(Uri.EscapeDataString(BlockBlob.Id(0)), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_location_that_ran_out_says_so_and_shows_no_signature()
    {
        var platform = new FakeOsduPlatform { SignedExpired = true, SignedUntil = "2026-01-01T00:00:00Z" };
        using var rig = new Rig(platform);
        var outcome = await rig.DeliverDatasetAsync(new LargeFile(Content));

        Assert.False(outcome.Succeeded);
        var message = outcome.Failure!.Message;
        Assert.Contains("The signed location ran out at 2026-01-01 00:00:00Z before log.las", message, StringComparison.Ordinal);
        Assert.Contains("uploadConcurrency", message, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_in_azure_storage_is_copied_by_the_store_and_no_byte_of_it_is_read_here()
    {
        using var rig = new Rig();
        var file = Copyable(rig.Platform);
        var outcome = await rig.DeliverDatasetAsync(file);

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(Content, rig.Platform.Staged["/staging/blob/f-1"]);
        Assert.Equal(0, file.Opens);
        Assert.All(rig.PutBlocks, c => Assert.Null(c.Body));

        // The ranges cover the file end to end, whatever order the blocks went in.
        var ranges = rig.PutBlocks
            .Select(c => c.Headers["x-ms-source-range"])
            .OrderBy(r => long.Parse(r["bytes=".Length..].Split('-')[0], System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        Assert.Equal(["bytes=0-1048575", "bytes=1048576-2097151", "bytes=2097152-3145727", "bytes=3145728-3670015"], ranges);

        var step = StorageStep(outcome);
        Assert.Equal("serverCopy", step["transfer"]);
        Assert.Equal("4", step["blocks"]);
        Assert.False(step.ContainsKey("notCopied"));
    }

    [Fact]
    public async Task A_copied_file_keeps_the_md5_its_own_store_holds_and_has_none_invented()
    {
        using var known = new Rig();
        await known.DeliverDatasetAsync(Copyable(known.Platform, Md5Of(Content)));
        Assert.Equal(("text/plain", ContentMd5), known.Platform.Committed["/staging/blob/f-1"]);

        using var unknown = new Rig();
        var outcome = await unknown.DeliverDatasetAsync(Copyable(unknown.Platform));
        Assert.Equal(("text/plain", null), unknown.Platform.Committed["/staging/blob/f-1"]);
        Assert.False(StorageStep(outcome).ContainsKey("md5"));
    }

    [Fact]
    public async Task A_store_that_may_not_read_the_source_answers_on_the_first_block_and_the_file_is_sent_from_here()
    {
        var platform = new FakeOsduPlatform { CopyRefused = true };
        using var rig = new Rig(platform);
        var file = Copyable(platform);
        var outcome = await rig.DeliverDatasetAsync(file);

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(Content, platform.Staged["/staging/blob/f-1"]);
        Assert.Equal(1, file.Opens);

        // One block was tried, not a file's worth, and the record says why the file went the long way.
        Assert.Single(rig.PutBlocks, c => c.Headers.ContainsKey("x-ms-copy-source"));
        var step = StorageStep(outcome);
        Assert.Equal("blocks", step["transfer"]);
        Assert.Contains("CannotVerifyCopySource", step["notCopied"], StringComparison.Ordinal);
        Assert.Equal(Convert.ToHexStringLower(Md5Of(Content)), step["md5"]);
    }

    [Fact]
    public async Task A_source_that_could_not_be_signed_is_sent_from_here_and_the_step_says_why()
    {
        using var rig = new Rig();
        var file = new LargeFile(Content, _ => throw new DeliveryException("Could not sign a read: the node holds no role on the account"));
        var outcome = await rig.DeliverDatasetAsync(file);

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal("blocks", StorageStep(outcome)["transfer"]);
        Assert.Contains("holds no role", StorageStep(outcome)["notCopied"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_copy_can_be_turned_off()
    {
        using var rig = new Rig(options: new ProtocolOptions { PayloadContentType = "text/plain", UploadBlockMiB = 1, UploadServerCopy = false });
        var file = Copyable(rig.Platform);
        var outcome = await rig.DeliverDatasetAsync(file);

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(1, file.Opens);
        Assert.DoesNotContain(rig.PutBlocks, c => c.Headers.ContainsKey("x-ms-copy-source"));
        Assert.False(StorageStep(outcome).ContainsKey("notCopied"));
    }

    [Fact]
    public async Task A_local_file_has_nothing_to_copy_from_and_goes_in_blocks_with_no_remark()
    {
        using var rig = new Rig();
        var outcome = await rig.DeliverDatasetAsync(new LargeFile(Content));

        Assert.Equal("blocks", StorageStep(outcome)["transfer"]);
        Assert.False(StorageStep(outcome).ContainsKey("notCopied"));
    }

    [Fact]
    public async Task A_source_that_changes_while_it_is_copied_commits_nothing()
    {
        using var rig = new Rig();
        rig.Platform.CopySources[SourceUrl] = Content;
        var outcome = await rig.DeliverDatasetAsync(new LargeFile(Content, asked => CopySource(etag: "v" + asked)));

        Assert.False(outcome.Succeeded);
        Assert.Contains("changed while it was being copied", outcome.Failure!.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Commits);
        Assert.Equal(0, rig.Registrations);
    }

    [Fact]
    public async Task A_source_that_changed_size_since_the_plan_is_not_copied()
    {
        using var rig = new Rig();
        var outcome = await rig.DeliverDatasetAsync(new LargeFile(Content, _ => CopySource(length: 12)));

        Assert.False(outcome.Succeeded);
        Assert.Contains("holds 12 bytes", outcome.Failure!.Message, StringComparison.Ordinal);
        Assert.Empty(rig.PutBlocks);
    }

    [Fact]
    public async Task A_file_that_ends_early_commits_nothing()
    {
        using var rig = new Rig();
        var outcome = await rig.DeliverDatasetAsync(new Shrunk(Content));

        Assert.False(outcome.Succeeded);
        Assert.Contains("ended after", outcome.Failure!.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Commits);
    }

    /// <summary>A file listed at its size whose bytes then stop short.</summary>
    private sealed class Shrunk(byte[] bytes) : IPayloadSource
    {
        public Task<IReadOnlyList<PayloadFile>> ListChunksAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PayloadFile>>([new PayloadFile(0, "mem://logs/log.las", bytes.LongLength)]);

        public Task<Stream> OpenAsync(PayloadFile file, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream(bytes, 0, bytes.Length / 2, writable: false));
    }

    [Fact]
    public async Task No_signature_shows_in_what_an_attempt_keeps()
    {
        var platform = new FakeOsduPlatform { CopyRefused = true };
        using var rig = new Rig(platform);
        var outcome = await rig.DeliverDatasetAsync(Copyable(platform));

        Assert.DoesNotContain(outcome.Steps.SelectMany(s => s.Returned.Values), v => v.Contains("sig=", StringComparison.Ordinal));
        Assert.DoesNotContain("source-secret", CopySource().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_file_route_gives_its_file_an_md5_so_the_file_service_need_not_read_the_blob_back()
    {
        var platform = new FakeOsduPlatform { LandingIsAzureBlob = true };
        using var rig = new Rig(platform);
        var protocol = new OsduFileProtocol(rig.Client, rig.Options);
        var file = Copyable(platform);
        var outcome = await protocol.DeliverAsync(Work(FakeOsduPlatform.Record("dev:work-product-component--WellLog:7", "osdu:wks:work-product-component--WellLog:1.4.0"), file));

        // Its source holds no MD5, and POST files/metadata would stream the whole blob through the service to compute one.
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(1, file.Opens);
        var step = outcome.Steps.Single(s => s.Name == FileUploads.UploadStep(0)).Returned;
        Assert.Equal("blocks", step["transfer"]);
        Assert.Contains("no content MD5", step["notCopied"], StringComparison.Ordinal);
        Assert.Equal(ContentMd5, platform.Committed["/staging/landing/l-1"].Md5);
    }

    [Fact]
    public async Task The_file_route_copies_a_file_whose_store_keeps_its_md5()
    {
        var platform = new FakeOsduPlatform { LandingIsAzureBlob = true };
        using var rig = new Rig(platform);
        var protocol = new OsduFileProtocol(rig.Client, rig.Options);
        var file = Copyable(platform, Md5Of(Content));
        var outcome = await protocol.DeliverAsync(Work(FakeOsduPlatform.Record("dev:work-product-component--WellLog:7", "osdu:wks:work-product-component--WellLog:1.4.0"), file));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(0, file.Opens);
        Assert.Equal("serverCopy", outcome.Steps.Single(s => s.Name == FileUploads.UploadStep(0)).Returned["transfer"]);
        Assert.Equal(ContentMd5, platform.Committed["/staging/landing/l-1"].Md5);
    }

    [Fact]
    public async Task An_enrichments_las_file_goes_the_same_way_as_a_records_own()
    {
        const string owner = "dev:work-product-component--WellLog:enrich-7";
        var dataset = WorkflowValues.DerivedDatasetId(owner, "dataset--File.Generic", "enrich.lasfile");
        var document = FakeOsduPlatform.Record(owner, "osdu:wks:work-product-component--WellLog:1.4.0", new JsonObject
        {
            ["Name"] = "log 7",
            ["Datasets"] = new JsonArray(JsonValue.Create(dataset + ":")),
        });
        using var rig = new Rig();
        var protocol = new EnrichPartsProtocol(new OsduRecordProtocol(rig.Client, rig.Options), rig.Client, rig.Options);
        var file = Copyable(rig.Platform);
        var part = new EnrichPart("lasfile", dataset, DatasetKind, "mem://las/*", "h1", "{}", null);
        var outcome = (await protocol.DeliverBatchAsync([Work(document, null, [new WorkStepPart(part, file)])]))[0];

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(Content, rig.Platform.Staged["/staging/blob/f-1"]);
        Assert.Equal(0, file.Opens);
        Assert.Equal("serverCopy", StorageStep(outcome)["transfer"]);
        Assert.True(rig.Platform.Records.ContainsKey(dataset));
    }

    [Fact]
    public async Task A_request_body_ceiling_bounds_each_block_rather_than_holding_a_file_that_can_be_split()
    {
        const int ceiling = 512 * 1024;
        using var rig = new Rig(ceiling: ceiling);
        var outcome = await rig.DeliverDatasetAsync(new LargeFile(Content));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(7, rig.PutBlocks.Count);
        Assert.Equal(Content, rig.Platform.Staged["/staging/blob/f-1"]);
    }

    [Fact]
    public async Task A_request_body_ceiling_holds_a_file_a_location_takes_only_whole()
    {
        var platform = new FakeOsduPlatform { Provider = FakeOsduPlatform.Staging.Minio };
        using var rig = new Rig(platform, ceiling: MiB);
        var outcome = await rig.DeliverDatasetAsync(new LargeFile(Content));

        Assert.False(outcome.Succeeded);
        Assert.IsType<RecordHeldException>(outcome.Failure);
        Assert.Contains("request body ceiling", outcome.Failure!.Message, StringComparison.Ordinal);
        Assert.Empty(rig.WholePuts);
    }

    [Fact]
    public async Task A_location_of_another_store_keeps_its_one_put()
    {
        var platform = new FakeOsduPlatform { Provider = FakeOsduPlatform.Staging.Minio };
        using var rig = new Rig(platform);
        var outcome = await rig.DeliverDatasetAsync(new LargeFile(Content));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Empty(rig.PutBlocks);
        Assert.Single(rig.WholePuts);
        Assert.False(rig.WholePuts[0].Headers.ContainsKey("x-ms-blob-type"));
    }

    [Fact]
    public async Task A_flow_that_declares_another_kind_of_blob_keeps_its_one_put()
    {
        using var rig = new Rig(options: new ProtocolOptions
        {
            PayloadContentType = "text/plain",
            UploadBlockMiB = 1,
            UploadHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-ms-blob-type"] = "AppendBlob" },
        });
        await rig.DeliverDatasetAsync(new LargeFile(Content));

        Assert.Empty(rig.PutBlocks);
        Assert.Equal("AppendBlob", Assert.Single(rig.WholePuts).Headers["x-ms-blob-type"]);
    }

    [Fact]
    public async Task What_a_flow_declares_for_the_upload_goes_on_the_commit_and_not_on_each_block()
    {
        using var rig = new Rig(options: new ProtocolOptions
        {
            PayloadContentType = "text/plain",
            UploadBlockMiB = 1,
            UploadHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-ms-blob-type"] = "BlockBlob", ["x-ms-meta-source"] = "welldb" },
        });
        await rig.DeliverDatasetAsync(new LargeFile(Content));

        Assert.Equal("welldb", Assert.Single(rig.Commits).Headers["x-ms-meta-source"]);
        Assert.False(rig.Commits[0].Headers.ContainsKey("x-ms-blob-type"));
        Assert.All(rig.PutBlocks, c => Assert.False(c.Headers.ContainsKey("x-ms-meta-source")));
    }

    [Fact]
    public async Task A_collections_file_is_read_once_however_many_parts_it_goes_in()
    {
        using var rig = new Rig();
        var datasets = new DatasetService(rig.Client, rig.Options);
        var storage = await datasets.StorageAsync("dataset--FileCollection.Generic", CancellationToken.None);
        var file = new LargeFile(Content);
        var chunks = await file.ListChunksAsync();
        await DatasetCollections.UploadAsync(rig.Client, rig.Options, storage, file, chunks, ["log.las"], CancellationToken.None);

        Assert.Equal(Content, rig.Platform.Staged["/staging/dfs/d-1/log.las"]);
        Assert.Equal(1, file.Opens);
        Assert.Equal(Content.LongLength, file.BytesRead);
        Assert.Equal(4, rig.Platform.Calls.Count(c => c.Uri.Query.Contains("action=append", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_collections_append_the_store_fails_is_sent_again_by_itself()
    {
        var platform = new FakeOsduPlatform { AppendFails = (position, attempt) => position == MiB && attempt == 1 ? HttpStatusCode.ServiceUnavailable : null };
        using var rig = new Rig(platform);
        var datasets = new DatasetService(rig.Client, rig.Options);
        var storage = await datasets.StorageAsync("dataset--FileCollection.Generic", CancellationToken.None);
        var file = new LargeFile(Content);
        await DatasetCollections.UploadAsync(rig.Client, rig.Options, storage, file, await file.ListChunksAsync(), ["log.las"], CancellationToken.None);

        Assert.Equal(Content, platform.Staged["/staging/dfs/d-1/log.las"]);
        Assert.Equal(1, file.Opens);
    }

    [Theory]
    [InlineData("https://account.blob.core.windows.net/c/f?sig=x", true)]
    [InlineData("https://account.blob.core.usgovcloudapi.net/c/f?sig=x", true)]
    [InlineData("https://account.z12.blob.storage.azure.net/c/f?sig=x", true)]
    [InlineData("https://files.example.com/c/f?sv=2021-06-08&sr=b&sp=cw&sig=x", true)]
    [InlineData("https://files.example.com/c/f?sig=x", false)]
    [InlineData("https://account.dfs.core.windows.net/fs/dir?sv=2021-06-08&sr=d&sig=x", false)]
    [InlineData("https://bucket.s3.amazonaws.com/f?X-Amz-Signature=x", false)]
    [InlineData("https://storage.googleapis.com/b/f?X-Goog-Signature=x", false)]
    public void An_azure_blob_is_known_by_its_host_or_its_signature(string url, bool expected)
        => Assert.Equal(expected, SignedUploads.IsAzureBlob(new Uri(url)));

    [Theory]
    [InlineData(1, 0, 16L * 1024 * MiB, 1 * MiB)]
    [InlineData(32, 0, 16L * 1024 * MiB, 32 * MiB)]
    [InlineData(32, MiB, 16L * 1024 * MiB, 1 * MiB)]
    [InlineData(32, 0, 2L * 1024 * 1024 * MiB, 42 * MiB)]
    public void A_block_is_the_flows_size_bounded_by_the_ceiling_and_grown_to_fit_the_blocks_a_blob_holds(int blockMiB, long ceiling, long size, long expected)
    {
        var block = SignedUploads.BlockBytes(new PayloadFile(0, "mem://f", size), blockMiB, ceiling);

        Assert.Equal(expected, block);
        Assert.True((size + block - 1) / block <= BlockBlob.MaxBlocks);
    }

    [Fact]
    public void A_file_too_large_for_the_blocks_a_ceiling_allows_holds_the_record()
    {
        var held = Assert.Throws<RecordHeldException>(() => SignedUploads.BlockBytes(new PayloadFile(3, "mem://f", 100L * 1024 * MiB), 32, MiB));
        Assert.Contains("more than 50000 blocks", held.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://a.blob.core.windows.net/c/f?sv=1&se=2026-11-01T22%3A21%3A00Z&sig=x", "2026-11-01T22:21:00+00:00")]
    [InlineData("https://a.blob.core.windows.net/c/f?sv=1&sig=x", null)]
    public void A_location_says_when_it_ends(string url, string? expected)
        => Assert.Equal(expected is null ? null : DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), SignedUploads.Expiry(new Uri(url)));
}
