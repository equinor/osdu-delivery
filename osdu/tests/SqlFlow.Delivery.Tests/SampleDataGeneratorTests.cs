using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Tools.SampleData;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The sample estate's data against the generator that writes it (osdu/tools/SampleData): the committed files are exactly
/// what the generator writes with its default seed, so none of them is edited by hand or copied from a real source; the
/// generator gives the same logs for the same seed and the earlier logs unchanged when asked for more; and the data keeps
/// the relationships the sample mappings and the suites rely on (every unit spelling translated, every code the curve
/// dictionary and the surveys give held by the sample reference data, values within each curve type's physical range,
/// every survey and set of tops in a wellbore the sample delivers and no deeper than its survey reaches).
/// </summary>
public sealed class SampleDataGeneratorTests
{
    [Fact]
    public void The_committed_well_logs_are_what_the_generator_writes_with_its_default_seed()
    {
        var generated = SyntheticEstate.WellLogs(SyntheticEstate.DefaultLogs, SyntheticEstate.DefaultSeed);
        var committed = SampleWellLogs.Logs();

        Assert.Equal(generated.Count, committed.Count);
        for (var i = 0; i < generated.Count; i++)
        {
            Assert.Equal(SampleWellLogs.LogRow(generated[i]), SampleWellLogs.LogRow(committed[i]));
            Assert.Equal(SampleWellLogs.CurveRows(generated[i]), SampleWellLogs.CurveRows(committed[i]));
            Assert.Equal(generated[i].Depths, committed[i].Depths);
            Assert.Equal(generated[i].GridHash(), committed[i].GridHash());
        }
    }

    [Fact]
    public async Task The_committed_lookup_tables_are_what_the_generator_writes()
    {
        var root = Samples.NewTempDirectory();
        try
        {
            await SyntheticEstate.WriteLookupsAsync(root, "20260901");
            foreach (var folder in new[] { SyntheticEstate.UnitAliasFolder, SyntheticEstate.DepthUnitAliasFolder, SyntheticEstate.CurveDictionaryFolder })
            {
                var written = Directory.GetFiles(Path.Combine(root, folder), "*.csv").Single();
                var committed = Directory.GetFiles(Path.Combine(Samples.Source, "cache", "data", folder), "*.csv").Single();
                Assert.Equal(Path.GetFileName(written), Path.GetFileName(committed));
                // A checkout may write the committed file with either line ending; the rows are what has to be the same.
                Assert.Equal(
                    (await File.ReadAllTextAsync(written)).ReplaceLineEndings("\n"),
                    (await File.ReadAllTextAsync(committed)).ReplaceLineEndings("\n"));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task The_committed_wells_and_wellbores_are_what_the_generator_writes_with_its_default_seed()
    {
        var root = Samples.NewTempDirectory();
        try
        {
            await SyntheticEstate.WriteMasterDataAsync(root, SyntheticEstate.DefaultLogs, SyntheticEstate.DefaultSeed, "20260924");
            foreach (var folder in new[] { SyntheticEstate.WellFolder, SyntheticEstate.WellboreFolder })
            {
                var written = Directory.GetFiles(Path.Combine(root, folder), "*.csv").Single();
                var committed = Directory.GetFiles(Path.Combine(Samples.Data, folder), "*.csv").Single();
                Assert.Equal(Path.GetFileName(written), Path.GetFileName(committed));
                Assert.Equal(
                    (await File.ReadAllTextAsync(written)).ReplaceLineEndings("\n"),
                    (await File.ReadAllTextAsync(committed)).ReplaceLineEndings("\n"));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task The_committed_surveys_and_marker_sets_are_what_the_generator_writes_with_its_default_seed()
    {
        var root = Samples.NewTempDirectory();
        try
        {
            await SyntheticEstate.WriteSurveysAsync(root, SyntheticEstate.DefaultLogs, SyntheticEstate.DefaultSeed, "20260924");
            foreach (var folder in new[] { SampleSurveys.SurveyFolder, SampleSurveys.PropertyFolder, SampleSurveys.MarkerSetFolder, SampleSurveys.MarkerFolder })
            {
                var written = Directory.GetFiles(Path.Combine(root, folder), "*.csv").Single();
                var committed = Directory.GetFiles(Path.Combine(Samples.Data, folder), "*.csv").Single();
                Assert.Equal(Path.GetFileName(written), Path.GetFileName(committed));
                Assert.Equal(
                    (await File.ReadAllTextAsync(written)).ReplaceLineEndings("\n"),
                    (await File.ReadAllTextAsync(committed)).ReplaceLineEndings("\n"));
            }

            // The stations are compared as read back: a payload file names the library that wrote it, which an upgrade
            // changes without changing a value.
            var generated = await SampleSurveys.LoadSurveysAsync(root);
            var surveys = SampleSurveys.Surveys();
            Assert.Equal(generated.Count, surveys.Count);
            Assert.All(generated.Zip(surveys), pair => Assert.Equal(pair.First.Stations, pair.Second.Stations));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Every_survey_and_set_of_tops_is_in_a_wellbore_of_the_sample_and_runs_no_deeper_than_its_survey()
    {
        var wells = Single(Path.Combine(Samples.Data, SyntheticEstate.WellFolder)).ToDictionary(r => r["well_name"]!, StringComparer.Ordinal);
        var wellbores = Single(Path.Combine(Samples.Data, SyntheticEstate.WellboreFolder)).ToDictionary(r => r["wellbore_name"]!, StringComparer.Ordinal);
        var logs = SampleWellLogs.Logs().ToDictionary(l => l.WellboreUwi, StringComparer.Ordinal);
        var surveys = SampleSurveys.Surveys();
        var sets = SampleSurveys.MarkerSets();

        // One survey and one set of tops per wellbore, measured from where the wellbore's well is.
        Assert.Equal(wellbores.Keys.Order(StringComparer.Ordinal), surveys.Select(s => s.WellboreUwi));
        Assert.Equal(wellbores.Keys.Order(StringComparer.Ordinal), sets.Select(s => s.WellboreUwi));
        foreach (var survey in surveys)
        {
            var well = wells[wellbores[survey.WellboreUwi]["well_name"]!];
            Assert.Equal($"{well["elevation"]} {well["elevation_unit"]}", survey.ElevMeasRef);

            // From the surface, a station every interval, to the first station at or below the deepest point the log reaches.
            Assert.Equal(0, survey.TopMd);
            Assert.All(survey.Stations.Select((station, i) => (station, i)), s => Assert.Equal(s.i * SyntheticEstate.StationInterval, s.station[0]));
            Assert.InRange(survey.BaseMd - logs[survey.WellboreUwi].IndexMax, 0, SyntheticEstate.StationInterval - 0.0001);

            // Every station holds an inclination of a hole and an azimuth below a full turn.
            Assert.All(survey.Stations, station => Assert.InRange(station[1], 0, 90));
            Assert.All(survey.Stations, station => Assert.InRange(station[2], 0, 359.99));
        }

        foreach (var set in sets)
        {
            var survey = surveys.Single(s => s.WellboreUwi == set.WellboreUwi);
            Assert.Equal(survey.ElevMeasRef, set.ElevMeasRef);
            Assert.InRange(set.Markers.Count, 3, 4);
            Assert.All(set.Markers.Zip(set.Markers.Skip(1)), pair => Assert.True(pair.First.MeasuredDepth < pair.Second.MeasuredDepth, $"{set.MarkerSetId}: {pair.Second.Name} lies below {pair.First.Name}"));
            Assert.All(set.Markers, marker => Assert.InRange(marker.MeasuredDepth, 0.1, survey.BaseMd));
        }

        // Every property a survey lists is a station property type the reference data holds, in a unit the unit table spells.
        var propertyTypes = Single(Path.Combine(ReferenceData, "trajectory-station-property-type")).Select(r => r["code"]!).ToHashSet(StringComparer.Ordinal);
        var spellings = SyntheticEstate.UnitAliasRows().Select(r => r["source_unit"]!).ToHashSet(StringComparer.Ordinal);
        Assert.All(surveys.SelectMany(s => s.Properties), property =>
        {
            Assert.Contains(property.PropertyType, propertyTypes);
            Assert.Contains(property.Unit, spellings);
        });
    }

    [Fact]
    public void Every_log_names_a_wellbore_of_the_sample_and_every_wellbore_a_well_measured_from_where_its_logs_are()
    {
        var wells = Single(Path.Combine(Samples.Data, SyntheticEstate.WellFolder)).ToDictionary(r => r["well_name"]!, StringComparer.Ordinal);
        var wellbores = Single(Path.Combine(Samples.Data, SyntheticEstate.WellboreFolder)).ToDictionary(r => r["wellbore_name"]!, StringComparer.Ordinal);
        var logs = SampleWellLogs.Logs();

        foreach (var log in logs)
        {
            Assert.True(wellbores.TryGetValue(log.WellboreUwi, out var wellbore), $"{log.LogId} is logged in {log.WellboreUwi}, which the wellbore file does not hold.");
            var well = wells[wellbore!["well_name"]!];
            Assert.Equal(log.ElevMeasRef, $"{well["elevation"]} {well["elevation_unit"]}");
            Assert.Equal(log.LogsMeasFrom, well["elevation_reference"]);
        }

        // Nothing is delivered that no log needs: every wellbore has a log, and every well a wellbore.
        Assert.Equal(wellbores.Keys.Order(StringComparer.Ordinal), logs.Select(l => l.WellboreUwi).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Assert.Equal(wells.Keys.Order(StringComparer.Ordinal), wellbores.Values.Select(w => w["well_name"]!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_committed_curve_types_and_reference_cache_records_are_what_the_generator_writes()
    {
        var curveTypes = Single(Path.Combine(ReferenceData, OsduReferenceValues.LogCurveTypeFolder));
        Assert.Equal(
            SyntheticEstate.LogCurveTypeRows().Select(r => string.Join("|", SyntheticEstate.LogCurveTypeColumns.Select(c => r[c]))),
            curveTypes.Select(r => string.Join("|", SyntheticEstate.LogCurveTypeColumns.Select(c => r[c]))));

        foreach (var (entity, items) in OsduReferenceValues.CacheRecords(ReferenceData))
        {
            var committed = await File.ReadAllTextAsync(Path.Combine(Samples.SampleCacheRecords, entity + ".json"));
            Assert.Equal(OsduReferenceValues.CacheFile(entity, items), committed.ReplaceLineEndings("\n"));
        }
    }

    [Fact]
    public void The_reference_data_holds_every_value_the_sample_refers_to_and_nothing_else()
    {
        var codes = OsduReferenceValues.Kinds.Select(k => (k.Entity, k.Folder))
            .Append(("LogCurveType", OsduReferenceValues.LogCurveTypeFolder))
            .ToDictionary(k => k.Item1, k => Single(Path.Combine(ReferenceData, k.Item2)), StringComparer.Ordinal);
        static IEnumerable<string> Column(IEnumerable<IReadOnlyDictionary<string, string?>> rows, string column) =>
            rows.Select(r => r[column]!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

        foreach (var (entity, referenced) in OsduReferenceValues.ReferencedCodes())
        {
            Assert.Equal(referenced.Order(StringComparer.Ordinal), Column(codes[entity], "code"));
        }

        Assert.Equal(Column(SyntheticEstate.CurveDictionaryRows(), "log_curve_type_id"), Column(codes["LogCurveType"], "code"));

        // The unit quantities are the ones the units, families, station property types and curve types are measured in,
        // so each of those references names a record the sample delivers.
        var quantities = codes["UnitOfMeasure"].Concat(codes["LogCurveFamily"]).Concat(codes["TrajectoryStationPropertyType"]).Concat(codes["LogCurveType"]);
        Assert.Equal(Column(quantities, "unit_quantity"), Column(codes["UnitQuantity"], "code"));

        // Every code is one OSDU's ids carry as the record's key: no value of a kind is written twice.
        Assert.All(codes, kind => Assert.Equal(kind.Value.Count, Column(kind.Value, "code").Count()));
    }

    private static string ReferenceData => Path.Combine(Samples.Source, "reference", "data");

    private static IReadOnlyList<IReadOnlyDictionary<string, string?>> Single(string folder) =>
        SampleWellLogs.ReadCsv(Directory.GetFiles(folder, "*.csv").Single());

    [Fact]
    public void A_seed_names_one_estate_and_more_logs_leave_the_earlier_ones_as_they_were()
    {
        var once = SyntheticEstate.WellLogs(5, 7);
        var again = SyntheticEstate.WellLogs(5, 7);
        Assert.Equal(once.Select(l => l.GridHash()), again.Select(l => l.GridHash()));
        Assert.Equal(once.Select(SampleWellLogs.LogRow), again.Select(SampleWellLogs.LogRow));

        var other = SyntheticEstate.WellLogs(5, 8);
        Assert.All(once.Zip(other), pair => Assert.NotEqual(pair.First.GridHash(), pair.Second.GridHash()));
        // The names and the curve suites follow a log's place; the seed moves the values.
        Assert.Equal(once.Select(l => (l.LogId, l.WellboreUwi)), other.Select(l => (l.LogId, l.WellboreUwi)));

        var more = SyntheticEstate.WellLogs(12, 7);
        Assert.Equal(12, more.Count);
        foreach (var log in once)
        {
            Assert.Equal(log.GridHash(), more.Single(l => l.LogId == log.LogId).GridHash());
        }

        Assert.Equal(more.Count, more.Select(l => l.LogId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(more.Select(l => l.WellboreUwi).Order(StringComparer.Ordinal), more.Select(l => l.WellboreUwi));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(SyntheticEstate.MaxLogs + 1)]
    public void An_estate_holds_between_one_and_the_most_logs(int count)
    {
        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => SyntheticEstate.WellLogs(count, SyntheticEstate.DefaultSeed));
        Assert.Contains(SyntheticEstate.MaxLogs.ToString(CultureInfo.InvariantCulture), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sample_logs_keep_the_shape_the_suites_rely_on()
    {
        var logs = SampleWellLogs.Logs();
        Assert.Equal(["LOG-0001/1", "LOG-0002/1", "LOG-0003/1", "LOG-0004/1", "LOG-0005/1"], logs.Select(l => l.LogId));
        Assert.Equal(["Wellbore A-1", "Wellbore B-1", "Wellbore B-2 A", "Wellbore B-2 B", "Wellbore C-1"], logs.Select(l => l.WellboreUwi));
        Assert.All(logs, l => Assert.Equal((SyntheticEstate.Project, SampleWellLogs.LogSource), (l.SourceProject, l.LogSource)));

        // A log's header and its index curve state exactly what its chunk holds.
        Assert.All(logs, l => Assert.Equal((l.Depths[0], l.Depths[^1]), (l.IndexMin, l.IndexMax)));
        Assert.All(logs, l => Assert.Equal((l.Depths[0], l.Depths[^1]), (l.Curves[0].TopDepth, l.Curves[0].BaseDepth)));
        Assert.Equal(["MD", "BS", "CALI", "DRHO", "DT", "DTS", "GR", "NPHI", "RD", "RHOB"], logs[0].Curves.Select(c => c.CurveId));
        Assert.Equal(["MD", "DRHO", "GR", "NPHI", "PEF", "RD", "RHOB"], logs[1].Curves.Select(c => c.CurveId));
        Assert.All(logs.Skip(2), l => Assert.Equal(["MD", "GR", "RD"], l.Curves.Select(c => c.CurveId)));

        // The two sidetracks of one wellbore share its elevation and were updated together.
        Assert.Equal((logs[2].ElevMeasRef, logs[2].UpdateDateUtc), (logs[3].ElevMeasRef, logs[3].UpdateDateUtc));

        // Some curves carry no business value, as the source leaves some without one, and the index curve always has one.
        Assert.Contains(logs.SelectMany(l => l.Curves), c => c.BusinessValue is null);
        Assert.All(logs, l => Assert.Equal("HIGH", l.Curves[0].BusinessValue));

        foreach (var log in logs)
        {
            // The grid runs from the lowest curve top to the highest curve base at the log's increment.
            var curves = log.Curves.Skip(1).ToList();
            Assert.Equal(curves.Min(c => c.TopDepth), log.Depths[0]);
            Assert.Equal(curves.Max(c => c.BaseDepth), log.Depths[^1]);
            Assert.All(log.Depths.Zip(log.Depths.Skip(1)), step => Assert.Equal(SyntheticEstate.Increment, Math.Round(step.Second - step.First, 4)));
            foreach (var curve in curves)
            {
                var values = log.Values[curve.CurveId];
                for (var i = 0; i < log.Depths.Count; i++)
                {
                    var inside = log.Depths[i] >= curve.TopDepth && log.Depths[i] <= curve.BaseDepth;
                    Assert.True(inside == values[i].HasValue, $"{log.LogId} {curve.CurveId} at {log.Depths[i]}: a value only between the curve's top and base.");
                }

                Assert.All(values.Where(v => v.HasValue).Select(v => v!.Value), v => Assert.InRange(v, Ranges[curve.CurveId].Min, Ranges[curve.CurveId].Max));
            }
        }
    }

    [Fact]
    public void Every_unit_and_curve_code_the_sample_logs_reach_names_a_record_of_the_sample_reference_data()
    {
        var lookups = Samples.SampleLookups().ToDictionary(t => t.Name, StringComparer.Ordinal);
        var units = Codes("UnitOfMeasure");
        var spellings = lookups["UnitAlias"];
        foreach (var unit in SampleWellLogs.Logs().SelectMany(l => l.Curves).Select(c => c.Unit).Distinct(StringComparer.Ordinal))
        {
            var row = spellings.Match("source_unit", unit);
            Assert.True(row is not null, $"The unit spelling {unit} has no row in UnitAlias.");
            Assert.Contains(row!.Select("osdu_unit")!.Text, units);
        }

        Assert.All(lookups["UnitAlias"].Items.Concat(lookups["DepthUnitAlias"].Items), row => Assert.Contains(row.Select("osdu_unit")!.Text, units));

        var types = Codes("LogCurveType");
        var mains = Codes("LogCurveMainFamily");
        var families = Codes("LogCurveFamily");
        foreach (var row in lookups["CurveDictionary"].Items)
        {
            Assert.Contains(row.Select("log_curve_type_id")!.Text, types);
            Assert.Contains(row.Select("log_curve_main_family_id")!.Text, mains);
            Assert.Contains(Uri.UnescapeDataString(row.Select("log_curve_family_id")!.Text!), families);
        }

        // Every curve the sample logs carry is in the dictionary, so each is classified rather than left out.
        var dictionary = lookups["CurveDictionary"];
        Assert.All(SampleWellLogs.Logs().SelectMany(l => l.Curves), c => Assert.NotNull(dictionary.Match("mnemonic", c.CurveId)));
    }

    /// <summary>The physical range the generator draws each curve type's values from.</summary>
    private static readonly IReadOnlyDictionary<string, (double Min, double Max)> Ranges = new Dictionary<string, (double, double)>(StringComparer.Ordinal)
    {
        ["BS"] = (8.5, 12.25),
        ["CALI"] = (8.5, 12.25 + 1.6),
        ["DRHO"] = (-0.05, 0.12),
        ["DT"] = (55, 140),
        ["DTS"] = (90, 260),
        ["GR"] = (15, 150),
        ["NPHI"] = (0.03, 0.45),
        ["PEF"] = (1.8, 5.5),
        ["RD"] = (0.3, 300),
        ["RHOB"] = (1.95, 2.75),
    };

    /// <summary>The codes of the records the sample reference data holds for one type (osdu/samples/cache-records).</summary>
    private static HashSet<string> Codes(string type)
    {
        var file = JsonNode.Parse(File.ReadAllText(Path.Combine(Samples.SampleCacheRecords, type + ".json")))!.AsObject();
        return file["items"]!.AsArray().Select(item => item!["Code"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
    }
}
