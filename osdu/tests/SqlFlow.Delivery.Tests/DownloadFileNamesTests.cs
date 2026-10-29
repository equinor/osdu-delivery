using System.Security.Cryptography;
using System.Text;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine.Downloads;
using SqlFlow.Delivery.Storage;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The name of the file a download writes a record to (osdu/docs/reference/flow/download.md, File names): the id itself, with
/// the two characters of an id no file name carries everywhere written as two no id holds, so it reads as the id and turns
/// back into it; and, where the id cannot be its own name, a name that says so and is the id's alone.
/// </summary>
public sealed class DownloadFileNamesTests
{
    /// <summary>What every name holds: characters every file system, a blob's address and Spark take as written.</summary>
    private static bool Safe(string name) => name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '~' or '=' or '@');

    [Theory]
    [InlineData("dev:master-data--Wellbore:WB-0001", "dev~master-data--Wellbore~WB-0001.json")]
    [InlineData("opendes:dataset--File.Generic:8118591ee2", "opendes~dataset--File.Generic~8118591ee2.json")]
    [InlineData("dev:reference-data--UnitOfMeasure:ft%2Fs", "dev~reference-data--UnitOfMeasure~ft=2Fs.json")]
    [InlineData("dev:reference-data--UnitOfMeasure:m:", "dev~reference-data--UnitOfMeasure~m~.json")]
    [InlineData("dev:work-product-component--WellLog:a:b_c.d", "dev~work-product-component--WellLog~a~b_c.d.json")]
    public void An_id_is_its_own_name_with_colon_and_percent_written_as_no_id_writes_them(string id, string name)
    {
        Assert.Equal(name, DownloadFileNames.Of(id));
        Assert.Equal(id, DownloadFileNames.IdOf(name));
        Assert.True(Safe(name), name);
    }

    [Fact]
    public void No_two_ids_share_a_name_where_replacing_the_colon_with_an_underscore_would_merge_them()
    {
        // An underscore is an id's own character, so a name that wrote ':' as '_' would give these two one file.
        var colon = DownloadFileNames.Of("dev:x--Y:a:b")!;
        var underscore = DownloadFileNames.Of("dev:x--Y:a_b")!;

        Assert.NotEqual(colon, underscore);
        Assert.Equal("dev:x--Y:a:b", DownloadFileNames.IdOf(colon));
        Assert.Equal("dev:x--Y:a_b", DownloadFileNames.IdOf(underscore));
    }

    [Fact]
    public void Ids_that_differ_only_in_case_take_distinct_names_that_differ_beyond_their_case()
    {
        // The foot and the femtotesla: two records OSDU holds, one file on a folder of Windows or macOS by their own names.
        const string Foot = "dev:reference-data--UnitOfMeasure:ft";
        const string Femtotesla = "dev:reference-data--UnitOfMeasure:fT";
        Assert.Equal(DownloadFileNames.Of(Foot), DownloadFileNames.Of(Femtotesla), StringComparer.OrdinalIgnoreCase);

        var foot = DownloadFileNames.Distinct(Foot);
        var femtotesla = DownloadFileNames.Distinct(Femtotesla);

        Assert.NotEqual(foot, femtotesla, StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith("dev~reference-data--UnitOfMeasure~fT@", femtotesla, StringComparison.Ordinal);
        Assert.EndsWith(".json", femtotesla, StringComparison.Ordinal);
        Assert.Equal(DownloadFileNames.Distinct(Femtotesla), femtotesla);
        Assert.Null(DownloadFileNames.IdOf(femtotesla));

        // The hash is the start of the SHA-256 of the id in lower-case hex, so it reads alike on every machine and in either case.
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Femtotesla)))[..DownloadFileNames.HashDigits];
        Assert.Equal($"dev~reference-data--UnitOfMeasure~fT@{hash}.json", femtotesla);

        // As the download flow's page names the foot's file (osdu/docs/reference/flow/download.md, File names).
        Assert.Equal("dev~reference-data--UnitOfMeasure~ft@69ec6b78c414.json", foot);
    }

    [Fact]
    public void An_id_too_long_for_a_name_is_cut_to_fit_and_made_its_own_by_its_hash()
    {
        var id = "dev:work-product-component--WellLog:" + new string('x', 300);

        Assert.Null(DownloadFileNames.Of(id));
        var name = DownloadFileNames.Distinct(id);

        Assert.Equal(DownloadFileNames.MaxLength, name.Length);
        Assert.StartsWith("dev~work-product-component--WellLog~xxx", name, StringComparison.Ordinal);
        Assert.Matches("@[0-9a-f]{12}\\.json$", name);
        Assert.NotEqual(name, DownloadFileNames.Distinct(id + "y"));
        Assert.True(Safe(name), name);

        // The longest id that is its own name still is.
        var longest = "d:e:" + new string('x', DownloadFileNames.MaxLength - ".json".Length - 4);
        Assert.Equal(DownloadFileNames.MaxLength, DownloadFileNames.Of(longest)!.Length);
    }

    [Theory]
    [InlineData("dev:x--Y:a b")]
    [InlineData("dev:x--Y:a/b")]
    [InlineData("dev:x--Y:café")]
    [InlineData("dev:x--Y:a?b#c")]
    public void An_id_holding_a_character_outside_the_id_pattern_is_named_by_its_hash(string id)
    {
        Assert.Null(DownloadFileNames.Of(id));
        var name = DownloadFileNames.Distinct(id);

        Assert.True(Safe(name), name);
        Assert.StartsWith("dev~x--Y~", name, StringComparison.Ordinal);
        Assert.Contains("@", name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abfss://lake@welldbstorage.dfs.core.windows.net/osdu/records")]
    [InlineData("https://welldbstorage.blob.core.windows.net/lake/osdu/records")]
    public void Every_name_lands_in_an_Azure_Storage_folder_as_written(string location)
    {
        // The '@' of a name made by its hash is in the path, never taken for the container's '@' of an abfss authority.
        var names = new[]
        {
            DownloadFileNames.Of("dev:master-data--Wellbore:WB-0001")!,
            DownloadFileNames.Of("dev:reference-data--UnitOfMeasure:ft%2Fs")!,
            DownloadFileNames.Distinct("dev:reference-data--UnitOfMeasure:ft"),
        };

        foreach (var name in names)
        {
            var parsed = AzureBlobLocation.Parse(FileStoreRegistry.Join(FileStoreRegistry.Join(location, "Wellbores"), name));
            Assert.Equal(("welldbstorage", "lake", "osdu/records/Wellbores/" + name), (parsed.Account, parsed.Container, parsed.BlobPath));
        }
    }

    [Fact]
    public void Every_name_is_a_file_of_a_local_folder_as_written()
    {
        var folder = Path.Combine(Path.GetTempPath(), "osdu-download-names-" + Guid.NewGuid().ToString("N"));
        try
        {
            var names = new[]
            {
                DownloadFileNames.Of("dev:reference-data--UnitOfMeasure:ft%2Fs")!,
                DownloadFileNames.Of("dev:reference-data--UnitOfMeasure:fT")!,
                DownloadFileNames.Distinct("dev:reference-data--UnitOfMeasure:ft"),
                DownloadFileNames.Distinct("dev:work-product-component--WellLog:" + new string('x', 300)),
            };
            Directory.CreateDirectory(folder);
            foreach (var name in names)
            {
                File.WriteAllText(FileStoreRegistry.Join(folder, name), name);
            }

            // Four files, each holding what was written to it, whatever the folder's file system makes of case.
            Assert.Equal(names.Order(StringComparer.Ordinal), Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.All(names, name => Assert.Equal(name, File.ReadAllText(Path.Combine(folder, name))));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("dev~x--Y~a.txt")]
    [InlineData(".json")]
    [InlineData("dev~x--Y~a b.json")]
    [InlineData("dev~x--Y~a@0123456789ab.json")]
    public void A_name_that_is_no_ids_own_turns_back_into_no_id(string name)
        => Assert.Null(DownloadFileNames.IdOf(name));
}
