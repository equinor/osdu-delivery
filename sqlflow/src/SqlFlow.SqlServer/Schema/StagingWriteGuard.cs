using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;

namespace SqlFlow.SqlServer.Schema;

/// <summary>
/// Stops a load from writing a staged value into a target column of another type when the implicit conversion would
/// change it. When a target column keeps its type while the source declares another (the change is pending an opt-in,
/// refused, or manual), the upsert converts each staged value to the column's type as it writes it, and SQL Server does
/// that silently even when the value changes: a <c>decimal</c> into an <c>int</c> drops its fraction, a
/// <c>datetime2</c> into a <c>date</c> drops its time, a <c>float</c> into a <c>varchar</c> keeps six digits. Before the
/// load, every staged value of such a column is checked with <see cref="LosslessConversion"/>; one that does not survive
/// fails the run before anything is written, naming the column, the count and a sample. A column whose type holds every
/// value of the staged type (a wider one) is not checked.
/// </summary>
public static class StagingWriteGuard
{
    /// <summary>One column the load writes: its name, the staged type, and the target's type.</summary>
    public sealed record WrittenColumn(string Name, SqlDataType Staged, SqlDataType Target);

    /// <summary>The columns whose staged values need checking: the target's type does not hold every value of the
    /// staged type.</summary>
    public static IReadOnlyList<WrittenColumn> ColumnsToCheck(IEnumerable<WrittenColumn> written)
    {
        ArgumentNullException.ThrowIfNull(written);
        return written
            .Where(c => c.Staged != c.Target && SqlTypeResolution.Resolve(c.Target, c.Staged).Action != SchemaChangeAction.Keep)
            .ToList();
    }

    /// <summary>Checks the staged values of <paramref name="written"/>'s columns that need it and throws a
    /// <see cref="SqlFlowException"/> when any would change on the way into the target. Returns the columns checked.</summary>
    public static async Task<IReadOnlyList<WrittenColumn>> EnsureLosslessAsync(
        string connectionString, RelationalObject staging, RelationalObject target, IEnumerable<WrittenColumn> written, int sampleSize,
        Action<string, string> trace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(trace);

        var checks = ColumnsToCheck(written);
        if (checks.Count == 0)
        {
            return checks;
        }

        var table = $"[{Escape(staging.Schema)}].[{Escape(staging.Name)}]";
        var counts = string.Join(", ", checks.Select(c =>
            $"COUNT_BIG(CASE WHEN {LosslessConversion.LosslessFlag(c.Staged, c.Target, Bracket(c.Name))} = 0 THEN 1 END)"));
        var sql = $"SELECT {counts} FROM {table};";
        trace("staging.type-guard", sql);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        var failing = new long[checks.Count];
        await using (var command = new SqlCommand(sql, connection) { CommandTimeout = 0 })
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            await reader.ReadAsync(ct).ConfigureAwait(false);
            for (var i = 0; i < checks.Count; i++)
            {
                failing[i] = reader.GetInt64(i);
            }
        }

        var problems = new List<string>();
        for (var i = 0; i < checks.Count; i++)
        {
            if (failing[i] == 0)
            {
                continue;
            }

            var c = checks[i];
            var sampleSql = ColumnRetyper.SampleSql(table, Bracket(c.Name),
                $"{LosslessConversion.LosslessFlag(c.Staged, c.Target, Bracket(c.Name))} = 0", sampleSize);
            trace("staging.type-guard.sample", sampleSql);
            var sample = new List<TypeChangeValueSample>();
            await using (var command = new SqlCommand(sampleSql, connection) { CommandTimeout = 0 })
            await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    sample.Add(new TypeChangeValueSample { Value = reader.GetString(0), Rows = reader.GetInt64(1) });
                }
            }

            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"[{c.Name}]: {failing[i]} staged value(s) of the source's {c.Staged.Render()} would change on the way into the target's " +
                $"{c.Target.Render()} (for example {TypeChangeApplier.DescribeSample(sample)})"));
        }

        if (problems.Count > 0)
        {
            throw new SqlFlowException(
                $"The load into {target.QualifiedName} stopped before writing anything: the target keeps types its source no longer matches, and " +
                $"writing these values into them would change them. {string.Join("; ", problems)}. The run log's target.typechange lines say why each " +
                "column kept its type: give the flow the opt-in it names, declare a type in the source the column can hold, or correct the values in the source.");
        }

        return checks;
    }

    private static string Bracket(string identifier) => $"[{Escape(identifier)}]";

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
