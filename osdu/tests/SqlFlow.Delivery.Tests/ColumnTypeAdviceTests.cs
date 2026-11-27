using SqlFlow.Delivery.Rendering;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The type a source column should have for the property a mapping writes it into, by the render's own rules: a number
/// takes an exact number, refuses a bit or a date and reads text; a date takes a date and writes text as it is; a text
/// property takes text and loses a code's leading zeros from a number; and a column a step reads as text, or a lookup
/// compares, is text whatever the property.
/// </summary>
public sealed class ColumnTypeAdviceTests
{
    [Theory]
    [InlineData("decimal(38,18)", ColumnTypeAdvice.Verdicts.Fits)]
    [InlineData("int", ColumnTypeAdvice.Verdicts.Fits)]
    [InlineData("float", ColumnTypeAdvice.Verdicts.Approximate)]
    [InlineData("bit", ColumnTypeAdvice.Verdicts.Refused)]
    [InlineData("datetime2", ColumnTypeAdvice.Verdicts.Refused)]
    [InlineData("nvarchar(50)", ColumnTypeAdvice.Verdicts.Parsed)]
    [InlineData(null, ColumnTypeAdvice.Verdicts.Unknown)]
    public void ANumberProperty_TakesAnExactNumber(string? column, string verdict)
    {
        var advice = ColumnTypeAdvice.For("number", column, changedOnItsWay: false, matchedOnly: false);

        Assert.Equal(ColumnTypeAdvice.NumberType, advice.Recommended);
        Assert.Equal(verdict, advice.Verdict);
        Assert.Equal(ColumnTypeAdvice.ProfileCounts.AsDecimal, advice.ProfileCount);
    }

    [Fact]
    public void ABitColumn_ForANumber_SaysTheRecordIsHeld()
    {
        var advice = ColumnTypeAdvice.For("number", "bit", changedOnItsWay: false, matchedOnly: false);

        Assert.Contains("a boolean is not a number", advice.Reason, StringComparison.Ordinal);
        Assert.Contains("decimal(38,18)", advice.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bigint", ColumnTypeAdvice.Verdicts.Fits)]
    [InlineData("decimal(18,0)", ColumnTypeAdvice.Verdicts.Fits)]
    [InlineData("decimal(18,2)", ColumnTypeAdvice.Verdicts.Parsed)]
    [InlineData("bit", ColumnTypeAdvice.Verdicts.Refused)]
    public void AnIntegerProperty_TakesAWholeNumber(string column, string verdict)
    {
        var advice = ColumnTypeAdvice.For("integer", column, changedOnItsWay: false, matchedOnly: false);

        Assert.Equal((ColumnTypeAdvice.IntegerType, verdict), (advice.Recommended, advice.Verdict));
        Assert.Equal(ColumnTypeAdvice.ProfileCounts.AsInteger, advice.ProfileCount);
    }

    [Theory]
    [InlineData("date-time", "datetime2", ColumnTypeAdvice.DateTimeType, ColumnTypeAdvice.Verdicts.Fits)]
    [InlineData("date-time", "varchar(30)", ColumnTypeAdvice.DateTimeType, ColumnTypeAdvice.Verdicts.Parsed)]
    [InlineData("date", "date", ColumnTypeAdvice.DateType, ColumnTypeAdvice.Verdicts.Fits)]
    [InlineData("date", "int", ColumnTypeAdvice.DateType, ColumnTypeAdvice.Verdicts.Refused)]
    public void ADateProperty_TakesADate(string property, string column, string recommended, string verdict)
    {
        var advice = ColumnTypeAdvice.For(property, column, changedOnItsWay: false, matchedOnly: false);

        Assert.Equal((recommended, verdict), (advice.Recommended, advice.Verdict));
    }

    [Theory]
    [InlineData("string", "nvarchar(200)", ColumnTypeAdvice.Verdicts.Fits)]
    [InlineData("reference", "int", ColumnTypeAdvice.Verdicts.Approximate)]
    [InlineData("string", "int", ColumnTypeAdvice.Verdicts.Parsed)]
    public void ATextProperty_TakesText(string property, string column, string verdict)
    {
        var advice = ColumnTypeAdvice.For(property, column, changedOnItsWay: false, matchedOnly: false);

        Assert.Equal((ColumnTypeAdvice.TextType, verdict), (advice.Recommended, advice.Verdict));
        Assert.Null(advice.ProfileCount);
    }

    [Fact]
    public void AColumnAStepReadsAsText_OrALookupCompares_IsText_WhateverTheProperty()
    {
        var trimmed = ColumnTypeAdvice.For("number", "nvarchar(20)", changedOnItsWay: true, matchedOnly: false);
        var key = ColumnTypeAdvice.For("number", "int", changedOnItsWay: false, matchedOnly: true);

        Assert.Equal((ColumnTypeAdvice.TextType, ColumnTypeAdvice.Verdicts.Fits), (trimmed.Recommended, trimmed.Verdict));
        Assert.Equal((ColumnTypeAdvice.TextType, ColumnTypeAdvice.Verdicts.Approximate), (key.Recommended, key.Verdict));
        Assert.Contains("leading zeros", key.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ABooleanProperty_TakesABit()
    {
        Assert.Equal(ColumnTypeAdvice.Verdicts.Fits, ColumnTypeAdvice.For("boolean", "bit", false, false).Verdict);
        Assert.Equal(ColumnTypeAdvice.Verdicts.Parsed, ColumnTypeAdvice.For("boolean", "nvarchar(5)", false, false).Verdict);
    }
}
