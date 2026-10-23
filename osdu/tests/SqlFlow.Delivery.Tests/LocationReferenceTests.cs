using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A file location a flow declares is written out: every payload root refuses a <c>${...}</c> reference, as a retrieval
/// flow's target does, with the same message, and what varies per run is a declared <c>{parameter}</c>. The work root
/// alone may open with a reference, which the run resolves, so the work batches go to the deployment's storage.
/// </summary>
public class LocationReferenceTests
{
    private const string Flow = """
        flowType: delivery
        name: welllog-delivery
        partitions: [dev]
        parameters:
          region: { required: true }
        source:
          connection: ${env:OSDU_DATA_DB}
          record: { object: OsduData.silver.WellLog, key: [log_id] }
          payloads:
            las: { root: "las/{region}", locationColumn: las_folder, hashColumn: las_hash }
          lastModified: update_date
          work: work/{region}
        render:
          mapping: WellLog@1.0.0
        target:
          endpoint: ${env:OSDU_URL}
          protocol: file
          protocolOptions: { payload: las }
        """;

    private static string With(string from, string to)
    {
        var yaml = Flow.ReplaceLineEndings("\n");
        var changed = yaml.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(yaml, changed);
        return changed;
    }

    private static string Refused(string yaml)
        => Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseSource(yaml, "flows/welllog.yaml")).Message;

    [Fact]
    public void Locations_written_out_with_declared_parameters_are_read()
        => Assert.Equal("welllog-delivery", new DeliveryDocumentLoader().ParseSource(Flow.ReplaceLineEndings("\n"), "flows/welllog.yaml").Name);

    /// <summary>
    /// The work root may open with a reference, which the run resolves with its own values as it opens, so the work batches
    /// go to the storage the deployment configures and every node of a fan-out reaches them.
    /// </summary>
    [Fact]
    public void The_work_root_may_open_with_a_reference_the_run_resolves()
    {
        var source = new DeliveryDocumentLoader().ParseSource(With("work: work/{region}", "work: ${env:LANDING_URL}/.work/welllog/{region}"), "flows/welllog.yaml");

        Assert.Equal("${env:LANDING_URL}/.work/welllog/{region}", Assert.Single(source.Interfaces).Source.Work);
    }

    [Theory]
    [InlineData("work/${env:WORK_ROOT}/{region}")]
    [InlineData("${env:LANDING_URL}/${env:WORK_FOLDER}/{region}")]
    public void A_reference_elsewhere_in_the_work_root_is_refused(string work)
    {
        var message = Refused(With("work: work/{region}", "work: " + work));

        Assert.Equal(
            $"flows/welllog.yaml: source.work '{work}' names a reference other than at its start. A work location that names one is the reference's value with a path after it, such as ${{env:LANDING_URL}}/.work/welllog/{{logSource}}.",
            message);
    }

    [Fact]
    public void A_token_after_the_work_root_s_reference_is_a_declared_parameter()
    {
        var message = Refused(With("work: work/{region}", "work: ${env:LANDING_URL}/.work/{basin}"));

        Assert.Equal("flows/welllog.yaml: source.work uses '{basin}', which is not declared under parameters.", message);
    }

    [Fact]
    public void A_reference_in_a_payload_root_is_refused()
    {
        var message = Refused(With("root: \"las/{region}\"", "root: \"${keyvault:estate/las-root}\""));

        Assert.StartsWith("flows/welllog.yaml: source.payloads.las.root '${keyvault:estate/las-root}' holds a ${...} reference", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reference_in_an_interfaces_files_root_is_refused_naming_the_interface()
    {
        const string Interfaces = """
            flowType: delivery
            name: welllogs
            partitions: [dev]
            source:
              connection: ${env:OSDU_DATA_DB}
              work: work/welllogs
            target:
              endpoint: ${env:OSDU_URL}
            interfaces:
              logs:
                record: { object: OsduData.silver.WellLog, key: [log_id] }
                lastModified: update_date
                files: { root: "${env:LAS_ROOT}", locationColumn: las_folder, hashColumn: las_hash }
                mapping: WellLog@1.0.0
            """;

        var message = Refused(Interfaces.ReplaceLineEndings("\n"));

        Assert.StartsWith("flows/welllog.yaml: interfaces.logs.files.root '${env:LAS_ROOT}' holds a ${...} reference", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_retrieval_target_is_refused_through_the_same_check()
    {
        const string Retrieval = """
            flowType: retrieval
            name: welllog-retrieval
            source:
              endpoint: ${env:OSDU_URL}
              headers: { data-partition-id: dev }
              kind: "osdu:wks:work-product-component--WellLog:1.*.*"
            target:
              location: ${env:EXPORT_ROOT}/welllogs
            """;

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseRetrieval(Retrieval.ReplaceLineEndings("\n"), "flows/retrieval.yaml"));

        Assert.Equal(
            "flows/retrieval.yaml: target.location '${env:EXPORT_ROOT}/welllogs' holds a ${...} reference, and a location is not resolved from one: write the path or storage URI itself, and vary it per run or environment with a {parameter} token declared under parameters.",
            refused.Message);
    }
}
