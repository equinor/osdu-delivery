using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using SqlFlow.Delivery.Engine.Protocols.Ddms;
using SqlFlow.Delivery.Http;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// A file sent to an object store in parts: read front to back once, so a forward-only stream over a remote file costs one
/// read of it however many parts it has, with at most <c>concurrency</c> parts held in memory and on the wire at a time. A
/// part that fails in transit is sent again by itself: a large file loses that part, not the upload.
/// </summary>
internal static class UploadParts
{
    /// <summary>How many times one part is sent before its failure ends the upload, on top of what the flow's retries repeat.</summary>
    public const int PartFailures = 3;

    /// <summary>Sends one part. <paramref name="part"/> holds only until the returned task ends.</summary>
    public delegate Task SendPart(int index, long offset, ReadOnlyMemory<byte> part, CancellationToken ct);

    /// <summary>
    /// Sends <paramref name="length"/> bytes of <paramref name="reader"/> in parts of <paramref name="partBytes"/> and
    /// returns how many there were. The first failure stops the read and whatever else is in flight, and is what the call
    /// fails with; nothing is still sending when it returns.
    /// </summary>
    public static async Task<int> SendAsync(PartReader reader, long length, int partBytes, int concurrency, SendPart send, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(send);
        ArgumentOutOfRangeException.ThrowIfLessThan(partBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var window = new SemaphoreSlim(concurrency);
        var sending = new List<Task>();
        var index = 0;
        try
        {
            try
            {
                for (long offset = 0; offset < length; index++)
                {
                    await window.WaitAsync(stop.Token).ConfigureAwait(false);
                    var count = (int)Math.Min(partBytes, length - offset);
                    var buffer = ArrayPool<byte>.Shared.Rent(count);
                    try
                    {
                        await reader.ReadIntoAsync(buffer.AsMemory(0, count), stop.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        throw;
                    }

                    sending.Add(SendOneAsync(send, index, offset, buffer, count, window, stop));
                    offset += count;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // A part failed, which stopped the read; its failure is the one reported below.
            }
            catch
            {
                await stop.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            // Each send owns its buffer and its place in the window, and neither outlives this call.
            await Task.WhenAll(sending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        await Task.WhenAll(sending).ConfigureAwait(false);
        return index;
    }

    /// <summary>
    /// Runs one part's send, again when it fails in transit or the store answers a 5xx. Every part here is keyed by what
    /// it is (a block id, a position), so a repeat lands on the same place. The flow's retries leave 500 alone, since an
    /// OSDU service's 500 tends to fail again; a store's is its own transient answer, such as <c>OperationTimedOut</c>.
    /// </summary>
    public static async Task RepeatedAsync(Func<Task> send, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(send);
        for (var failures = 1; ; failures++)
        {
            try
            {
                await send().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (failures < PartFailures && Transient(ex))
            {
                await Task.Delay(TimeSpan.FromSeconds(failures), ct).ConfigureAwait(false);
            }
        }
    }

    private static bool Transient(Exception ex)
        => ex is DeliveryException { InnerException: HttpRequestException or IOException or TaskCanceledException } or OsduStatusException { StatusCode: >= 500 };

    private static async Task SendOneAsync(SendPart send, int index, long offset, byte[] buffer, int count, SemaphoreSlim window, CancellationTokenSource stop)
    {
        try
        {
            await RepeatedAsync(() => send(index, offset, buffer.AsMemory(0, count), stop.Token), stop.Token).ConfigureAwait(false);
        }
        catch
        {
            await stop.CancelAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            window.Release();
        }
    }
}

/// <summary>The bodies a request to an object store carries, and the checksum its parts are sent with.</summary>
internal static class ObjectBody
{
    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "Content-MD5 is the integrity checksum the object store checks each part against; no security rests on it.")]
    public static byte[] Md5Of(ReadOnlySpan<byte> part) => MD5.HashData(part);

    public static ReadOnlyMemoryContent Bytes(ReadOnlyMemory<byte> part, byte[]? md5)
    {
        var content = new ReadOnlyMemoryContent(part);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (md5 is not null)
        {
            content.Headers.ContentMD5 = md5;
        }

        return content;
    }

    public static ByteArrayContent Xml(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        return content;
    }
}
