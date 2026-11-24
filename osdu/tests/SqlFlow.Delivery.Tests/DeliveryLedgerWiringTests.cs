using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How a host reaches the ledger (<see cref="DeliveryServices.AddDeliveryLedger(IServiceCollection, Func{IServiceProvider, Func{OsduDbContext}?})"/>):
/// the services that maintain the ledger's own tables (the record identity and problem backfills) ask for it as itself,
/// everything else by its contract, by the part of it they use, or by a store beside it, and all of them are the one
/// ledger the host opened.
/// </summary>
public sealed class DeliveryLedgerWiringTests
{
    /// <summary>Every part <see cref="ILedger"/> is made of, which a service may ask for on its own.</summary>
    public static TheoryData<Type> Parts()
    {
        var parts = new TheoryData<Type>();
        foreach (var part in typeof(ILedger).GetInterfaces())
        {
            parts.Add(part);
        }

        return parts;
    }

    [Fact]
    public async Task The_contract_and_the_ledger_itself_are_one_ledger()
    {
        // The ledger opens a context per operation; resolving it opens none.
        await using var provider = Provider(_ => () => throw new InvalidOperationException("No operation runs in this test."));

        Assert.Same(provider.GetRequiredService<OsduLedger>(), provider.GetRequiredService<ILedger>());
    }

    [Theory]
    [MemberData(nameof(Parts))]
    public async Task Each_part_of_the_contract_is_the_one_ledger(Type part)
    {
        await using var provider = Provider(_ => () => throw new InvalidOperationException("No operation runs in this test."));

        Assert.Same(provider.GetRequiredService<OsduLedger>(), provider.GetRequiredService(part));
    }

    [Fact]
    public async Task Each_store_beside_the_ledger_is_the_one_ledger_s()
    {
        await using var provider = Provider(_ => () => throw new InvalidOperationException("No operation runs in this test."));
        var ledger = provider.GetRequiredService<OsduLedger>();

        Assert.Same(ledger.Inventories, provider.GetRequiredService<InventoryStore>());
        Assert.Same(ledger.Downloads, provider.GetRequiredService<DownloadStore>());
        Assert.Same(ledger.Assertions, provider.GetRequiredService<AssertionStore>());
        Assert.Same(ledger.Dimensions, provider.GetRequiredService<DimensionStore>());
    }

    [Fact]
    public void Every_operation_of_the_contract_is_on_one_part_of_it()
    {
        // The whole ledger adds only the stores beside it; an operation declared on it, or on two parts, would be one a
        // caller of a part could not reach, or could reach two ways.
        Assert.All(typeof(ILedger).GetMethods(), m => Assert.True(m.IsSpecialName, $"{m} is declared on ILedger, not on a part of it"));
        var owners = typeof(ILedger).GetInterfaces().SelectMany(p => p.GetMethods().Select(m => (Part: p.Name, Member: m.ToString()!)))
            .GroupBy(x => x.Member)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.Part))}");
        Assert.Empty(owners);
    }

    [Fact]
    public async Task A_host_without_the_module_database_refuses_the_ledger_either_way()
    {
        await using var provider = Provider(_ => null);

        Assert.Equal(DeliveryServices.NoLedgerMessage, Assert.Throws<DeliveryException>(() => provider.GetRequiredService<OsduLedger>()).Message);
        Assert.Equal(DeliveryServices.NoLedgerMessage, Assert.Throws<DeliveryException>(() => provider.GetRequiredService<ILedger>()).Message);
        Assert.Equal(DeliveryServices.NoLedgerMessage, Assert.Throws<DeliveryException>(() => provider.GetRequiredService<ILedgerRecords>()).Message);
        Assert.Equal(DeliveryServices.NoLedgerMessage, Assert.Throws<DeliveryException>(() => provider.GetRequiredService<DimensionStore>()).Message);
    }

    private static ServiceProvider Provider(Func<IServiceProvider, Func<OsduDbContext>?> contexts)
        => new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddDeliveryLedger(contexts)
            .BuildServiceProvider();
}
