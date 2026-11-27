using SqlFlow.Catalog;
using Xunit;

namespace SqlFlow.Tests.Catalog;

/// <summary>
/// The comparison behind the catalog's own schema history: two reads of one object's columns, set side by side column by
/// column, and the way back from the columns an object has now to the ones it had before a run of changes. Pins what
/// counts as a change (a type, a nullability, a place among the shared columns) and what does not (a name's case, a
/// type's spacing, a column pushed along by another being added).
/// </summary>
public sealed class SchemaColumnDiffTests
{
    private static SchemaColumnState Col(int ordinal, string name, string? type, bool nullable = true)
        => new(name, type, nullable, ordinal);

    [Fact]
    public void Compare_TheSameColumns_FindsNothing_WhateverTheCaseOrSpacing()
    {
        var before = new[] { Col(1, "Id", "int", false), Col(2, "Amount", "decimal(18, 2)") };
        var after = new[] { Col(1, "ID", "INT", false), Col(2, "amount", "DECIMAL(18,2)") };

        Assert.Empty(SchemaColumnDiff.Compare(before, after));
    }

    [Fact]
    public void Compare_NamesEveryColumnAddedDroppedOrRetyped_WithBothSides()
    {
        var before = new[] { Col(1, "Id", "int", false), Col(2, "Name", "nvarchar(50)"), Col(3, "Legacy", "varchar(10)") };
        var after = new[] { Col(1, "Id", "int", false), Col(2, "Name", "nvarchar(100)", false), Col(3, "Amount", "decimal(18,2)") };

        var changes = SchemaColumnDiff.Compare(before, after);

        Assert.Collection(changes,
            name =>
            {
                Assert.Equal(("Name", SchemaChangeKinds.Changed), (name.Name, name.ChangeType));
                Assert.Equal(("nvarchar(50)", "nvarchar(100)"), (name.FromType, name.ToType));
                Assert.Equal((true, false), (name.FromNullable!.Value, name.ToNullable!.Value));
                Assert.Equal((2, 2), (name.FromOrdinal!.Value, name.ToOrdinal!.Value));
            },
            amount =>
            {
                Assert.Equal(("Amount", SchemaChangeKinds.Added), (amount.Name, amount.ChangeType));
                Assert.Null(amount.FromType);
                Assert.Equal("decimal(18,2)", amount.ToType);
                Assert.Equal(3, amount.ToOrdinal);
            },
            legacy =>
            {
                Assert.Equal(("Legacy", SchemaChangeKinds.Deleted), (legacy.Name, legacy.ChangeType));
                Assert.Equal("varchar(10)", legacy.FromType);
                Assert.Null(legacy.ToType);
                Assert.Equal(3, legacy.FromOrdinal);
            });
    }

    [Fact]
    public void Compare_AColumnAddedInTheMiddle_DoesNotMoveTheOnesAfterIt()
    {
        var before = new[] { Col(1, "Id", "int"), Col(2, "Name", "nvarchar(50)") };
        var after = new[] { Col(1, "Id", "int"), Col(2, "Code", "varchar(5)"), Col(3, "Name", "nvarchar(50)") };

        var change = Assert.Single(SchemaColumnDiff.Compare(before, after));
        Assert.Equal(("Code", SchemaChangeKinds.Added), (change.Name, change.ChangeType));
    }

    [Fact]
    public void Compare_AColumnPutElsewhere_IsChanged_WithItsPlaces()
    {
        var before = new[] { Col(1, "Id", "int"), Col(2, "Name", "nvarchar(50)"), Col(3, "Code", "varchar(5)") };
        var after = new[] { Col(1, "Code", "varchar(5)"), Col(2, "Id", "int"), Col(3, "Name", "nvarchar(50)") };

        var moved = SchemaColumnDiff.Compare(before, after);

        Assert.Equal(["Code", "Id", "Name"], moved.Select(c => c.Name));
        Assert.All(moved, c => Assert.Equal(SchemaChangeKinds.Changed, c.ChangeType));
        var code = moved[0];
        Assert.Equal((3, 1), (code.FromOrdinal!.Value, code.ToOrdinal!.Value));
    }

    [Fact]
    public void Compare_ATypeOneReadDidNotGive_IsNoChange()
    {
        var before = new[] { Col(1, "Id", null) };
        var after = new[] { Col(1, "Id", "int") };

        Assert.Empty(SchemaColumnDiff.Compare(before, after));
    }

    [Fact]
    public void AllAdded_ListsEveryColumnOnce_InOrder()
    {
        var added = SchemaColumnDiff.AllAdded([Col(2, "Name", "nvarchar(50)"), Col(1, "Id", "int", false), Col(3, "ID", "bigint")]);

        Assert.Equal(["Id", "Name"], added.Select(c => c.Name));
        Assert.All(added, c => Assert.Equal(SchemaChangeKinds.Added, c.ChangeType));
    }

    [Fact]
    public void Before_UndoesEachChange_NewestFirst()
    {
        var now = new[] { Col(1, "Id", "int", false), Col(2, "Name", "nvarchar(200)"), Col(3, "Region", "varchar(20)") };
        var newestFirst = new[]
        {
            Change(SchemaChangeKinds.Changed,
                Moved(SchemaChangeKinds.Changed, "Name", "nvarchar(100)", "nvarchar(200)", 2, 2),
                Moved(SchemaChangeKinds.Added, "Region", null, "varchar(20)", null, 3)),
            Change(SchemaChangeKinds.Changed,
                Moved(SchemaChangeKinds.Changed, "Name", "nvarchar(50)", "nvarchar(100)", 2, 2),
                Moved(SchemaChangeKinds.Deleted, "Legacy", "char(1)", null, 3, null)),
        };

        var before = SchemaColumnDiff.Before(now, newestFirst);

        Assert.NotNull(before);
        Assert.Equal(
            ["[Id] int NOT NULL", "[Name] nvarchar(50) NULL", "[Legacy] char(1) NULL"],
            SchemaColumnDiff.Render(before)!.Split('\n'));

        // Only the newer change is in reach: the column dropped before it is still gone.
        var lastWeek = SchemaColumnDiff.Before(now, newestFirst.Take(1));
        Assert.Equal(["Id", "Name"], lastWeek!.Select(c => c.Name));
        Assert.Equal("nvarchar(100)", lastWeek![1].DataType);
    }

    [Fact]
    public void Before_AnObjectAddedInReach_DidNotExist()
    {
        var now = new[] { Col(1, "Id", "int") };
        var newestFirst = new[] { Change(SchemaChangeKinds.Added, Moved(SchemaChangeKinds.Added, "Id", null, "int", null, 1)) };

        Assert.Null(SchemaColumnDiff.Before(now, newestFirst));
    }

    [Fact]
    public void Render_QuotesTheName_AndSaysWhatItDoesNotKnow()
    {
        Assert.Equal("[a]]b] (type not read) NOT NULL", SchemaColumnDiff.Render([Col(1, "a]b", null, false)]));
        Assert.Null(SchemaColumnDiff.Render([]));
    }

    [Fact]
    public void LineTally_CountsARetypedColumn_AsOneLineEachWay()
    {
        var before = "[Id] int NOT NULL\n[Name] nvarchar(50) NULL";
        var after = "[Id] int NOT NULL\n[Name] nvarchar(100) NULL\n[Region] varchar(20) NULL";

        Assert.Equal((2, 1), SchemaColumnDiff.LineTally(before, after));
        Assert.Equal((0, 2), SchemaColumnDiff.LineTally(before, null));
        Assert.Equal((3, 0), SchemaColumnDiff.LineTally(null, after));
    }

    private static CatalogSchemaChange Change(string changeType, params CatalogSchemaChangeColumn[] columns) => new()
    {
        ChangeType = changeType,
        Origin = SchemaChangeOrigins.Sync,
        Database = "Sink",
        Category = "Table",
        Name = "T",
        Columns = [.. columns],
    };

    private static CatalogSchemaChangeColumn Moved(
        string changeType, string name, string? fromType, string? toType, int? fromOrdinal, int? toOrdinal) => new()
    {
        Name = name,
        ChangeType = changeType,
        FromType = fromType,
        ToType = toType,
        FromNullable = fromType is null ? null : true,
        ToNullable = toType is null ? null : true,
        FromOrdinal = fromOrdinal,
        ToOrdinal = toOrdinal,
    };
}
