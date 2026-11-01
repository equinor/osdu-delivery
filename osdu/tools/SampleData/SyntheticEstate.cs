using System.Globalization;
using SqlFlow.Delivery.Tests;

namespace SqlFlow.Delivery.Tools.SampleData;

/// <summary>
/// The sample estate's data, made up: a handful of well logs with their curve grids, the wells and wellbores they are
/// logged in with each wellbore's directional survey and formation tops, and the lookup tables the sample mapping
/// translates their values through. Nothing here comes from a real well, a real company or a real database; every
/// value is generated from a seed, so the same seed always gives the same logs, byte for byte, and the suites can check
/// that the committed files are exactly what the generator writes (SampleDataGeneratorTests).
/// <para>The logs keep the shape the sample flows read and the relationships the suites rely on. The first log is a full
/// suite logged from the surface (its index curve starts at depth 0), the second a density-neutron suite with a
/// photoelectric factor, and the rest gamma ray and resistivity alone; two of them are the sidetracks A and B of one
/// wellbore, which share its elevation and were updated together. The grid of each log is built the way a well database's
/// export builds it: depths from the lowest curve top to the highest curve base at the log's increment, and each curve
/// holding a value at every depth between its own top and base and a gap elsewhere.</para>
/// </summary>
public static class SyntheticEstate
{
    /// <summary>The seed the committed sample data was generated with.</summary>
    public const ulong DefaultSeed = 20260924;

    /// <summary>How many logs the committed sample data holds.</summary>
    public const int DefaultLogs = 5;

    /// <summary>The most logs one estate holds: an example, never a load test.</summary>
    public const int MaxLogs = 40;

    /// <summary>The source project every sample log belongs to.</summary>
    public const string Project = "PROJECT_A";

    /// <summary>What the well database records as the source and the creator of every log it holds.</summary>
    public const string SourceSystem = "WELLDB";

    /// <summary>The sampling increment of every sample log: half a foot, in metres.</summary>
    public const double Increment = 0.1524;

    /// <summary>The folder under the cache data root holding the curve unit spellings.</summary>
    public const string UnitAliasFolder = "unit-alias";

    /// <summary>The folder under the cache data root holding the depth unit spellings.</summary>
    public const string DepthUnitAliasFolder = "depth-unit-alias";

    /// <summary>The folder under the cache data root holding the curve dictionary.</summary>
    public const string CurveDictionaryFolder = "curve-dictionary";

    /// <summary>The columns of both unit spelling files.</summary>
    public static IReadOnlyList<string> UnitAliasColumns { get; } = ["source_unit", "osdu_unit"];

    /// <summary>The columns of the curve dictionary file.</summary>
    public static IReadOnlyList<string> CurveDictionaryColumns { get; } =
        ["mnemonic", "log_curve_type_id", "log_curve_main_family_id", "log_curve_family_id", "unit_quantity_id", "unit", "comment"];

    /// <summary>The curve suites a log is logged with, in the order the logs take them (a sixth log starts again).</summary>
    private static readonly string[][] Suites =
    [
        ["BS", "CALI", "DRHO", "DT", "DTS", "GR", "NPHI", "RD", "RHOB"],
        ["DRHO", "GR", "NPHI", "PEF", "RD", "RHOB"],
        ["GR", "RD"],
        ["GR", "RD"],
        ["GR", "RD"],
    ];

    /// <summary>
    /// The wellbore of each log in a round of five, as (well, wellbore, sidetrack): the third and fourth logs are the
    /// sidetracks A and B of the same wellbore, so their names share a prefix and their well shares an elevation.
    /// </summary>
    private static readonly (int Well, int Wellbore, string? Sidetrack)[] Wellbores =
    [
        (0, 1, null),
        (1, 1, null),
        (1, 2, "A"),
        (1, 2, "B"),
        (2, 1, null),
    ];

    /// <summary>
    /// The curve types a sample log can carry: the spelling the well database writes the unit in, the description it
    /// gives, and the physical range of the values a log of that type holds.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, CurveType> CurveTypes = new Dictionary<string, CurveType>(StringComparer.Ordinal)
    {
        ["BS"] = new("IN", "Bit Size", 8.5, 12.25, Shape.Constant),
        ["CALI"] = new("IN", "Caliper [hole diameter]", 0, 1.6, Shape.AboveBitSize),
        ["DRHO"] = new("G/CC", "Density correction", -0.05, 0.12, Shape.Linear),
        ["DT"] = new("US/F", "Compressional Slowness", 55, 140, Shape.Linear),
        ["DTS"] = new("US/F", "Shear Slowness", 90, 260, Shape.Linear),
        ["GR"] = new("GAPI", "Gamma Ray", 15, 150, Shape.Linear),
        ["NPHI"] = new("V/V", "Neutron Porosity", 0.03, 0.45, Shape.Linear),
        ["PEF"] = new("B/E", "Photoelectric Factor", 1.8, 5.5, Shape.Linear),
        ["RD"] = new("OHMM", "Deep resistivity", 0.3, 300, Shape.Logarithmic),
        ["RHOB"] = new("G/CC", "Bulk Density", 1.95, 2.75, Shape.Linear),
    };

    /// <summary>
    /// <paramref name="count"/> sample logs generated from <paramref name="seed"/>, in the order a well database lists them
    /// (by wellbore). A log's values depend on the seed and its position only, so asking for more logs adds logs and
    /// leaves the earlier ones as they were.
    /// </summary>
    public static IReadOnlyList<SampleLog> WellLogs(int count, ulong seed)
    {
        if (count is < 1 or > MaxLogs)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, $"A sample estate holds between 1 and {MaxLogs} logs.");
        }

        var logs = new List<SampleLog>(count);
        for (var index = 0; index < count; index++)
        {
            logs.Add(WellLog(index, seed));
        }

        return logs.OrderBy(l => l.WellboreUwi, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The curve unit spellings the well database writes, each with the code of the UnitOfMeasure record it means: the
    /// spellings the sample logs' curves carry, the other spellings the well database has for the same units, and the
    /// degrees its survey stations are written in.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> UnitAliasRows() => Rows(UnitAliasColumns,
    [
        // OSDU's unit catalog has no barn per electron: its Photoelectric Factor family is dimensionless, written in euclid.
        ["B/E", "Euc"],
        ["B/ELEC", "Euc"],
        ["DEG", "dega"],
        ["FRAC", "m3/m3"],
        ["G/CC", "g/cm3"],
        ["G/CM3", "g/cm3"],
        ["GAPI", "gAPI"],
        ["IN", "in"],
        ["INCH", "in"],
        ["M", "m"],
        ["OHM.M", "ohm.m"],
        ["OHMM", "ohm.m"],
        ["US/F", "us/ft"],
        ["US/FT", "us/ft"],
        ["V/V", "m3/m3"],
    ]);

    /// <summary>The depth and elevation unit spellings the well database writes, each with the UnitOfMeasure code it means.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> DepthUnitAliasRows() => Rows(UnitAliasColumns,
    [
        ["M", "m"],
        ["metre", "m"],
        ["FT", "ft"],
        ["FEET", "ft"],
    ]);

    /// <summary>
    /// The curve dictionary: what each mnemonic is, as the codes of the LogCurveType, LogCurveMainFamily and LogCurveFamily
    /// records its id ends with (percent-escaped, as an id writes them), the unit quantity, and the unit of a curve of that
    /// type. The curve types are the well database's own, named as OSDU names a company's (<c>WellDB:GR</c>, as OSDU's
    /// values hold <c>Schlumberger:GR</c>), since a partition defines its LogCurveType values itself; the families, main
    /// families, quantities and units are OSDU's published reference values. The comment is the word null where an entry
    /// has none, as the export writes it. The dictionary lists the mnemonics the sample logs carry, and one mnemonic the
    /// well database files under another (GRC under GR).
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> CurveDictionaryRows() => Rows(CurveDictionaryColumns,
    [
        ["MD", "WellDB:MD", "Reference", "Measured%20Depth", "length", "m", "null"],
        ["BS", "WellDB:BS", "BoreholeProperties", "Bit%20Size", "length", "in", "null"],
        ["CALI", "WellDB:CALI", "BoreholeProperties", "Caliper", "length", "in", "null"],
        ["GR", "WellDB:GR", "GammaRay", "Gamma%20Ray", "API%20gamma%20ray", "gAPI", "null"],
        ["GRC", "WellDB:GR", "GammaRay", "Gamma%20Ray", "API%20gamma%20ray", "gAPI", "Environmentally corrected gamma ray; filed as GR"],
        ["RD", "WellDB:RD", "Resistivity", "Resistivity%20-%20Deep", "electrical%20resistivity", "ohm.m", "null"],
        ["RHOB", "WellDB:RHOB", "Density", "Bulk%20Density", "mass%20per%20volume", "g/cm3", "null"],
        ["DRHO", "WellDB:DRHO", "Density", "Bulk%20Density%20Correction", "mass%20per%20volume", "g/cm3", "null"],
        ["NPHI", "WellDB:NPHI", "Porosity", "Neutron%20Porosity", "dimensionless", "m3/m3", "null"],
        ["DT", "WellDB:DT", "Slowness", "Compressional%20Slowness", "time%20per%20length", "us/ft", "null"],
        ["DTS", "WellDB:DTS", "Slowness", "Shear%20Slowness", "time%20per%20length", "us/ft", "null"],
        ["PEF", "WellDB:PEF", "PhotoelectricFactor", "Photoelectric%20Factor", "dimensionless", "Euc", "null"],
    ]);

    /// <summary>
    /// The reference values the sample's mappings write as fixed codes rather than read from a source column, by entity
    /// type: a log's type, its sampling domain and its technical assurance, the curves' business value, and the point a
    /// well's elevation is measured from. With the codes the lookup tables translate to, they are the OSDU reference
    /// values the sample's records refer to, and the sample delivers each of them (<see cref="OsduReferenceValues"/>).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> MappedCodes { get; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["LogType"] = ["Interpreted"],
        ["WellLogSamplingDomainType"] = ["Depth"],
        ["TechnicalAssuranceType"] = ["Unevaluated"],
        ["LogCurveBusinessValue"] = ["High"],
        ["VerticalMeasurementType"] = ["KellyBushing"],
    };

    /// <summary>The columns of the well database's curve type file.</summary>
    public static IReadOnlyList<string> LogCurveTypeColumns { get; } =
        ["code", "name", "description", "unit_quantity", "attribution_authority", "commit_date"];

    /// <summary>
    /// The curve types the well database defines, one row per type its curve dictionary files a mnemonic under: the code
    /// (also its name, as OSDU's curve types are named), a description in the form OSDU's curve types carry, and the unit
    /// quantity a curve of the type is measured in, as the code of an OSDU UnitQuantity record.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> LogCurveTypeRows()
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        foreach (var entry in CurveDictionaryRows().GroupBy(r => r["log_curve_type_id"]!, StringComparer.Ordinal))
        {
            var row = entry.First();
            var mnemonic = entry.Key[(entry.Key.IndexOf(':', StringComparison.Ordinal) + 1)..];
            var quantity = Uri.UnescapeDataString(row["unit_quantity_id"]!);
            var name = mnemonic == SampleWellLogs.IndexCurveId ? "Measured Depth" : CurveTypes[mnemonic].Description;
            rows.Add(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["code"] = entry.Key,
                ["name"] = entry.Key,
                ["description"] = $"{name}; Company: WellDB; UnitQuantity: {quantity}",
                ["unit_quantity"] = quantity,
                ["attribution_authority"] = SourceSystem,
                ["commit_date"] = "2026-09-01T00:00:00Z",
            });
        }

        return rows;
    }

    /// <summary>The columns of the well file.</summary>
    public static IReadOnlyList<string> WellColumns { get; } = ["well_name", "elevation", "elevation_unit", "elevation_reference", "update_date"];

    /// <summary>The columns of the wellbore file.</summary>
    public static IReadOnlyList<string> WellboreColumns { get; } = ["wellbore_name", "well_name", "update_date"];

    /// <summary>The folder under the data root the pre flow reads the well file from.</summary>
    public const string WellFolder = "well";

    /// <summary>The folder under the data root the pre flow reads the wellbore file from.</summary>
    public const string WellboreFolder = "wellbore";

    /// <summary>The name a well of the estate has: its letter.</summary>
    public static string WellName(int well)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(well);
        return string.Create(CultureInfo.InvariantCulture, $"Well {(char)('A' + well)}");
    }

    /// <summary>
    /// The wells and wellbores the first <paramref name="count"/> logs of <paramref name="seed"/> are logged in: one well
    /// row per well, with the elevation its logs are measured from and when the well database last changed it, and one
    /// wellbore row per wellbore, naming its well. Both are in the order a well database lists them (by name), and every
    /// log's wellbore and every wellbore's well is a row here, so the master data and the logs agree by construction.
    /// </summary>
    public static (IReadOnlyList<IReadOnlyDictionary<string, string?>> Wells, IReadOnlyList<IReadOnlyDictionary<string, string?>> Wellbores) MasterData(int count, ulong seed)
    {
        if (count is < 1 or > MaxLogs)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, $"A sample estate holds between 1 and {MaxLogs} logs.");
        }

        var wells = new SortedDictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        var wellbores = new SortedDictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var (well, _, _) = WellboreOf(index);
            var (elevation, updated) = WellFacts(well, seed);
            var stamp = updated.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            wells[WellName(well)] = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["well_name"] = WellName(well),
                ["elevation"] = elevation.ToString("0.0", CultureInfo.InvariantCulture),
                ["elevation_unit"] = "M",
                ["elevation_reference"] = "KB",
                ["update_date"] = stamp,
            };
            wellbores[WellboreName(index)] = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["wellbore_name"] = WellboreName(index),
                ["well_name"] = WellName(well),
                ["update_date"] = stamp,
            };
        }

        return ([.. wells.Values], [.. wellbores.Values]);
    }

    /// <summary>Writes the well and wellbore files under <paramref name="dataRoot"/>, named for <paramref name="date"/> (yyyyMMdd).</summary>
    public static async Task WriteMasterDataAsync(string dataRoot, int count, ulong seed, string date, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(date);
        var (wells, wellbores) = MasterData(count, seed);
        await SampleWellLogs.WriteCsvAsync(Path.Combine(dataRoot, WellFolder, $"well_{date}.csv"), WellColumns, wells, ct).ConfigureAwait(false);
        await SampleWellLogs.WriteCsvAsync(Path.Combine(dataRoot, WellboreFolder, $"wellbore_{date}.csv"), WellboreColumns, wellbores, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the lookup tables under <paramref name="cacheDataRoot"/>: one file per table in a folder of its own, named for
    /// <paramref name="date"/> (yyyyMMdd), as the lookup tables' pre-ingestion flows read them.
    /// </summary>
    public static async Task WriteLookupsAsync(string cacheDataRoot, string date, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(date);
        await SampleWellLogs.WriteCsvAsync(LookupFile(cacheDataRoot, UnitAliasFolder, "unit_alias", date), UnitAliasColumns, UnitAliasRows(), ct).ConfigureAwait(false);
        await SampleWellLogs.WriteCsvAsync(LookupFile(cacheDataRoot, DepthUnitAliasFolder, "depth_unit_alias", date), UnitAliasColumns, DepthUnitAliasRows(), ct).ConfigureAwait(false);
        await SampleWellLogs.WriteCsvAsync(LookupFile(cacheDataRoot, CurveDictionaryFolder, "curve_dictionary", date), CurveDictionaryColumns, CurveDictionaryRows(), ct).ConfigureAwait(false);
    }

    /// <summary>The name a wellbore of the estate has: its well's letter, its number, and its sidetrack when it has one.</summary>
    public static string WellboreName(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var (well, wellbore, sidetrack) = WellboreOf(index);
        var name = string.Create(CultureInfo.InvariantCulture, $"Wellbore {(char)('A' + well)}-{wellbore}");
        return sidetrack is null ? name : name + " " + sidetrack;
    }

    /// <summary>
    /// The id of the log at <paramref name="index"/>: the log and its version, as the well database writes them, so
    /// LOG-0001/1 for the first. The slash is what a key part, an OSDU id made from a key and a payload folder must each
    /// carry through (escaped, or as an underscore in a folder name).
    /// </summary>
    public static string LogId(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return string.Create(CultureInfo.InvariantCulture, $"LOG-{index + 1:D4}/1");
    }

    /// <summary>
    /// The properties every sample survey's stations carry, as the well database's survey metadata names them: the column
    /// in the payload file, the code of the TrajectoryStationPropertyType record it is, and the unit as the well database
    /// writes it. The measured depth comes first, as the DDMS reads it as the reference the survey's top and base agree on.
    /// </summary>
    public static IReadOnlyList<SampleStationProperty> StationProperties { get; } =
    [
        new("MD", "MD", "M"),
        new("INC", "Inclination", "DEG"),
        new("AZI", "AzimuthTN", "DEG"),
    ];

    /// <summary>How far apart a survey's stations are along the hole, in metres: a station every hundred feet or so.</summary>
    public const double StationInterval = 30;

    /// <summary>The formation tops the well database picks, shallowest first; a marker set takes the first three or four.</summary>
    private static readonly string[] Tops = ["Top Alder", "Top Birch", "Top Cedar", "Top Dogwood"];

    /// <summary>The id of the survey of the wellbore of the log at <paramref name="index"/>, written as a log id is.</summary>
    public static string SurveyId(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return string.Create(CultureInfo.InvariantCulture, $"SRV-{index + 1:D4}/1");
    }

    /// <summary>The id of the marker set of the wellbore of the log at <paramref name="index"/>.</summary>
    public static string MarkerSetId(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return string.Create(CultureInfo.InvariantCulture, $"MKS-{index + 1:D4}/1");
    }

    /// <summary>
    /// The directional surveys of the wellbores the first <paramref name="count"/> logs of <paramref name="seed"/> are
    /// logged in: one per wellbore, in the order a well database lists them (by wellbore). Each survey runs from the
    /// surface to the first station at or below the deepest point its wellbore's log reaches, a station every
    /// <see cref="StationInterval"/> metres: straight down to a kick-off point, then building angle at a steady rate to a
    /// tangent it holds, the azimuth drifting a little from station to station. A survey's values depend on the seed and
    /// its wellbore's place alone, so asking for more logs leaves the earlier surveys as they were.
    /// </summary>
    public static IReadOnlyList<SampleSurvey> Surveys(int count, ulong seed)
    {
        if (count is < 1 or > MaxLogs)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, $"A sample estate holds between 1 and {MaxLogs} logs.");
        }

        var surveys = new List<SampleSurvey>(count);
        for (var index = 0; index < count; index++)
        {
            surveys.Add(Survey(index, seed));
        }

        return [.. surveys.OrderBy(s => s.WellboreUwi, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The formation tops the well database picked in the wellbores the first <paramref name="count"/> logs of
    /// <paramref name="seed"/> are logged in: one marker set per wellbore, three or four tops each, every top deeper than the
    /// one above it and above the wellbore's total depth.
    /// </summary>
    public static IReadOnlyList<SampleMarkerSet> MarkerSets(int count, ulong seed)
    {
        if (count is < 1 or > MaxLogs)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, $"A sample estate holds between 1 and {MaxLogs} logs.");
        }

        var sets = new List<SampleMarkerSet>(count);
        for (var index = 0; index < count; index++)
        {
            sets.Add(MarkerSet(index, seed));
        }

        return [.. sets.OrderBy(s => s.WellboreUwi, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Writes the surveys and marker sets of the first <paramref name="count"/> logs' wellbores under
    /// <paramref name="dataRoot"/>, named for <paramref name="date"/> (yyyyMMdd).
    /// </summary>
    public static Task WriteSurveysAsync(string dataRoot, int count, ulong seed, string date, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(date);
        return SampleSurveys.WriteAsync(dataRoot, Surveys(count, seed), MarkerSets(count, seed), date, ct);
    }

    private static SampleSurvey Survey(int index, ulong seed)
    {
        // Each survey draws from a stream of its own, apart from its wellbore's log's, so the log's values stay as they were.
        var random = new SplitMix64(seed ^ (0xD1B54A32D192ED03UL * (ulong)(index + 1)));
        var log = WellLog(index, seed);
        var (well, _, _) = WellboreOf(index);
        var (elevation, updated) = WellFacts(well, seed);

        var stations = (int)Math.Ceiling(log.IndexMax / StationInterval) + 1;
        var kickoff = StationInterval * Math.Round(random.Between(10, 30));
        var buildPerStation = random.Between(1.5, 3.0);
        var tangent = random.Between(25, 55);
        var azimuth = random.Between(0, 360);
        var rows = new List<IReadOnlyList<double>>(stations);
        for (var i = 0; i < stations; i++)
        {
            var md = i * StationInterval;
            if (i == 0)
            {
                // The surface station: the survey's tie-on, straight down.
                rows.Add([md, 0, 0]);
            }
            else if (md <= kickoff)
            {
                // A vertical hole wanders a fraction of a degree, its azimuth anywhere.
                rows.Add([md, Angle(Math.Min(Math.Abs(0.25 * random.Normal()), 1)), Azimuth(random.Between(0, 360))]);
            }
            else
            {
                var inclination = Math.Clamp(Math.Min(tangent, (md - kickoff) / StationInterval * buildPerStation) + (0.15 * random.Normal()), 0, 90);
                azimuth = (azimuth + (0.4 * random.Normal()) + 360) % 360;
                rows.Add([md, Angle(inclination), Azimuth(azimuth)]);
            }
        }

        return new SampleSurvey(
            Project, SurveyId(index), "Definitive survey", "Definitive", "1", WellboreName(index),
            elevation.ToString("0.0", CultureInfo.InvariantCulture) + " M", updated, StationProperties, rows);
    }

    private static SampleMarkerSet MarkerSet(int index, ulong seed)
    {
        var random = new SplitMix64(seed ^ (0x8CB92BA72F3D8DD7UL * (ulong)(index + 1)));
        var log = WellLog(index, seed);
        var (well, _, _) = WellboreOf(index);
        var (elevation, updated) = WellFacts(well, seed);

        // The tops sit in the lower two thirds of the hole, each a little deeper than the one above it.
        var total = Math.Ceiling(log.IndexMax / StationInterval) * StationInterval;
        var picks = random.Between(0, 1) < 0.5 ? 3 : 4;
        var depth = total * random.Between(0.3, 0.45);
        var markers = new List<SampleMarker>(picks);
        for (var i = 0; i < picks; i++)
        {
            markers.Add(new SampleMarker(Tops[i], Math.Round(depth, 1, MidpointRounding.AwayFromZero)));
            depth += total * random.Between(0.08, 0.14);
        }

        return new SampleMarkerSet(
            Project, MarkerSetId(index), "WellDB formation tops", WellboreName(index),
            elevation.ToString("0.0", CultureInfo.InvariantCulture) + " M", updated, markers);
    }

    /// <summary>An angle as a survey export writes it: degrees, to two decimals.</summary>
    private static double Angle(double degrees) => Math.Round(degrees, 2, MidpointRounding.AwayFromZero);

    /// <summary>An azimuth as a survey export writes it: an angle from 0 up to, but not including, 360 degrees.</summary>
    private static double Azimuth(double degrees) => Angle(degrees) % 360;

    private static string LookupFile(string root, string folder, string stem, string date) => Path.Combine(root, folder, $"{stem}_{date}.csv");

    private static IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows(IReadOnlyList<string> columns, string[][] cells)
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>(cells.Length);
        foreach (var row in cells)
        {
            if (row.Length != columns.Count)
            {
                throw new InvalidOperationException($"A lookup row of the catalog has {row.Length} cells where the table has {columns.Count} columns: {string.Join(",", row)}.");
            }

            rows.Add(columns.Select((column, i) => (column, row[i])).ToDictionary(c => c.column, c => (string?)c.Item2, StringComparer.Ordinal));
        }

        return rows;
    }

    /// <summary>The wellbore of the log at <paramref name="index"/>: each round of five takes the next three well letters.</summary>
    private static (int Well, int Wellbore, string? Sidetrack) WellboreOf(int index)
    {
        var (well, wellbore, sidetrack) = Wellbores[index % Wellbores.Length];
        return (well + (3 * (index / Wellbores.Length)), wellbore, sidetrack);
    }

    /// <summary>
    /// What belongs to a well: the elevation its logs are measured from and the moment the well database last changed it,
    /// drawn from a stream of the well's own, so every log and the well row agree.
    /// </summary>
    private static (double Elevation, DateTime Updated) WellFacts(int well, ulong seed)
    {
        var wellRandom = new SplitMix64(seed ^ (0xC2B2AE3D27D4EB4FUL * (ulong)(well + 1)));
        var elevation = Math.Round(wellRandom.Between(20, 90), 1, MidpointRounding.AwayFromZero);
        var updated = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc).AddSeconds(Math.Floor(wellRandom.Between(0, 7200)));
        return (elevation, updated);
    }

    private static SampleLog WellLog(int index, ulong seed)
    {
        // Each log draws from a stream of its own, so its values depend on the seed and its position alone.
        var random = new SplitMix64(seed ^ (0x9E3779B97F4A7C15UL * (ulong)(index + 1)));
        var (well, _, _) = WellboreOf(index);
        var suite = Suites[index % Suites.Length];
        var logId = LogId(index);

        // The wellbore's elevation and the moment the log last changed belong to the well, so the two sidetracks of one
        // wellbore were measured from the same point and updated together.
        var (elevation, updated) = WellFacts(well, seed);
        var recorded = new DateTime(2021, 11, 10, 6, 0, 0, DateTimeKind.Utc)
            .AddDays(index * 2)
            .AddSeconds(Math.Floor(random.Between(0, 43200)));

        // The log's interval: a whole number of increments below the surface, a few hundred samples long.
        var rows = (int)random.Between(120, 460);
        var startStep = (long)random.Between(9_000, 21_000);
        var curves = new List<SampleCurve>(suite.Length + 1);
        var placed = new List<(string CurveId, int First, int Last)>(suite.Length);
        foreach (var curveId in suite)
        {
            // One curve of the suite spans the whole interval; the others start and end a few samples in.
            var first = placed.Count == 0 ? 0 : (int)random.Between(0, 12);
            var last = placed.Count == 0 ? rows - 1 : rows - 1 - (int)random.Between(0, 12);
            placed.Add((curveId, first, last));
        }

        var depths = Enumerable.Range(0, rows).Select(i => Depth((startStep + i) * Increment)).ToList();

        // The header states what the chunk holds. The first log of every round keeps a draw it no longer uses, so its curve
        // values stay the ones its seed has always named.
        if (index % Suites.Length == 0)
        {
            random.Next();
        }

        var indexMin = depths[0];
        var indexMax = depths[^1];

        curves.Add(new SampleCurve(SampleWellLogs.IndexCurveId, "M", "Measured depth", "HIGH", indexMin, indexMax, null, null));
        var values = new Dictionary<string, IReadOnlyList<double?>>(StringComparer.Ordinal);
        var bitSize = random.Between(0, 1) < 0.5 ? 8.5 : 12.25;
        for (var c = 0; c < placed.Count; c++)
        {
            var (curveId, first, last) = placed[c];
            var type = CurveTypes[curveId];
            values[curveId] = Series(type, rows, first, last, bitSize, random);

            // A curve the source holds no business value for is written without one; the index curve always has one.
            var businessValue = random.Between(0, 1) < 0.3 ? null : "HIGH";
            var stamp = recorded.AddSeconds(c / 2).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            curves.Add(new SampleCurve(curveId, type.Unit, type.Description, businessValue, depths[first], depths[last], "1", stamp));
        }

        return new SampleLog(
            Project, logId, WellboreName(index), "COMPOSITE", "C", SourceSystem, "DEPTH", "M",
            indexMin, indexMax, Increment, "REGULAR",
            elevation.ToString("0.0", CultureInfo.InvariantCulture) + " M", "KB", SourceSystem, "HYBRID", "1", "INTERPRETED", SourceSystem,
            "C", "FINAL", logId, updated, curves, depths, values);
    }

    /// <summary>
    /// One curve's values over the grid: a smooth walk through the curve type's physical range between its first and last
    /// sample, and a gap at every other depth. Values are rounded to four decimals, as an export writes them.
    /// </summary>
    private static List<double?> Series(CurveType type, int rows, int first, int last, double bitSize, SplitMix64 random)
    {
        var values = new double?[rows];
        var level = random.Between(0.2, 0.8);
        for (var i = first; i <= last; i++)
        {
            // A mean-reverting walk in the unit interval: beds come and go without the curve leaving its range.
            level = Math.Clamp(level + (0.15 * (0.5 - level)) + (0.06 * random.Normal()), 0, 1);
            var value = type.Shape switch
            {
                Shape.Constant => bitSize,
                Shape.AboveBitSize => bitSize + type.Min + (level * (type.Max - type.Min)),
                Shape.Logarithmic => Math.Exp(Math.Log(type.Min) + (level * (Math.Log(type.Max) - Math.Log(type.Min)))),
                _ => type.Min + (level * (type.Max - type.Min)),
            };
            values[i] = Math.Round(value, 4, MidpointRounding.AwayFromZero);
        }

        return [.. values];
    }

    private static double Depth(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private enum Shape
    {
        Linear,
        Logarithmic,
        Constant,
        AboveBitSize,
    }

    private sealed record CurveType(string Unit, string Description, double Min, double Max, Shape Shape);

    /// <summary>
    /// SplitMix64: a small generator whose sequence is fixed by its seed on every platform and runtime, which
    /// <see cref="Random"/> does not promise, so a seed names the same estate wherever the generator runs.
    /// </summary>
    private sealed class SplitMix64(ulong seed)
    {
        private ulong _state = seed;

        private bool _hasSpare;

        private double _spare;

        /// <summary>The next value, uniform in [0, 1).</summary>
        public double Next()
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 11) * (1.0 / (1UL << 53));
        }

        /// <summary>A value uniform in [<paramref name="min"/>, <paramref name="max"/>).</summary>
        public double Between(double min, double max) => min + (Next() * (max - min));

        /// <summary>A value of the standard normal distribution (Box-Muller, the second value of a pair kept for the next call).</summary>
        public double Normal()
        {
            if (_hasSpare)
            {
                _hasSpare = false;
                return _spare;
            }

            var u1 = 1.0 - Next();
            var u2 = Next();
            var radius = Math.Sqrt(-2.0 * Math.Log(u1));
            _spare = radius * Math.Sin(2.0 * Math.PI * u2);
            _hasSpare = true;
            return radius * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}
