using System.Text.RegularExpressions;

namespace Ferry.Core.Migrators.Apps;

/// <summary>
/// Well-known programs whose winget package id is stable, for when winget itself doesn't connect the installed
/// program to its package (common for apps that updated themselves, or were installed before winget existed).
/// </summary>
internal static class KnownWingetIds
{
    private static readonly (Regex Name, string Id)[] Map =
    [
        (R(@"^Mozilla Firefox\b(?!.*(ESR|Developer|Nightly|Beta))"), "Mozilla.Firefox"),
        (R(@"^Mozilla Firefox ESR\b"), "Mozilla.Firefox.ESR"),
        (R(@"^Mozilla Thunderbird\b"), "Mozilla.Thunderbird"),
        (R(@"^Google Chrome$"), "Google.Chrome"),
        (R(@"^Brave$"), "Brave.Brave"),
        (R(@"^Vivaldi$"), "Vivaldi.Vivaldi"),
        (R(@"^Opera Stable\b"), "Opera.Opera"),
        (R(@"^Python (3\.\d+)\.\d+"), "Python.Python.$1"),
        (R(@"^7-Zip\b"), "7zip.7zip"),
        (R(@"^VLC media player$"), "VideoLAN.VLC"),
        (R(@"^Zoom( Workplace)?\b"), "Zoom.Zoom"),
        (R(@"^Slack$"), "SlackTechnologies.Slack"),
        (R(@"^Spotify$"), "Spotify.Spotify"),
        (R(@"^Adobe Acrobat Reader\b"), "Adobe.Acrobat.Reader.64-bit"),
        (R(@"^Notepad\+\+"), "Notepad++.Notepad++"),
        (R(@"^WinRAR\b"), "RARLab.WinRAR"),
        (R(@"^Dropbox$"), "Dropbox.Dropbox"),
        (R(@"^Discord$"), "Discord.Discord"),
        (R(@"^Steam$"), "Valve.Steam"),
        (R(@"^OBS Studio$"), "OBSProject.OBSStudio"),
        (R(@"^Audacity\b"), "Audacity.Audacity"),
        (R(@"^GIMP\b"), "GIMP.GIMP.3"),
        (R(@"^paint\.net$"), "dotPDN.PaintDotNet"),
        (R(@"^LibreOffice\b"), "TheDocumentFoundation.LibreOffice"),
        (R(@"^KeePass Password Safe 2"), "DominikReichl.KeePass"),
        (R(@"^KeePassXC$"), "KeePassXCTeam.KeePassXC"),
        (R(@"^PuTTY release\b"), "PuTTY.PuTTY"),
        (R(@"^WinSCP\b"), "WinSCP.WinSCP"),
        (R(@"^FileZilla\b"), "TimKosse.FileZilla.Client"),
        (R(@"^TeamViewer\b"), "TeamViewer.TeamViewer"),
        (R(@"^AnyDesk$"), "AnyDesk.AnyDesk"),
        (R(@"^Webex$"), "Cisco.Webex"),
        (R(@"^ShareX$"), "ShareX.ShareX"),
        (R(@"^Greenshot\b"), "Greenshot.Greenshot"),
        (R(@"^Everything\b"), "voidtools.Everything"),
        (R(@"^Signal\b"), "OpenWhisperSystems.Signal"),
        (R(@"^Telegram Desktop\b"), "Telegram.TelegramDesktop"),
        (R(@"^Docker Desktop$"), "Docker.DockerDesktop"),
        (R(@"^Postman\b"), "Postman.Postman"),
        (R(@"^Wireshark\b"), "WiresharkFoundation.Wireshark"),
        (R(@"^Foxit PDF Reader\b"), "Foxit.FoxitReader"),
        (R(@"^Microsoft Teams$"), "Microsoft.Teams"),
        (R(@"^Microsoft Visual Studio Code\b"), "Microsoft.VisualStudioCode"),
        (R(@"^PowerShell 7\b"), "Microsoft.PowerShell"),
        (R(@"^Windows Terminal$"), "Microsoft.WindowsTerminal"),
        (R(@"^Git$"), "Git.Git"),
        (R(@"^GitHub Desktop$"), "GitHub.GitHubDesktop"),
        (R(@"^Node\.js$"), "OpenJS.NodeJS"),
        (R(@"^Java\(TM\)|^Java \d+ Update"), "Oracle.JavaRuntimeEnvironment"),
        (R(@"^Logi Options\+$"), "Logitech.OptionsPlus"),
        (R(@"^Malwarebytes\b"), "Malwarebytes.Malwarebytes"),
        (R(@"^WhatsApp$"), "9NKSQGP7F2NH"),
        (R(@"^Evernote\b"), "evernote.evernote"),
        (R(@"^Obsidian$"), "Obsidian.Obsidian"),
        (R(@"^Notion$"), "Notion.Notion"),
        (R(@"^Figma$"), "Figma.Figma"),
        (R(@"^Blender$"), "BlenderFoundation.Blender"),
        (R(@"^HandBrake$"), "HandBrake.HandBrake"),
        (R(@"^qBittorrent\b"), "qBittorrent.qBittorrent"),
        (R(@"^calibre\b"), "calibre.calibre"),
        (R(@"^Inkscape\b"), "Inkscape.Inkscape"),
        (R(@"^Krita\b"), "KDE.Krita"),
        (R(@"^Microsoft PowerToys\b|^PowerToys\b"), "Microsoft.PowerToys"),
    ];

    private static Regex R(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A winget id for a display name, or null. Store ids (no dot) come with the "msstore" source.</summary>
    public static (string Id, string Source)? Lookup(string displayName)
    {
        foreach (var (name, id) in Map)
        {
            var match = name.Match(displayName);
            if (match.Success)
            {
                var resolved = match.Result(id);
                return (resolved, resolved.Contains('.') ? "winget" : "msstore");
            }
        }
        return null;
    }
}
