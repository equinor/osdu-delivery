using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using Xunit;
using Xunit.Abstractions;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A dimension flow of a production estate's shape, built at scale on SQL Server against the stand-in search: every SMDA
/// wellbore keyed by its id, with attributes of one step and attributes read through its well, field, country, basin and
/// technical assurance type, the wells beside it, and the view joining them. Run with <c>SQLFLOW_DIMENSION_SCALE</c> set to
/// a number of wellbores to measure a build of that size; it builds 20,000 otherwise.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DimensionBuildScaleTests : IDisposable
{
    /// <summary>The variable naming how many wellbores the estate holds.</summary>
    private const string ScaleVariable = "SQLFLOW_DIMENSION_SCALE";

    /// <summary>The variable <c>target.connection</c> names, set to the test database's connection string for the suite's runs.</summary>
    private const string TargetVariable = "OSDU_SCALE_TESTS_DB";

    private const string Wellbore = "osdu:wks:master-data--Wellbore:1.3.0";
    private const string Well = "osdu:wks:master-data--Well:1.2.0";
    private const string Field = "osdu:wks:master-data--Field:1.0.0";
    private const string Entity = "osdu:wks:master-data--GeoPoliticalEntity:1.0.0";
    private const string EntityType = "osdu:wks:reference-data--GeoPoliticalEntityType:1.0.0";
    private const string Basin = "osdu:wks:master-data--Basin:1.2.0";
    private const string Assurance = "osdu:wks:reference-data--TechnicalAssuranceType:1.0.0";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly FakeDimensionPlatform _platform = new() { IndexIds = true };
    private readonly ITestOutputHelper _output;

    public DimensionBuildScaleTests(ITestOutputHelper output)
    {
        _output = output;
        Environment.SetEnvironmentVariable(TargetVariable, _db.ConnectionString);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TargetVariable, null);
        _db.Dispose();
        _platform.Dispose();
    }

    /// <summary>The variable naming how long, in milliseconds, the stand-in search takes to answer a request.</summary>
    private const string LatencyVariable = "SQLFLOW_DIMENSION_SCALE_LATENCY_MS";

    private static int Wellbores => Setting(ScaleVariable, 20_000);

    private static int LatencyMs => Setting(LatencyVariable, 20);

    private static int Setting(string variable, int otherwise)
        => int.TryParse(Environment.GetEnvironmentVariable(variable), NumberStyles.None, CultureInfo.InvariantCulture, out var asked) && asked > 0 ? asked : otherwise;

    private const string Flow = """
        flowType: dimension
        name: scale-dimensions
        partitions: [dev]
        source:
          endpoint: http://localhost
        target:
          connection: ${env:OSDU_SCALE_TESTS_DB}
        reliability: { retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        parallelism: { recordsAtOnce: 4 }
        incremental:
          keyColumns: [id]

        dimensions:
          - name: SmdaWellbore
            kind: osdu:wks:master-data--Wellbore:1.3.0
            query: 'tags.source:"SMDA"'
            path: id
            columns: { key: WellboreID, value: WellboreCode }
            attributes:
              WellID: { path: data.WellID, keep: id }
              FacilityID: data.FacilityID
              FacilityName: data.FacilityName
              DefaultVerticalMeasurementID: { path: data.DefaultVerticalMeasurementID, keep: key }
              Source: data.Source
              TechnicalAssuranceTypeID: { path: data.TechnicalAssuranceTypeID, keep: id }
              FieldID: { path: data.GeoContexts.FieldID, keep: id }
              CountryID: { path: 'data.GeoContexts[GeoTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityID', keep: id }
              BasinID: { path: data.GeoContexts.BasinID, keep: id }
              WellName: [data.WellID, data.FacilityName]
              WellUWI: [data.WellID, data.FacilityID]
              FieldName: [data.GeoContexts.FieldID, data.FieldName]
              FieldNames: { path: [data.GeoContexts.FieldID, data.FieldName], many: join, maxReferences: 100 }
              CountryName: ['data.GeoContexts[GeoTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityID', data.GeoPoliticalEntityName]
              CountryByType: ['data.GeoContexts[GeoTypeID->data.Code=Country].GeoPoliticalEntityID', data.GeoPoliticalEntityName]
              BasinName: [data.GeoContexts.BasinID, data.BasinName]
              TechnicalAssuranceType: [data.TechnicalAssuranceTypeID, data.Name]
              TechnicalAssuranceCode: [data.TechnicalAssuranceTypeID, data.Code]
            maxValues: 5000000
          - name: SmdaWell
            kind: osdu:wks:master-data--Well:1.2.0
            query: 'tags.source:"SMDA"'
            path: id
            columns: { key: WellID, value: WellCode }
            attributes:
              FacilityID: data.FacilityID
              FacilityName: data.FacilityName
            maxValues: 5000000
          - name: RefTechnicalAssuranceType
            kind: osdu:wks:reference-data--TechnicalAssuranceType:1.0.0
            path: id
            label: data.Name
            columns: { key: TechnicalAssuranceTypeID, value: TechnicalAssuranceTypeName }
            attributes:
              Code: data.Code
              Name: data.Name

        views:
          - name: scale_wellbores
            from: SmdaWellbore
            join:
              - { on: WellID, to: SmdaWell, as: Well }
              - { on: TechnicalAssuranceTypeID, to: RefTechnicalAssuranceType, as: TechnicalAssuranceType }
            columns:
              wellbore_id: WellboreID
              well_id: WellID
              wellbore_uuid: FacilityID
              unique_wellbore_identifier: FacilityName
              well_name: WellName
              field_name: FieldName
              country_name: CountryName
              basin_name: BasinName
              well_uuid: Well.FacilityID
              unique_well_identifier: Well.FacilityName
              technical_assurance_type_code: TechnicalAssuranceType.Code
              technical_assurance_type_name: TechnicalAssuranceType.Name
        """;

    /// <summary>
    /// The estate: <paramref name="wellbores"/> SMDA wellbores, their wells (about one for every 1.16 wellbores), three hundred
    /// fields, fifty basins, the countries and their types, and the technical assurance types.
    /// </summary>
    private void Estate(int wellbores)
    {
        foreach (var type in new[] { "Country", "Region" })
        {
            _platform.Add($"dev:reference-data--GeoPoliticalEntityType:{type}", EntityType, new JsonObject { ["Code"] = type });
        }

        string[] countries = ["NO", "GB", "DK", "NL", "US", "BR", "AO", "CA", "AZ", "TZ"];
        foreach (var country in countries)
        {
            _platform.Add($"dev:master-data--GeoPoliticalEntity:{country}", Entity, new JsonObject
            {
                ["GeoPoliticalEntityName"] = "Country " + country,
                ["GeoPoliticalEntityTypeID"] = "dev:reference-data--GeoPoliticalEntityType:Country:",
            });
        }

        var smda = new JsonObject { ["source"] = "SMDA" };
        for (var f = 0; f < 300; f++)
        {
            _platform.Add($"dev:master-data--Field:{Guid(f, 1)}", Field, new JsonObject { ["FieldName"] = $"FIELD {f:D3}", ["FieldID"] = Guid(f, 2) }, tags: smda.DeepClone().AsObject());
        }

        for (var b = 0; b < 50; b++)
        {
            _platform.Add($"dev:master-data--Basin:{Guid(b, 3)}", Basin, new JsonObject { ["BasinName"] = $"Basin {b:D2}" });
        }

        string[] assurances = ["Certified", "Unevaluated", "Suitable", "Unsuitable", "Trusted"];
        foreach (var assurance in assurances)
        {
            _platform.Add($"dev:reference-data--TechnicalAssuranceType:{assurance}", Assurance, new JsonObject { ["Name"] = assurance, ["Code"] = assurance.ToUpperInvariant() });
        }

        var wells = (int)(wellbores / 1.16);
        for (var w = 0; w < wells; w++)
        {
            _platform.Add($"dev:master-data--Well:{Guid(w, 4)}", Well, new JsonObject { ["FacilityID"] = Guid(w, 5), ["FacilityName"] = $"NO {w / 100}/{w % 100}" }, tags: smda.DeepClone().AsObject());
        }

        for (var i = 0; i < wellbores; i++)
        {
            var contexts = new JsonArray(
                new JsonObject { ["FieldID"] = $"dev:master-data--Field:{Guid(i % 300, 1)}:", ["GeoTypeID"] = "dev:reference-data--FieldType:Field:" },
                new JsonObject { ["GeoPoliticalEntityID"] = $"dev:master-data--GeoPoliticalEntity:{countries[i % countries.Length]}:", ["GeoTypeID"] = "dev:reference-data--GeoPoliticalEntityType:Country:" },
                new JsonObject { ["BasinID"] = $"dev:master-data--Basin:{Guid(i % 50, 3)}:", ["GeoTypeID"] = "dev:reference-data--BasinType:Basin:" });
            if (i % 7 == 0)
            {
                contexts.Add(new JsonObject { ["FieldID"] = $"dev:master-data--Field:{Guid((i + 1) % 300, 1)}:", ["GeoTypeID"] = "dev:reference-data--FieldType:Field:" });
            }

            _platform.Add($"dev:master-data--Wellbore:{Guid(i, 6)}", Wellbore, new JsonObject
            {
                ["FacilityID"] = Guid(i, 7),
                ["FacilityName"] = $"NO {i / 100}/{i % 100}-{i % 9}",
                ["WellID"] = $"dev:master-data--Well:{Guid(i % wells, 4)}:",
                ["DefaultVerticalMeasurementID"] = "RKB",
                ["Source"] = "SMDA",
                ["TechnicalAssuranceTypeID"] = $"dev:reference-data--TechnicalAssuranceType:{assurances[i % assurances.Length]}:",
                ["GeoContexts"] = contexts,
            }, tags: smda.DeepClone().AsObject());
        }
    }

    /// <summary>A GUID-shaped text of <paramref name="n"/> in the series <paramref name="series"/>, as SMDA's ids are.</summary>
    private static string Guid(int n, int series) => string.Create(CultureInfo.InvariantCulture, $"{n:x8}-{series:x4}-4a1b-8c2d-{n * 7919L % 0xFFFFFFFFFFFF:x12}");

    [Fact]
    public async Task A_wellbore_dimension_of_a_production_estates_shape_builds_at_scale()
    {
        var wellbores = Wellbores;
        Estate(wellbores);
        _platform.Latency = TimeSpan.FromMilliseconds(LatencyMs);
        var log = new TimedLog();
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock);
        var flow = new DeliveryDocumentLoader().ParseDimension(Flow, "flows/scale.yaml").ForPartition("dev");
        var runner = new DimensionRunner(engine, flow, new Dictionary<string, string>(), log, _platform, allowLoopback: true);

        var watch = Stopwatch.StartNew();
        var outcome = await runner.BuildAsync([], System.Guid.NewGuid(), "scale", CancellationToken.None);
        watch.Stop();

        foreach (var line in log.Lines)
        {
            _output.WriteLine(line);
        }

        var searches = _platform.Calls.Count;
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{wellbores} wellbores: built in {watch.Elapsed.TotalSeconds:N1} s, {searches} request(s), at most {_platform.MostAtOnce} at once"));
        Assert.Equal(0, outcome.Failed);
        Assert.Equal(wellbores, outcome.Dimensions.Single(d => d.Dimension == "SmdaWellbore").Keys);
    }

    /// <summary>The variable naming how many well logs the curve estate holds.</summary>
    private const string LogsVariable = "SQLFLOW_DIMENSION_SCALE_LOGS";

    private const string WellLog = "osdu:wks:work-product-component--WellLog:1.4.0";

    /// <summary>
    /// The well log metadata flow of a production estate, as its repository declares it,
    /// read from the stand-in search and written to the test database: the logs, their curves, header fields and ACLs, the
    /// SMDA wellbores, wells and fields, and the reference data the curves and logs name, with the wide curve view and the
    /// four views a pipeline publishes.
    /// </summary>
    private const string CurveFlow = """
        flowType: dimension
        name: scale-curves
        partitions: [dev]
        source:
          endpoint: http://localhost
        target:
          connection: ${env:OSDU_SCALE_TESTS_DB}
        reliability: { retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        parallelism: { recordsAtOnce: 4 }
        incremental:
          keyColumns: [id]

        dimensions:
          - name: WellLog
            kind: osdu:wks:work-product-component--WellLog:1.4.0
            query: 'data.Name:("COMPOSITE" OR "CPI")'
            path: id
            label: data.Name
            columns: { key: WellLogID, value: WellLogName }
            unlabelled: Not specified
            attributes:
              WellboreID: { path: data.WellboreID, keep: key }
              WellboreUWI: [data.WellboreID, data.FacilityName]
              LogVersion: data.LogVersion
              Source: data.Source
              LogRun: data.LogRun
              SamplingStart: data.SamplingStart
              SamplingStop: data.SamplingStop
              SamplingInterval: data.SamplingInterval
              ReferenceCurveID: data.ReferenceCurveID
              ServiceCompanyID: { path: data.ServiceCompanyID, keep: id }
              ServiceCompany: [data.ServiceCompanyID, data.OrganisationName]
              WellLogType: [data.WellLogTypeID, data.Name]
              SamplingDomainType: [data.SamplingDomainTypeID, data.Name]
              VerticalMeasurement: data.VerticalMeasurement.VerticalMeasurement
              VerticalMeasurementType: [data.VerticalMeasurement.VerticalMeasurementTypeID, data.Name]
              VerticalMeasurementUnit: [data.VerticalMeasurement.VerticalMeasurementUnitOfMeasureID, data.Name]
            maxValues: 5000000
          - name: LogCurve
            kind: osdu:wks:work-product-component--WellLog:1.4.0
            query: 'data.Name:("COMPOSITE" OR "CPI")'
            path: id
            label: data.Name
            columns: { key: WellLogID, value: WellLogName }
            unlabelled: Not specified
            elements:
              path: data.Curves
              fields:
                CurveID: CurveID
                Mnemonic: Mnemonic
                CurveDescription: CurveDescription
                CurveVersion: CurveVersion
                LogVersion: { path: LogVersion, up: 1 }
                CurveUnit: CurveUnit
                CurveUnitID: { path: CurveUnit, keep: id }
                DepthUnit: DepthUnit
                DepthUnitID: { path: DepthUnit, keep: id }
                TopDepth: TopDepth
                BaseDepth: BaseDepth
                NumberOfColumns: NumberOfColumns
                LogCurveFamilyID: { path: LogCurveFamilyID, keep: id }
                LogCurveMainFamilyID: { path: LogCurveMainFamilyID, keep: id }
                LogCurveTypeID: { path: LogCurveTypeID, keep: id }
                Owners: { path: acl.owners, up: 2, many: join }
                Viewers: { path: acl.viewers, up: 2, many: join }
            maxValues: 5000000
          - name: WellLogRecord
            kind: osdu:wks:work-product-component--WellLog:1.4.0
            query: 'data.Name:("COMPOSITE" OR "CPI")'
            path: id
            columns: { key: WellLogID, value: WellLogCode }
            attributes:
              Name: data.Name
              WellboreID: { path: data.WellboreID, keep: id }
              SamplingStart: data.SamplingStart
              SamplingStop: data.SamplingStop
              Source: data.Source
              LogRun: data.LogRun
              LogVersion: data.LogVersion
              SamplingInterval: data.SamplingInterval
              ReferenceCurveID: data.ReferenceCurveID
              VerticalMeasurement: data.VerticalMeasurement.VerticalMeasurement
              WellLogTypeID: { path: data.WellLogTypeID, keep: id }
              SamplingDomainTypeID: { path: data.SamplingDomainTypeID, keep: id }
              ServiceCompanyID: { path: data.ServiceCompanyID, keep: id }
              VerticalMeasurementTypeID: { path: data.VerticalMeasurement.VerticalMeasurementTypeID, keep: id }
              VerticalMeasurementUnitID: { path: data.VerticalMeasurement.VerticalMeasurementUnitOfMeasureID, keep: id }
            maxValues: 5000000
          - name: WellLogAcl
            kind: osdu:wks:work-product-component--WellLog:1.4.0
            query: 'data.Name:("COMPOSITE" OR "CPI")'
            path: id
            columns: { key: WellLogID, value: WellLogCode }
            elements:
              path: acl
              fields:
                Owners: { path: owners, many: join }
                Viewers: { path: viewers, many: join }
            maxValues: 5000000
          - name: SmdaWellbore
            kind: osdu:wks:master-data--Wellbore:1.3.0
            query: 'tags.source:"SMDA"'
            path: id
            columns: { key: WellboreID, value: WellboreCode }
            attributes:
              WellID: { path: data.WellID, keep: id }
              FacilityID: data.FacilityID
              FacilityName: data.FacilityName
              DefaultVerticalMeasurementID: { path: data.DefaultVerticalMeasurementID, keep: key }
              Source: data.Source
              TechnicalAssuranceTypeID: { path: data.TechnicalAssuranceTypeID, keep: id }
              FieldID: { path: data.GeoContexts.FieldID, keep: id }
              CountryID: { path: 'data.GeoContexts[GeoTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityID', keep: id }
              BasinID: { path: data.GeoContexts.BasinID, keep: id }
            maxValues: 5000000
          - name: SmdaWellboreGeoContext
            kind: osdu:wks:master-data--Wellbore:1.3.0
            query: 'tags.source:"SMDA"'
            path: id
            columns: { key: WellboreID, value: WellboreCode }
            elements:
              path: data.GeoContexts
              fields:
                FieldID: { path: FieldID, keep: id }
                GeoPoliticalEntityID: { path: GeoPoliticalEntityID, keep: id }
                BasinID: { path: BasinID, keep: id }
            maxValues: 5000000
          - name: SmdaWell
            kind: osdu:wks:master-data--Well:1.2.0
            query: 'tags.source:"SMDA"'
            path: id
            columns: { key: WellID, value: WellCode }
            attributes:
              FacilityID: data.FacilityID
              FacilityName: data.FacilityName
            maxValues: 5000000
          - name: SmdaField
            kind: osdu:wks:master-data--Field:1.0.0
            query: 'tags.source:"SMDA"'
            path: id
            columns: { key: FieldID, value: FieldCode }
            attributes:
              FieldUUID: data.FieldID
              FieldName: data.FieldName
          - name: RefUnitOfMeasure
            kind: osdu:wks:reference-data--UnitOfMeasure:1.0.0
            path: id
            label: data.Code
            columns: { key: UnitID, value: UnitCode }
            attributes: { Code: data.Code, Name: data.Name }
          - name: RefLogCurveFamily
            kind: osdu:wks:reference-data--LogCurveFamily:1.1.0
            path: id
            label: data.Code
            columns: { key: LogCurveFamilyID, value: LogCurveFamilyCode }
            attributes: { Code: data.Code, Name: data.Name }
          - name: RefLogCurveMainFamily
            kind: osdu:wks:reference-data--LogCurveMainFamily:1.0.0
            path: id
            label: data.Code
            columns: { key: LogCurveMainFamilyID, value: LogCurveMainFamilyCode }
            attributes: { Code: data.Code, Name: data.Name }
          - name: RefLogCurveType
            kind: osdu:wks:reference-data--LogCurveType:1.1.0
            path: id
            label: data.Code
            columns: { key: LogCurveTypeID, value: LogCurveTypeCode }
            attributes: { Code: data.Code, Name: data.Name }
          - name: RefLogType
            kind: osdu:wks:reference-data--LogType:1.0.0
            path: id
            label: data.Code
            columns: { key: LogTypeID, value: LogTypeCode }
            attributes: { Code: data.Code, Name: data.Name }
          - name: RefSamplingDomainType
            kind: osdu:wks:reference-data--WellLogSamplingDomainType:1.0.0
            path: id
            label: data.Name
            columns: { key: SamplingDomainTypeID, value: SamplingDomainTypeName }
            attributes: { Code: data.Code, Name: data.Name }
          - name: RefVerticalMeasurementType
            kind: osdu:wks:reference-data--VerticalMeasurementType:1.0.2
            path: id
            label: data.Name
            columns: { key: VerticalMeasurementTypeID, value: VerticalMeasurementTypeName }
            attributes: { Code: data.Code, Name: data.Name }
          - name: RefTechnicalAssuranceType
            kind: osdu:wks:reference-data--TechnicalAssuranceType:1.0.0
            path: id
            label: data.Name
            columns: { key: TechnicalAssuranceTypeID, value: TechnicalAssuranceTypeName }
            attributes: { Code: data.Code, Name: data.Name }
          - name: RefOrganisation
            kind: osdu:wks:master-data--Organisation:1.2.0
            path: id
            label: data.OrganisationName
            columns: { key: OrganisationID, value: OrganisationName }
            attributes: { Name: data.OrganisationName }
          - name: RefGeoPoliticalEntity
            kind: osdu:wks:master-data--GeoPoliticalEntity:1.0.0
            path: id
            columns: { key: GeoPoliticalEntityID, value: GeoPoliticalEntityCode }
            attributes: { GeoPoliticalEntityUUID: data.GeoPoliticalEntityID, GeoPoliticalEntityName: data.GeoPoliticalEntityName }
          - name: RefBasin
            kind: osdu:wks:master-data--Basin:1.2.0
            path: id
            columns: { key: BasinID, value: BasinCode }
            attributes: { BasinUUID: data.BasinID, BasinName: data.BasinName }

        views:
          - name: ScaleWellLogCurve
            from: LogCurve
            join:
              - { on: WellLogID, to: WellLog, as: Log }
              - { on: WellLogID, to: WellLogRecord, as: Rec }
              - { on: Rec.WellboreID, to: SmdaWellbore, as: Wellbore }
              - { on: Wellbore.WellID, to: SmdaWell, as: Well }
              - { on: Wellbore.TechnicalAssuranceTypeID, to: RefTechnicalAssuranceType, as: TechnicalAssuranceType }
              - { on: Wellbore.FieldID, to: SmdaField, as: Field }
              - { on: Wellbore.CountryID, to: RefGeoPoliticalEntity, as: Country }
              - { on: Wellbore.BasinID, to: RefBasin, as: Basin }
              - { on: CurveUnitID, to: RefUnitOfMeasure, as: CurveUnitOfMeasure }
              - { on: DepthUnitID, to: RefUnitOfMeasure, as: DepthUnitOfMeasure }
              - { on: LogCurveFamilyID, to: RefLogCurveFamily, as: Family }
              - { on: LogCurveMainFamilyID, to: RefLogCurveMainFamily, as: MainFamily }
              - { on: LogCurveTypeID, to: RefLogCurveType, as: CurveType }
              - { on: Rec.WellLogTypeID, to: RefLogType, as: LogType }
              - { on: Rec.SamplingDomainTypeID, to: RefSamplingDomainType, as: SamplingDomain }
              - { on: Rec.ServiceCompanyID, to: RefOrganisation, as: ServiceCompany }
              - { on: Rec.VerticalMeasurementTypeID, to: RefVerticalMeasurementType, as: VerticalMeasurementType }
              - { on: Rec.VerticalMeasurementUnitID, to: RefUnitOfMeasure, as: VerticalMeasurementUnitOfMeasure }
            columns:
              WellLogID: WellLogID
              WellLogName: Rec.Name
              WellboreID: Log.WellboreID
              WellboreUWI: Wellbore.FacilityName
              WellboreUUID: Wellbore.FacilityID
              Source: Rec.Source
              LogVersion: Rec.LogVersion
              LogRun: Rec.LogRun
              WellLogType: LogType.Name
              ServiceCompany: ServiceCompany.Name
              SamplingDomainType: SamplingDomain.Name
              SamplingStart: { expression: Rec.SamplingStart, dataType: float }
              SamplingStop: { expression: Rec.SamplingStop, dataType: float }
              SamplingInterval: { expression: Rec.SamplingInterval, dataType: float }
              ReferenceCurveID: Rec.ReferenceCurveID
              VerticalMeasurement: { expression: Rec.VerticalMeasurement, dataType: float }
              VerticalMeasurementType: VerticalMeasurementType.Name
              VerticalMeasurementUnit: VerticalMeasurementUnitOfMeasure.Name
              Acls:
                expression: |-
                  IIF(Owners IS NULL AND Viewers IS NULL, NULL, CONCAT(
                    '{',
                    IIF(Owners IS NULL, '', CONCAT('"owners":["', REPLACE(Owners, '; ', '","'), '"]')),
                    IIF(Owners IS NULL OR Viewers IS NULL, '', ','),
                    IIF(Viewers IS NULL, '', CONCAT('"viewers":["', REPLACE(Viewers, '; ', '","'), '"]')),
                    '}'))
                dataType: nvarchar(max)
              WellboreSource: Wellbore.Source
              TechnicalAssuranceTypeName: TechnicalAssuranceType.Name
              WellID: Wellbore.WellID
              WellUWI: Well.FacilityName
              FieldName: Field.FieldName
              CountryName: Country.GeoPoliticalEntityName
              BasinName: Basin.BasinName
              CurveNumber: element
              CurveID: CurveID
              Mnemonic: Mnemonic
              IsReferenceCurve: { expression: "IIF(Mnemonic = Rec.ReferenceCurveID, 1, 0)", dataType: bit }
              CurveDescription: CurveDescription
              CurveUnit: CurveUnit
              CurveUnitName: CurveUnitOfMeasure.Name
              DepthUnitName: DepthUnitOfMeasure.Name
              TopDepth: { expression: TopDepth, dataType: float }
              BaseDepth: { expression: BaseDepth, dataType: float }
              NumberOfColumns: { expression: NumberOfColumns, dataType: int }
              LogCurveFamily: Family.Name
              LogCurveMainFamily: MainFamily.Name
              LogCurveType: CurveType.Name
          - name: scale_well_logs
            from: WellLogAcl
            join:
              - { on: WellLogID, to: WellLogRecord, as: Log }
              - { on: Log.WellboreID, to: SmdaWellbore, as: Wellbore }
              - { on: Log.WellLogTypeID, to: RefLogType, as: LogType }
              - { on: Log.SamplingDomainTypeID, to: RefSamplingDomainType, as: SamplingDomainType }
              - { on: Log.ServiceCompanyID, to: RefOrganisation, as: Organisation }
              - { on: Log.VerticalMeasurementTypeID, to: RefVerticalMeasurementType, as: VerticalMeasurementType }
              - { on: Log.VerticalMeasurementUnitID, to: RefUnitOfMeasure, as: VerticalMeasurementUom }
            columns:
              well_log_id: WellLogID
              wellbore_uuid: Wellbore.FacilityID
              unique_wellbore_identifier: Wellbore.FacilityName
              name: Log.Name
              sampling_start: { expression: Log.SamplingStart, dataType: float }
              sampling_stop: { expression: Log.SamplingStop, dataType: float }
              source: Log.Source
              log_type_name: LogType.Name
              sampling_domain_type_name: SamplingDomainType.Name
              organisation_name: Organisation.Name
              vertical_measurement_type_name: VerticalMeasurementType.Name
              vertical_measurement_uom_name: VerticalMeasurementUom.Name
          - name: scale_log_curves
            from: LogCurve
            where: element IS NOT NULL
            join:
              - { on: CurveUnitID, to: RefUnitOfMeasure, as: CurveUom }
              - { on: DepthUnitID, to: RefUnitOfMeasure, as: DepthUom }
              - { on: LogCurveMainFamilyID, to: RefLogCurveMainFamily, as: MainFamily }
              - { on: LogCurveFamilyID, to: RefLogCurveFamily, as: Family }
              - { on: LogCurveTypeID, to: RefLogCurveType, as: CurveType }
            columns:
              well_log_id: WellLogID
              curve_id: CurveID
              mnemonic: Mnemonic
              base_depth: { expression: BaseDepth, dataType: float }
              top_depth: { expression: TopDepth, dataType: float }
              curve_unit_name: CurveUom.Name
              depth_unit_name: DepthUom.Name
              log_curve_main_family_name: MainFamily.Name
              log_curve_family_name: Family.Name
              log_curve_type_name: CurveType.Name
          - name: scale_wellbore_geo_contexts
            from: SmdaWellboreGeoContext
            where: element IS NOT NULL
            join:
              - { on: FieldID, to: SmdaField, as: Field }
              - { on: GeoPoliticalEntityID, to: RefGeoPoliticalEntity, as: GeoPoliticalEntity }
              - { on: BasinID, to: RefBasin, as: Basin }
            columns:
              wellbore_id: WellboreID
              field_name: Field.FieldName
              geo_political_entity_name: GeoPoliticalEntity.GeoPoliticalEntityName
              basin_name: Basin.BasinName
        """;

    /// <summary>
    /// The curve estate: <paramref name="logs"/> COMPOSITE and CPI well logs of about ten curves each, on a sixth as many
    /// wellbores among four and a half times as many SMDA wellbores, with their wells, fields, basins and countries, and the
    /// reference data the curves and logs name. A curve in a hundred names a unit the reference data does not hold, and a log
    /// in a thousand a wellbore SMDA does not, as the estate's do.
    /// </summary>
    private void CurveEstate(int logs)
    {
        var wellbores = Math.Max(10, (int)(logs * 4.6));
        Estate(wellbores);

        void Reference(string type, string kind, int count, Func<int, string> code)
        {
            for (var i = 0; i < count; i++)
            {
                _platform.Add($"dev:reference-data--{type}:{code(i)}", kind, new JsonObject { ["Code"] = code(i), ["Name"] = $"{type} {code(i)}" });
            }
        }

        Reference("UnitOfMeasure", "osdu:wks:reference-data--UnitOfMeasure:1.0.0", 200, i => i == 0 ? "m" : i == 1 ? "ft" : $"U{i:D3}");
        Reference("LogCurveFamily", "osdu:wks:reference-data--LogCurveFamily:1.1.0", 100, i => $"F{i:D3}");
        Reference("LogCurveMainFamily", "osdu:wks:reference-data--LogCurveMainFamily:1.0.0", 20, i => $"M{i:D2}");
        Reference("LogCurveType", "osdu:wks:reference-data--LogCurveType:1.1.0", 500, i => $"T{i:D3}");
        Reference("LogType", "osdu:wks:reference-data--LogType:1.0.0", 3, i => $"LT{i}");
        Reference("WellLogSamplingDomainType", "osdu:wks:reference-data--WellLogSamplingDomainType:1.0.0", 2, i => i == 0 ? "Depth" : "Time");
        Reference("VerticalMeasurementType", "osdu:wks:reference-data--VerticalMeasurementType:1.0.2", 5, i => $"V{i}");
        for (var o = 0; o < 50; o++)
        {
            _platform.Add($"dev:master-data--Organisation:O{o:D2}", "osdu:wks:master-data--Organisation:1.2.0", new JsonObject { ["OrganisationName"] = $"Service company {o}" });
        }

        var logWellbores = Math.Max(1, logs / 6);
        for (var i = 0; i < logs; i++)
        {
            var curves = new JsonArray();
            for (var c = 0; c < 8 + (i % 5); c++)
            {
                var unit = (i * 13 + c) % 100 == 0 ? "LOGICAL" : $"U{(i + c) % 198 + 2:D3}";
                curves.Add(new JsonObject
                {
                    ["CurveID"] = $"C{c}",
                    ["Mnemonic"] = c == 0 ? "DEPT" : $"M{(i + c) % 400:D3}",
                    ["CurveDescription"] = $"curve {c} of log {i}",
                    ["CurveVersion"] = "1",
                    ["CurveUnit"] = $"dev:reference-data--UnitOfMeasure:{unit}:",
                    ["DepthUnit"] = "dev:reference-data--UnitOfMeasure:m:",
                    ["TopDepth"] = 100.5 + c,
                    ["BaseDepth"] = 2500.25 + c,
                    ["NumberOfColumns"] = 1,
                    ["LogCurveFamilyID"] = $"dev:reference-data--LogCurveFamily:F{(i + c) % 100:D3}:",
                    ["LogCurveMainFamilyID"] = $"dev:reference-data--LogCurveMainFamily:M{(i + c) % 20:D2}:",
                    ["LogCurveTypeID"] = $"dev:reference-data--LogCurveType:T{(i + c) % 500:D3}:",
                });
            }

            var wellbore = i % 1_000 == 999 ? "NOT-IN-SMDA" : Guid(i % logWellbores, 6);
            var record = _platform.Add($"dev:work-product-component--WellLog:{Guid(i, 9)}", WellLog, new JsonObject
            {
                ["Name"] = i % 3 == 0 ? "CPI" : "COMPOSITE",
                ["WellboreID"] = $"dev:master-data--Wellbore:{wellbore}:",
                ["LogVersion"] = "1",
                ["Source"] = i % 2 == 0 ? "SMDA" : "WL",
                ["LogRun"] = $"R{i % 7}",
                ["SamplingStart"] = 100.0 + (i % 50),
                ["SamplingStop"] = 2500.0 + (i % 300),
                ["SamplingInterval"] = 0.1524,
                ["ReferenceCurveID"] = "DEPT",
                ["ServiceCompanyID"] = $"dev:master-data--Organisation:O{i % 50:D2}:",
                ["WellLogTypeID"] = $"dev:reference-data--LogType:LT{i % 3}:",
                ["SamplingDomainTypeID"] = "dev:reference-data--WellLogSamplingDomainType:Depth:",
                ["VerticalMeasurement"] = new JsonObject
                {
                    ["VerticalMeasurement"] = 25.0 + (i % 10),
                    ["VerticalMeasurementTypeID"] = $"dev:reference-data--VerticalMeasurementType:V{i % 5}:",
                    ["VerticalMeasurementUnitOfMeasureID"] = "dev:reference-data--UnitOfMeasure:m:",
                },
                ["Curves"] = curves,
            });
            record["acl"] = new JsonObject
            {
                ["owners"] = new JsonArray("data.default.owners@dev.dataservices.energy", $"data.wl-{i % 4}.owners@dev.dataservices.energy"),
                ["viewers"] = new JsonArray("data.default.viewers@dev.dataservices.energy"),
            };
        }
    }

    [Fact]
    public async Task The_wide_curve_view_of_a_production_estates_shape_is_written_and_checked_at_scale()
    {
        var logs = Setting(LogsVariable, 3_000);
        CurveEstate(logs);
        _platform.Latency = TimeSpan.FromMilliseconds(LatencyMs);
        var log = new TimedLog();
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock);
        var flow = new DeliveryDocumentLoader().ParseDimension(CurveFlow, "flows/scale-curves.yaml").ForPartition("dev");
        var runner = new DimensionRunner(engine, flow, new Dictionary<string, string>(), log, _platform, allowLoopback: true);

        var watch = Stopwatch.StartNew();
        var outcome = await runner.BuildAsync([], System.Guid.NewGuid(), "scale", CancellationToken.None);
        watch.Stop();

        foreach (var line in log.Lines)
        {
            _output.WriteLine(line);
        }

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{logs} logs: built in {watch.Elapsed.TotalSeconds:N1} s, {_platform.Calls.Count} request(s), at most {_platform.MostAtOnce} at once"));
        Assert.Equal(0, outcome.Failed);
        Assert.Equal(0, outcome.ViewsFailed);
        var curves = outcome.Views.Single(v => v.View == "ScaleWellLogCurve");
        Assert.True(curves.Rows > logs * 8, $"The curve view holds {curves.Rows} row(s) for {logs} logs.");
        Assert.Contains(curves.Notes, n => n.Contains("name no row of RefUnitOfMeasure", StringComparison.Ordinal));

        // An hour on, nothing changed: every dimension loads incrementally and finds nothing to write, and every view keeps its check.
        _clock.Advance(TimeSpan.FromHours(1));
        var again = new TimedLog();
        var quiet = new DimensionRunner(engine, flow, new Dictionary<string, string>(), again, _platform, allowLoopback: true);
        watch.Restart();
        var second = await quiet.BuildAsync([], System.Guid.NewGuid(), "scale", CancellationToken.None);
        watch.Stop();
        foreach (var line in again.Lines)
        {
            _output.WriteLine(line);
        }

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{logs} logs, nothing changed: built in {watch.Elapsed.TotalSeconds:N1} s"));
        Assert.Equal(0, second.Failed);
        Assert.All(second.Dimensions, d => Assert.Equal(DimensionRunModes.Incremental, d.Load));
        var detail = await ledger.GetDimensionViewAsync("ScaleWellLogCurve");
        Assert.NotNull(detail!.Checks[0].ReusedFrom);

        // An hour on, one log in six saved again with a new run, as a day of deliveries leaves them: the logs' dimensions read
        // those again and write their rows, and the views reading them are read again.
        var changedAt = OsduSearch.LuceneTime(_clock.GetUtcNow().UtcDateTime.AddMinutes(30));
        var changed = 0;
        foreach (var record in _platform.Records.Where(r => r["kind"]!.GetValue<string>() == WellLog))
        {
            if (changed++ % 6 == 0)
            {
                record["data"]!["LogRun"] = "R-again";
                record["modifyTime"] = changedAt;
            }
        }

        _clock.Advance(TimeSpan.FromHours(1));
        var some = new TimedLog();
        var busy = new DimensionRunner(engine, flow, new Dictionary<string, string>(), some, _platform, allowLoopback: true);
        watch.Restart();
        var third = await busy.BuildAsync([], System.Guid.NewGuid(), "scale", CancellationToken.None);
        watch.Stop();
        foreach (var line in some.Lines)
        {
            _output.WriteLine(line);
        }

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{logs} logs, one in six changed: built in {watch.Elapsed.TotalSeconds:N1} s"));
        Assert.Equal(0, third.Failed);
        Assert.Equal((logs + 5) / 6, third.Dimensions.Single(d => d.Dimension == "LogCurve").ChangedRecords);
        Assert.Null((await ledger.GetDimensionViewAsync("ScaleWellLogCurve"))!.Checks[0].ReusedFrom);
    }

    /// <summary>A run's log, each line with the seconds since the first.</summary>
    private sealed class TimedLog : ILogger
    {
        private readonly Stopwatch _since = Stopwatch.StartNew();
        private readonly Lock _gate = new();

        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = string.Create(CultureInfo.InvariantCulture, $"{_since.Elapsed.TotalSeconds,8:N1} s  {formatter(state, exception)}");
            lock (_gate)
            {
                Lines.Add(line);
            }
        }
    }
}
