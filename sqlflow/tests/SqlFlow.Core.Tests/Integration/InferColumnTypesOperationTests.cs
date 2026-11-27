using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Execution;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The <c>inferColumnTypes</c> compute operation against a real SQL Server table of text columns, as a pre-ingestion flow
/// lands them: the engine-built executor profiles the columns it is asked for with the engine's own inference and answers
/// each with the type chosen and the counts behind it, and nothing of a value. A column of 0 and 1 is chosen as bit while
/// its counts show it is a number too; a code with leading zeros stays text; a column the table lacks is refused by name.
/// </summary>
[Trait("Category", "Integration")]
public sealed class InferColumnTypesOperationTests
{
    [SkippableFact]
    public async Task AProfile_ChoosesEachType_AndShowsTheCountsBehindIt()
    {
        var cs = IntegrationDb.Require();
        var table = "infer_op_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.ExecuteAsync(cs, $"""
            CREATE TABLE [dbo].[{table}] (
                [flag] nvarchar(max) NULL, [elevation] nvarchar(max) NULL, [code] nvarchar(max) NULL, [spud] nvarchar(max) NULL);
            INSERT INTO [dbo].[{table}] VALUES
                (N'0', N'10.5', N'007', N'2026-01-01'),
                (N'1', N'2128.7232', N'012', N'2025-12-31'),
                (N'1', N'3', N'099', N'2024-06-15'),
                (N'0', NULL, N'100', N'2023-02-28');
            """);

        using var provider = new ServiceCollection().AddLogging().AddSqlFlowEngine().BuildServiceProvider();
        var executor = provider.GetRequiredService<ComputeTaskExecutor>();
        try
        {
            var answer = await executor.ExecuteAsync(Payload(table, ["flag", "elevation", "code"]), CancellationToken.None);
            using var document = JsonDocument.Parse(answer);
            var root = document.RootElement;
            Assert.Equal(1000, root.GetProperty("sampleSize").GetInt32());
            var columns = root.GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("columnName").GetString()!);
            Assert.Equal(["code", "elevation", "flag"], columns.Keys.Order(StringComparer.Ordinal));

            var flag = columns["flag"];
            Assert.Equal("bit", flag.GetProperty("inferredType").GetString());
            var flagCounts = flag.GetProperty("evidence");
            Assert.Equal(4, flagCounts.GetProperty("nonNull").GetInt64());
            Assert.Equal(4, flagCounts.GetProperty("asBoolean").GetInt64());
            Assert.Equal(4, flagCounts.GetProperty("asInteger").GetInt64());
            Assert.Equal(4, flagCounts.GetProperty("asDecimal").GetInt64());

            var elevation = columns["elevation"];
            Assert.StartsWith("decimal(", elevation.GetProperty("inferredType").GetString(), StringComparison.Ordinal);
            var elevationCounts = elevation.GetProperty("evidence");
            Assert.Equal(3, elevationCounts.GetProperty("nonNull").GetInt64());
            Assert.Equal(4, elevationCounts.GetProperty("maxScale").GetInt32());
            Assert.Equal(4, elevationCounts.GetProperty("maxIntegerDigits").GetInt32());

            // A code's leading zeros are counted, so a reader sees that a number type would drop them.
            var code = columns["code"];
            Assert.Equal(3, code.GetProperty("evidence").GetProperty("withLeadingZeros").GetInt64());
            Assert.Equal(4, code.GetProperty("evidence").GetProperty("asInteger").GetInt64());

            // Counts and types only: no value of the table is in the answer.
            Assert.DoesNotContain("2128.7232", answer, StringComparison.Ordinal);
            Assert.DoesNotContain("007", answer, StringComparison.Ordinal);

            var missing = await Assert.ThrowsAsync<SqlFlowException>(
                () => executor.ExecuteAsync(Payload(table, ["depth"]), CancellationToken.None));
            Assert.Contains("'depth'", missing.Message, StringComparison.Ordinal);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    private static ComputeTaskPayload Payload(string table, IReadOnlyList<string> columns) => new()
    {
        Operation = ComputeOperations.InferColumnTypes,
        SourceRef = "${env:SQLFlowSinkConStr}",
        Schema = "dbo",
        ObjectName = table,
        Columns = columns,
        SampleSize = 1000,
    };
}
