using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// What a dimension holds, looked up by text, each text compared exactly: the keys read through records, the originals
/// named with their members' clean values, and the keys records held when a load last read them.
/// </summary>
internal static partial class SqlServerDimensionStore
{
    /// <summary>
    /// The keys dimension <paramref name="dimensionId"/> holds now that were read through one of <paramref name="recordIds"/>,
    /// each looked up by its id: a seek a record, however many keys the dimension holds. At most a lookup chunk of ids.
    /// </summary>
    public static async Task<List<string>> KeysReadThroughAsync(OsduDbContext db, short partitionId, int dimensionId, IReadOnlyList<string> recordIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(recordIds);
        var ids = recordIds.Where(id => id.Length <= DeliveryDimensionValue.MaxKeyLength).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var sql = "SELECT DISTINCT v.[Original] FROM [osdu].[DimensionKeyRecord] AS k WITH (FORCESEEK) "
            + "INNER JOIN [osdu].[DimensionValue] AS v WITH (FORCESEEK) ON v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[ValueId] = k.[ValueId] "
            + "WHERE k.[PartitionId] = @p AND k.[DimensionId] = @d AND v.[RemovedRunId] IS NULL AND k.[RecordId] IN ("
            + Named(ids.Count) + ");";
        var keys = new List<string>();
        await LookupAsync(db, partitionId, dimensionId, sql, ids, reader => keys.Add(reader.GetString(0)), ct).ConfigureAwait(false);
        return keys;
    }

    /// <summary>
    /// The originals of dimension <paramref name="dimensionId"/> named, removed ones included, each with its member's clean
    /// value, each looked up by its text, exactly. At most a lookup chunk of originals.
    /// </summary>
    public static async Task<List<DimensionValueState>> OriginalsAsync(OsduDbContext db, short partitionId, int dimensionId, IReadOnlyList<string> originals, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(originals);
        var named = originals.Where(o => o.Length <= DeliveryDimensionValue.MaxKeyLength).Distinct(StringComparer.Ordinal).ToList();
        if (named.Count == 0)
        {
            return [];
        }

        var sql = "SELECT v.[ValueId], v.[Original], v.[MemberId], m.[Value], v.[LeftOut], v.[Note], v.[Label], v.[LabelFrom], v.[Filter], v.[Count], "
            + "v.[Filterable], v.[FirstSeenRunId], v.[FirstSeenUtc], v.[MemberSinceRunId], v.[RemovedRunId], v.[RemovedUtc] "
            + "FROM [osdu].[DimensionValue] AS v WITH (FORCESEEK) "
            + "LEFT JOIN [osdu].[DimensionMember] AS m WITH (FORCESEEK) ON m.[PartitionId] = @p AND m.[DimensionId] = @d AND m.[MemberId] = v.[MemberId] "
            + "WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[Original] IN ("
            + Named(named.Count) + ");";
        var asked = named.ToHashSet(StringComparer.Ordinal);
        var found = new List<DimensionValueState>(named.Count);
        await LookupAsync(db, partitionId, dimensionId, sql, named, reader => found.Add(new DimensionValueState
        {
            ValueId = reader.GetInt64(0),
            DimensionId = dimensionId,
            Original = reader.GetString(1),
            MemberId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
            MemberValue = reader.IsDBNull(3) ? null : reader.GetString(3),
            LeftOut = reader.IsDBNull(4) ? null : reader.GetString(4),
            Note = reader.IsDBNull(5) ? null : reader.GetString(5),
            Label = reader.IsDBNull(6) ? null : reader.GetString(6),
            LabelFrom = reader.IsDBNull(7) ? null : reader.GetString(7),
            Filter = reader.IsDBNull(8) ? null : reader.GetString(8),
            Count = reader.GetInt64(9),
            Filterable = reader.GetBoolean(10),
            FirstSeenRunId = reader.GetInt64(11),
            FirstSeenUtc = DateTime.SpecifyKind(reader.GetDateTime(12), DateTimeKind.Utc),
            MemberSinceRunId = reader.GetInt64(13),
            RemovedRunId = reader.IsDBNull(14) ? null : reader.GetInt64(14),
            RemovedUtc = reader.IsDBNull(15) ? null : DateTime.SpecifyKind(reader.GetDateTime(15), DateTimeKind.Utc),
        }), ct).ConfigureAwait(false);
        return found.Where(v => asked.Contains(v.Original)).ToList();
    }

    /// <summary>
    /// The keys the records of dimension <paramref name="dimensionId"/> named by <paramref name="recordKeys"/> (each record's
    /// unique key, <see cref="DeliveryDimensionRecord.RecordKey"/>) held when a load last read them, among those the dimension
    /// holds now. At most a lookup chunk of records.
    /// </summary>
    public static async Task<List<string>> RecordKeysAsync(OsduDbContext db, short partitionId, int dimensionId, IReadOnlyList<string> recordKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(recordKeys);
        var named = recordKeys.Where(r => r.Length <= DeliveryDimensionValue.MaxKeyLength).Distinct(StringComparer.Ordinal).ToList();
        if (named.Count == 0)
        {
            return [];
        }

        var sql = "SELECT DISTINCT v.[Original] FROM [osdu].[DimensionRecord] AS r WITH (FORCESEEK) "
            + "INNER JOIN [osdu].[DimensionValue] AS v WITH (FORCESEEK) ON v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[ValueId] = r.[ValueId] "
            + "WHERE r.[PartitionId] = @p AND r.[DimensionId] = @d AND v.[RemovedRunId] IS NULL AND r.[RecordKey] IN ("
            + Named(named.Count) + ");";
        var keys = new List<string>();
        await LookupAsync(db, partitionId, dimensionId, sql, named, reader => keys.Add(reader.GetString(0)), ct).ConfigureAwait(false);
        return keys;
    }

    /// <summary>The parameters a lookup of <paramref name="count"/> texts names, <c>@k0</c> onwards.</summary>
    private static string Named(int count) => string.Join(", ", Enumerable.Range(0, count).Select(i => "@k" + i.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Runs a lookup by texts, <c>@k0</c> onwards, each as long as a key a dimension keeps and compared exactly, over one
    /// dimension, handing each row to <paramref name="row"/>.
    /// </summary>
    private static async Task LookupAsync(
        OsduDbContext db, short partitionId, int dimensionId, string sql, IReadOnlyList<string> texts, Action<SqlDataReader> row, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = new SqlCommand(sql, (SqlConnection)db.Database.GetDbConnection()) { CommandTimeout = CommandTimeoutSeconds };
            command.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = dimensionId });
            for (var i = 0; i < texts.Count; i++)
            {
                command.Parameters.Add(new SqlParameter("@k" + i.ToString(CultureInfo.InvariantCulture), SqlDbType.NVarChar, DeliveryDimensionValue.MaxKeyLength) { Value = texts[i] });
            }

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                row(reader);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
