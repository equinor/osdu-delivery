using System.Globalization;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// An idle node is compute paid for nothing, and a delivery split over many nodes leaves one per part behind. The
/// worker templates therefore take an idle pool to zero a minute after its last work, where the platforms wait five,
/// and have the scaler read the target several times inside that minute, so one reading never takes a pool to zero.
/// These tests read the templates as committed, so a change that lets the platform's default back in fails here.
/// </summary>
public sealed class WorkerScaleInTemplateTests
{
    [Fact]
    public void TheContainerAppsWorker_GoesToZero_AMinuteAfterItsLastWork()
    {
        var bicep = File.ReadAllText(RepositoryRoot.Combine("osdu", "deploy", "bicep", "worker.bicep"));

        Assert.Contains("minReplicas: 0", bicep, StringComparison.Ordinal);
        Assert.Equal(60, Setting(bicep, "cooldownPeriod:"));
        Assert.True(Setting(bicep, "pollingInterval:") * 4 <= 60, "the scaler reads the target fewer than four times inside the cool-down");

        // The compiled templates are what a deployment sends: they carry the same values.
        foreach (var compiled in new[] { "worker.json", "main.json" })
        {
            var json = File.ReadAllText(RepositoryRoot.Combine("osdu", "deploy", "bicep", compiled));
            Assert.Contains("\"cooldownPeriod\": 60", json, StringComparison.Ordinal);
            Assert.Contains("\"pollingInterval\": 15", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheKubernetesWorkerPool_GoesToZero_AMinuteAfterItsLastWork()
    {
        var yaml = File.ReadAllText(RepositoryRoot.Combine("osdu", "deploy", "k8s", "worker-pool.yaml"));

        Assert.Contains("minReplicaCount: 0", yaml, StringComparison.Ordinal);
        Assert.Equal(60, Setting(yaml, "cooldownPeriod:"));
        Assert.True(Setting(yaml, "pollingInterval:") * 4 <= 60, "the scaler reads the target fewer than four times inside the cool-down");
    }

    /// <summary>The number a template gives one setting, which it states exactly once.</summary>
    private static int Setting(string template, string name)
    {
        var line = template.Split('\n').Single(l => l.TrimStart().StartsWith(name, StringComparison.Ordinal));
        var value = line.TrimStart()[name.Length..].Split('#')[0].Trim();
        return int.Parse(value, CultureInfo.InvariantCulture);
    }
}
