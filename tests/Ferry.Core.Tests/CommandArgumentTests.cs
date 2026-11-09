using System.Text;
using Ferry.Core.Infrastructure;
using Ferry.Core.Migrators.Apps;
using Ferry.Core.Migrators.Windows;

namespace Ferry.Core.Tests;

/// <summary>
/// netsh and printui keep their own quoting, so a quote, CR/LF, or a leading dash is refused.
/// sc.exe takes discrete arguments; the undo line is that list in Windows command-line form.
/// </summary>
public class CommandArgumentTests
{
    [Theory]
    [InlineData("Cafe WiFi", true)]
    [InlineData("it's fine", true)]
    [InlineData("My-Network", true)]
    [InlineData("カフェ", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData(" -still a name", true)]
    [InlineData("a\"b", false)]
    [InlineData("a\nb", false)]
    [InlineData("a\rb", false)]
    [InlineData("a\r\nb", false)]
    [InlineData("-flag", false)]
    [InlineData("-", false)]
    public void QuotedValuesRejectBreakoutCharacters(string? value, bool safe) =>
        Assert.Equal(safe, ProcessRunner.IsSafeQuotedValue(value));

    [Theory]
    [InlineData("Cafe WiFi")]
    [InlineData("Home")]
    [InlineData("Joe's (Guest)")]
    [InlineData("My-Network")]
    [InlineData("")]
    public void NormalWifiNamesKeepTheNetshQuoting(string name)
    {
        Assert.True(WifiMigrator.TryProfileArguments(name, out var show, out var delete));
        Assert.Equal($"wlan show profile name=\"{name}\"", show);
        Assert.Equal($"wlan delete profile name=\"{name}\"", delete);
    }

    [Theory]
    [InlineData("Cafe\"WiFi")]
    [InlineData("bad\nname")]
    [InlineData("bad\rname")]
    [InlineData("-hidden")]
    [InlineData("\"")]
    public void UnsafeWifiNamesAreRefused(string name)
    {
        Assert.False(WifiMigrator.TryProfileArguments(name, out var show, out var delete));
        Assert.Equal("", show);
        Assert.Equal("", delete);
    }

    [Theory]
    [InlineData(@"\\server\printer", true, "/in", "/dn")]
    [InlineData(@"\\server\printer", false, "/ga", "/gd")]
    [InlineData(@"\\server\Room 4", true, "/in", "/dn")]
    [InlineData(@"\\server\Joe's Printer", false, "/ga", "/gd")]
    public void NormalPrinterSharesKeepThePrintUiQuoting(string connection, bool currentUser, string add, string remove)
    {
        Assert.True(PrintersMigrator.TryConnectionArguments(connection, currentUser, out var args, out var undo));
        Assert.Equal($"printui.dll,PrintUIEntry {add} /q /n\"{connection}\"", args);
        Assert.Equal($"printui.dll,PrintUIEntry {remove} /q /n\"{connection}\"", undo);
    }

    [Theory]
    [InlineData("\\\\server\\bad\"name")]
    [InlineData("\\\\server\\a\nb")]
    [InlineData("\\\\server\\a\rb")]
    [InlineData("-\\\\server\\printer")]
    public void UnsafePrinterPathsAreRefused(string connection)
    {
        Assert.False(PrintersMigrator.TryConnectionArguments(connection, true, out var args, out var undo));
        Assert.Equal("", args);
        Assert.Equal("", undo);
        Assert.False(PrintersMigrator.TryConnectionArguments(connection, false, out _, out _));
    }

    [Theory]
    [InlineData("FerrySvc", "Ferry", @"C:\App\svc.exe", "demand", "own")]
    [InlineData("FerrySvc", "Ferry Service", @"C:\Program Files\Ferry\svc.exe", "auto", "share")]
    [InlineData("My-Service", "Widget \"Pro\"", @"C:\App\svc.exe", "auto", "own")]
    [InlineData("-dash", "-Monitor", @"C:\App\svc.exe", "demand", "own")]
    public void NormalServiceCommandsParseLikeThePreviousQuoting(string name, string displayName, string binPath, string start, string type)
    {
        var args = ApplicationsMigrator.ServiceCreateArguments(name, displayName, binPath, start, type);
        var formatted = ProcessRunner.FormatArguments(args);
        var previous = $"create \"{name}\" binPath= \"{binPath}\" start= {start} type= {type} DisplayName= \"{displayName.Replace("\"", "\\\"")}\"";
        Assert.Equal<string>(ParseWindowsArguments(previous), ParseWindowsArguments(formatted));
        Assert.Equal<string>(ParseWindowsArguments($"delete \"{name}\""), ParseWindowsArguments(ProcessRunner.FormatArguments(ApplicationsMigrator.ServiceDeleteArguments(name))));
    }

    [Fact]
    public void ServiceCreateKeepsTheSpaceScRequiresAfterEquals()
    {
        var args = ApplicationsMigrator.ServiceCreateArguments(
            "FerrySvc", "Ferry Service", @"C:\Program Files\Ferry\svc.exe", "auto", "own");
        Assert.Equal<string>(
            ["create", "FerrySvc", "binPath=", @"C:\Program Files\Ferry\svc.exe", "start=", "auto", "type=", "own", "DisplayName=", "Ferry Service"],
            args);
        Assert.Equal(
            @"create FerrySvc binPath= ""C:\Program Files\Ferry\svc.exe"" start= auto type= own DisplayName= ""Ferry Service""",
            ProcessRunner.FormatArguments(args));
    }

    [Fact]
    public void QuotedServiceNameDoesNotBecomeAnotherScOption()
    {
        var name = "ok\" binPath= C:\\Windows\\System32\\cmd.exe";
        var displayName = "Display\" start= auto";
        var args = ApplicationsMigrator.ServiceCreateArguments(name, displayName, @"C:\App\svc.exe", "demand", "own");
        Assert.Equal<string>(args, ParseWindowsArguments(ProcessRunner.FormatArguments(args)));

        var delete = ApplicationsMigrator.ServiceDeleteArguments(name);
        Assert.Equal<string>(delete, ParseWindowsArguments(ProcessRunner.FormatArguments(delete)));
        Assert.True(ParseWindowsArguments($"delete \"{name}\"").Length > 2);
    }

    [Theory]
    [InlineData("Spooler", "delete Spooler")]
    [InlineData("Print Spooler", "delete \"Print Spooler\"")]
    [InlineData("foo\"bar", "delete \"foo\\\"bar\"")]
    [InlineData("line\nbreak", "delete \"line\nbreak\"")]
    [InlineData("-dash", "delete -dash")]
    [InlineData("", "delete \"\"")]
    public void ServiceDeleteUndoIsTheArgumentListForm(string name, string expected)
    {
        var argv = ApplicationsMigrator.ServiceDeleteArguments(name);
        var command = ProcessRunner.FormatArguments(argv);
        Assert.Equal(expected, command);
        Assert.Equal<string>(argv, ParseWindowsArguments(command));
    }

    /// <summary>CommandLineToArgvW rules, including the post-2008 "" inside quotes, so the undo line can be checked on Linux.</summary>
    private static string[] ParseWindowsArguments(string commandLine)
    {
        var args = new List<string>();
        var i = 0;
        while (i < commandLine.Length)
        {
            while (i < commandLine.Length && commandLine[i] is ' ' or '\t')
                i++;
            if (i >= commandLine.Length)
                break;

            var current = new StringBuilder();
            var inQuotes = false;
            while (i < commandLine.Length)
            {
                var slashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    slashes++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 0)
                    {
                        if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                        {
                            current.Append('"');
                            i += 2;
                        }
                        else
                        {
                            inQuotes = !inQuotes;
                            i++;
                        }
                    }
                    else
                    {
                        current.Append('"');
                        i++;
                    }
                }
                else
                {
                    if (slashes > 0)
                        current.Append('\\', slashes);
                    if (i >= commandLine.Length)
                        break;
                    var c = commandLine[i];
                    if (!inQuotes && c is ' ' or '\t')
                        break;
                    current.Append(c);
                    i++;
                }
            }
            args.Add(current.ToString());
        }
        return args.ToArray();
    }
}
