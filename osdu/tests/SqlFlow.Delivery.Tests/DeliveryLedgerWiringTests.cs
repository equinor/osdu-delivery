using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How a host reaches the ledger (<see cref="DeliveryServices.AddDeliveryLedger(IServiceCollection, Func{IServiceProvider, Func{OsduDbContext}?})"/>):
/// the services that maintain the ledger's own tables (the record identity and problem backfills) ask for it as itself,
/// everything else by its contract, and both are the one ledger the host opened.
/// </summary>
public sealed class DeliveryLedgerWiringTests
{
    [Fact]
    public async Task The_contract_and_the_ledger_itself_are_one_ledger()
    {
        // The ledger opens a context per operation; resolving it opens none.
        var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddDeliveryLedger(_ => () => throw new InvalidOperationException("No operation runs in this test."));
        await using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<OsduLedger>(), provider.GetRequiredService<ILedger>());
    }

    [Fact]
    public async Task A_host_without_the_module_database_refuses_the_ledger_either_way()
    {
        var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddDeliveryLedger(_ => null);
        await using var provider = services.BuildServiceProvider();

        Assert.Equal(DeliveryServices.NoLedgerMessage, Assert.Throws<DeliveryException>(() => provider.GetRequiredService<OsduLedger>()).Message);
        Assert.Equal(DeliveryServices.NoLedgerMessage, Assert.Throws<DeliveryException>(() => provider.GetRequiredService<ILedger>()).Message);
    }
}
