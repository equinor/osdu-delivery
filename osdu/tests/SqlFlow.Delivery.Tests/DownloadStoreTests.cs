using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The downloads the ledger keeps as a store beside it (<see cref="DownloadStore"/>, osdu/docs/reference/flow/download.md,
/// What a run records) on SQL Server. The store reads and writes under the ledger's own directory of partitions, so a
/// download ledger a move took away is gone for the store the moment the ledger moved it, and its downloads go along under
/// the new identity. A store that kept a directory of its own would go on writing under the identity the move removed.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DownloadStoreTests : IDisposable
{
    private const string OldName = "welldb-welllog-06-download";
    private const string NewName = "welldb-welllog-06-v2-download";
    private const string Kind = "osdu:wks:work-product-component--WellLog:1.4.0";

    private static readonly Guid Old = FlowId.Of(OldName);
    private static readonly Guid New = FlowId.Of(NewName);

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public void Dispose() => _db.Dispose();

    private static Task<LedgerEntry> RegisterAsync(OsduLedger ledger, Guid flowId, string name) => ledger.RegisterLedgerAsync(new LedgerEntry
    {
        FlowId = flowId,
        Partition = "dev",
        Kind = LedgerKinds.Download,
        FlowName = name,
        LedgerName = name,
    });

    [Fact]
    public async Task A_download_ledger_moved_away_takes_its_downloads_along_and_the_store_writes_nothing_more_under_its_old_identity()
    {
        // One ledger throughout: what its directory knows is what its store knows.
        var ledger = _db.Ledger(_clock);
        await RegisterAsync(ledger, Old, OldName);
        await RegisterAsync(ledger, New, NewName);
        var logs = await ledger.Downloads.RegisterAsync(Old, OldName, "logs", Kind, null, "search");
        Assert.Equal("logs", Assert.Single(await ledger.Downloads.ListAsync(Old)).Name);

        await using (var context = _db.CreateDbContext())
        {
            var partition = await context.DeliveryLedgers.Where(l => l.FlowId == Old).Select(l => l.PartitionId).SingleAsync();
            var request = new LedgerMoveRequest(partition, Old, OldName, OldName, New, NewName, string.Empty, NewName, Guid.NewGuid(), LedgerKinds.Download);
            Assert.Equal(LedgerMoveAsk.Asked, (await LedgerMoves.RequestAsync(context, request, Now, CancellationToken.None)).Ask);
        }

        Assert.Equal(LedgerMoves.Completed, Assert.Single(await ledger.CompleteLedgerMovesAsync(null)).Outcome);

        // The download went along with its number, and the old identity holds none.
        var moved = Assert.Single(await ledger.Downloads.ListAsync(New));
        Assert.Equal((logs.DownloadId, "logs", New), (moved.DownloadId, moved.Name, moved.FlowId));
        Assert.Empty(await ledger.Downloads.ListAsync(Old));

        // The move took the old identity out of the directory, and the store, reading the same directory, writes nothing under it.
        var refused = await Assert.ThrowsAsync<DeliveryException>(() => ledger.Downloads.RegisterAsync(Old, OldName, "logs", Kind, null, "search"));
        Assert.Contains("is not in the ledger's directory", refused.Message, StringComparison.Ordinal);
    }
}
