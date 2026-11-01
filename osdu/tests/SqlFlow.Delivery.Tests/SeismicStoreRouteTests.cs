using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Protocols.Ddms;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The ddms route to Seismic Store v3 (osdu/specs/seismic-ddms/INTEGRATION.md) against a fake of the service and of the
/// Azure, Google and S3 stores behind it: a dataset registered under a lock id recorded first, its files uploaded in the
/// layout the service's clients read, the dataset closed with its file metadata, every replay and refusal the brief
/// describes, a delivery resumed past what landed, credentials renewed mid-upload, and removals that never take files
/// back at the reversible scope.
/// </summary>
public sealed class SeismicStoreRouteTests
{
    private const string LineId = "dev:dataset--FileCollection.SEGY:line-001";
    private const string LineKind = "osdu:wks:dataset--FileCollection.SEGY:1.1.0";
    private const string SdPath = "sd://dev/seismic/surveys/north/line-001";
    private const string Base = FakeOsduPlatform.SeismicRoot + "/dataset/tenant/dev/subproject/seismic/dataset/line-001";
    private const string Credentials = "GET " + FakeOsduPlatform.SeismicRoot + "/utility/upload-connection-string?sdpath=" + SdPath;
    private const string ReadRecord = "GET /api/storage/v2/records/" + LineId;
    private const string Service = "the DDMS 'seismic' (" + FakeOsduPlatform.SeismicRoot + ")";
    private const int MiB = 1024 * 1024;

    private static readonly SeismicDataset Line001 = new("dev", "seismic", "surveys/north", "line-001");

    private static SeismicStoreSettings Settings(string? provider = null, int chunkMiB = 1, bool readOnly = false) => new()
    {
        Subproject = "seismic",
        Folder = "surveys/north",
        Provider = provider is null ? null : Enum.Parse<DdmsProvider>(provider, ignoreCase: true),
        ObjectStore = provider switch
        {
            "gc" => FakeOsduPlatform.GcsEndpoint,
            "anthos" or "ibm" => FakeOsduPlatform.S3Endpoint,
            _ => null,
        },
        ChunkMiB = chunkMiB,
        ReadOnly = readOnly,
    };

    private sealed class Rig : IDisposable
    {
        public Rig(FakeOsduPlatform platform, SeismicStoreSettings? settings = null, ProtocolOptions? options = null)
        {
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                new SecretResolver([new EnvSecretProvider()]), new TestClock(), platform, allowLoopback: true);
            var client = new OsduHttpClient(
                Runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
            options ??= new ProtocolOptions { WorkflowPollSeconds = 1, DatasetIndexWaitSeconds = 0 };
            var seismic = new DdmsService("seismic", FakeOsduPlatform.SeismicRoot, DdmsShape.SeismicStoreV3, DdmsCatalog.SeismicStoreCollections)
            {
                SeismicStore = settings ?? Settings(),
            };
            Flow = Samples.Targeting(new FlowTarget { Endpoint = FakeOsduPlatform.Endpoint, Protocol = DeliveryProtocol.Ddms, Ddms = [seismic], ProtocolOptions = options }, "seismic");
            Protocol = new OsduDdmsProtocol(client, options, NullLogger.Instance, time: new ProductionTimeSeriesRouteTests.SteppingClock(), routing: DdmsRouting.Of(Flow));
        }

        public HttpRuntime Runtime { get; }

        public FlowDefinition Flow { get; }

        public OsduDdmsProtocol Protocol { get; }

        public void Dispose() => Runtime.Dispose();
    }

    private static JsonObject Line(string name = "Line 001") => FakeOsduPlatform.Record(LineId, LineKind, new JsonObject
    {
        ["Name"] = name,
        ["DatasetProperties"] = new JsonObject
        {
            ["FileCollectionPath"] = "sd://placeholder/",
            ["FileSourceInfos"] = new JsonArray(new JsonObject { ["FileSource"] = "line-001.sgy", ["EncodingFormatTypeID"] = "dev:reference-data--EncodingFormatType:SEG-Y:" }),
        },
    });

    /// <summary>Deterministic bytes that differ from offset to offset, so a misplaced block is caught.</summary>
    private static byte[] Bytes(int length, int seed = 7)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static RafsRouteTests.NamedFiles Files(params (string Name, byte[] Bytes)[] files) => new(files);

    private static DeliveryWork Work(
        JsonObject document, IPayloadSource? files = null, bool metadata = true, long? existing = null,
        IReadOnlyDictionary<string, string>? state = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? completed = null,
        Dictionary<string, IReadOnlyDictionary<string, string>>? reported = null) => new()
        {
            Key = DeliveryKey.Derive("seismic", [document["id"]!.GetValue<string>()]),
            TargetId = document["id"]!.GetValue<string>(),
            Document = document,
            DeliverMetadata = metadata,
            DeliverPayload = files is not null,
            Payload = files,
            ExistingVersion = existing,
            TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
            CompletedSteps = completed ?? new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
            StepCompleted = reported is null
                ? null
                : (report, _) =>
                {
                    reported[report.Step] = report.Returned;
                    return Task.CompletedTask;
                },
        };

    /// <summary>The calls to Seismic Store and Storage, in order; the object stores' are left out.</summary>
    private static List<string> ServiceCalls(FakeOsduPlatform platform, int from = 0)
        => platform.Calls.Skip(from).Where(c => !Store(c)).Select(c => c.Method + " " + Uri.UnescapeDataString(c.Uri.PathAndQuery)).ToList();

    private static List<FakeHttpHandler.Request> ObjectStoreRequests(FakeOsduPlatform platform, int from = 0) => platform.Calls.Skip(from).Where(Store).ToList();

    /// <summary>The requests to an object store, which no OSDU contract describes.</summary>
    private static bool Store(FakeHttpHandler.Request request)
        => request.Uri.AbsolutePath.StartsWith("/blob/", StringComparison.Ordinal)
           || request.Uri.AbsolutePath.StartsWith("/gcs/", StringComparison.Ordinal)
           || request.Uri.AbsolutePath.StartsWith("/s3/", StringComparison.Ordinal);

    /// <summary>Where the fake keeps the dataset's objects: its <c>gcsurl</c>, an anthos folder separator read as a slash.</summary>
    private static string Location(FakeOsduPlatform platform)
        => platform.SeismicDatasets[SdPath]["gcsurl"]!.GetValue<string>().Replace("$$", "/", StringComparison.Ordinal);

    private static byte[] Joined(FakeOsduPlatform platform, int objects)
        => Enumerable.Range(0, objects).SelectMany(i => platform.SeismicObjects[$"{Location(platform)}/{i}"]).ToArray();

    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "The MD5 Seismic Store's clients and Azure keep for a file are integrity checksums the stores define; the tests compare them.")]
    private static byte[] Md5(ReadOnlySpan<byte> bytes) => MD5.HashData(bytes);

    private static void Same(byte[] expected, byte[] actual)
        => Assert.True(expected.AsSpan().SequenceEqual(actual), $"expected {expected.Length} bytes, and the store holds {actual.Length} that differ");

    private static JsonNode Metadata(FakeOsduPlatform platform) => platform.SeismicDatasets[SdPath]["filemetadata"]!;

    private static string Close(string lockId) => $"PATCH {Base}?path=surveys/north&close={lockId}";

    private static string LockIdOf(FakeOsduPlatform platform, int from = 0)
        => platform.Calls.Skip(from).First(c => c.Headers.ContainsKey(SeismicStoreShape.LockHeader)).Headers[SeismicStoreShape.LockHeader];

    [Fact]
    public async Task A_dataset_is_registered_its_file_cut_into_blobs_on_azure_and_closed_with_its_file_metadata()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var file = Bytes((2 * MiB) + (512 * 1024));
        var reported = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var work = Work(Line(), Files(("line-001.sgy", file)), reported: reported);

        var outcome = await rig.Protocol.DeliverAsync(work);
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);

        // The lock id is the record's own, recorded before the call that took it.
        var lockId = LockIdOf(platform);
        Assert.Matches("^W[A-Za-z0-9]{32}$", lockId);
        Assert.Equal(SeismicStoreShape.LockIdOf(work.Key, Line001), lockId);
        Assert.Equal(lockId, reported[SeismicStoreShape.LockStep]["lockId"]);
        Assert.Equal([$"POST {Base}?path=surveys/north", Credentials, Close(lockId), ReadRecord], ServiceCalls(platform));
        Assert.Equal("dev-public", platform.Calls[0].Headers["ltag"]);

        // Three blobs, each one staged block and a block list; each keeps the file's MD5 up to its end, as sdutil writes it.
        var location = Location(platform);
        var store = ObjectStoreRequests(platform);
        Assert.Equal(6, store.Count);
        Assert.All(store, c => Assert.Equal("2021-08-06", c.Headers["x-ms-version"]));
        Assert.All(store, c => Assert.False(c.Headers.ContainsKey("Authorization")));
        Same(file, Joined(platform, 3));
        Assert.Equal(MiB, platform.SeismicObjects[location + "/0"].Length);
        Assert.Equal(512 * 1024, platform.SeismicObjects[location + "/2"].Length);
        Assert.Equal(Convert.ToBase64String(Md5(file.AsSpan(0, MiB))), platform.AzureBlobMd5[location + "/0"]);
        Assert.Equal(Convert.ToBase64String(Md5(file.AsSpan(0, 2 * MiB))), platform.AzureBlobMd5[location + "/1"]);
        Assert.Equal(Convert.ToBase64String(Md5(file)), platform.AzureBlobMd5[location + "/2"]);

        // The dataset is closed with what its clients read it by, and holds the record, which points back at it.
        var dataset = platform.SeismicDatasets[SdPath];
        Assert.Equal("GENERIC", Metadata(platform)["type"]!.GetValue<string>());
        Assert.Equal(file.Length, Metadata(platform)["size"]!.GetValue<long>());
        Assert.Equal(3, Metadata(platform)["nobjects"]!.GetValue<int>());
        Assert.Equal(Convert.ToHexStringLower(Md5(file)), Metadata(platform)["md5Checksum"]!.GetValue<string>());
        Assert.False(dataset["readonly"]!.GetValue<bool>());
        Assert.Equal(LineId, dataset["seismicmeta_guid"]!.GetValue<string>());
        Assert.Empty(platform.SeismicLocks);
        var stored = platform.Records[LineId];
        var properties = stored["data"]!["DatasetProperties"]!;
        Assert.Equal("sd://dev/seismic/surveys/north/", properties["FileCollectionPath"]!.GetValue<string>());
        var source = Assert.Single(properties["FileSourceInfos"]!.AsArray())!;
        Assert.Equal("line-001", source["FileSource"]!.GetValue<string>());
        Assert.Equal("dev:reference-data--EncodingFormatType:SEG-Y:", source["EncodingFormatTypeID"]!.GetValue<string>());
        Assert.Equal(file.Length.ToString(CultureInfo.InvariantCulture), source["FileSize"]!.GetValue<string>());
        Assert.Equal(file.Length.ToString(CultureInfo.InvariantCulture), stored["data"]!["TotalSize"]!.GetValue<string>());

        Assert.Equal(["lock", "register", "metadata", "upload", "close"], outcome.Steps.Select(s => s.Name));
        Assert.Equal(stored["version"]!.GetValue<long>(), outcome.TargetVersion);
        Assert.True(outcome.MetadataDelivered);
        Assert.True(outcome.PayloadDelivered);
        Assert.Equal(3, outcome.ChunksSent);
        Assert.Equal(SdPath, outcome.Returned[SeismicStoreShape.DatasetKey]);
        Assert.Equal(dataset["gcsurl"]!.GetValue<string>(), outcome.Returned[SeismicStoreShape.LocationKey]);
        Assert.Equal("azure", outcome.Returned[SeismicStoreShape.ProviderKey]);
        Assert.Equal("3", outcome.Returned[SeismicStoreShape.ObjectsKey]);
        Assert.Equal("chunks", outcome.Returned[SeismicStoreShape.LayoutKey]);
        Assert.Equal(file.Length.ToString(CultureInfo.InvariantCulture), outcome.Returned[SeismicStoreShape.SizeKey]);
        Assert.Equal(Convert.ToHexStringLower(Md5(file)), outcome.Returned[SeismicStoreShape.Md5Key]);
        Assert.Equal("delivery-app", outcome.Returned["seismicStore.createdBy"]);
        Assert.Equal("uniform", outcome.Returned["seismicStore.accessPolicy"]);
        Assert.Equal($"Azure Blob Storage (localhost/blob/{location})", outcome.Returned["seismicStore.store"]);
        Assert.Equal($"3 object(s) of {file.Length} bytes in {SdPath}, 3 sent by this try", outcome.Detail);

        // Nothing the delivery keeps names the SAS.
        Assert.DoesNotContain(outcome.Returned.Values, v => v.Contains("sig=", StringComparison.Ordinal));
        Assert.DoesNotContain(reported.Values.SelectMany(v => v.Values), v => v.Contains("sig=", StringComparison.Ordinal));

        Assert.Equal(
            [
                "core/storage GET /records/{id}",
                "seismic-ddms GET /utility/upload-connection-string",
                "seismic-ddms PATCH /dataset/tenant/{tenantid}/subproject/{subprojectid}/dataset/{datasetid}",
                "seismic-ddms POST /dataset/tenant/{tenantid}/subproject/{subprojectid}/dataset/{datasetid}",
            ],
            OsduContracts.AssertConform(platform.Calls, Store, OsduContracts.Storage, OsduContracts.SeismicDdms));
    }

    [Theory]
    [InlineData(false, false, 4)]
    [InlineData(true, false, 44)]
    [InlineData(false, true, 5)]
    public async Task On_gc_a_file_is_one_object_sent_in_pieces_whose_checksum_is_compared_and_a_short_or_lost_piece_is_settled(bool halfPieces, bool loseAnswer, int requests)
    {
        // Three pieces: two of a MiB and the last 100 bytes; a session that keeps half of a piece has the rest sent again,
        // and a piece whose answer is lost is settled by asking the session what it holds.
        var file = Bytes((2 * MiB) + 100);
        var platform = new FakeOsduPlatform { SeismicProvider = "gc", GcsHalfPieces = halfPieces, GcsLoseNextAnswer = loseAnswer };
        using var rig = new Rig(platform, Settings("gc"));

        var outcome = await rig.Protocol.DeliverAsync(Work(Line(), Files(("line-001.sgy", file))));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);

        var location = Location(platform);
        Same(file, platform.SeismicObjects[location + "/0"]);
        Assert.Equal(1, Metadata(platform)["nobjects"]!.GetValue<int>());
        Assert.Equal("gc", outcome.Returned[SeismicStoreShape.ProviderKey]);
        Assert.Equal("chunks", outcome.Returned[SeismicStoreShape.LayoutKey]);

        // The object is named under the dataset's location without its bucket, and every request carries the issued token.
        var store = ObjectStoreRequests(platform);
        Assert.Equal(requests, store.Count);
        Assert.Equal("/gcs/upload/storage/v1/b/ss-dev-seismic00001/o", store[0].Uri.AbsolutePath);
        var query = HttpUtility.ParseQueryString(store[0].Uri.Query);
        Assert.Equal(("resumable", location["ss-dev-seismic00001/".Length..] + "/0"), (query["uploadType"], query["name"]));
        Assert.Equal(file.Length.ToString(CultureInfo.InvariantCulture), store[0].Headers["X-Upload-Content-Length"]);
        Assert.All(store, c => Assert.Equal("Bearer gcs-token-1", c.Headers["Authorization"]));
        Assert.Equal($"Google Cloud Storage (gs://{location})", outcome.Returned["seismicStore.store"]);
    }

    [Fact]
    public async Task A_google_piece_that_cannot_be_settled_fails_the_try_without_naming_its_session()
    {
        var platform = new FakeOsduPlatform { SeismicProvider = "gc" };
        platform.SeismicStoreFailing.UnionWith([2, 3]);
        using var rig = new Rig(platform, Settings("gc"));

        var failed = await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Line(), Files(("line-001.sgy", Bytes(1000))))));
        Assert.Equal(500, failed.StatusCode);
        Assert.Contains("/gcs/session/1", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("upload", failed.Message, StringComparison.Ordinal);

        // The try failed, and the dataset stays locked for the next one, which takes the lock as its own.
        Assert.True(platform.SeismicLocks.ContainsKey(SdPath));
    }

    [Fact]
    public async Task On_anthos_a_large_file_goes_as_a_signed_multipart_upload_and_on_ibm_a_small_one_in_one_put()
    {
        // The fake store checks every signature with the AWS SDK and refuses a request that does not match.
        var platform = new FakeOsduPlatform { SeismicProvider = "anthos" };
        using (var rig = new Rig(platform, Settings("anthos", chunkMiB: 5)))
        {
            var file = Bytes((11 * MiB) + 3);
            var outcome = await rig.Protocol.DeliverAsync(Work(Line(), Files(("line-001.sgy", file))));
            Assert.True(outcome.Succeeded, outcome.Failure?.Message);

            Assert.Contains("$$folder01/", platform.SeismicDatasets[SdPath]["gcsurl"]!.GetValue<string>(), StringComparison.Ordinal);
            Same(file, platform.SeismicObjects[Location(platform) + "/0"]);
            var store = ObjectStoreRequests(platform);
            Assert.Equal(["POST", "PUT", "PUT", "PUT", "POST"], store.Select(c => c.Method.Method));
            Assert.Equal("?uploads", store[0].Uri.Query);
            Assert.Equal("/s3/" + Location(platform) + "/0", store[0].Uri.AbsolutePath);
            Assert.Equal(["partNumber=1", "partNumber=2", "partNumber=3"], store.Skip(1).Take(3).Select(c => c.Uri.Query.TrimStart('?').Split('&')[0]));
            Assert.All(store, c => Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKID1/20260917/us-east-1/s3/aws4_request, SignedHeaders=", c.Headers["Authorization"], StringComparison.Ordinal));
            Assert.All(store, c => Assert.Equal("session1", c.Headers["x-amz-security-token"]));
            Assert.All(store, c => Assert.Equal("UNSIGNED-PAYLOAD", c.Headers["x-amz-content-sha256"]));
            Assert.Equal($"S3 (localhost, bucket ss-dev-seismic00001, {Location(platform)["ss-dev-seismic00001/".Length..]})", outcome.Returned["seismicStore.store"]);
            Assert.Equal(Convert.ToHexStringLower(Md5(file)), Metadata(platform)["md5Checksum"]!.GetValue<string>());
        }

        var ibm = new FakeOsduPlatform { SeismicProvider = "ibm" };
        using (var rig = new Rig(ibm, Settings("ibm")))
        {
            var file = Bytes(300_000);
            var outcome = await rig.Protocol.DeliverAsync(Work(Line(), Files(("line-001.sgy", file))));
            Assert.True(outcome.Succeeded, outcome.Failure?.Message);
            Same(file, ibm.SeismicObjects[Location(ibm) + "/0"]);
            var put = Assert.Single(ObjectStoreRequests(ibm));
            Assert.Equal(HttpMethod.Put, put.Method);
            Assert.Equal($"/s3/{Location(ibm)}/0", put.Uri.AbsolutePath);
            Assert.Equal("ibm", outcome.Returned[SeismicStoreShape.ProviderKey]);
        }
    }

    public static TheoryData<string, int, int, string, string> Expiries => new()
    {
        // provider, file bytes, chunk MiB, the credential that stops working, what the renewed one shows in a request
        { "azure", 3 * MiB, 1, "sig1", "sig=sig2" },
        { "gc", (2 * MiB) + 100, 1, "gcs-token-1", "Bearer gcs-token-2" },
        { "anthos", 12 * MiB, 5, "session1", "Credential=AKID2/" },
    };

    [Theory]
    [MemberData(nameof(Expiries))]
    public async Task A_credential_that_expires_during_the_upload_is_renewed_and_the_upload_goes_on(string provider, int length, int chunkMiB, string expired, string renewed)
    {
        var platform = new FakeOsduPlatform { SeismicProvider = provider };
        using var rig = new Rig(platform, Settings(provider == "azure" ? null : provider, chunkMiB));
        var file = Bytes(length);

        // The credential stops working once the second part of the file is read, as time passing makes it.
        var outcome = await rig.Protocol.DeliverAsync(Work(Line(), new ExpiringFile(file, atRead: 2, () => platform.SeismicExpired.Add(expired))));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);

        Assert.Equal(2, platform.SeismicCredentialsIssued);
        Same(file, provider == "azure" ? Joined(platform, length / MiB) : platform.SeismicObjects[Location(platform) + "/0"]);
        var store = ObjectStoreRequests(platform);
        Assert.Contains(store, c => (c.Uri.Query + " " + c.Headers.GetValueOrDefault("Authorization")).Contains(renewed, StringComparison.Ordinal));

        // A multipart upload keeps its id across the renewal.
        if (provider == "anthos")
        {
            Assert.Single(store, c => c.Method == HttpMethod.Post && c.Uri.Query == "?uploads");
        }
    }

    /// <summary>One file, whose <paramref name="atRead"/>th read runs <paramref name="expire"/>, as time passing expires a credential.</summary>
    private sealed class ExpiringFile(byte[] bytes, int atRead, Action expire) : IPayloadSource
    {
        private int _reads;

        public Task<IReadOnlyList<PayloadFile>> ListChunksAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PayloadFile>>([new PayloadFile(0, "mem://content/line-001.sgy", bytes.Length)]);

        public Task<Stream> OpenAsync(PayloadFile file, CancellationToken ct = default)
            => Task.FromResult<Stream>(new Counting(bytes, () =>
            {
                if (Interlocked.Increment(ref _reads) == atRead)
                {
                    expire();
                }
            }));

        private sealed class Counting(byte[] bytes, Action onRead) : MemoryStream(bytes, writable: false)
        {
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                onRead();
                return base.ReadAsync(buffer, cancellationToken);
            }
        }
    }

    [Fact]
    public async Task A_try_that_failed_part_way_resumes_after_the_objects_it_recorded_and_registers_nothing_again()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var file = Bytes((35 * MiB) / 2);
        var reported = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

        // The block of object 17 fails: seventeen objects landed, and the progress recorded after the sixteenth says so.
        platform.FailBlock(35);
        var failed = await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Line(), Files(("line-001.sgy", file)), reported: reported)));
        Assert.Equal(500, failed.StatusCode);
        Assert.Equal("16", reported[SeismicStoreShape.UploadStep]["objects"]);
        Assert.Equal("false", reported[SeismicStoreShape.UploadStep]["complete"]);
        Assert.Equal((16L * MiB).ToString(CultureInfo.InvariantCulture), reported[SeismicStoreShape.UploadStep]["bytes"]);
        Assert.True(platform.SeismicLocks.ContainsKey(SdPath));
        Assert.DoesNotContain(SeismicStoreShape.CloseStep, reported.Keys);

        var calls = platform.Calls.Count;
        var again = new Dictionary<string, IReadOnlyDictionary<string, string>>(reported, StringComparer.Ordinal);
        var outcome = await rig.Protocol.DeliverAsync(Work(Line(), Files(("line-001.sgy", file)), completed: reported, reported: again));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);

        // The dataset is opened under the same lock id, objects 16 and 17 are sent, and the dataset is closed.
        var lockId = reported[SeismicStoreShape.LockStep]["lockId"];
        Assert.Equal([$"PUT {Base}/lock?path=surveys/north&openmode=write", Credentials, Close(lockId), ReadRecord], ServiceCalls(platform, calls));
        Assert.Equal(lockId, platform.Calls[calls].Headers[SeismicStoreShape.LockHeader]);
        Assert.Equal(4, ObjectStoreRequests(platform, calls).Count);
        Assert.Equal(2, outcome.ChunksSent);
        Same(file, Joined(platform, 18));
        Assert.Equal(Convert.ToHexStringLower(Md5(file)), Metadata(platform)["md5Checksum"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(Md5(file)), platform.AzureBlobMd5[Location(platform) + "/17"]);
        Assert.Equal(["metadata", "lock", "register", "upload", "close"], outcome.Steps.Select(s => s.Name));
        Assert.Equal([true, true, true, false, false], outcome.Steps.Select(s => s.Resumed));
        Assert.Equal("true", again[SeismicStoreShape.UploadStep]["complete"]);
        Assert.Empty(platform.SeismicLocks);

        // A try after the close reads the record's version again, and nothing else.
        calls = platform.Calls.Count;
        var closed = await rig.Protocol.DeliverAsync(Work(Line(), Files(("line-001.sgy", file)), completed: again));
        Assert.True(closed.Succeeded, closed.Failure?.Message);
        Assert.Equal([ReadRecord], ServiceCalls(platform, calls));
        Assert.Empty(ObjectStoreRequests(platform, calls));
        Assert.Equal(outcome.TargetVersion, closed.TargetVersion);
        Assert.Equal(["metadata", "lock", "register", "upload", "close"], closed.Steps.Select(s => s.Name));
        Assert.All(closed.Steps, s => Assert.True(s.Resumed));
        Assert.Equal("18", closed.Returned[SeismicStoreShape.ObjectsKey]);
        Assert.Equal("azure", closed.Returned[SeismicStoreShape.ProviderKey]);
        Assert.Equal(outcome.Returned[SeismicStoreShape.LocationKey], closed.Returned[SeismicStoreShape.LocationKey]);
        Assert.Equal($"18 object(s) of {file.Length} bytes in {SdPath}, 0 sent by this try", closed.Detail);
    }

    [Fact]
    public async Task A_registration_replayed_without_a_dataset_is_unlocked_and_made_again()
    {
        var platform = new FakeOsduPlatform { SeismicRegisterEmptyOnce = true };
        using var rig = new Rig(platform);
        var outcome = await rig.Protocol.DeliverAsync(Work(Line(), Files(("line-001.sgy", Bytes(1000)))));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(
            [$"POST {Base}?path=surveys/north", $"PUT {Base}/unlock?path=surveys/north", $"POST {Base}?path=surveys/north", Credentials],
            ServiceCalls(platform).Take(4));
        Assert.Equal(LineId, platform.SeismicDatasets[SdPath]["seismicmeta_guid"]!.GetValue<string>());
        Assert.Equal(SeismicStoreShape.TookLock, outcome.Steps.Single(s => s.Name == SeismicStoreShape.RegisterStep).Returned[SeismicStoreShape.TookValue]);
        OsduContracts.AssertConform(platform.Calls, Store, OsduContracts.Storage, OsduContracts.SeismicDdms);
    }

    [Fact]
    public async Task A_dataset_that_exists_and_holds_the_record_is_taken_over_under_the_lock_and_its_stale_blobs_removed()
    {
        // A delivery whose outcome was lost: the dataset exists with three blobs, holds this record, and the ledger does not know it.
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var first = await rig.Protocol.DeliverAsync(Work(Line(), Files(("line-001.sgy", Bytes(3 * MiB)))));
        Assert.True(first.Succeeded, first.Failure?.Message);
        var location = Location(platform);

        var calls = platform.Calls.Count;
        var smaller = Bytes((3 * MiB) / 2, seed: 9);
        var outcome = await rig.Protocol.DeliverAsync(Work(Line("renamed"), Files(("line-001.sgy", smaller))));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        var lockId = LockIdOf(platform, calls);
        Assert.Equal(
            [
                $"POST {Base}?path=surveys/north",
                $"GET {Base}?path=surveys/north&translate-user-info=false",
                $"PUT {Base}/lock?path=surveys/north&openmode=write",
                Credentials,
                Close(lockId),
                ReadRecord,
            ],
            ServiceCalls(platform, calls));
        Assert.Equal(lockId, platform.Calls[calls + 2].Headers[SeismicStoreShape.LockHeader]);

        // The same record takes the same lock id in every delivery.
        Assert.Equal(first.Steps.Single(s => s.Name == SeismicStoreShape.LockStep).Returned["lockId"], lockId);

        // Two blobs written, the third one of the lost delivery removed, and the record sent with the close.
        Assert.Equal(["PUT", "PUT", "PUT", "PUT", "DELETE"], ObjectStoreRequests(platform, calls).Select(c => c.Method.Method));
        Same(smaller, Joined(platform, 2));
        Assert.False(platform.SeismicObjects.ContainsKey(location + "/2"));
        Assert.Equal(smaller.Length, Metadata(platform)["size"]!.GetValue<long>());
        Assert.Equal(2, Metadata(platform)["nobjects"]!.GetValue<int>());
        Assert.Equal("renamed", platform.Records[LineId]["data"]!["Name"]!.GetValue<string>());
        Assert.Contains("\"seismicmeta\":", platform.Calls.Skip(calls).Single(c => c.Method == HttpMethod.Patch).Body, StringComparison.Ordinal);
        var register = outcome.Steps.Single(s => s.Name == SeismicStoreShape.RegisterStep);
        Assert.Equal(((int?)409, SeismicStoreShape.TookExisting, "3"), (register.Status, register.Returned[SeismicStoreShape.TookValue], register.Returned["earlierObjects"]));
        Assert.DoesNotContain("earlierObjects", outcome.Returned.Keys);
        Assert.Equal(["lock", "register", "upload", "close", "metadata"], outcome.Steps.Select(s => s.Name));
        Assert.Equal($"2 object(s) of {smaller.Length} bytes in {SdPath}, 2 sent by this try; 1 object(s) an earlier delivery left removed", outcome.Detail);
        Assert.Empty(platform.SeismicLocks);
        OsduContracts.AssertConform(platform.Calls, Store, OsduContracts.Storage, OsduContracts.SeismicDdms);
    }

    [Fact]
    public async Task A_dataset_that_holds_another_record_is_held_and_one_locked_or_being_deleted_is_tried_again()
    {
        static JsonObject Existing(string? holder, string? status = null)
        {
            var dataset = new JsonObject
            {
                ["name"] = "line-001",
                ["tenant"] = "dev",
                ["subproject"] = "seismic",
                ["path"] = "/surveys/north/",
                ["created_by"] = "someone",
                ["created_date"] = "2026-09-01T00:00:00Z",
                ["last_modified_date"] = "2026-09-01T00:00:00Z",
                ["gcsurl"] = "ss-dev-seismic00001/0f0f",
                ["ctag"] = "abcdefghijklmnop",
                ["seismicmeta_guid"] = holder,
            };
            if (status is not null)
            {
                dataset["status"] = status;
            }

            return dataset;
        }

        var other = new FakeOsduPlatform();
        other.SeismicDatasets[SdPath] = Existing("dev:dataset--FileCollection.SEGY:someone-else");
        using (var rig = new Rig(other))
        {
            var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Line(), Files(("line-001.sgy", Bytes(1000))))));
            Assert.Equal(
                $"the dataset {SdPath} already exists and belongs to dev:dataset--FileCollection.SEGY:someone-else; the dataset's name is the record key, so {LineId} cannot take it",
                held.Message);
            Assert.Equal([$"POST {Base}?path=surveys/north", $"GET {Base}?path=surveys/north&translate-user-info=false"], ServiceCalls(other));
        }

        var orphan = new FakeOsduPlatform();
        orphan.SeismicDatasets[SdPath] = Existing(null);
        using (var rig = new Rig(orphan))
        {
            var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Line())));
            Assert.StartsWith($"the dataset {SdPath} already exists and belongs to no record;", held.Message, StringComparison.Ordinal);
        }

        var deleting = new FakeOsduPlatform();
        deleting.SeismicDatasets[SdPath] = Existing(LineId, "DELETE:1789000000000");
        using (var rig = new Rig(deleting))
        {
            var busy = await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(Work(Line(), Files(("line-001.sgy", Bytes(1000))))));
            Assert.StartsWith($"Seismic Store is deleting the dataset {SdPath} (status DELETE:1789000000000); the next try registers it again", busy.Message, StringComparison.Ordinal);
            Assert.Empty(ObjectStoreRequests(deleting));
        }

        var locked = new FakeOsduPlatform();
        locked.SeismicLocks[SdPath] = "WsomeoneElse";
        using (var rig = new Rig(locked))
        {
            var busy = await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(Work(Line(), Files(("line-001.sgy", Bytes(1000))))));
            Assert.Equal(
                $"Seismic Store keeps {SdPath} locked for another writer ([seismic-store-service] {SdPath} is write locked [RCODE:WL86400]); "
                + "the next try registers it again once the lock is released or has expired.",
                busy.Message);
            Assert.Equal("WsomeoneElse", locked.SeismicLocks[SdPath]);
            Assert.Empty(locked.SeismicDatasets);
        }
    }

    [Fact]
    public async Task A_redelivery_sends_what_changed_removes_objects_the_file_no_longer_has_and_a_record_change_alone_is_a_patch()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform, Settings(readOnly: true));
        var first = await rig.Protocol.DeliverAsync(Work(Line(), Files(("line-001.sgy", Bytes(3 * MiB)))));
        Assert.True(first.Succeeded, first.Failure?.Message);
        Assert.True(platform.SeismicDatasets[SdPath]["readonly"]!.GetValue<bool>());
        var location = Location(platform);

        // The file shrinks to two objects: the read-only dataset is opened, the third blob removed, and the dataset closed read-only again.
        var calls = platform.Calls.Count;
        var smaller = Bytes((3 * MiB) / 2, seed: 11);
        var second = await rig.Protocol.DeliverAsync(Work(Line(), Files(("line-001.sgy", smaller)), metadata: false, existing: first.TargetVersion, state: first.Returned));
        Assert.True(second.Succeeded, second.Failure?.Message);
        var lockId = LockIdOf(platform, calls);
        Assert.Equal(
            [$"PATCH {Base}?path=surveys/north", $"PUT {Base}/lock?path=surveys/north&openmode=write", Credentials, Close(lockId), ReadRecord],
            ServiceCalls(platform, calls));
        Assert.Equal("{\"readonly\":false}", platform.Calls[calls].Body);
        Assert.Equal(["PUT", "PUT", "PUT", "PUT", "DELETE"], ObjectStoreRequests(platform, calls).Select(c => c.Method.Method));
        Assert.False(platform.SeismicObjects.ContainsKey(location + "/2"));
        Same(smaller, Joined(platform, 2));
        Assert.True(platform.SeismicDatasets[SdPath]["readonly"]!.GetValue<bool>());
        Assert.DoesNotContain("seismicmeta", platform.Calls.Skip(calls).Last(c => c.Method == HttpMethod.Patch).Body, StringComparison.Ordinal);
        Assert.Equal($"2 object(s) of {smaller.Length} bytes in {SdPath}, 2 sent by this try; 1 object(s) an earlier delivery left removed", second.Detail);
        Assert.False(second.MetadataDelivered);
        Assert.Equal(first.TargetVersion, second.TargetVersion);
        Assert.Equal(["lock", "upload", "close"], second.Steps.Select(s => s.Name));

        // The record alone changes: one patch carries it, without a lock, and the record keeps the size of its files.
        calls = platform.Calls.Count;
        var third = await rig.Protocol.DeliverAsync(Work(Line("renamed"), existing: second.TargetVersion, state: second.Returned));
        Assert.True(third.Succeeded, third.Failure?.Message);
        Assert.Equal([$"PATCH {Base}?path=surveys/north", ReadRecord], ServiceCalls(platform, calls));
        Assert.False(platform.Calls[calls].Headers.ContainsKey(SeismicStoreShape.LockHeader));
        Assert.Contains("\"seismicmeta\":", platform.Calls[calls].Body, StringComparison.Ordinal);
        var stored = platform.Records[LineId]["data"]!;
        Assert.Equal("renamed", stored["Name"]!.GetValue<string>());
        Assert.Equal(smaller.Length.ToString(CultureInfo.InvariantCulture), stored["DatasetProperties"]!["FileSourceInfos"]![0]!["FileSize"]!.GetValue<string>());
        Assert.Equal(smaller.Length.ToString(CultureInfo.InvariantCulture), stored["TotalSize"]!.GetValue<string>());
        Assert.True(third.TargetVersion > second.TargetVersion);
        Assert.Empty(ObjectStoreRequests(platform, calls));
        Assert.Equal(["metadata"], third.Steps.Select(s => s.Name));
        Assert.False(third.PayloadDelivered);
        Assert.Null(third.Detail);
        Assert.Equal(second.Returned[SeismicStoreShape.LocationKey], third.Returned[SeismicStoreShape.LocationKey]);

        // A dataset gone from under a record change holds the record: only its files bring the dataset back.
        platform.SeismicDatasets.Remove(SdPath);
        var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Line("again"), existing: third.TargetVersion, state: third.Returned)));
        Assert.StartsWith($"Seismic Store no longer holds the dataset {SdPath}, whose record this delivery changes", held.Message, StringComparison.Ordinal);

        // With its files, it is registered again at a new location.
        var fourth = await rig.Protocol.DeliverAsync(Work(Line("again"), Files(("line-001.sgy", Bytes(10))), existing: third.TargetVersion, state: third.Returned));
        Assert.True(fourth.Succeeded, fourth.Failure?.Message);
        Assert.NotEqual(location, Location(platform));
        Assert.Equal(Location(platform), fourth.Returned[SeismicStoreShape.LocationKey]);
        Assert.Equal("again", platform.Records[LineId]["data"]!["Name"]!.GetValue<string>());
        Assert.Equal(["lock", "register", "metadata", "upload", "close"], fourth.Steps.Select(s => s.Name));
        Assert.DoesNotContain("removed", fourth.Detail, StringComparison.Ordinal);
        OsduContracts.AssertConform(platform.Calls, Store, OsduContracts.Storage, OsduContracts.SeismicDdms);
    }

    [Fact]
    public async Task A_record_sent_again_keeps_the_data_keys_osdu_owns()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform, options: new ProtocolOptions { PreserveDataKeys = ["Owned"] });

        // A first delivery has nothing to keep, and reads nothing.
        var first = await rig.Protocol.DeliverAsync(Work(Line()));
        Assert.Equal(ReadRecord, Assert.Single(ServiceCalls(platform), c => c.StartsWith("GET /api/storage/", StringComparison.Ordinal)));
        platform.Records[LineId]["data"]!["Owned"] = "kept";

        // Every call that sends the record sends what Storage holds for the keys the flow preserves.
        var calls = platform.Calls.Count;
        var patched = await rig.Protocol.DeliverAsync(Work(Line("renamed"), existing: first.TargetVersion, state: first.Returned));
        Assert.True(patched.Succeeded, patched.Failure?.Message);
        Assert.Equal([ReadRecord, $"PATCH {Base}?path=surveys/north", ReadRecord], ServiceCalls(platform, calls));
        Assert.Equal(("renamed", "kept"), (platform.Records[LineId]["data"]!["Name"]!.GetValue<string>(), platform.Records[LineId]["data"]!["Owned"]!.GetValue<string>()));

        platform.SeismicDatasets.Remove(SdPath);
        var again = await rig.Protocol.DeliverAsync(Work(Line("again"), Files(("line-001.sgy", Bytes(10))), existing: patched.TargetVersion, state: patched.Returned));
        Assert.True(again.Succeeded, again.Failure?.Message);
        Assert.Equal(("again", "kept"), (platform.Records[LineId]["data"]!["Name"]!.GetValue<string>(), platform.Records[LineId]["data"]!["Owned"]!.GetValue<string>()));
        Assert.Equal("sd://dev/seismic/surveys/north/", platform.Records[LineId]["data"]!["DatasetProperties"]!["FileCollectionPath"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_record_without_files_registers_its_dataset_empty_and_several_files_keep_their_names()
    {
        var platform = new FakeOsduPlatform { SeismicProvider = "gc" };
        using (var rig = new Rig(platform, Settings("gc")))
        {
            var outcome = await rig.Protocol.DeliverAsync(Work(Line()));
            Assert.True(outcome.Succeeded, outcome.Failure?.Message);
            Assert.Equal((0, 0L), (Metadata(platform)["nobjects"]!.GetValue<int>(), Metadata(platform)["size"]!.GetValue<long>()));
            Assert.Empty(ObjectStoreRequests(platform));
            Assert.False(outcome.PayloadDelivered);
            Assert.Equal(("0", "0"), (outcome.Returned[SeismicStoreShape.ObjectsKey], outcome.Returned[SeismicStoreShape.SizeKey]));
            Assert.Equal(["lock", "register", "metadata", "close"], outcome.Steps.Select(s => s.Name));
            Assert.Empty(platform.SeismicLocks);
        }

        var google = new FakeOsduPlatform { SeismicProvider = "gc" };
        using (var rig = new Rig(google, Settings("gc")))
        {
            var outcome = await rig.Protocol.DeliverAsync(Work(Line(), Files(("a.vds", Bytes(10)), ("b.vds", Array.Empty<byte>()), ("c d.vds", Bytes(700_000)))));
            Assert.True(outcome.Succeeded, outcome.Failure?.Message);
            var location = Location(google);
            Assert.Equal(700_000, google.SeismicObjects[location + "/c d.vds"].Length);
            Assert.Empty(google.SeismicObjects[location + "/b.vds"]);
            Assert.Equal("files", outcome.Returned[SeismicStoreShape.LayoutKey]);
            Assert.Equal("a.vds/b.vds/c d.vds", outcome.Returned[SeismicStoreShape.NamesKey]);
            Assert.Equal(3, Metadata(google)["nobjects"]!.GetValue<int>());
            Assert.Equal(700_010, Metadata(google)["size"]!.GetValue<long>());
            Assert.False(Metadata(google).AsObject().ContainsKey("md5Checksum"));

            // Each object is a session of its own; the empty one is one request that states its end.
            Assert.Equal(["POST", "PUT", "POST", "PUT", "POST", "PUT"], ObjectStoreRequests(google).Select(c => c.Method.Method));
        }

        var azure = new FakeOsduPlatform();
        using (var rig = new Rig(azure))
        {
            var bigger = Bytes((3 * MiB) / 2);
            var outcome = await rig.Protocol.DeliverAsync(Work(Line(), Files(("a.vds", bigger), ("empty.vds", Array.Empty<byte>()))));
            Assert.True(outcome.Succeeded, outcome.Failure?.Message);
            var location = Location(azure);

            // On Azure too, a file of a dataset of several stays whole, as a blob of blocks, and an empty one is one blob.
            Same(bigger, azure.SeismicObjects[location + "/a.vds"]);
            Assert.Empty(azure.SeismicObjects[location + "/empty.vds"]);
            Assert.Equal(Convert.ToBase64String(Md5(bigger)), azure.AzureBlobMd5[location + "/a.vds"]);
            Assert.Equal(Convert.ToBase64String(Md5(ReadOnlySpan<byte>.Empty)), azure.AzureBlobMd5[location + "/empty.vds"]);
            Assert.Equal(["PUT", "PUT", "PUT", "PUT"], ObjectStoreRequests(azure).Select(c => c.Method.Method));
            Assert.Equal("BlockBlob", ObjectStoreRequests(azure)[^1].Headers["x-ms-blob-type"]);
        }
    }

    [Fact]
    public async Task A_record_seismic_store_did_not_write_is_written_through_storage()
    {
        var platform = new FakeOsduPlatform { SeismicSkipsStorage = true };
        using var rig = new Rig(platform);
        var outcome = await rig.Protocol.DeliverAsync(Work(Line(), Files(("line-001.sgy", Bytes(100)))));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal("PUT /api/storage/v2/records", ServiceCalls(platform)[^1]);
        Assert.Equal(platform.Records[LineId]["version"]!.GetValue<long>(), outcome.TargetVersion);
        Assert.Equal("sd://dev/seismic/surveys/north/", platform.Records[LineId]["data"]!["DatasetProperties"]!["FileCollectionPath"]!.GetValue<string>());
        Assert.EndsWith("; Seismic Store did not write the record to Storage, so the route wrote it", outcome.Detail, StringComparison.Ordinal);

        // A record change Seismic Store leaves unwritten keeps the version the ledger holds, and is written through Storage too.
        var calls = platform.Calls.Count;
        var changed = await rig.Protocol.DeliverAsync(Work(Line("renamed"), existing: outcome.TargetVersion, state: outcome.Returned));
        Assert.True(changed.Succeeded, changed.Failure?.Message);
        Assert.Equal([$"PATCH {Base}?path=surveys/north", ReadRecord, "PUT /api/storage/v2/records"], ServiceCalls(platform, calls));
        Assert.Equal("renamed", platform.Records[LineId]["data"]!["Name"]!.GetValue<string>());
        Assert.True(changed.TargetVersion > outcome.TargetVersion);
        Assert.Equal("Seismic Store left the record at the version the ledger holds, so the route wrote it to Storage", changed.Detail);

        // A delivery of files alone does not write the record, and holds it when Storage lost it.
        platform.Records.Remove(LineId);
        var held = await Assert.ThrowsAsync<RecordHeldException>(
            () => rig.Protocol.DeliverAsync(Work(Line("renamed"), Files(("line-001.sgy", Bytes(50))), metadata: false, existing: changed.TargetVersion, state: changed.Returned)));
        Assert.Equal($"Storage does not hold {LineId}, whose dataset this delivery wrote; redeliver the record to write it again", held.Message);
        OsduContracts.AssertConform(platform.Calls, Store, OsduContracts.Storage, OsduContracts.SeismicDdms);
    }

    [Fact]
    public async Task Whatever_seismic_store_would_refuse_holds_the_record_before_anything_is_sent()
    {
        var wrongKind = Line();
        wrongKind["kind"] = "osdu:wks:dataset--FileCollection.Slb.OpenZGY:1.0.0";
        var shortKind = Line();
        shortKind["kind"] = "dataset--FileCollection.SEGY:1.1.0";
        var noCountries = Line();
        noCountries["legal"]!.AsObject().Remove("otherRelevantDataCountries");
        var blankTag = Line();
        blankTag["legal"]!["legaltags"] = new JsonArray(" ");
        var noViewers = Line();
        noViewers["acl"]!["viewers"] = new JsonArray();
        var noData = Line();
        noData.Remove("data");

        var cases = new (JsonObject Document, IPayloadSource? Files, SeismicStoreSettings? Settings, string Expected)[]
        {
            (wrongKind, null, null, $"the kind 'osdu:wks:dataset--FileCollection.Slb.OpenZGY:1.0.0' is not a kind of dataset--FileCollection.SEGY (<authority>:<source>:dataset--FileCollection.SEGY:<version>), which {Service} registers these records as"),
            (shortKind, null, null, "the kind 'dataset--FileCollection.SEGY:1.1.0' is not a kind of dataset--FileCollection.SEGY"),
            (noCountries, Files(("a.sgy", Bytes(1))), null, "legal must name at least one legal tag and one country; Seismic Store would otherwise write US as the country"),
            (blankTag, null, null, "legal must name at least one legal tag and one country"),
            (noViewers, null, null, "acl must name owners and viewers; Seismic Store would otherwise give the record the partition's default groups"),
            (noData, null, null, "the record has no data object, which Seismic Store requires of a dataset's record"),
            (FakeOsduPlatform.Record("dev:dataset--FileCollection.SEGY:line 001", LineKind), null, null, "the key 'line 001' of dev:dataset--FileCollection.SEGY:line 001 cannot name a Seismic Store dataset"),
            (FakeOsduPlatform.Record("dev:dataset--FileCollection.SEGY:a/b", LineKind), null, null, "the key 'a/b' of dev:dataset--FileCollection.SEGY:a/b cannot name a Seismic Store dataset"),
            (Line(), Files(), null, $"no file was found for the dataset {SdPath}"),
            (Line(), Files(("a.sgy", Bytes(1)), ("a.sgy", Bytes(2))), null, $"two files of the dataset {SdPath} have the same name, and a dataset of several files keeps each under its name"),
            (Line(), Files(("a.sgy", Bytes(1)), ("..", Bytes(2))), null, $"the file name '..' cannot name an object of the dataset {SdPath}"),
            (Line(), null, Settings() with { Tenant = "bad tenant" }, "the tenant 'bad tenant' is not a Seismic Store tenant name"),
        };

        foreach (var (document, files, settings, expected) in cases)
        {
            var platform = new FakeOsduPlatform();
            using var rig = new Rig(platform, settings);
            var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(document, files)));
            Assert.True(held.Message.StartsWith(expected, StringComparison.Ordinal), $"expected '{expected}', got: {held.Message}");
            Assert.Empty(platform.Calls);
        }
    }

    public static TheoryData<string, string?, string> ProvidersWithoutAStore => new()
    {
        // the provider the service names, the object store the flow declares, the hold
        { "anthos", null, Service + " runs on anthos and issues a key triple for an S3 store it does not name; declare objectStore" },
        { "aws", FakeOsduPlatform.S3Endpoint, Service + " runs on aws, whose upload credentials osdu/specs/seismic-ddms/INTEGRATION.md section 4.3 does not describe" },
        { "oracle", null, Service + " names its provider 'oracle', which tells the route no object store to upload to" },
    };

    [Theory]
    [MemberData(nameof(ProvidersWithoutAStore))]
    public async Task A_record_held_after_its_dataset_was_locked_releases_the_lock(string provider, string? objectStore, string expected)
    {
        var platform = new FakeOsduPlatform { SeismicProvider = provider };
        using var rig = new Rig(platform, Settings() with { ObjectStore = objectStore });

        var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Line(), Files(("line-001.sgy", Bytes(10))))));
        Assert.StartsWith(expected, held.Message, StringComparison.Ordinal);

        // The lock is released under the delivery's own id alone (a close with an empty patch), never another writer's.
        Assert.Equal(Close(LockIdOf(platform)), ServiceCalls(platform)[^1]);
        Assert.Equal("{}", platform.Calls.Last(c => c.Method == HttpMethod.Patch).Body);
        Assert.Empty(platform.SeismicLocks);
        Assert.Equal(0, platform.SeismicCredentialsIssued);

        // Once the flow says where the files go, the next delivery takes the dataset over and writes them.
        platform.SeismicProvider = "anthos";
        using var corrected = new Rig(platform, Settings("anthos"));
        var outcome = await corrected.Protocol.DeliverAsync(Work(Line(), Files(("line-001.sgy", Bytes(10)))));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(SeismicStoreShape.TookExisting, outcome.Steps.Single(s => s.Name == SeismicStoreShape.RegisterStep).Returned[SeismicStoreShape.TookValue]);
        Assert.Empty(platform.SeismicLocks);
    }

    [Fact]
    public async Task A_file_too_large_for_one_azure_blob_holds_the_record_before_credentials_are_asked_for()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform, Settings(chunkMiB: 0));

        var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Line(), new ListedOnly(2L * 1024 * MiB * 1024))));
        Assert.Equal(
            $"the object 0 of {SdPath} is 2199023255552 bytes, more than an Azure blob of 50000 blocks of 32 MiB holds; "
            + "set chunkMiB, the size of each block and of each blob a single file is cut into, higher",
            held.Message);
        Assert.Equal(0, platform.SeismicCredentialsIssued);
        Assert.Empty(ObjectStoreRequests(platform));
        Assert.Empty(platform.SeismicLocks);
    }

    /// <summary>A file that is listed and never read.</summary>
    private sealed class ListedOnly(long size) : IPayloadSource
    {
        public Task<IReadOnlyList<PayloadFile>> ListChunksAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PayloadFile>>([new PayloadFile(0, "mem://content/line-001.sgy", size)]);

        public Task<Stream> OpenAsync(PayloadFile file, CancellationToken ct = default)
            => throw new InvalidOperationException("The file is not read.");
    }

    [Fact]
    public async Task A_dataset_made_read_only_elsewhere_holds_a_delivery_of_its_files_without_touching_its_lock()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var first = await rig.Protocol.DeliverAsync(Work(Line(), Files(("line-001.sgy", Bytes(10)))));
        Assert.True(first.Succeeded, first.Failure?.Message);
        platform.SeismicDatasets[SdPath]["readonly"] = true;

        var calls = platform.Calls.Count;
        var held = await Assert.ThrowsAsync<RecordHeldException>(
            () => rig.Protocol.DeliverAsync(Work(Line(), Files(("line-001.sgy", Bytes(20))), metadata: false, existing: first.TargetVersion, state: first.Returned)));
        Assert.Equal(
            $"Seismic Store refuses to open {SdPath} for writing: [seismic-store-service] The dataset {SdPath} is read only and cannot be locked for write. "
            + "A read-only dataset opens once its flag is lifted (readOnly: true on the DDMS lifts it itself)",
            held.Message);
        Assert.Equal([$"PUT {Base}/lock?path=surveys/north&openmode=write"], ServiceCalls(platform, calls));
    }

    [Fact]
    public async Task A_removal_soft_deletes_the_record_and_leaves_the_dataset_and_everything_deletes_both_except_on_gc()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var delivered = await rig.Protocol.DeliverAsync(Work(Line(), Files(("line-001.sgy", Bytes(100)))));
        Assert.True(delivered.Succeeded, delivered.Failure?.Message);
        var location = Location(platform);

        var calls = platform.Calls.Count;
        var removed = await rig.Protocol.DeleteAsync(LineId, RemovalScope.Record, delivered.Returned);
        Assert.Equal((true, false), (removed.Deleted, removed.AlreadyGone));
        Assert.Equal($"removed from OSDU (reversible); the dataset {SdPath} and its files stay in Seismic Store, which has no reversible delete", removed.Detail);
        Assert.Equal([$"POST /api/storage/v2/records/{LineId}:delete"], ServiceCalls(platform, calls));
        Assert.Contains(LineId, platform.Removed);
        Assert.True(platform.SeismicDatasets.ContainsKey(SdPath));
        Assert.True(platform.SeismicObjects.ContainsKey(location + "/0"));

        var again = await rig.Protocol.DeleteAsync(LineId, RemovalScope.Record, delivered.Returned);
        Assert.Equal((false, true), (again.Deleted, again.AlreadyGone));

        calls = platform.Calls.Count;
        var history = await rig.Protocol.DeleteAsync(LineId, RemovalScope.History, delivered.Returned);
        Assert.Equal("earlier versions purged; the latest version is still live", history.Detail);
        Assert.Equal([$"DELETE /api/storage/v2/records/{LineId}/versions"], ServiceCalls(platform, calls));

        calls = platform.Calls.Count;
        var everything = await rig.Protocol.DeleteAsync(LineId, RemovalScope.Everything, delivered.Returned);
        Assert.Equal((true, false), (everything.Deleted, everything.AlreadyGone));
        Assert.Equal($"the dataset {SdPath} and its files deleted from Seismic Store; the record purged from OSDU", everything.Detail);
        Assert.Equal([$"DELETE {Base}?path=surveys/north", $"DELETE /api/storage/v2/records/{LineId}"], ServiceCalls(platform, calls));
        Assert.False(platform.SeismicDatasets.ContainsKey(SdPath));
        Assert.DoesNotContain(platform.SeismicObjects.Keys, k => k.StartsWith(location, StringComparison.Ordinal));
        Assert.Contains(LineId, platform.Purged);

        var gone = await rig.Protocol.DeleteAsync(LineId, RemovalScope.Everything, delivered.Returned);
        Assert.Equal($"the dataset {SdPath} and its files deleted from Seismic Store; the record was already gone", gone.Detail);

        var endpoints = RemovalEndpoints.Of(rig.Flow, LineKind);
        Assert.Equal(("/api/storage/v2/records/{id}:delete", "POST"), (endpoints.Record, endpoints.RecordMethod));
        Assert.Equal("/api/storage/v2/records/{id}/versions", endpoints.History);
        Assert.Equal(
            "/api/seismic-store/v3/dataset/tenant/{partition}/subproject/seismic/dataset/{dataset} (the dataset and its files), then /api/storage/v2/records/{id}",
            endpoints.Everything);
        OsduContracts.AssertConform(platform.Calls, Store, OsduContracts.Storage, OsduContracts.SeismicDdms);

        // On gc, deleting one dataset deletes the files of every dataset in the subproject, so it is refused, whether the
        // flow, the record's delivery or the service says the deployment runs there.
        var gc = new FakeOsduPlatform { SeismicProvider = "gc" };
        using var gcRig = new Rig(gc, Settings("gc"));
        var line = await gcRig.Protocol.DeliverAsync(Work(Line(), Files(("line-001.sgy", Bytes(100)))));
        calls = gc.Calls.Count;
        var refused = await Assert.ThrowsAsync<RecordHeldException>(() => gcRig.Protocol.DeleteAsync(LineId, RemovalScope.Everything, line.Returned));
        Assert.StartsWith("Seismic Store on gc deletes the files of every dataset in a subproject when one dataset is deleted", refused.Message, StringComparison.Ordinal);
        Assert.Equal(calls, gc.Calls.Count);
        Assert.Equal(RemovalEndpoints.SeismicGcDeleteRefused, RemovalEndpoints.Of(gcRig.Flow, LineKind).Everything);

        using var unsaid = new Rig(gc);
        await Assert.ThrowsAsync<RecordHeldException>(() => unsaid.Protocol.DeleteAsync(LineId, RemovalScope.Everything, line.Returned));
        await Assert.ThrowsAsync<RecordHeldException>(() => unsaid.Protocol.DeleteAsync(LineId, RemovalScope.Everything));
        Assert.Equal([$"GET {FakeOsduPlatform.SeismicRoot}/svcstatus"], ServiceCalls(gc, calls));
        Assert.True(gc.SeismicDatasets.ContainsKey(SdPath));
    }

    [Fact]
    public async Task The_probe_asks_the_status_the_status_behind_the_token_and_the_subproject_of_the_partition()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var probe = await rig.Protocol.ProbeAsync();
        Assert.True(probe.Reachable, probe.Detail);
        Assert.Equal(
            [
                $"GET {FakeOsduPlatform.SeismicRoot}/svcstatus",
                $"GET {FakeOsduPlatform.SeismicRoot}/svcstatus/access",
                $"GET {FakeOsduPlatform.SeismicRoot}/subproject/tenant/dev/subproject/seismic",
            ],
            ServiceCalls(platform));
        Assert.Equal("the DDMS answered all 3 probes", probe.Detail);
        OsduContracts.AssertConform(platform.Calls, Store, OsduContracts.SeismicDdms);

        var missing = new FakeOsduPlatform();
        missing.SeismicSubprojects.Clear();
        using var other = new Rig(missing);
        var failed = await other.Protocol.ProbeAsync();
        Assert.False(failed.Reachable);
        Assert.Equal(404, failed.Status);
        Assert.Equal($"{FakeOsduPlatform.SeismicRoot}/subproject/tenant/dev/subproject/seismic", failed.Path);
        Assert.Contains("The subproject seismic does not exist", failed.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_reads_the_record_from_storage_and_finds_drift_when_its_dataset_is_gone_or_holds_another_record()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var delivered = await rig.Protocol.DeliverAsync(Work(Line()));
        var version = delivered.TargetVersion;

        var calls = platform.Calls.Count;
        var verified = await rig.Protocol.VerifyAsync(LineId, version);
        Assert.Equal((VerifyOutcome.Match, version), (verified.Outcome, verified.ObservedVersion));
        Assert.Equal([ReadRecord, $"GET {Base}?path=surveys/north&translate-user-info=false"], ServiceCalls(platform, calls));
        Assert.Equal(LineId, (await rig.Protocol.ReadAsync(LineId))!["id"]!.GetValue<string>());

        var drifted = await rig.Protocol.VerifyAsync(LineId, version - 1);
        Assert.Equal(VerifyOutcome.Drifted, drifted.Outcome);
        Assert.Equal($"observed version {version}, ledger holds {version - 1}", drifted.Detail);

        platform.SeismicDatasets[SdPath]["seismicmeta_guid"] = "dev:dataset--FileCollection.SEGY:other";
        var taken = await rig.Protocol.VerifyAsync(LineId, version);
        Assert.Equal((VerifyOutcome.Drifted, version), (taken.Outcome, taken.ObservedVersion));
        Assert.Equal($"the dataset {SdPath} holds dev:dataset--FileCollection.SEGY:other rather than this record", taken.Detail);

        platform.SeismicDatasets[SdPath]["seismicmeta_guid"] = LineId;
        platform.SeismicDatasets[SdPath]["status"] = "DELETE:1789000000000";
        Assert.Equal($"Seismic Store is deleting the dataset {SdPath} (status DELETE:1789000000000)", (await rig.Protocol.VerifyAsync(LineId, version)).Detail);

        platform.SeismicDatasets.Remove(SdPath);
        var gone = await rig.Protocol.VerifyAsync(LineId, version - 1);
        Assert.Equal(VerifyOutcome.Drifted, gone.Outcome);
        Assert.Equal($"observed version {version}, ledger holds {version - 1}; Seismic Store no longer holds the dataset {SdPath}", gone.Detail);

        platform.Records.Remove(LineId);
        calls = platform.Calls.Count;
        Assert.Equal(VerifyOutcome.Missing, (await rig.Protocol.VerifyAsync(LineId, version)).Outcome);
        Assert.Equal([ReadRecord], ServiceCalls(platform, calls));
        OsduContracts.AssertConform(platform.Calls, Store, OsduContracts.Storage, OsduContracts.SeismicDdms);
    }

    [Fact]
    public void The_link_to_the_dataset_is_carried_into_a_document_the_record_is_rewritten_from()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);

        var rendered = Line();
        Assert.False(rig.Protocol.CarryLink(LineId, null, rendered));
        var properties = rendered["data"]!["DatasetProperties"]!;
        Assert.Equal("sd://dev/seismic/surveys/north/", properties["FileCollectionPath"]!.GetValue<string>());
        Assert.Equal("line-001", properties["FileSourceInfos"]![0]!["FileSource"]!.GetValue<string>());
        Assert.Equal("dev:reference-data--EncodingFormatType:SEG-Y:", properties["FileSourceInfos"]![0]!["EncodingFormatTypeID"]!.GetValue<string>());

        var bare = FakeOsduPlatform.Record(LineId, LineKind);
        Assert.True(rig.Protocol.CarryLink(LineId, null, bare));
        Assert.Equal("line-001", Assert.Single(bare["data"]!["DatasetProperties"]!["FileSourceInfos"]!.AsArray())!["FileSource"]!.GetValue<string>());
        Assert.True(rig.Protocol.CarryLink(LineId, null, bare));

        var two = FakeOsduPlatform.Record(LineId, LineKind, new JsonObject
        {
            ["DatasetProperties"] = new JsonObject
            {
                ["FileCollectionPath"] = "sd://dev/seismic/surveys/north/",
                ["FileSourceInfos"] = new JsonArray(new JsonObject { ["FileSource"] = "line-001" }, new JsonObject { ["FileSource"] = "line-002" }),
            },
        });
        Assert.False(rig.Protocol.CarryLink(LineId, null, two));
        Assert.Single(two["data"]!["DatasetProperties"]!["FileSourceInfos"]!.AsArray());
        Assert.Empty(platform.Calls);
    }

    [Theory]
    [InlineData("azure", DdmsProvider.Azure)]
    [InlineData("gc", DdmsProvider.Gc)]
    [InlineData("google", DdmsProvider.Gc)]
    [InlineData(" GCP ", DdmsProvider.Gc)]
    [InlineData("anthos", DdmsProvider.Anthos)]
    [InlineData("IBM", DdmsProvider.Ibm)]
    [InlineData("aws", DdmsProvider.Aws)]
    [InlineData("oracle", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_provider_is_read_from_the_label_seismic_store_answers_with(string? label, DdmsProvider? expected)
        => Assert.Equal(expected, SeismicStoreShape.ProviderOf(label));

    [Fact]
    public void A_single_file_is_cut_into_objects_on_azure_only_and_several_files_keep_their_names()
    {
        static PayloadFile Listed(int index, long size) => new(index, $"mem://f/{index}", size);
        var single = new SeismicPlan(Line001, [Listed(0, (5L * MiB) + 1)], ["line.sgy"], (5L * MiB) + 1);

        Assert.Equal(
            [("0", 0L, 2L * MiB), ("1", 2L * MiB, 2L * MiB), ("2", 4L * MiB, MiB + 1L)],
            SeismicStoreShape.Layout(single, DdmsProvider.Azure, 2 * MiB).Select(o => (o.Key, o.Offset, o.Length)));
        Assert.Equal([("0", 0L, (5L * MiB) + 1)], SeismicStoreShape.Layout(single, DdmsProvider.Azure, 0).Select(o => (o.Key, o.Offset, o.Length)));
        Assert.Equal([("0", 0L, (5L * MiB) + 1)], SeismicStoreShape.Layout(single, DdmsProvider.Azure, (5L * MiB) + 1).Select(o => (o.Key, o.Offset, o.Length)));
        Assert.Equal(["0", "1"], SeismicStoreShape.Layout(single, DdmsProvider.Azure, 5L * MiB).Select(o => o.Key));
        Assert.Equal([("0", 0L, (5L * MiB) + 1)], SeismicStoreShape.Layout(single, DdmsProvider.Gc, 2 * MiB).Select(o => (o.Key, o.Offset, o.Length)));
        Assert.Equal([("0", 0L, 0L)], SeismicStoreShape.Layout(single with { Files = [Listed(0, 0)] }, DdmsProvider.Azure, 2 * MiB).Select(o => (o.Key, o.Offset, o.Length)));

        var several = new SeismicPlan(Line001, [Listed(0, 3), Listed(1, 4)], ["a", "b"], 7);
        Assert.Equal([("a", 0, 3L), ("b", 1, 4L)], SeismicStoreShape.Layout(several, DdmsProvider.Azure, 2).Select(o => (o.Key, o.File, o.Length)));

        Assert.Equal(5L * MiB, S3Store.PartSize(12 * MiB, MiB));
        Assert.Equal(8L * MiB, S3Store.PartSize(12 * MiB, 8 * MiB));
        Assert.Equal(10L * MiB, S3Store.PartSize(100_000L * MiB, 5 * MiB));
        Assert.Equal(11L * MiB, S3Store.PartSize(100_001L * MiB, 5 * MiB));
    }

    [Fact]
    public void A_record_takes_one_lock_id_for_its_dataset_whatever_the_delivery()
    {
        var key = DeliveryKey.Derive("seismic", [LineId]);
        var id = SeismicStoreShape.LockIdOf(key, Line001);
        Assert.Matches("^W[A-Za-z0-9]{32}$", id);
        Assert.Equal(id, SeismicStoreShape.LockIdOf(DeliveryKey.Derive("seismic", [LineId]), new SeismicDataset("dev", "seismic", "surveys/north", "line-001")));
        Assert.NotEqual(id, SeismicStoreShape.LockIdOf(DeliveryKey.Derive("other-flow", [LineId]), Line001));
        Assert.NotEqual(id, SeismicStoreShape.LockIdOf(key, Line001 with { Folder = "surveys/south" }));
    }

    [Fact]
    public void A_dataset_path_is_the_folder_the_record_links_to_and_the_name()
    {
        Assert.Equal(("sd://dev/seismic/surveys/north/", SdPath), (Line001.FolderPath, Line001.SdPath));
        var root = Line001 with { Folder = null };
        Assert.Equal(("sd://dev/seismic/", "sd://dev/seismic/line-001"), (root.FolderPath, root.SdPath));
    }
}
