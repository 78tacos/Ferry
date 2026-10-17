using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Migrators;
using Ferry.Core.Model;
using Ferry.Core.Packaging;

namespace Ferry.Core.Tests;

/// <summary>
/// Captures a fake old user's profile with the real files migrator and restores it, sandboxed, onto the account
/// running the tests: files must land in that account's own known folders (wherever Windows has them).
/// </summary>
public class UserFilesRoundTripTests
{
    [Fact]
    public async Task FilesLandInTheNewUsersOwnFolders()
    {
        var oldProfile = TestEnvironment.NewFolder("oldprofile");
        Directory.CreateDirectory(Path.Combine(oldProfile, "Documents", "Taxes"));
        Directory.CreateDirectory(Path.Combine(oldProfile, "Documents", "Empty folder"));
        Directory.CreateDirectory(Path.Combine(oldProfile, "Desktop"));
        Directory.CreateDirectory(Path.Combine(oldProfile, ".ssh"));
        await File.WriteAllTextAsync(Path.Combine(oldProfile, "Documents", "Taxes", "2025.txt"), "refund");
        await File.WriteAllTextAsync(Path.Combine(oldProfile, "Documents", "desktop.ini"), "[.ShellClassInfo]");
        await File.WriteAllTextAsync(Path.Combine(oldProfile, "Documents", "~$lock.docx"), "junk");
        await File.WriteAllTextAsync(Path.Combine(oldProfile, "Desktop", "note.txt"), "hello");
        await File.WriteAllTextAsync(Path.Combine(oldProfile, ".ssh", "config"), "Host *");
        await File.WriteAllTextAsync(Path.Combine(oldProfile, ".gitconfig"), "[user]");

        const string oldSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
        var oldUser = new UserProfileInfo { Sid = oldSid, AccountName = @"OLDPC\joe", UserName = "joe", ProfilePath = oldProfile };
        foreach (var folder in KnownFolders.All)
            oldUser.FolderPaths[folder.Key] = Path.Combine(oldProfile, folder.DefaultRelativePath);

        var migrator = new UserFilesMigrator();
        using var hives = new HiveCache(writable: false);
        var scan = new ScanContext(new ScanOptions(), new MachineInfo { ComputerName = "OLDPC" }, [oldUser], hives, new ExclusionRules(), _ => { });
        await migrator.ScanAsync(scan, default);
        var inventory = new Inventory { Machine = new MachineInfo { ComputerName = "OLDPC" }, Users = [oldUser], Items = [.. scan.Items] };
        Assert.Contains(inventory.Items, i => i.Name == "Documents" && i.FileCount == 1);
        Assert.Contains(inventory.Items, i => i.Name == ".ssh");
        Assert.Contains(inventory.Items, i => i.Name == "Files in the user folder");

        var engine = new MigrationEngine([migrator]);
        using var package = new MemoryStream();
        await using (var writer = await PackageWriter.CreateAsync(package, new PackageHeader(), null, default))
            await engine.CaptureAsync(inventory, Selection.All(inventory), new CaptureOptions { UseShadowCopy = false }, writer, null, default);

        var me = UserProfiles.Enumerate().Single(u => u.Sid == Elevation.CurrentUserSid);
        var sandbox = TestEnvironment.NewFolder("sandbox");
        package.Position = 0;
        await using var reader = await PackageReader.OpenAsync(package, null, ownsInput: false, default);
        var (_, manifest, _) = await reader.ReadManifestAsync(default);
        var options = new RestoreOptions { SandboxRoot = sandbox, CreateRestorePoint = false, UserMap = { [oldSid] = me.Sid } };
        var report = await engine.RestoreAsync(reader, manifest, Selection.All(manifest), options, null, default);
        Assert.Null(report.FatalError);
        Assert.DoesNotContain(report.Items, i => i.Status == ItemStatus.Failed);

        string Sandboxed(string path) => Path.Combine(sandbox, path[..1], path[3..]);
        using var myHive = UserHive.Open(me, writable: false);
        var myDocuments = KnownFolders.Resolve(KnownFolders.Documents, me.ProfilePath, myHive.Root);
        var myDesktop = KnownFolders.Resolve(KnownFolders.Desktop, me.ProfilePath, myHive.Root);

        Assert.Equal("refund", await File.ReadAllTextAsync(Sandboxed(Path.Combine(myDocuments, "Taxes", "2025.txt"))));
        Assert.True(Directory.Exists(Sandboxed(Path.Combine(myDocuments, "Empty folder"))));
        Assert.False(File.Exists(Sandboxed(Path.Combine(myDocuments, "desktop.ini"))));
        Assert.False(File.Exists(Sandboxed(Path.Combine(myDocuments, "~$lock.docx"))));
        Assert.Equal("hello", await File.ReadAllTextAsync(Sandboxed(Path.Combine(myDesktop, "note.txt"))));
        Assert.Equal("Host *", await File.ReadAllTextAsync(Sandboxed(Path.Combine(me.ProfilePath, ".ssh", "config"))));
        Assert.Equal("[user]", await File.ReadAllTextAsync(Sandboxed(Path.Combine(me.ProfilePath, ".gitconfig"))));
        Assert.NotNull(report.UndoId);
    }
}
