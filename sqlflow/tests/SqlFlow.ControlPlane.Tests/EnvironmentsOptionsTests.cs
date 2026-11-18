using SqlFlow.ControlPlane.Configuration;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The environments the title bar switches between (<c>ControlPlane:Environments</c>), as <see cref="EnvironmentsOptions.Read"/>
/// reads them. Nothing configured shows nothing and says nothing; the current one may be named alone. A mistake never
/// throws: an entry the menu cannot show is left out, a description it cannot show is dropped, and when the environment
/// this control plane is cannot be told nothing is shown, each with a sentence naming the setting and never repeating an
/// address.
/// </summary>
public sealed class EnvironmentsOptionsTests
{
    private static EnvironmentLinkOptions Link(string? name, string? url, string? description = null)
        => new() { Name = name, Url = url, Description = description };

    private static EnvironmentsOptions Estate(string? current = "test") => new()
    {
        Current = current,
        Links =
        [
            Link("dev", "https://sqlflow-dev.example.com", "Development"),
            Link("test", "https://sqlflow-test.example.com/", "Acceptance testing"),
            Link("prod", "https://sqlflow.example.com/workbench", "Production"),
        ],
    };

    private static string[] Names(ConfiguredEnvironments read) => [.. read.Environments.Select(e => e.Name)];

    private static void ShowsNothing(ConfiguredEnvironments read)
    {
        Assert.Null(read.Current);
        Assert.Empty(read.Environments);
    }

    [Fact]
    public void NothingConfigured_ShowsNothing_AndSaysNothing()
    {
        var read = new EnvironmentsOptions().Read();

        ShowsNothing(read);
        Assert.Empty(read.Problems);
    }

    [Fact]
    public void AListWithTheCurrentOneAmongIt_IsShownWhole_InItsOrder()
    {
        var read = Estate().Read();

        Assert.Equal("test", read.Current);
        ConfiguredEnvironment[] expected =
        [
            new("dev", "https://sqlflow-dev.example.com", "Development"),
            new("test", "https://sqlflow-test.example.com/", "Acceptance testing"),
            new("prod", "https://sqlflow.example.com/workbench", "Production"),
        ];
        Assert.Equal(expected, read.Environments);
        Assert.Empty(read.Problems);
    }

    [Fact]
    public void TheCurrentOne_IsSpelledAsTheListSpellsIt()
    {
        var read = Estate(" TEST ").Read();

        Assert.Equal("test", read.Current);
        Assert.Empty(read.Problems);
    }

    [Fact]
    public void TheCurrentOneAlone_IsNamed_WithNothingToOpen()
    {
        var read = new EnvironmentsOptions { Current = "prod" }.Read();

        Assert.Equal("prod", read.Current);
        Assert.Empty(read.Environments);
        Assert.Empty(read.Problems);
    }

    [Fact]
    public void AListThatNamesNoCurrentOne_ShowsNothing_AndSaysWhy()
    {
        var read = Estate(current: null).Read();

        ShowsNothing(read);
        var problem = Assert.Single(read.Problems);
        Assert.Contains("ControlPlane:Environments:Current names none of them", problem, StringComparison.Ordinal);
        Assert.Contains("dev, test, prod", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ACurrentOneTheListDoesNotGive_ShowsNothing_RatherThanALabelThatCouldBeWrong()
    {
        var read = Estate("stage").Read();

        ShowsNothing(read);
        var problem = Assert.Single(read.Problems);
        Assert.Contains("ControlPlane:Environments:Current is 'stage'", problem, StringComparison.Ordinal);
        Assert.Contains("(dev, test, prod)", problem, StringComparison.Ordinal);
        Assert.Contains("the title bar shows no environment", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pre prod")]
    [InlineData("-dev")]
    [InlineData("dev/1")]
    [InlineData("utvikling\u00e6")]
    public void ACurrentNameThatIsNoName_ShowsNothing(string current)
    {
        var read = new EnvironmentsOptions { Current = current }.Read();

        ShowsNothing(read);
        Assert.Contains("is not an environment name", Assert.Single(read.Problems), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("pre prod")]
    [InlineData(".dev")]
    public void AListedNameThatIsNoName_LeavesOnlyThatEntryOut(string? name)
    {
        var options = Estate("dev");
        options.Links[1] = Link(name, "https://sqlflow-test.example.com");

        var read = options.Read();

        Assert.Equal("dev", read.Current);
        Assert.Equal(["dev", "prod"], Names(read));
        var problem = Assert.Single(read.Problems);
        Assert.Contains("ControlPlane:Environments:Links:1:Name", problem, StringComparison.Ordinal);
        Assert.Contains("leaves the entry out", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameOfTheLongestLength_IsShown_AndOneLonger_IsLeftOut()
    {
        var longest = new string('e', EnvironmentsOptions.MaxNameLength);
        var options = new EnvironmentsOptions
        {
            Current = longest,
            Links = [Link(longest, "https://e.example.com"), Link(longest + "e", "https://f.example.com")],
        };

        var read = options.Read();

        Assert.Equal([longest], Names(read));
        Assert.Contains("ControlPlane:Environments:Links:1:Name", Assert.Single(read.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnvironmentListedAgain_IgnoringCase_KeepsItsFirstEntry()
    {
        var options = Estate();
        options.Links.Add(Link("PROD", "https://sqlflow-prod-2.example.com"));

        var read = options.Read();

        Assert.Equal(["dev", "test", "prod"], Names(read));
        Assert.Equal("https://sqlflow.example.com/workbench", read.Environments[2].Url);
        Assert.Contains("ControlPlane:Environments:Links:3:Name 'PROD'", Assert.Single(read.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyEntry_IsLeftOut()
    {
        var options = Estate();
        options.Links.Insert(0, null);

        var read = options.Read();

        Assert.Equal(["dev", "test", "prod"], Names(read));
        Assert.Contains("ControlPlane:Environments:Links:0 is empty", Assert.Single(read.Problems), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("sqlflow-prod.example.com")]
    [InlineData("/workbench")]
    [InlineData("ftp://sqlflow-prod.example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///c:/estate")]
    [InlineData("https://sqlflow-prod.example.com:99999")]
    [InlineData("https://sqlflow-prod.example.com/?partition=prod")]
    [InlineData("https://sqlflow-prod.example.com/#records")]
    public void AnAddressThatIsNoGuiAddress_LeavesItsEnvironmentOut_WithoutRepeatingIt(string? url)
    {
        var options = Estate();
        options.Links[2] = Link("prod", url);

        var read = options.Read();

        Assert.Equal("test", read.Current);
        Assert.Equal(["dev", "test"], Names(read));
        var problem = Assert.Single(read.Problems);
        Assert.Contains("ControlPlane:Environments:Links:2:Url", problem, StringComparison.Ordinal);
        Assert.Contains("leaves 'prod' out", problem, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(url))
        {
            Assert.DoesNotContain(url.Trim(), problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnAddressCarryingACredential_LeavesItsEnvironmentOut_WithoutRepeatingIt()
    {
        var options = Estate();
        options.Links[0] = Link("dev", "https://operator:s3cret-Pa55@sqlflow-dev.example.com");

        var read = options.Read();

        Assert.Equal(["test", "prod"], Names(read));
        var problem = Assert.Single(read.Problems);
        Assert.Contains("ControlPlane:Environments:Links:0:Url carries a user name or a password", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret-Pa55", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("operator", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCurrentOnesOwnBadAddress_LeavesItOutOfTheMenu_ButStillNamesIt()
    {
        var options = Estate();
        options.Links[1] = Link("test", "not an address");

        var read = options.Read();

        Assert.Equal("test", read.Current);
        Assert.Equal(["dev", "prod"], Names(read));
        Assert.Single(read.Problems);
    }

    [Fact]
    public void AnHttpAddress_IsShown_ForAnEstateServedWithoutTls()
    {
        var options = Estate();
        options.Links[0] = Link("dev", "http://localhost:5173");

        var read = options.Read();

        Assert.Equal("http://localhost:5173", read.Environments[0].Url);
        Assert.Empty(read.Problems);
    }

    [Fact]
    public void AnAddressLongerThanTheLongest_LeavesItsEnvironmentOut()
    {
        var options = Estate();
        options.Links[0] = Link("dev", "https://sqlflow-dev.example.com/" + new string('a', EnvironmentsOptions.MaxUrlLength));

        var read = options.Read();

        Assert.Equal(["test", "prod"], Names(read));
        Assert.Contains("ControlPlane:Environments:Links:0:Url is not an absolute http or https address", Assert.Single(read.Problems), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EnvironmentsOptions.MaxDescriptionLength + 1, "")]
    [InlineData(10, "\n")]
    [InlineData(10, "\t")]
    public void ADescriptionItCannotShow_IsDropped_AndItsEnvironmentKept(int length, string control)
    {
        var options = Estate();
        var half = new string('d', (length + 1) / 2);
        options.Links[0] = Link("dev", "https://sqlflow-dev.example.com", half + control + half);

        var read = options.Read();

        Assert.Equal(new ConfiguredEnvironment("dev", "https://sqlflow-dev.example.com", null), read.Environments[0]);
        Assert.Contains("ControlPlane:Environments:Links:0:Description", Assert.Single(read.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void ADescriptionOfTheLongestLength_IsShown()
    {
        var options = Estate();
        options.Links[0] = Link("dev", "https://sqlflow-dev.example.com", new string('d', EnvironmentsOptions.MaxDescriptionLength));

        var read = options.Read();

        Assert.Equal(EnvironmentsOptions.MaxDescriptionLength, read.Environments[0].Description!.Length);
        Assert.Empty(read.Problems);
    }

    [Fact]
    public void AListLongerThanTheMost_ShowsTheFirstOnes()
    {
        var options = new EnvironmentsOptions { Current = "env0" };
        for (var i = 0; i <= EnvironmentsOptions.MaxLinks; i++)
        {
            options.Links.Add(Link($"env{i}", $"https://env{i}.example.com"));
        }

        var read = options.Read();

        Assert.Equal(EnvironmentsOptions.MaxLinks, read.Environments.Count);
        Assert.Equal("env0", read.Current);
        Assert.Contains($"shows the first {EnvironmentsOptions.MaxLinks}", Assert.Single(read.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void AListWhoseEntriesAreAllUnreadable_ShowsNothing()
    {
        var options = new EnvironmentsOptions { Current = "dev", Links = [Link("pre prod", "https://a.example.com")] };

        var read = options.Read();

        ShowsNothing(read);
        Assert.Equal(2, read.Problems.Count);
        Assert.Contains("(no environment it could read)", read.Problems[1], StringComparison.Ordinal);
    }

    [Fact]
    public void TheControlPlanesOwnValidation_NeverRefusesTheEnvironments()
    {
        var options = new ControlPlaneOptions
        {
            Jwt = new JwtOptions { SigningKey = new string('k', 64) },
            Environments = new EnvironmentsOptions { Current = "stage", Links = [null, Link("pre prod", "javascript:alert(1)")] },
        };

        options.Validate();
    }
}
