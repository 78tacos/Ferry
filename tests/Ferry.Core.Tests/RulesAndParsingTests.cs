using Ferry.Core.Infrastructure;
using Ferry.Core.Migrators.Apps;
using Ferry.Core.Migrators.Windows;
using Ferry.Core.Transport;

namespace Ferry.Core.Tests;

public class ExclusionRulesTests
{
    [Theory]
    [InlineData("Thumbs.db", true)]
    [InlineData(@"Photos\2024\Thumbs.db", true)]
    [InlineData(@"Docs\~$report.docx", true)]
    [InlineData(@"Docs\report.docx", false)]
    [InlineData(@"x\.claude.json.tmp.2324.abc", true)]
    [InlineData(@"cache.tmp", true)]
    public void BuiltInJunkIsExcluded(string path, bool excluded) => Assert.Equal(excluded, new ExclusionRules().IsExcluded(path));

    [Fact]
    public void PathPatternsMatchAcrossSegments()
    {
        var rules = new ExclusionRules(["**\\Cache", @"Downloads\*.exe", "node_modules"], includeBuiltIn: false);
        Assert.True(rules.IsExcluded(@"Code\User\Cache"));
        Assert.True(rules.IsExcluded("Cache"));
        Assert.False(rules.IsExcluded(@"Code\User\Cache2"));
        Assert.True(rules.IsExcluded(@"Downloads\setup.exe"));
        Assert.False(rules.IsExcluded(@"Downloads\sub\setup.exe"));
        Assert.True(rules.IsExcluded(@"src\app\node_modules"));
    }

    [Fact]
    public void LeadingBackslashAnchorsToTheRoot()
    {
        var rules = new ExclusionRules([@"\desktop.ini"], includeBuiltIn: false);
        Assert.True(rules.IsExcluded("desktop.ini"));
        Assert.False(rules.IsExcluded(@"Projects\desktop.ini"));
        Assert.True(rules.With(["*.log"]).IsExcluded("desktop.ini"));
    }
}

public class PathRemapperTests
{
    private static PathRemapper Remapper() => new PathRemapper()
        .Add(@"C:\Users\joe", @"C:\Users\joseph")
        .Add(@"C:\Users\joe\Documents", @"C:\Users\joseph\OneDrive - Contoso\Documents");

    [Theory]
    [InlineData(@"C:\Users\joe\Desktop\a.txt", @"C:\Users\joseph\Desktop\a.txt")]
    [InlineData(@"c:\users\JOE\documents\x.docx", @"C:\Users\joseph\OneDrive - Contoso\Documents\x.docx")]
    [InlineData(@"""C:\Users\joe\app.exe"" --flag", @"""C:\Users\joseph\app.exe"" --flag")]
    [InlineData(@"C:\Users\joey\Desktop", @"C:\Users\joey\Desktop")]
    [InlineData(@"C:\Users\joe", @"C:\Users\joseph")]
    [InlineData(@"C:/Users/joe/Downloads", @"C:/Users/joseph/Downloads")]
    [InlineData(@"{""dir"":""C:\\Users\\joe\\Downloads""}", @"{""dir"":""C:\\Users\\joseph\\Downloads""}")]
    [InlineData(@"PATH=C:\Users\joe\bin;C:\Tools", @"PATH=C:\Users\joseph\bin;C:\Tools")]
    [InlineData("nothing to see", "nothing to see")]
    public void RewritesOnlyWholePathPrefixes(string input, string expected) => Assert.Equal(expected, Remapper().Remap(input));

    [Fact]
    public void UnchangedStringsAreReturnedAsTheSameInstance()
    {
        var value = @"D:\Data\file.txt";
        Assert.Same(value, Remapper().Remap(value));
    }

    [Fact]
    public void RemapPrefixOnlyAppliesAtTheStart()
    {
        Assert.Equal(@"C:\Users\joseph\x", Remapper().RemapPrefix(@"C:\Users\joe\x"));
        Assert.Null(Remapper().RemapPrefix(@"D:\C:\Users\joe\x"));
    }
}

public class WingetParsingTests
{
    private const string Output =
        "\r   - \r   \\ \r" +
        "Name                       Id                          Version      Available    Source\r\n" +
        "----------------------------------------------------------------------------------------\r\n" +
        "1Password                  AgileBits.1Password         > 8.12.36.40              winget\r\n" +
        "Brave                      ARP\\Machine\\X86\\BraveSoftwa… 155.1.97.57\r\n" +
        "Adobe Acrobat (64-bit)     Adobe.Acrobat.Pro           26.002.22031 26.003.1     winget\r\n" +
        "Some Very Long Program Na… Vendor.SomeVeryLongProgram  1.0                       winget\r\n" +
        "WhatsApp                   9NKSQGP7F2NH                2.2.1                     msstore\r\n" +
        "\r\n" +
        "3 upgrades available.\r\n";

    [Fact]
    public void ParsesColumnsFromTheHeaderPositions()
    {
        var rows = Winget.ParseTable(Output);
        Assert.Equal(5, rows.Count);
        Assert.Equal(new WingetPackage("1Password", "AgileBits.1Password", "8.12.36.40", "winget"), rows[0]);
        Assert.Equal("", rows[1].Source);
        Assert.Equal("Adobe.Acrobat.Pro", rows[2].Id);
        Assert.Equal("26.002.22031", rows[2].Version);
        Assert.Equal("msstore", rows[4].Source);
    }

    [Fact]
    public void MatchesInstalledProgramsByExactAndTruncatedName()
    {
        var apps = new List<AppRecord>
        {
            new() { DisplayName = "Adobe Acrobat (64-bit)" },
            new() { DisplayName = "Some Very Long Program Name Pro" },
            new() { DisplayName = "Brave" },
            new() { DisplayName = "Mozilla Firefox (x64 en-US)" },
            new() { DisplayName = "Unknown Thing" },
        };
        AppCatalog.MatchWinget(apps, Winget.ParseTable(Output), new Dictionary<string, string>());
        Assert.Equal("Adobe.Acrobat.Pro", apps[0].WingetId);
        Assert.Equal("Vendor.SomeVeryLongProgram", apps[1].WingetId);
        Assert.Equal("Brave.Brave", apps[2].WingetId);
        Assert.Equal("Mozilla.Firefox", apps[3].WingetId);
        Assert.Null(apps[4].WingetId);
    }

    [Theory]
    [InlineData("Python 3.14.8", "Python.Python.3.14")]
    [InlineData("Mozilla Firefox ESR (x64 en-US)", "Mozilla.Firefox.ESR")]
    [InlineData("7-Zip 24.08 (x64)", "7zip.7zip")]
    [InlineData("Zoom Workplace (64-bit)", "Zoom.Zoom")]
    public void KnownProgramsMapToStableIds(string name, string id) => Assert.Equal(id, KnownWingetIds.Lookup(name)?.Id);

    [Theory]
    [InlineData("Notepad++ (64-bit x64)", "notepad")]
    [InlineData("7-Zip 23.01 (x64)", "7zip")]
    [InlineData("Mozilla Firefox (x64 en-US)", "mozillafirefox")]
    public void CoreNameDropsVersionsAndArchitectures(string name, string core) => Assert.Equal(core, AppCatalog.CoreName(name));
}

public class MiscParsingTests
{
    [Fact]
    public void PairingCodesAreTenUnambiguousCharacters()
    {
        for (var i = 0; i < 50; i++)
        {
            var code = PairingCode.Generate();
            Assert.Matches("^[A-HJ-NP-Z2-9]{5}-[A-HJ-NP-Z2-9]{5}$", code);
        }
        Assert.Equal(PairingCode.Normalize("K7QMX-2XRP9"), PairingCode.Normalize(" k7qmx 2xrp9 "));
    }

    [Fact]
    public void PathMergeAddsOnlyNewFoldersThatExist()
    {
        var existing = TestEnvironment.NewFolder("pathmerge");
        var merged = EnvironmentMigrator.MergePath(@"C:\Windows;C:\Tools\", $@"C:\Tools;{existing};C:\Does\Not\Exist;%FERRY_TEST_UNSET_VARIABLE%\bin", out var added);
        Assert.Equal([existing, @"%FERRY_TEST_UNSET_VARIABLE%\bin"], added);
        Assert.Equal($@"C:\Windows;C:\Tools\;{existing};%FERRY_TEST_UNSET_VARIABLE%\bin", merged);
    }

    [Fact]
    public void IniFilesRoundTrip()
    {
        var ini = IniFile.Parse("[General]\nStartWithLastProfile=1\n\n[Profile0]\nName=default-release\nIsRelative=1\nPath=Profiles/abc.default-release\nDefault=1\n");
        Assert.Equal("Profiles/abc.default-release", ini.Get("Profile0", "Path"));
        ini.Set("Profile1", "Name", "moved");
        ini.Set("Profile0", "Default", "0");
        var again = IniFile.Parse(ini.ToString());
        Assert.Equal("moved", again.Get("Profile1", "Name"));
        Assert.Equal("0", again.Get("profile0", "default"));
    }

    [Theory]
    [InlineData("4GB", 4L << 30)]
    [InlineData("500 MB", 500L << 20)]
    [InlineData("1.5g", 3L << 29)]
    [InlineData("1000", 1000)]
    public void SizesParse(string text, long bytes) => Assert.Equal(bytes, Format.ParseSize(text));
}
