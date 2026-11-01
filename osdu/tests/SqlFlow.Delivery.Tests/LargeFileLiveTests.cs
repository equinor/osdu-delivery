using System.Security.Cryptography;
using System.Text;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Azure;
using SqlFlow.Azure.Storage;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Storage;
using Xunit;
using static SqlFlow.Delivery.Engine.Protocols.ObjectBody;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The large file path against real Azure Storage, no OSDU involved: a staging blob created empty and signed <c>sp=cw</c>
/// with a user delegation key, as the File service's Azure provider does it
/// (osdu/specs/core/INTEGRATION.md section 2.5.1), filled from a Data Lake account the way a worker fills it.
/// <see cref="StagingAccountVariable"/> and <see cref="SourceAccountVariable"/> name the accounts, which the ambient Azure
/// identity holds Storage Blob Data Contributor on; unset, the tests skip, and one of the two alone fails.
/// <see cref="LargeGibVariable"/> sizes the one test that copies a truly large file, which it builds from one block, so only
/// that block's bytes leave the machine.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LargeFileLiveTests : IAsyncLifetime
{
    public const string StagingAccountVariable = "SQLFLOW_AZTEST_LARGEFILE_STAGING_ACCOUNT";
    public const string SourceAccountVariable = "SQLFLOW_AZTEST_LARGEFILE_SOURCE_ACCOUNT";
    public const string LargeGibVariable = "SQLFLOW_AZTEST_LARGEFILE_GIB";

    private const string StagingContainer = "file-staging-area";
    private const string SourceContainer = "landing";
    private const int MiB = 1024 * 1024;

    private static readonly SecretResolver Secrets = new([new EnvSecretProvider()]);

    private readonly string? _stagingAccount = Environment.GetEnvironmentVariable(StagingAccountVariable);
    private readonly string? _sourceAccount = Environment.GetEnvironmentVariable(SourceAccountVariable);
    private readonly AzureCliCredential _credential = new();
    private readonly string _run = "live-" + Guid.NewGuid().ToString("N")[..12];
    private readonly List<BlobClient> _created = [];

    private BlobServiceClient Staging => new(new Uri($"https://{_stagingAccount}.blob.core.windows.net"), _credential);

    private BlobServiceClient Source => new(new Uri($"https://{_sourceAccount}.blob.core.windows.net"), _credential);

    private void RequireAccounts()
    {
        var staging = string.IsNullOrWhiteSpace(_stagingAccount);
        var source = string.IsNullOrWhiteSpace(_sourceAccount);
        Skip.If(staging && source, $"Set {StagingAccountVariable} and {SourceAccountVariable} to run the large file tests against real storage.");
        Assert.False(staging || source, $"{StagingAccountVariable} and {SourceAccountVariable} go together.");
    }

    /// <summary>A location as the File service makes one: the blob created empty, then signed for create and write alone.</summary>
    private async Task<Uri> LocationAsync(string name, TimeSpan life)
    {
        var blob = Staging.GetBlobContainerClient(StagingContainer).GetBlobClient($"delivery/{_run}/{name}");
        await Staging.GetBlobContainerClient(StagingContainer).GetBlockBlobClient(blob.Name).UploadAsync(new MemoryStream([]));
        _created.Add(blob);
        var now = DateTimeOffset.UtcNow;
        var key = await Staging.GetUserDelegationKeyAsync(now.AddMinutes(-5), now.AddHours(1), CancellationToken.None);
        var signature = new BlobSasBuilder(BlobSasPermissions.Create | BlobSasPermissions.Write, now + life)
        {
            BlobContainerName = blob.BlobContainerName,
            BlobName = blob.Name,
            Resource = "b",
            StartsOn = now.AddMinutes(-5),
        };
        return new BlobUriBuilder(blob.Uri) { Sas = signature.ToSasQueryParameters(key.Value, _stagingAccount) }.ToUri();
    }

    private async Task<PayloadFile> SourceFileAsync(byte[] bytes)
    {
        var blob = Source.GetBlobContainerClient(SourceContainer).GetBlobClient($"{_run}/log.las");
        await blob.UploadAsync(new MemoryStream(bytes));
        _created.Add(blob);
        return new PayloadFile(0, $"abfss://{SourceContainer}@{_sourceAccount}.dfs.core.windows.net/{blob.Name}", bytes.LongLength);
    }

    private static AzureStorageAccess StorageAccess() => new(new AzureCredentialFactory(), NullLogger<AzureStorageAccess>.Instance);

    /// <summary>The payload source a worker has: the real file stores over the node's identity, and a local folder.</summary>
    private static StoragePayloadSource Payload(PayloadFile file)
    {
        var storage = StorageAccess();
        var registry = new FileStoreRegistry([new AzureBlobFileStore(storage)], [], [new AzureBlobFileReader(storage, new AzureCredentialFactory())]);
        var location = PayloadLocation.Parse(file.Path);
        return new StoragePayloadSource(new StoragePayloadFiles(registry), location);
    }

    private sealed class LocalBytes(byte[] bytes) : IPayloadSource
    {
        public Task<IReadOnlyList<PayloadFile>> ListChunksAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PayloadFile>>([new PayloadFile(0, "local.las", bytes.LongLength)]);

        public Task<Stream> OpenAsync(PayloadFile file, CancellationToken ct = default) => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    /// <summary>
    /// A file this process reads and the store may not: its source carries a signature no key made, so a real store answers
    /// with a real refusal, as it does for a source behind a firewall.
    /// </summary>
    private sealed class RefusedBytes(byte[] bytes, string account) : IPayloadSource
    {
        public Task<IReadOnlyList<PayloadFile>> ListChunksAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PayloadFile>>([new PayloadFile(0, "local.las", bytes.LongLength)]);

        public Task<Stream> OpenAsync(PayloadFile file, CancellationToken ct = default) => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));

        public Task<PayloadCopySource?> CopySourceAsync(PayloadFile file, DateTimeOffset? until, CancellationToken ct = default)
        {
            var url = new Uri($"https://{account}.blob.core.windows.net/{SourceContainer}/absent.las?sv=2021-08-06&sr=b&sp=r&se=2030-01-01T00%3A00%3A00Z&sig=bm90LWEtc2lnbmF0dXJl");
            return Task.FromResult<PayloadCopySource?>(new PayloadCopySource(url, bytes.LongLength, "v1", null));
        }
    }

    private static async Task<SignedTransfer> PutAsync(Uri location, IPayloadSource source, PayloadFile file, ProtocolOptions? options = null)
    {
        using var runtime = new HttpRuntime(new FlowReliability(), Secrets);
        var client = new OsduHttpClient(
            runtime, "https://staging.invalid", new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        return await SignedUploads.PutAsync(client, options ?? new ProtocolOptions { PayloadContentType = "text/plain", UploadBlockMiB = 1 }, location, source, file, 0, wantsMd5: false, CancellationToken.None);
    }

    private BlobClient Staged(Uri location) => Staging.GetBlobContainerClient(StagingContainer).GetBlobClient(new BlobUriBuilder(location).BlobName);

    private static async Task<byte[]> DownloadAsync(BlobClient blob)
    {
        using var buffer = new MemoryStream();
        await blob.DownloadToAsync(buffer);
        return buffer.ToArray();
    }

    [SkippableFact]
    public async Task A_small_file_sent_from_here_goes_in_blocks_under_a_real_staging_signature()
    {
        RequireAccounts();
        var bytes = RandomNumberGenerator.GetBytes((3 * MiB) + 12345);
        var location = await LocationAsync("sent.las", TimeSpan.FromMinutes(30));
        var sent = await PutAsync(location, new LocalBytes(bytes), new PayloadFile(0, "local.las", bytes.LongLength));

        Assert.Equal(SignedTransfer.Streamed, sent.How);
        Assert.Equal(4, sent.Blocks);
        var blob = Staged(location);
        var properties = (await blob.GetPropertiesAsync()).Value;
        Assert.Equal(bytes.LongLength, properties.ContentLength);
        Assert.Equal(Md5Of(bytes), properties.ContentHash);
        Assert.Equal("text/plain", properties.ContentType);
        Assert.Equal(BlobType.Block, properties.BlobType);
        Assert.Equal(bytes, await DownloadAsync(blob));
    }

    [SkippableFact]
    public async Task A_small_file_in_a_data_lake_account_is_copied_by_the_store()
    {
        RequireAccounts();
        var bytes = RandomNumberGenerator.GetBytes((3 * MiB) + 12345);
        var file = await SourceFileAsync(bytes);
        var location = await LocationAsync("copied.las", TimeSpan.FromMinutes(30));
        var sent = await PutAsync(location, Payload(file), file);

        Assert.Equal(SignedTransfer.ServerCopied, sent.How);
        Assert.Null(sent.NotCopied);
        var blob = Staged(location);
        Assert.Equal(bytes, await DownloadAsync(blob));
        Assert.Equal("text/plain", (await blob.GetPropertiesAsync()).Value.ContentType);
    }

    [SkippableFact]
    public async Task A_store_that_may_not_read_the_source_refuses_and_the_file_is_sent_from_here()
    {
        RequireAccounts();
        var bytes = RandomNumberGenerator.GetBytes((3 * MiB) + 12345);
        var location = await LocationAsync("refused.las", TimeSpan.FromMinutes(30));
        var sent = await PutAsync(location, new RefusedBytes(bytes, _sourceAccount!), new PayloadFile(0, "local.las", bytes.LongLength));

        Assert.Equal(SignedTransfer.Streamed, sent.How);
        Assert.Contains("HTTP 40", sent.NotCopied, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", sent.NotCopied, StringComparison.Ordinal);
        Assert.Equal(bytes, await DownloadAsync(Staged(location)));
    }

    [SkippableFact]
    public async Task A_signed_read_of_a_file_in_a_data_lake_account_reads_that_file_alone()
    {
        RequireAccounts();
        var bytes = Encoding.UTF8.GetBytes("~Version Information");
        var file = await SourceFileAsync(bytes);
        var copy = await Payload(file).CopySourceAsync(file, DateTimeOffset.UtcNow.AddMinutes(10));

        Assert.NotNull(copy);
        Assert.Equal(bytes.LongLength, copy.Length);
        Assert.False(string.IsNullOrEmpty(copy.ETag));
        using var http = new HttpClient();
        Assert.Equal(bytes, await http.GetByteArrayAsync(copy.Url));
        var other = new UriBuilder(copy.Url) { Path = copy.Url.AbsolutePath + "-other" }.Uri;
        var refused = await http.GetAsync(other);
        Assert.False(refused.IsSuccessStatusCode);
    }

    [SkippableFact]
    public async Task A_location_that_has_run_out_says_so()
    {
        RequireAccounts();
        var bytes = RandomNumberGenerator.GetBytes(2 * MiB);
        var location = await LocationAsync("expired.las", TimeSpan.FromSeconds(-30));
        var failed = await Assert.ThrowsAsync<DeliveryException>(() => PutAsync(location, new LocalBytes(bytes), new PayloadFile(0, "local.las", bytes.LongLength)));

        Assert.Contains("The signed location ran out", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", failed.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_truly_large_file_is_copied_between_accounts_and_this_process_sends_none_of_it()
    {
        RequireAccounts();
        var gib = Environment.GetEnvironmentVariable(LargeGibVariable);
        Skip.If(string.IsNullOrWhiteSpace(gib), $"Set {LargeGibVariable} to the size to copy between the accounts.");
        var blockSize = 64 * MiB;
        var block = RandomNumberGenerator.GetBytes(blockSize);
        var repeats = (int)(long.Parse(gib, System.Globalization.CultureInfo.InvariantCulture) * 1024 * MiB / blockSize);

        var blob = Source.GetBlobContainerClient(SourceContainer).GetBlobClient($"{_run}/huge.las");
        var source = Source.GetBlobContainerClient(SourceContainer).GetBlockBlobClient(blob.Name);
        var id = Convert.ToBase64String("block0"u8.ToArray());
        await source.StageBlockAsync(id, new MemoryStream(block));
        await source.CommitBlockListAsync(Enumerable.Repeat(id, repeats));
        _created.Add(blob);
        var length = (long)repeats * blockSize;
        var file = new PayloadFile(0, $"abfss://{SourceContainer}@{_sourceAccount}.dfs.core.windows.net/{blob.Name}", length);

        var location = await LocationAsync("huge.las", TimeSpan.FromHours(1));
        var sent = await PutAsync(location, Payload(file), file, new ProtocolOptions { PayloadContentType = "text/plain", UploadConcurrency = 8 });

        Assert.Equal(SignedTransfer.ServerCopied, sent.How);
        var copied = Staged(location);
        Assert.Equal(length, (await copied.GetPropertiesAsync()).Value.ContentLength);

        // Offsets across the blob, each read back and held against the one block it was built from.
        foreach (var offset in new[] { 0L, length / 3, length - MiB })
        {
            var read = (await copied.DownloadStreamingAsync(new BlobDownloadOptions { Range = new global::Azure.HttpRange(offset, MiB) })).Value.Content;
            var bytes = new byte[MiB];
            await read.ReadExactlyAsync(bytes);
            var from = (int)(offset % blockSize);
            Assert.True(bytes.AsSpan().SequenceEqual(block.AsSpan(from, MiB)), $"the bytes at {offset} differ");
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var blob in _created)
        {
            await blob.DeleteIfExistsAsync();
        }
    }
}
