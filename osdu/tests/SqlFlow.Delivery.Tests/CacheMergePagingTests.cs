using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A merge sends statements bounded by a page of rows, never by the size of a type (osdu/docs/reference/concepts/partition-cache.md,
/// How a merge reads and writes): it reads a version, a type's open ranges and a type's holders page by page, compares a
/// record's values by the hash the database keeps for them, and writes only the membership that moved. Each test works in a
/// partition of its own, with a store that pages by three rows so every read crosses page boundaries.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class CacheMergePagingTests : IDisposable
{
    private const int Page = 3;
    private const string Flow = "wellbore-cache";
    private static readonly DateTimeOffset Start = new(2026, 11, 7, 23, 0, 0, TimeSpan.Zero);

    private readonly OsduTestDatabase _db = new();
    private readonly string _scope = "page" + Guid.NewGuid().ToString("N")[..10];

    public void Dispose()
    {
        using (var db = _db.CreateDbContext())
        {
            db.DeliveryCacheItems.Where(i => i.Scope == _scope).ExecuteDelete();
            db.DeliveryCacheMembers.Where(m => m.Scope == _scope).ExecuteDelete();
            db.DeliveryCacheVersions.Where(v => v.Scope == _scope).ExecuteDelete();
        }

        _db.Dispose();
    }

    [Fact]
    public async Task A_refresh_that_finds_what_its_flow_held_writes_no_membership_and_one_that_moves_writes_only_what_moved()
    {
        await Store().MergeAsync(_scope, Flow, [Wellbores(1, 10)], Capture(), Start);
        await Store().MergeAsync(_scope, "other-flow", [Wellbores(1, 2)], Capture(), Start.AddMinutes(1));

        // The same capture again: the cache does not move, and the flow's hold of every record stands as it was.
        var counter = new MemberWrites();
        var again = await Store(counter).MergeAsync(_scope, Flow, [Wellbores(1, 10)], Capture(), Start.AddMinutes(2));
        Assert.False(again.Written);
        Assert.Empty(counter.Writes);

        // Two records left the flow's capture and one arrived: one statement takes out the two, one puts in the one.
        counter = new MemberWrites();
        var moved = await Store(counter).MergeAsync(_scope, Flow, [Wellbores(3, 11)], Capture(), Start.AddMinutes(3));
        Assert.True(moved.Written);
        Assert.Single(counter.Writes, w => w.StartsWith("DELETE", StringComparison.Ordinal));
        Assert.Single(counter.Writes, w => w.Contains("INSERT", StringComparison.Ordinal));
        Assert.Equal(Ids(3, 11), await HeldAsync(Flow));

        // The other flow's hold is its own, and record 1 and 2 stay in the cache because it still holds them.
        Assert.Equal(Ids(1, 2), await HeldAsync("other-flow"));
        var current = (await Store().LoadAsync(_scope, moved.Snapshot.Version))!;
        Assert.Equal(Ids(1, 11), current.Type("Wellbore")!.Items.Select(i => i.Id).Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task A_capture_that_adds_keeps_what_its_holder_held_and_writes_only_the_records_it_newly_holds()
    {
        await Store().MergeAsync(_scope, Flow, [Wellbores(1, 4)], Capture(), Start);

        var counter = new MemberWrites();
        await Store(counter).MergeAsync(_scope, Flow, [Wellbores(3, 6, "changed")], Capture() with { Adds = true }, Start.AddMinutes(1));

        Assert.DoesNotContain(counter.Writes, w => w.StartsWith("DELETE", StringComparison.Ordinal));
        Assert.Equal(Ids(1, 6), await HeldAsync(Flow));
    }

    [Fact]
    public async Task A_version_of_many_pages_is_read_whole_and_a_merge_ends_only_the_ranges_whose_values_changed()
    {
        // Nine wellbores and three units fill four pages exactly, so the read that finds no more rows is a page of its own,
        // and the nine wellbores' open ranges fill three.
        var first = await Store().MergeAsync(_scope, Flow, [Wellbores(1, 9), Lookup()], Capture(), Start);

        // A store with nothing in memory reads the version back from the database, page by page.
        var loaded = (await Store().LoadAsync(_scope, first.Snapshot.Version))!;
        Assert.Equal(first.Snapshot.ContentHash(), loaded.ContentHash());
        Assert.Equal(Ids(1, 9), loaded.Type("Wellbore")!.Items.Select(i => i.Id).Order(StringComparer.Ordinal).ToList());

        // One wellbore changes and one leaves: a fresh store compares the open ranges by their hashes and ends exactly two.
        var changed = Wellbores(1, 8).Items.Select(i => i.Id == Id(5) ? Wellbore(5, "renamed") : i).ToList();
        var second = await Store().MergeAsync(_scope, Flow, [new ReferenceType("Wellbore", "master-data--Wellbore", changed), Lookup()], Capture(), Start.AddMinutes(1));

        Assert.True(second.Written);
        await using var db = _db.CreateDbContext();
        var rows = await db.DeliveryCacheItems.AsNoTracking().Where(i => i.Scope == _scope && i.TypeName == "Wellbore").ToListAsync();
        Assert.Equal([Id(5), Id(9)], rows.Where(r => r.ToSequence is not null).Select(r => r.RecordId).Order(StringComparer.Ordinal));
        Assert.Equal(Ids(1, 8), rows.Where(r => r.ToSequence is null).Select(r => r.RecordId).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(10, rows.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"FacilityName\":\"plain\"}")]
    [InlineData("{\"FacilityName\":\"Snøhvit Ø-1 å\"}")]
    [InlineData("{\"FacilityName\":\"\U0001F6E2 well é 井\"}")]
    public async Task The_database_hashes_values_as_the_merge_does_whatever_characters_they_hold(string fieldsJson)
    {
        // The column's own expression, over a value instead of the column.
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT " + DeliveryCacheItem.FieldsHashSql.Replace("[FieldsJson]", "@value", StringComparison.Ordinal) + ";", connection);
        command.Parameters.Add(new SqlParameter("@value", System.Data.SqlDbType.NVarChar, -1) { Value = fieldsJson });

        Assert.Equal(DeliveryCacheItem.HashOf(fieldsJson), (byte[])(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task A_record_whose_values_did_not_change_keeps_its_range_by_the_hash_the_database_keeps()
    {
        string[] names = ["Snøhvit Ø-1 å", "\U0001F6E2 well ́combined", "plain"];
        var items = names.Select((name, index) => Item(Id(index + 1), name)).ToList();
        await Store().MergeAsync(_scope, Flow, [new ReferenceType("Wellbore", "master-data--Wellbore", items)], Capture(), Start);

        await using (var db = _db.CreateDbContext())
        {
            var rows = await db.DeliveryCacheItems.AsNoTracking().Where(i => i.Scope == _scope).ToListAsync();
            Assert.Equal(3, rows.Count);
            Assert.All(rows, row => Assert.Equal(DeliveryCacheItem.HashOf(row.FieldsJson), row.FieldsHash));
        }

        // The plain record changes; the two holding characters outside ASCII compare equal by their hashes and keep their ranges.
        var next = items.Take(2).Append(Item(Id(3), "plain again")).ToList();
        await Store().MergeAsync(_scope, Flow, [new ReferenceType("Wellbore", "master-data--Wellbore", next)], Capture(), Start.AddMinutes(1));

        await using var after = _db.CreateDbContext();
        var closed = await after.DeliveryCacheItems.AsNoTracking().Where(i => i.Scope == _scope && i.ToSequence != null).Select(i => i.RecordId).ToListAsync();
        Assert.Equal([Id(3)], closed);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public async Task Releasing_a_type_takes_out_every_row_a_page_at_a_time(int records)
    {
        await Store().MergeAsync(_scope, Flow, [Wellbores(1, records)], Capture(), Start);

        await using var db = _db.CreateDbContext();
        var counter = new MemberWrites();
        await using var counted = Context(counter);
        var removed = await OsduCacheStore.DeleteMembersAsync(
            counted.DeliveryCacheMembers.Where(m => m.Scope == _scope && m.FlowName == Flow && m.TypeName == "Wellbore"), Page, CancellationToken.None);

        Assert.Equal(records, removed);
        Assert.Equal((records / Page) + 1, counter.Writes.Count);
        Assert.Equal(0, await db.DeliveryCacheMembers.CountAsync(m => m.Scope == _scope));
    }

    [Fact]
    public async Task A_merge_the_database_does_not_answer_in_time_names_the_step_it_stopped_at_and_keeps_nothing()
    {
        await Store().MergeAsync(_scope, Flow, [Wellbores(1, 4)], Capture(), Start);

        // Another session holds the membership table until the merge gives up: its timeout is one second.
        await using var blocker = new SqlConnection(_db.ConnectionString);
        await blocker.OpenAsync();
        await using var hold = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using (var command = new SqlCommand("SELECT COUNT_BIG(*) FROM [osdu].[CacheMember] WITH (TABLOCKX, HOLDLOCK);", blocker, hold))
        {
            await command.ExecuteScalarAsync();
        }

        var store = new OsduCacheStore(
            () =>
            {
                var db = _db.CreateDbContext();
                db.Database.SetCommandTimeout(1);
                return db;
            },
            Page);
        var error = await Assert.ThrowsAsync<DeliveryException>(() => store.MergeAsync(_scope, Flow, [Wellbores(2, 5)], Capture(), Start.AddMinutes(1)));
        await hold.RollbackAsync();

        // Whether the read of the holders or the write of the flow's holds met the lock depends on the database reading
        // committed rows by their versions; either step names the type.
        Assert.StartsWith(
            $"Cache flow '{Flow}' could not merge into the cache of partition '{_scope}': the database did not answer within the command timeout of 1 s while ",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains("records of Wellbore", error.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing of this capture was kept; the next refresh merges it again.", error.Message, StringComparison.Ordinal);
        await using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.DeliveryCacheVersions.CountAsync(v => v.Scope == _scope));
        Assert.Equal(Ids(1, 4), await HeldAsync(Flow));
    }

    private OsduCacheStore Store(MemberWrites? counter = null)
        => counter is null ? new OsduCacheStore(_db.CreateDbContext, Page) : new OsduCacheStore(() => Context(counter), Page);

    private OsduDbContext Context(MemberWrites counter)
        => new(new DbContextOptionsBuilder<OsduDbContext>(OsduDbContext.SqlServerOptions(_db.ConnectionString)).AddInterceptors(counter).Options);

    private async Task<List<string>> HeldAsync(string flow)
    {
        await using var db = _db.CreateDbContext();
        return await db.DeliveryCacheMembers.AsNoTracking()
            .Where(m => m.Scope == _scope && m.FlowName == flow)
            .Select(m => m.RecordId)
            .OrderBy(id => id)
            .ToListAsync();
    }

    private static CacheCapture Capture() => new(null, "tests", "paging");

    private static string Id(int number) => $"osdu:master-data--Wellbore:{number:D3}";

    private static List<string> Ids(int from, int to) => Enumerable.Range(from, to - from + 1).Select(Id).ToList();

    private static ReferenceType Wellbores(int from, int to, string name = "well")
        => new("Wellbore", "master-data--Wellbore", Enumerable.Range(from, to - from + 1).Select(n => Wellbore(n, name)));

    private static ReferenceItem Wellbore(int number, string name) => Item(Id(number), $"{name} {number}");

    private static ReferenceItem Item(string id, string name)
        => new(id, new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase) { ["FacilityName"] = ReferenceValue.Of(name) });

    /// <summary>A small lookup table beside the wellbores, so a version holds more than one type across its pages.</summary>
    private static ReferenceType Lookup()
        => new(
            "Units",
            "lookup--Units",
            new[] { "m", "ft", "s" }.Select(code => new ReferenceItem(code, new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase) { ["code"] = ReferenceValue.Of(code) })),
            "code");

    /// <summary>The statements a context sends that write <c>osdu.CacheMember</c>.</summary>
    private sealed class MemberWrites : DbCommandInterceptor
    {
        private readonly List<string> _writes = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<string> Writes
        {
            get
            {
                lock (_gate)
                {
                    return [.. _writes];
                }
            }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Note(command, result));

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Note(command, result));

        private T Note<T>(DbCommand command, T result)
        {
            var text = command.CommandText.TrimStart();
            if (text.Contains("[CacheMember]", StringComparison.Ordinal)
                && (text.Contains("DELETE", StringComparison.Ordinal) || text.Contains("INSERT", StringComparison.Ordinal) || text.Contains("MERGE", StringComparison.Ordinal)))
            {
                lock (_gate)
                {
                    _writes.Add(text.StartsWith("SET NOCOUNT ON;", StringComparison.Ordinal) ? text["SET NOCOUNT ON;".Length..].TrimStart() : text);
                }
            }

            return result;
        }
    }
}
