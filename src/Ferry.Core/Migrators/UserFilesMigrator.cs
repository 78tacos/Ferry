using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Ferry.Core.Packaging;

namespace Ferry.Core.Migrators;

/// <summary>
/// Each user's Documents, Desktop, Pictures… plus other folders and files in their profile (.ssh, source, .gitconfig).
/// Lands in the target user's matching known folders, wherever those are (OneDrive, redirected, another drive).
/// </summary>
public sealed class UserFilesMigrator : MigratorBase
{
    public override string Id => "files";
    public override MigrationCategory Category => MigrationCategory.Files;
    public override int Order => 90;

    private const string KnownFolderKind = "kf";
    private const string ProfileFolderKind = "p";
    private const string ProfileRootKind = "r";

    /// <summary>Profile folders that are Windows plumbing, handled elsewhere, or legacy junctions.</summary>
    private static readonly HashSet<string> SkippedProfileFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "Application Data", "Cookies", "Local Settings", "My Documents", "NetHood", "PrintHood", "Recent", "SendTo",
        "Start Menu", "Templates", "MicrosoftEdgeBackups", "IntelGraphicsProfiles", "Tracing", "ansel", "OneDriveTemp",
        "3D Objects", ".ms-ad", "Creative Cloud Files",
    };

    /// <summary>Tool caches in the profile root: re-downloadable, often huge, so offered but not ticked.</summary>
    private static readonly HashSet<string> CacheProfileFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nuget", ".cache", ".gradle", ".m2", ".npm", ".rustup", ".android", ".dotnet", ".templateengine", ".vscode-server",
        "scoop", ".pyenv", ".conda", "anaconda3", "miniconda3", ".ollama", ".lmstudio", ".docker", ".minikube", "go",
    };

    private static readonly string[] ProfileRootFileExclusions =
        ["NTUSER*", "ntuser*", "*.blf", "*.regtrans-ms", "*.LOG1", "*.LOG2", "UsrClass*", "Sti_Trace.log", "*.lnk"];

    public override async Task ScanAsync(ScanContext context, CancellationToken ct)
    {
        foreach (var user in context.Users)
        {
            ct.ThrowIfCancellationRequested();
            context.Status($"Measuring {user.UserName}'s files");
            var knownPaths = new List<string>();

            var tasks = new List<Task>();
            foreach (var folder in KnownFolders.UserContent)
            {
                var path = user.FolderPaths.GetValueOrDefault(folder.Key) ?? Path.Combine(user.ProfilePath, folder.DefaultRelativePath);
                knownPaths.Add(path);
                if (!Directory.Exists(path))
                    continue;
                var item = NewItem($"{user.Sid}:{KnownFolderKind}:{folder.Key}", user.AccountName, folder.DisplayName, user.Sid);
                item.Description = path;
                item.Properties["path"] = path;
                item.Properties["kind"] = KnownFolderKind;
                item.Properties["folder"] = folder.Key;

                if (path.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    item.SelectedByDefault = false;
                    item.Warnings.Add($"This folder is redirected to {path} on a server, so the files stay there. Point the new PC at the same location instead.");
                }
                else if (user.OneDriveRoots.Any(r => KnownFolders.IsUnder(path, r)))
                {
                    item.SelectedByDefault = false;
                    item.Warnings.Add("Backed up by OneDrive. Sign in to OneDrive on the new PC and it will sync these files. " +
                                      "Files that are only in the cloud are never copied.");
                }

                context.Add(item);
                tasks.Add(MeasureAsync(item, path, FolderRules(context.Exclusions), ct));
            }

            foreach (var dir in SafeDirectories(user.ProfilePath))
            {
                var name = Path.GetFileName(dir);
                if (SkippedProfileFolders.Contains(name) || knownPaths.Any(k => string.Equals(k, dir, StringComparison.OrdinalIgnoreCase)) ||
                    user.OneDriveRoots.Any(r => string.Equals(r, dir, StringComparison.OrdinalIgnoreCase)) ||
                    name.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase) || IsLink(dir))
                    continue;

                var item = NewItem($"{user.Sid}:{ProfileFolderKind}:{name}", user.AccountName, name, user.Sid);
                item.Description = dir;
                item.Properties["path"] = dir;
                item.Properties["kind"] = ProfileFolderKind;
                item.Properties["folder"] = name;
                if (CacheProfileFolders.Contains(name))
                {
                    item.SelectedByDefault = false;
                    item.Warnings.Add("Looks like a download cache for developer tools; it is rebuilt automatically when needed.");
                }
                context.Add(item);
                tasks.Add(MeasureAsync(item, dir, context.Exclusions, ct));
            }

            var rootFiles = RootFiles(user.ProfilePath, context.Exclusions).ToList();
            if (rootFiles.Count > 0)
            {
                var item = NewItem($"{user.Sid}:{ProfileRootKind}", user.AccountName, "Files in the user folder", user.Sid);
                item.Description = $"{rootFiles.Count} files directly in {user.ProfilePath}, such as settings files for developer tools.";
                item.Properties["path"] = user.ProfilePath;
                item.Properties["kind"] = ProfileRootKind;
                item.FileCount = rootFiles.Count;
                item.SizeBytes = rootFiles.Sum(f => f.Length);
                context.Add(item);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private static Task MeasureAsync(InventoryItem item, string path, ExclusionRules rules, CancellationToken ct) => Task.Run(() =>
    {
        var stats = FileWalker.Measure(path, rules, ct);
        item.SizeBytes = stats.Bytes;
        item.FileCount = stats.Files;
        if (stats.Files == 0 && !item.Warnings.Any())
            item.SelectedByDefault = false;
    }, ct);

    /// <summary>The target's own desktop.ini decides how a known folder looks; never overwrite it.</summary>
    private static ExclusionRules FolderRules(ExclusionRules baseRules) => baseRules.With([@"\desktop.ini"]);

    public override async Task CaptureAsync(CaptureContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var path = item.Prop("path")!;
            switch (item.Prop("kind"))
            {
                case KnownFolderKind:
                    await context.CaptureFolderAsync(item, path, Entry(item.UserSid!, KnownFolderKind, item.Prop("folder")!), FolderRules(context.Exclusions), ct)
                        .ConfigureAwait(false);
                    break;
                case ProfileFolderKind:
                    await context.CaptureFolderAsync(item, path, Entry(item.UserSid!, ProfileFolderKind, item.Prop("folder")!), context.Exclusions, ct)
                        .ConfigureAwait(false);
                    break;
                case ProfileRootKind:
                    foreach (var file in RootFiles(path, context.Exclusions))
                    {
                        context.Status(item, file.Name);
                        await context.CaptureFileAsync(item, file.FullName, Entry(item.UserSid!, ProfileRootKind, file.Name), FileMeta.From(file), ct)
                            .ConfigureAwait(false);
                    }
                    break;
            }
        }
    }

    public override async Task RestoreEntryAsync(RestoreContext context, InventoryItem item, PackageEntry entry, CancellationToken ct)
    {
        if (!context.TryGetTargetUser(item, out var target))
            return;

        // files/<sid>/<kind>/<key>/<relative>  or  files/<sid>/r/<file>
        var rest = EntryNames.After(entry.Name, Entry(item.UserSid!))!;
        var (kind, tail) = EntryNames.SplitFirst(rest);
        string logicalPath;
        switch (kind)
        {
            case KnownFolderKind:
            {
                var (folderKey, relative) = EntryNames.SplitFirst(tail);
                var folder = KnownFolders.ByKey(folderKey);
                var root = folder is null ? Path.Combine(target.ProfilePath, folderKey) : context.TargetFolder(target, folder);
                logicalPath = Path.Combine(root, relative);
                break;
            }
            case ProfileFolderKind:
            {
                var (folderName, relative) = EntryNames.SplitFirst(tail);
                logicalPath = Path.Combine(target.ProfilePath, folderName, relative);
                break;
            }
            case ProfileRootKind:
                logicalPath = Path.Combine(target.ProfilePath, tail);
                break;
            default:
                return;
        }

        context.Status(item, logicalPath);
        if (entry.Kind == EntryKind.Directory)
            context.Files.RestoreDirectory(entry, logicalPath);
        else
            await context.Files.RestoreAsync(entry, item, logicalPath, ct, context.RemapperFor(item), background: true).ConfigureAwait(false);
    }

    private static IEnumerable<FileInfo> RootFiles(string profilePath, ExclusionRules rules)
    {
        var excluded = rules.With(ProfileRootFileExclusions);
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(profilePath).EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var file in files)
        {
            if (excluded.IsExcluded(file.Name) || FileWalker.IsCloudOnly(file.Attributes) || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            yield return file;
        }
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsLink(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).LinkTarget is not null;
        }
        catch
        {
            return true;
        }
    }
}
