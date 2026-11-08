using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine;
using SqlFlow.Execution;
using SqlFlow.Lineage.Collection;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// OSDU Delivery's schedules run every flow of a wave at once unless they set <c>maxConcurrency</c>: the module gives
/// its product the unbounded default, so a fire with more work than one node runs asks for more nodes. A schedule that
/// sets the key keeps it, and a host that registered its own default before the module keeps that.
/// </summary>
public sealed class ScheduleFireWidthTests
{
    [Fact]
    public void TheModule_GivesItsSchedulesTheUnboundedDefault()
    {
        using var provider = new ServiceCollection().AddLogging().AddSqlFlowEngine().AddDeliveryKind().BuildServiceProvider();

        Assert.Same(ScheduleFireDefaults.Unbounded, provider.GetRequiredService<YamlDocumentLoader>().FireDefaults);
    }

    [Fact]
    public void AHostsOwnDefault_RegisteredFirst_IsKept()
    {
        var own = new ScheduleFireDefaults(12);
        using var provider = new ServiceCollection().AddLogging().AddSingleton(own).AddSqlFlowEngine().AddDeliveryKind()
            .BuildServiceProvider();

        Assert.Same(own, provider.GetRequiredService<YamlDocumentLoader>().FireDefaults);
    }

    [Fact]
    public void AnEstatesSchedules_AreUnboundedUnlessTheySetABound()
    {
        using var provider = new ServiceCollection().AddLogging().AddSqlFlowEngine().AddDeliveryKind().BuildServiceProvider();
        var root = Directory.CreateTempSubdirectory("osdu-fire-width-").FullName;
        try
        {
            File.WriteAllText(
                Path.Combine(root, "schedules.yaml"),
                """
                schedules:
                  cache-osdu:
                    cron: "30 0 * * *"
                    timezone: Europe/Oslo
                  welllog:
                    cron: "0 3 * * *"
                    maxConcurrency: 4
                """);

            var collected = new FlowSetCollector(provider.GetRequiredService<YamlDocumentLoader>()).Collect(root);

            Assert.Null(Assert.Single(collected.Schedules, s => s.Name == "cache-osdu").Spec.MaxConcurrency);
            Assert.Equal(4, Assert.Single(collected.Schedules, s => s.Name == "welllog").Spec.MaxConcurrency);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
