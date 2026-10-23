using SqlFlow.Delivery.Engine;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A fan-out's members read and write the submission's work batches on whichever node runs them, so a delivery fans out
/// only when its work location is one every node reaches (osdu/docs/reference/concepts/submissions.md, Many nodes).
/// </summary>
public sealed class FanOutWorkLocationTests
{
    [Theory]
    [InlineData("../.work/{logSource}")]
    [InlineData(".work/welllog")]
    [InlineData("work")]
    public void A_work_location_in_the_node_s_copy_of_the_repository_keeps_a_delivery_on_one_run(string work)
    {
        var reason = FlowRuntime.WorkStaysOnThisNode(work);

        Assert.Equal(
            $"source.work '{work}' is a folder in this node's copy of the repository, which no other node reads; write it on storage every node reaches, such as the deployment's (${{env:LANDING_URL}}/.work/...), to fan out",
            reason);
    }

    [Theory]
    [InlineData("abfss://work@account.dfs.core.windows.net/welllog/{logSource}")]
    [InlineData("https://account.blob.core.windows.net/work/welllog/{logSource}")]
    public void A_work_location_on_storage_lets_a_delivery_fan_out(string work)
        => Assert.Null(FlowRuntime.WorkStaysOnThisNode(work));

    [Fact]
    public void An_absolute_work_location_is_the_deployment_s_shared_storage_and_lets_a_delivery_fan_out()
        => Assert.Null(FlowRuntime.WorkStaysOnThisNode(Path.Combine(Path.GetTempPath(), "work", "{logSource}")));
}
