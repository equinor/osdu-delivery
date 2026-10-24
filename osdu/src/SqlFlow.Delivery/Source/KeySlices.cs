using System.Globalization;

namespace SqlFlow.Delivery.Source;

/// <summary>
/// How a plan's candidate records are cut into key slices for a split delivery: contiguous ranges of the record key, one per
/// part (at most <see cref="MaxSlices"/> of them), dealt to the parts as contiguous shares. Slices partition the key space
/// itself: the first has no lower bound, the last no upper bound, and each starts where the one before it ends, so every
/// record a read meets falls in exactly one of them.
/// </summary>
public static class KeySlices
{
    /// <summary>The most slices one plan is cut into.</summary>
    public const int MaxSlices = 1024;

    /// <summary>How many ranges of values one slice's candidates are counted in, so a cut lands within a small part of a slice.</summary>
    public const int BucketsPerSlice = 64;

    /// <summary>
    /// The inclusive upper bounds that cut a histogram of identity values into at most <paramref name="slices"/> ranges of
    /// roughly equal count. Bucket <c>b</c> holds the candidates whose value is in
    /// <c>[firstValue + b × width, firstValue + (b + 1) × width - 1]</c>; <paramref name="buckets"/> lists the non-empty
    /// ones in ascending order. Cut <c>k</c> aims at the <c>k × total ÷ slices</c>-th candidate and falls on whichever edge
    /// of the bucket holding it is nearer, so no cut is further from its aim than half a bucket, and no error carries into
    /// the next: every range holds its share give or take one bucket, and a bucket never holds more candidates than its
    /// width because the values are unique. A cut that would leave a range empty is dropped, so a histogram whose
    /// candidates crowd into few buckets yields fewer ranges. The last range has no upper bound, so the bounds number one
    /// fewer than the ranges.
    /// </summary>
    public static IReadOnlyList<long> CutHistogram(long firstValue, long width, IReadOnlyList<(long Bucket, long Count)> buckets, int slices)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentNullException.ThrowIfNull(buckets);
        ArgumentOutOfRangeException.ThrowIfLessThan(slices, 1);
        var total = 0L;
        var previous = -1L;
        foreach (var (bucket, count) in buckets)
        {
            if (bucket <= previous || count < 1)
            {
                throw new ArgumentException("A histogram lists its non-empty buckets once each, in ascending order.", nameof(buckets));
            }

            previous = bucket;
            total = checked(total + count);
        }

        // Positions are compared scaled by the slice count, so the aims (k × total ÷ slices) stay whole numbers.
        var bounds = new List<long>(Math.Min(slices - 1, buckets.Count));
        var cut = 0L;
        var aim = 1;
        var before = 0L;
        for (var i = 0; i < buckets.Count && aim < slices; i++)
        {
            var (bucket, count) = buckets[i];
            var after = before + count;
            while (aim < slices && checked(after * slices) >= checked(aim * total))
            {
                var target = aim * total;
                var nearerBefore = (target - (before * slices)) <= ((after * slices) - target);
                if (nearerBefore && before > cut)
                {
                    bounds.Add(checked(firstValue + (bucket * width) - 1));
                    cut = before;
                }
                else if (i < buckets.Count - 1 && after > cut)
                {
                    bounds.Add(checked(firstValue + ((bucket + 1) * width) - 1));
                    cut = after;
                }

                aim++;
            }

            before = after;
        }

        return bounds;
    }

    /// <summary>Deals slices <c>0..slices-1</c> into at most <paramref name="workers"/> contiguous, non-empty shares, in slice order.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> Shares(int slices, int workers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slices, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        var count = Math.Min(slices, workers);
        var shares = new List<IReadOnlyList<int>>(count);
        var next = 0;
        for (var share = 0; share < count; share++)
        {
            var size = (slices - next) / (count - share);
            shares.Add(Enumerable.Range(next, size).ToList());
            next += size;
        }

        return shares;
    }

    /// <summary>A slice list for a log line: every index when there are few, the first and last with the count otherwise.</summary>
    public static string Describe(IReadOnlyList<int> slices)
    {
        ArgumentNullException.ThrowIfNull(slices);
        return slices.Count <= 8
            ? string.Join(",", slices.Select(s => s.ToString(CultureInfo.InvariantCulture)))
            : string.Create(CultureInfo.InvariantCulture, $"{slices[0]}..{slices[^1]} ({slices.Count})");
    }
}
