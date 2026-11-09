using System.Text;
using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Migrators;
using Ferry.Core.Model;
using Ferry.Core.Packaging;
using Ferry.Core.Rollback;

namespace Ferry.Core.Tests;

/// <summary>A package entry name is not a path the new PC chose, so ".." and rooted tails must not be written.</summary>
public class PackagePathTests
{
    [Fact]
    public void RelativePathsStayInsideTheDestination()
    {
        var parent = TestEnvironment.NewFolder("paths");
        var dest = Path.Combine(parent, "Dest");
        Directory.CreateDirectory(dest);

        Assert.True(PackagePaths.TryCombineUnder(dest + Path.DirectorySeparatorChar, Path.Combine("notes", "a.txt"), out var inside));
        Assert.Equal(Path.GetFullPath(Path.Combine(dest, "notes", "a.txt")), Path.GetFullPath(inside));

        // A package path uses backslashes. It is still a child, including when the root has a trailing separator.
        Assert.True(PackagePaths.TryCombineUnder(dest + Path.DirectorySeparatorChar, @"notes\readme.txt", out _));

        Assert.False(PackagePaths.TryCombineUnder(dest, ".." + Path.DirectorySeparatorChar + "outside.txt", out _));
        Assert.False(PackagePaths.TryCombineUnder(dest, @"..\..\..\Windows\System32\x.dll", out _));
        Assert.False(PackagePaths.TryCombineUnder(dest, @"sub\..\file.txt", out _));
        Assert.False(PackagePaths.TryCombineUnder(@"C:\Users\alex\Documents", @"..\..\..\Windows\System32\x.dll", out _));
        Assert.False(PackagePaths.TryCombineUnder(@"C:\Users\alex\Documents", @"C:\Windows\System32\x.dll", out _));
        Assert.False(PackagePaths.TryCombineUnder(@"C:\Users\alex\Documents", @"\Windows\System32\x.dll", out _));
        Assert.False(PackagePaths.TryCombineUnder(dest, Path.Combine(parent, "outside.txt"), out _));
    }

    [Fact]
    public async Task DotDotEntryIsNotWrittenOutsideTheDestination()
    {
        var parent = TestEnvironment.NewFolder("contain");
        var destination = Path.Combine(parent, "dest");
        Directory.CreateDirectory(destination);

        // The migrator chose this absolute folder (as it would D:\Projects). Only the entry's tail is from the package.
        const string source = @"D:\Projects";
        var key = FoldersMigrator.KeyFor(source);
        var item = new InventoryItem { Id = $"folders:{key}", MigratorId = "folders", Name = "Projects" };
        item.Properties["path"] = source;

        var good = Encoding.UTF8.GetBytes("hello");
        var escaped = Encoding.UTF8.GetBytes("pwned");
        var ms = new MemoryStream();
        await using (var writer = await PackageWriter.CreateAsync(ms, new PackageHeader(), null, default))
        {
            await writer.WriteFileAsync($"folders/{key}/readme.txt", item.Id, new MemoryStream(good), Meta(good.Length));
            // The shape FileWalker writes: a backslash-separated relative path, here walking out of the folder.
            await writer.WriteFileAsync($"folders/{key}/..\\escaped.txt", item.Id, new MemoryStream(escaped), Meta(escaped.Length));
            await writer.CompleteAsync();
        }
        ms.Position = 0;

        using var hives = new HiveCache(writable: false);
        var results = new ResultSet();
        var context = new RestoreContext(
            new Inventory { Machine = new MachineInfo { ComputerName = "OLDPC" } },
            new Selection(),
            new RestoreOptions { CreateRestorePoint = false, FolderMap = { [source] = destination } },
            new MachineInfo { ComputerName = "NEWPC" },
            [], hives, NullJournal.Instance, results, (_, _) => { });

        var migrator = new FoldersMigrator();
        await using var reader = await PackageReader.OpenAsync(ms, null, ownsInput: false, default);
        var kept = (await reader.ReadNextAsync(default))!;
        await migrator.RestoreEntryAsync(context, item, kept, default);
        var rejected = (await reader.ReadNextAsync(default))!;
        await migrator.RestoreEntryAsync(context, item, rejected, default);
        await context.Files.DrainAsync();

        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(destination, "readme.txt")));
        Assert.False(File.Exists(Path.Combine(parent, "escaped.txt")));
        Assert.Equal(1, results.For(item).FilesDone);
        Assert.Equal(1, results.For(item).FilesFailed);
        foreach (var file in Directory.EnumerateFiles(parent, "*", SearchOption.AllDirectories))
            Assert.NotEqual("pwned", await File.ReadAllTextAsync(file));
    }

    private static FileMeta Meta(int length) => new(FileAttributes.Normal, DateTime.UnixEpoch, DateTime.UnixEpoch, length);
}
