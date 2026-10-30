namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Dimension documents written before attributes, elements and lookups took the settings they take now, each with the
/// definition hash a build of that time kept: a build of today hashes them the same, so no dimension is loaded in full,
/// or its views written again, only because the engine learned a setting it does not use.
/// </summary>
internal static class DimensionHashBaselines
{
    public const string Head = """
        flowType: dimension
        name: wells-dimensions
        partitions: [dev]
        source:
          endpoint: ${env:OSDU_URL}

        """;

    /// <summary>Each document by a name, the dimension the hash is of first in it.</summary>
    public static IReadOnlyList<(string Name, string Yaml)> Documents { get; } =
    [
        ("plain", """
            dimensions:
              - name: CurveMnemonic
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.Curves.Mnemonic
            """),
        ("labelled", """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:1.4.0"
                query: 'data.Name:("COMPOSITE" OR "CPI")'
                path: data.WellboreID
                label: data.FacilityName
                unlabelled: Not specified
                maxValues: 5000000
                countRecords: true
            """),
        ("attributes", """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: data.FacilityName
                attributes:
                  UUID: data.FacilityID
                  Field: [data.GeoContexts.FieldID, data.FieldName]
                  Country: [data.GeoContexts.GeoPoliticalEntityID, 'data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityName']
                  WellboreKey: { path: data.WellboreID, keep: key }
                  CompanyID: { path: data.ServiceCompanyID, keep: id }
                  Source: { collect: data.Source }
            """),
        ("settings", """
            dimensions:
              - name: WellboreContext
                kind: "osdu:wks:master-data--Wellbore:1.3.0"
                query: 'tags.source:"WellDB"'
                path: id
                columns: { key: WellboreID, value: WellboreCode }
                attributes:
                  FieldID: { path: data.GeoContexts.FieldID, keep: id }
                  Name: { path: data.FacilityName }
                  Origin: { path: data.Source, keep: value }
            """),
        ("elements", """
            dimensions:
              - name: LogCurve
                kind: "osdu:wks:work-product-component--WellLog:1.4.0"
                path: id
                label: data.Name
                columns: { key: WellLogID, value: WellLogName }
                unlabelled: Not specified
                elements:
                  path: data.Curves
                  fields:
                    CurveID: CurveID
                    Mnemonic: Mnemonic
                    LogVersion: { path: LogVersion, up: 1 }
                    CurveUnitID: { path: CurveUnit, keep: id }
                    Owners: { path: acl.owners, up: 2, many: join }
            """),
        ("filtered-elements", """
            dimensions:
              - name: WellboreContext
                kind: "osdu:wks:master-data--Wellbore:*"
                path: id
                columns: { key: WellboreID, value: WellboreCode }
                attributes:
                  Name: data.FacilityName
                elements:
                  path: 'data.GeoContexts[GeoTypeID$=:Field:]'
                  fields:
                    FieldID: { path: FieldID, keep: id }
                    Self: '@'
            """),
        ("cleaned", """
            dimensions:
              - name: CurveMnemonic
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.Curves.Mnemonic
                clean:
                  - trim
                  - collapseSpaces
                  - upper
                  - replace: { pattern: '^(\w+)_\d+$', with: '$1' }
                partitions: [dev]
            """),
    ];
}
