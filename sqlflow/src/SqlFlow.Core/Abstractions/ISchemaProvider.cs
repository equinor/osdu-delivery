using SqlFlow.Core.Model;

namespace SqlFlow.Core.Abstractions;

/// <summary>Introspects and mutates the target's schema.</summary>
public interface ISchemaProvider
{
    /// <summary>Returns the live schema of the table, or <c>null</c> if it does not exist.</summary>
    Task<TableSchema?> GetTableSchemaAsync(string connectionString, string schema, string table, CancellationToken ct = default);

    /// <summary>Executes DDL statements in a single transaction.</summary>
    Task ExecuteDdlAsync(string connectionString, IReadOnlyList<string> statements, CancellationToken ct = default);

    /// <summary>For each check, counts the values of the column in <paramref name="schema"/>.<paramref name="table"/>
    /// that would not convert from its type to the check's type without loss (a value rounded, cut, a part dropped, or
    /// read differently under another language), with a sample of at most <paramref name="sampleSize"/> of them.
    /// Read only.</summary>
    Task<IReadOnlyList<LossFinding>> FindLossyConversionsAsync(
        string connectionString, string schema, string table, IReadOnlyList<LossCheck> checks, int sampleSize,
        CancellationToken ct = default);
}
