using System.Runtime.CompilerServices;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>
/// A dimension's table (osdu/docs/reference/flow/dimension-table.md, The dimension's table): the one unified form of a
/// dimension, a row per key and value it collects with a column per attribute, that the page, the export, the CLI and
/// any SQL client all read. A build makes the table and keeps it; a dimension built before dimensions had a table, or
/// whose table was dropped by hand, has it made here from what the ledger holds of it, the first time its table is
/// asked for, so a reader never has to know which case it is.
/// </summary>
public static class DimensionTable
{
    /// <summary>
    /// The table of <paramref name="dimension"/>, from the declaration its last build kept. Its key's and its value's
    /// columns are the ones its table was last made ready with; for a dimension whose table never was (one built before
    /// dimensions had tables), the ones its path, its label and its name give, which the store sets aside for the names
    /// a table already there has, and its next build for the ones its document gives.
    /// </summary>
    public static DimensionTableSpec SpecOf(DimensionState dimension)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        var attributes = DimensionRunner.AttributesOf(dimension.AttributesJson);
        var elements = DimensionRunner.ElementsOf(dimension.ElementsJson);
        var columns = attributes.Select(a => a.Name).Concat(elements?.Fields.Select(f => f.Name) ?? []);
        var (key, value) = dimension is { KeyColumn: { } keyColumn, ValueColumn: { } valueColumn }
            ? (keyColumn, valueColumn)
            : DimensionColumnNames.Settled(dimension.Path, DimensionRunner.LabelOf(dimension.LabelJson), dimension.Name, columns);
        return DimensionTables.Of(dimension.Name, key, value, attributes, elements);
    }

    /// <summary>Makes sure the dimension has its table with its rows, and answers whether the table had to be made or widened.</summary>
    public static Task<bool> EnsureAsync(ILedger ledger, DimensionState dimension, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        return ledger.EnsureDimensionTableAsync(dimension.DimensionId, SpecOf(dimension), ct);
    }

    /// <summary>The table's name, the names of the columns that hold its key and its value, and its attribute columns.</summary>
    public static async Task<DimensionTableShape> ShapeAsync(ILedger ledger, DimensionState dimension, CancellationToken ct)
        => await WithTableAsync(ledger, dimension, () => ledger.DimensionTableShapeAsync(dimension.DimensionId, ct), ct).ConfigureAwait(false)
            ?? throw Gone(dimension);

    /// <summary>A page of the dimension's table, as <paramref name="query"/> narrows and orders it.</summary>
    /// <exception cref="DeliveryException">The query names a column the table does not have, or the dimension is gone.</exception>
    public static async Task<DimensionTablePage> ReadAsync(ILedger ledger, DimensionState dimension, DimensionTableQuery query, CancellationToken ct)
        => await WithTableAsync(ledger, dimension, () => ledger.ReadDimensionTableAsync(dimension.DimensionId, query, ct), ct).ConfigureAwait(false)
            ?? throw Gone(dimension);

    /// <summary>Every row of the dimension's table, by value then row number, one at a time.</summary>
    public static async IAsyncEnumerable<DimensionTableRow> StreamAsync(ILedger ledger, DimensionState dimension, [EnumeratorCancellation] CancellationToken ct)
    {
        // The shape is read first, which makes the table when it is missing, so the rows are read from a table that is there.
        _ = await ShapeAsync(ledger, dimension, ct).ConfigureAwait(false);
        await foreach (var row in ledger.StreamDimensionTableAsync(dimension.DimensionId, ct).ConfigureAwait(false))
        {
            yield return row;
        }
    }

    /// <summary>
    /// Reads the table, making it first when the database does not hold it, and reading once more; and once more when a
    /// build renamed a column of it between the read's two steps, which finds the column under its name.
    /// </summary>
    private static async Task<T> WithTableAsync<T>(ILedger ledger, DimensionState dimension, Func<Task<T>> read, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(dimension);
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (DimensionTableMissingException)
        {
            await EnsureAsync(ledger, dimension, ct).ConfigureAwait(false);
            return await read().ConfigureAwait(false);
        }
        catch (DimensionTableRenamedException)
        {
            return await read().ConfigureAwait(false);
        }
    }

    private static DeliveryException Gone(DimensionState dimension)
        => new($"Dimension {dimension.Name} of {dimension.FlowName} is no longer in the ledger.");
}
