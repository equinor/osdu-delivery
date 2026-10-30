using System.Globalization;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>
/// The query a test's searches ask (osdu/docs/reference/flow/assertion.md, Disabling a test, and the day its values hold until):
/// its own, narrowed by <c>asOfField</c> to the records whose date there is on or before its <c>asOf</c>, so what it counts
/// and reads is what OSDU held of the records on the day its values were stated. Every search of the test asks it: the
/// count, the groups, the pages of records, a plan, and the searches of known values.
/// </summary>
public static class AssertionQueries
{
    /// <summary>The test's query, narrowed to its asOf when it names an asOfField; its own query otherwise.</summary>
    public static string? Of(AssertionTest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        if (test.AsOfField is not { } field || test.AsOf is not { } asOf || AssertionActivity.After(asOf) is not { } after)
        {
            return test.Query;
        }

        // The range leaves its upper bound out: a date takes in the whole of its day, an instant itself.
        var clause = OsduQuery.Range(OsduField.Date(field), null, after.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        return OsduSearch.Within(test.Query, clause.Text);
    }
}
