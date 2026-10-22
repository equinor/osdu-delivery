using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What the sample and fixture mappings deliver for rows the suites know: each row rendered as a run in the sample partition
/// renders it, against the sample cache and a platform that holds the row's wellbore, and compared with the record the
/// suites keep for it (<see cref="RenderedRecord.Folder"/>). A change to a mapping, its template or the sample cache that
/// changes what a row delivers fails here, and the kept record is updated when the change is meant.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SampleRecordTests
{
    /// <summary>The values the samples deliver with: the partition, the owners and viewers every record carries, and its legal tag.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [RenderContext.DataPartitionParameter] = Samples.SamplePartition,
        ["aclOwner"] = "data.welldb.owners@dev.dataservices.energy",
        ["aclViewer"] = "data.welldb.viewers@dev.dataservices.energy",
        ["legalTag"] = "dev-osdu-default-legal",
    };

    /// <summary>
    /// Two sample logs: a sidetrack whose deep resistivity the well database gives no business value, and a gamma ray and
    /// resistivity log with a business value on every curve.
    /// </summary>
    [Theory]
    [InlineData("LOG-0003/1")]
    [InlineData("LOG-0005/1")]
    public async Task A_sample_log_renders_the_record_kept_for_it(string logId)
    {
        var log = SampleWellLogs.Logs().Single(l => l.LogId == logId);
        var result = await RenderAsync(
            WellLogVersions.CurrentMapping,
            SampleWellLogs.LogRow(log),
            new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> { ["curves"] = SampleWellLogs.CurveRows(log) },
            Wellbore(log.WellboreUwi));

        RenderedRecord.AssertIsKept(WellLogVersions.CurrentMapping, logId.Replace('/', '_'), result);
    }

    /// <summary>
    /// A sample survey: a sidetrack's trajectory with the three properties every sample survey's stations carry, its
    /// depths measured from its well's KB.
    /// </summary>
    [Fact]
    public async Task A_sample_survey_renders_the_record_kept_for_it()
    {
        var survey = SampleSurveys.Surveys().Single(s => s.SurveyId == "SRV-0003/1");
        var result = await RenderAsync(
            "WellboreTrajectory@1.3.0",
            SampleSurveys.SurveyRow(survey),
            new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> { ["stations"] = SampleSurveys.PropertyRows(survey) },
            Wellbore(survey.WellboreUwi));

        RenderedRecord.AssertIsKept("WellboreTrajectory@1.3.0", "SRV-0003_1", result);
    }

    /// <summary>A sample marker set: the formation tops picked in a wellbore, shallowest first.</summary>
    [Fact]
    public async Task A_sample_marker_set_renders_the_record_kept_for_it()
    {
        var set = SampleSurveys.MarkerSets().Single(m => m.MarkerSetId == "MKS-0001/1");
        var result = await RenderAsync(
            "WellboreMarkerSet@1.2.1",
            SampleSurveys.MarkerSetRow(set),
            new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> { ["markers"] = SampleSurveys.MarkerRows(set) },
            Wellbore(set.WellboreUwi));

        RenderedRecord.AssertIsKept("WellboreMarkerSet@1.2.1", "MKS-0001_1", result);
    }

    [Fact]
    public async Task A_wellbore_with_an_alias_renders_the_record_kept_for_it()
    {
        var result = await RenderAsync(
            "Wellbore@1.0.0",
            Row(("facility_name", " SAMPLE-WELLBORE-A "), ("facility_description", "Sample wellbore A"), ("facility_id", "srn:master-data/Wellbore:A")),
            new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> { ["aliases"] = [Row(("alias_name", "WB-A"))] });

        RenderedRecord.AssertIsKept("Wellbore@1.0.0", "wellbore-A", result);
    }

    [Fact]
    public async Task A_trajectory_with_three_station_properties_renders_the_record_kept_for_it()
    {
        var result = await RenderAsync(
            "WellboreTrajectory@1.0.0",
            Row(
                ("source_project", "PROJECT_A"),
                ("survey_id", "T-1001"),
                ("wellbore_uwi", "OSDU-DEV-1-A"),
                ("survey_name", " GYRO_2026 "),
                ("survey_type", "Gyro"),
                ("survey_version", "1"),
                ("top_md", "1000"),
                ("base_md", "1004"),
                ("elev_meas_ref", "23.5 M")),
            new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>>
            {
                ["stations"] =
                [
                    Row(("station_property", "MD"), ("property_type", "MD"), ("property_unit", "M")),
                    Row(("station_property", "INC"), ("property_type", "Inclination"), ("property_unit", "DEG")),
                    Row(("station_property", "AZI"), ("property_type", "AzimuthTN"), ("property_unit", "DEG")),
                ],
            },
            Wellbore("OSDU-DEV-1-A"));

        RenderedRecord.AssertIsKept("WellboreTrajectory@1.0.0", "T-1001", result);
    }

    [Fact]
    public async Task A_document_with_a_title_and_a_description_renders_the_record_kept_for_it()
    {
        var result = await RenderAsync(
            "Document@1.0.0",
            Row(("doc_id", "D-0001"), ("doc_title", " Well report Wellbore A-1 "), ("doc_description", "Geological observations over the logged interval.")));

        RenderedRecord.AssertIsKept("Document@1.0.0", "D-0001", result);
    }

    /// <summary>A platform that holds the wellbore named <paramref name="facilityName"/>, under the id the suites give it in the sample partition.</summary>
    private static (string Field, string Value, string Id) Wellbore(string facilityName)
        => ("data.FacilityName", facilityName, $"{Samples.SamplePartition}:master-data--Wellbore:{FixedRecordSearchFactory.WellboreId(facilityName)}");

    private static IReadOnlyDictionary<string, string?> Row(params (string Column, string? Value)[] values)
        => values.ToDictionary(v => v.Column, v => v.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Renders <paramref name="row"/> with <paramref name="reference"/> as a run in the sample partition does: the template the
    /// mapping pins, as the samples and the fixtures bundle it, the sample cache at its current version when the mapping reads the cache or searches, and a platform
    /// holding the records <paramref name="known"/> names, asked as a run asks it.
    /// </summary>
    private static async Task<RenderResult> RenderAsync(
        string reference,
        IReadOnlyDictionary<string, string?> row,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>>? datasets = null,
        params (string Field, string Value, string Id)[] known)
    {
        var mapping = new MappingCatalog(Samples.FixtureMappings, new DeliveryDocumentLoader()).Load(reference);
        var schema = Samples.SampleTemplate(mapping.Template.Kind);
        Assert.Equal(mapping.Template.Version, schema.Version);

        var references = ReferenceSnapshot.Empty;
        string? scope = null;
        if (mapping.CacheTypesRead().Count > 0 || mapping.Searches.Count > 0)
        {
            scope = Samples.SampleCacheScope;
            var version = await Samples.SampleCache.CurrentVersionAsync(scope);
            Assert.NotNull(version);
            references = await Samples.SampleCache.LoadAsync(scope, version);
            Assert.NotNull(references);
        }

        var context = new RenderContext
        {
            MappingReference = mapping.Reference,
            MappingFingerprint = mapping.Fingerprint,
            CacheScope = scope,
            CacheVersion = references.Version,
            SchemaSnapshotVersion = schema.Version,
            Parameters = Parameters,
            SystemProperties = mapping.Searches.Count > 0 ? SystemProperties.Pinned(references.SystemProperties) : [],
        };
        var searches = await RenderResolver.SearchesAsync(Samples.SampleTemplates, mapping, schema);
        var renderer = new MappingRenderer(mapping, schema, references, context, searches, new FixedRecordSearch(known));
        return await Samples.RenderSettledAsync(renderer, new SourceRecord
        {
            Row = SourceRow.FromStrings(row),
            Scopes = (datasets ?? new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>>()).ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<SourceRow>)kv.Value.Select(SourceRow.FromStrings).ToList(),
                StringComparer.OrdinalIgnoreCase),
        });
    }
}
