using System.Globalization;
using System.Text;
using SqlFlow.Delivery.Hashing;

namespace SqlFlow.Delivery.Source;

/// <summary>What one child dataset contributes to a record's fingerprint: how many rows it holds and the newest of their update times.</summary>
public readonly record struct DatasetVersion(int Rows, DateTime? MaxUpdatedUtc);

/// <summary>
/// The version the ingestion tables give a record (osdu/docs/reference/concepts/change-detection.md, The versions and
/// hashes compared): a SHA-256 over the record row's <c>UpdatedDate_DW</c> and, per child dataset in name order, its
/// row count and the newest <c>UpdatedDate_DW</c> among its rows. It changes when any row of the record is inserted,
/// updated or removed, which is what lets a run decide a record is unchanged without rendering it, and it is stable
/// across runs, nodes and machines, which is what lets the ledger compare today's read with the version it holds.
/// </summary>
public static class IngestionFingerprint
{
    /// <summary>The fingerprint of one record.</summary>
    /// <param name="recordUpdatedUtc">The record row's update time, or null when the row carries none.</param>
    /// <param name="datasets">Each child dataset by name, with its row count and newest update time.</param>
    /// <param name="lookups">
    /// For a flow whose tables join lookup tables (osdu/docs/reference/flow/delivery-lookups.md), each lookup by
    /// <c>&lt;table&gt;.&lt;lookup&gt;</c> with what its rows' update moments read (<see cref="Moments"/>); none for a flow that joins
    /// none, whose records keep the fingerprint they always had.
    /// </param>
    public static string Of(DateTime? recordUpdatedUtc, IReadOnlyDictionary<string, DatasetVersion> datasets, IReadOnlyDictionary<string, string>? lookups = null)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        var text = new StringBuilder();
        text.Append(SourceDatasets.Record).Append((char)0x1F).Append(Ticks(recordUpdatedUtc)).Append('\n');
        foreach (var name in datasets.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            var dataset = datasets[name];
            text.Append(name).Append((char)0x1F)
                .Append(dataset.Rows.ToString(CultureInfo.InvariantCulture)).Append((char)0x1F)
                .Append(Ticks(dataset.MaxUpdatedUtc)).Append('\n');
        }

        foreach (var name in (lookups?.Keys ?? []).OrderBy(n => n, StringComparer.Ordinal))
        {
            text.Append((char)0x1E).Append(name).Append((char)0x1F).Append(lookups![name]).Append('\n');
        }

        return ContentHash.Of(text.ToString());
    }

    /// <summary>
    /// What a lookup's rows read for a fingerprint: the update moment each joined lookup row had, in the order the rows were read,
    /// none where a row joined nothing, as one hash, so a lookup row that moved, arrived or left moves it.
    /// </summary>
    public static string Moments(IEnumerable<DateTime?> moments)
    {
        ArgumentNullException.ThrowIfNull(moments);
        return ContentHash.Of(string.Join((char)0x1F, moments.Select(Ticks)));
    }

    private static string Ticks(DateTime? value)
        => value is { } moment
            ? (moment.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(moment, DateTimeKind.Utc) : moment.ToUniversalTime()).Ticks.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
}
