using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// The T-SQL of a proof that reads a partition's cache: what <see cref="ICacheStore.ReadProofAsync"/> runs and a person copies
/// into SSMS, the same statement, its values bound as parameters there and declared at the top of the script here. Every value
/// is a parameter, so no value can carry a fragment of the statement, and the version is read only while its records are
/// kept: a version that is gone or pruned stops the statement with an error saying so, so an empty answer is never an answer
/// about records nobody can read.
/// </summary>
public static class CacheProofSql
{
    /// <summary>The partition.</summary>
    public const string ScopeParameter = "@scope";

    /// <summary>The version's label.</summary>
    public const string VersionParameter = "@version";

    /// <summary>The types, as a JSON array.</summary>
    public const string TypesParameter = "@types";

    /// <summary>The fields, as a JSON array.</summary>
    public const string FieldsParameter = "@fields";

    /// <summary>What is looked for, as a JSON array of objects (<c>value</c>, and for an id <c>record</c> and <c>code</c>).</summary>
    public const string LookedParameter = "@looked";

    /// <summary>The most rows listed.</summary>
    public const string RowsParameter = IngestionSql.ProofRowsParameter;

    /// <summary>The error a statement stops with when the version cannot be read.</summary>
    public const int GoneError = 50000;

    /// <summary>The collation that compares as the cache does exactly: ordinal.</summary>
    private const string Exact = "Latin1_General_100_BIN2";

    /// <summary>The collation that compares ignoring case.</summary>
    private const string Caseless = "Latin1_General_100_CI_AS";

    /// <summary>The statement for <paramref name="kind"/>, written over the parameters above.</summary>
    public static string Statement(CacheProofKind kind)
    {
        var sql = new StringBuilder();
        sql.AppendLine("SET NOCOUNT ON;");
        sql.AppendLine(CultureInfo.InvariantCulture, $"DECLARE @sequence int = (SELECT v.[Sequence] FROM [osdu].[CacheVersion] v WHERE v.[Scope] = {ScopeParameter} AND v.[Version] = {VersionParameter} AND v.[PrunedUtc] IS NULL);");
        sql.AppendLine("IF @sequence IS NULL");
        sql.AppendLine(CultureInfo.InvariantCulture, $"    THROW {GoneError}, N'The partition''s cache holds no such version, or its retention pruned its records, so nothing can be shown of it. Check the attribute again to read the current version.', 1;");
        sql.Append(kind switch
        {
            CacheProofKind.Ids => Ids(),
            CacheProofKind.Values => Values(),
            CacheProofKind.Records => Records(),
            CacheProofKind.Types => Types(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a kind of cache proof."),
        });
        return sql.ToString();
    }

    /// <summary>The parameters of <paramref name="query"/>, in the order a script declares them: name, SQL type and value.</summary>
    public static IReadOnlyList<(string Name, string SqlType, object Value)> Parameters(CacheProofQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var looked = query.Kind == CacheProofKind.Ids
            ? query.Looked.Select(id => (object)new { value = id, record = Record(id), code = Code(id) }).ToList()
            : query.Looked.Select(value => (object)new { value }).ToList();
        return
        [
            (ScopeParameter, "nvarchar(200)", query.Scope),
            (VersionParameter, "nvarchar(64)", query.Version),
            (TypesParameter, "nvarchar(max)", JsonSerializer.Serialize(query.Types)),
            (FieldsParameter, "nvarchar(max)", JsonSerializer.Serialize(query.Fields)),
            (LookedParameter, "nvarchar(max)", JsonSerializer.Serialize(looked)),
            (RowsParameter, "bigint", (long)query.Limit),
        ];
    }

    /// <summary>The statement as a person runs it: each parameter declared with its value, then the statement.</summary>
    /// <param name="query">What is read.</param>
    /// <param name="rows">The most rows the script lists, which the person may change.</param>
    public static string Script(CacheProofQuery query, long rows)
    {
        ArgumentNullException.ThrowIfNull(query);
        var script = new StringBuilder();
        foreach (var (name, type, value) in Parameters(query))
        {
            var literal = name == RowsParameter
                ? rows.ToString(CultureInfo.InvariantCulture)
                : IngestionSql.Literal((string)value);
            script.Append("DECLARE ").Append(name).Append(' ').Append(type).Append(" = ").Append(literal).AppendLine(";");
        }

        script.AppendLine();
        script.Append(Statement(query.Kind));
        return script.ToString();
    }

    /// <summary>An id as a record is kept under: without the colon, and the version, it may end in.</summary>
    internal static string Record(string id)
        => CachedReferences.Named(id) is { } named ? named.Id : id.TrimEnd(':');

    /// <summary>The code an id ends with (<c>ft</c> of <c>dev:reference-data--UnitOfMeasure:ft:</c>), decoded.</summary>
    internal static string Code(string id)
    {
        var parts = Record(id).Split(':');
        if (parts.Length < 3)
        {
            return id;
        }

        var code = string.Join(':', parts.Skip(2));
        try
        {
            return Uri.UnescapeDataString(code);
        }
        catch (UriFormatException)
        {
            return code;
        }
    }

    /// <summary>The records of the types the version holds; <paramref name="narrow"/>, when given, a further condition on <c>i</c>.</summary>
    private static string Items(string columns, string? narrow) => $"""
        [items] AS (
            SELECT {columns}
            FROM [osdu].[CacheItem] i
            WHERE i.[Scope] = {ScopeParameter}
              AND i.[TypeName] IN (SELECT t.[value] FROM OPENJSON({TypesParameter}) WITH ([value] nvarchar(200) '$') t)
              AND i.[FromSequence] <= @sequence AND (i.[ToSequence] IS NULL OR i.[ToSequence] > @sequence){(narrow is null ? string.Empty : "\n      AND " + narrow)})
        """;

    /// <summary>Whether the item <c>i</c> holds <paramref name="value"/> as a whole value of any field, ignoring case.</summary>
    private static string Holds(string value)
        => $"CHARINDEX(NCHAR(10) + {value} COLLATE {Caseless} + NCHAR(10), NCHAR(10) + i.[Terms] COLLATE {Caseless} + NCHAR(10)) > 0";

    private static string Ids() => $"""
        -- One row per id looked for: how many records of the types hold exactly that id (0 proves the cache has none), and
        -- the records whose id differs only by case or that hold the code the id ends with, which is what to look at instead.
        WITH [looked] AS (
            SELECT l.[value], l.[record], l.[code]
            FROM OPENJSON({LookedParameter}) WITH ([value] nvarchar(1024) '$.value', [record] nvarchar(1024) '$.record', [code] nvarchar(1024) '$.code') l),
        {Items("i.[RecordId], i.[Terms]", $"EXISTS (SELECT 1 FROM [looked] l WHERE i.[RecordId] COLLATE {Caseless} = l.[record] COLLATE {Caseless} OR {Holds("l.[code]")})")}
        SELECT TOP ({RowsParameter})
            l.[value] AS [id looked for],
            (SELECT COUNT_BIG(*) FROM [items] i WHERE i.[RecordId] = l.[record] COLLATE {Exact}) AS [records with this id],
            (SELECT STRING_AGG(s.[RecordId], N', ') WITHIN GROUP (ORDER BY s.[RecordId])
             FROM (SELECT TOP (5) i.[RecordId] FROM [items] i
                   WHERE i.[RecordId] <> l.[record] COLLATE {Exact}
                     AND (i.[RecordId] COLLATE {Caseless} = l.[record] COLLATE {Caseless} OR {Holds("l.[code]")})
                   ORDER BY i.[RecordId]) s) AS [records like it]
        FROM [looked] l
        ORDER BY l.[value] COLLATE {Exact};
        """;

    private static string Values() => $"""
        -- One row per value looked for: how many records of the types hold it at the fields compared, exactly (0 proves the
        -- cache has none) and once case is ignored, and the first records holding it in any field, ignoring case.
        WITH [looked] AS (
            SELECT l.[value] FROM OPENJSON({LookedParameter}) WITH ([value] nvarchar(4000) '$.value') l),
        [fields] AS (
            SELECT f.[value] AS [field] FROM OPENJSON({FieldsParameter}) WITH ([value] nvarchar(400) '$') f),
        {Items("i.[RecordId], i.[FieldsJson], i.[Terms]", $"EXISTS (SELECT 1 FROM [looked] l WHERE {Holds("l.[value]")} OR i.[RecordId] COLLATE {Caseless} = l.[value] COLLATE {Caseless} OR RIGHT(i.[RecordId], LEN(l.[value]) + 1) COLLATE {Caseless} = (N':' + l.[value]) COLLATE {Caseless})")},
        [held] AS (
            -- What the records hold at the fields compared: a field holding a list gives each of its values. Compared by id, a
            -- record is found by its id and by the code its id ends with.
            SELECT i.[RecordId], LTRIM(RTRIM(COALESCE(e.[value], j.[value]))) AS [term]
            FROM [items] i
            CROSS APPLY OPENJSON(i.[FieldsJson]) j
            OUTER APPLY (SELECT x.[value] FROM OPENJSON(CASE WHEN j.[type] = 4 THEN j.[value] END) x WHERE x.[type] IN (1, 2, 3)) e
            WHERE j.[key] COLLATE {Caseless} IN (SELECT f.[field] COLLATE {Caseless} FROM [fields] f)
              AND (j.[type] IN (1, 2, 3) OR e.[value] IS NOT NULL)
            UNION ALL
            SELECT i.[RecordId], t.[term]
            FROM [items] i
            CROSS APPLY (VALUES (i.[RecordId]), (SUBSTRING(i.[RecordId], CHARINDEX(N':', i.[RecordId], CHARINDEX(N':', i.[RecordId]) + 1) + 1, 1024))) t([term])
            WHERE EXISTS (SELECT 1 FROM [fields] f WHERE f.[field] COLLATE {Caseless} = N'id'))
        SELECT TOP ({RowsParameter})
            l.[value] AS [value looked for],
            (SELECT COUNT(DISTINCT h.[RecordId]) FROM [held] h WHERE h.[term] COLLATE {Exact} = l.[value] COLLATE {Exact}) AS [at the fields],
            (SELECT COUNT(DISTINCT h.[RecordId]) FROM [held] h WHERE h.[term] COLLATE {Caseless} = l.[value] COLLATE {Caseless}) AS [ignoring case],
            (SELECT STRING_AGG(s.[RecordId], N', ') WITHIN GROUP (ORDER BY s.[RecordId])
             FROM (SELECT TOP (5) i.[RecordId] FROM [items] i WHERE {Holds("l.[value]")} ORDER BY i.[RecordId]) s) AS [in any field]
        FROM [looked] l
        ORDER BY l.[value] COLLATE {Exact};
        """;

    private static string Records() => $"""
        -- The records named, with every field the cache holds of them.
        WITH [looked] AS (
            SELECT l.[value] FROM OPENJSON({LookedParameter}) WITH ([value] nvarchar(1024) '$.value') l),
        {Items("i.[TypeName], i.[RecordId], i.[FieldsJson]", $"i.[RecordId] IN (SELECT l.[value] COLLATE {Exact} FROM [looked] l)")}
        SELECT TOP ({RowsParameter}) i.[TypeName] AS [type], i.[RecordId] AS [record], i.[FieldsJson] AS [fields]
        FROM [items] i
        ORDER BY i.[RecordId];
        """;

    private static string Types() => $"""
        -- The types the version holds, with how many records each: one missing from the list is a type the cache does not hold.
        SELECT TOP ({RowsParameter}) t.[name] AS [type], t.[entityType] AS [entity type], t.[items] AS [records]
        FROM [osdu].[CacheVersion] v
        CROSS APPLY OPENJSON(v.[TypesJson]) WITH ([name] nvarchar(200) '$.name', [entityType] nvarchar(200) '$.entityType', [items] bigint '$.items') t
        WHERE v.[Scope] = {ScopeParameter} AND v.[Sequence] = @sequence
        ORDER BY t.[name];
        """;
}
