using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The estate's environments (dev, test and prod, each a deployment of its own) reach each control plane from the
/// deployment templates, so the GUI's title bar names the environment and opens the others
/// (sqlflow/docs/reference/concepts/environments.md). This test reads the Bicep template as committed: it takes them and
/// its compiled form, which a deployment sends, carries them too. The Terraform estate's own tests
/// (osdu/deploy/terraform/tests) check what it writes; Radix, compose and Kubernetes carry them as a comment to uncomment.
/// </summary>
public sealed class EnvironmentTemplateTests
{
    [Fact]
    public void TheBicepTemplate_TakesTheEnvironments_AndItsCompiledFormCarriesThem()
    {
        var bicep = Template("bicep", "main.bicep");
        Assert.Contains("param environments environmentsOptions?", bicep, StringComparison.Ordinal);
        Assert.Contains("  environmentSettings,", bicep, StringComparison.Ordinal);

        var json = Template("bicep", "main.json");
        Assert.Contains("\"environments\": {", json, StringComparison.Ordinal);
        foreach (var setting in new[]
        {
            "ControlPlane__Environments__Current",
            "ControlPlane__Environments__Links__{0}__Name",
            "ControlPlane__Environments__Links__{0}__Url",
            "ControlPlane__Environments__Links__{0}__Description",
        })
        {
            Assert.Contains(setting, json, StringComparison.Ordinal);
        }
    }

    private static string Template(params string[] path)
        => File.ReadAllText(RepositoryRoot.Combine(["osdu", "deploy", .. path]));
}
