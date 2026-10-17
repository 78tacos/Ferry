using System.Text;
using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Ferry.Core.Packaging;
using Ferry.Core.Rollback;
using Microsoft.Win32;

namespace Ferry.Core.Tests;

public sealed class RestoreAndUndoTests : IDisposable
{
    private static readonly DateTime OldTime = new(2023, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NewTime = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly string folder = TestEnvironment.NewFolder("restore");
    private readonly List<IAsyncDisposable> readers = [];
    private readonly HiveCache hives = new(writable: true);

    public void Dispose()
    {
        foreach (var reader in readers)
            reader.DisposeAsync().AsTask().GetAwaiter().GetResult();
        hives.Dispose();
    }

    private (RestoreContext Context, RollbackJournal Journal, ResultSet Results, InventoryItem Item) Context(ConflictPolicy policy, bool dryRun = false)
    {
        var inventory = new Inventory { Machine = new MachineInfo { ComputerName = "OLDPC" } };
        var item = new InventoryItem { Id = "test:1", MigratorId = "test", Name = "Test item" };
        var journal = RollbackJournal.Create("OLDPC", ["tester"]);
        var results = new ResultSet();
        var context = new RestoreContext(inventory, new Selection(), new RestoreOptions { Conflicts = policy, DryRun = dryRun },
            new MachineInfo(), [], hives, dryRun ? NullJournal.Instance : journal, results, (_, _) => { });
        return (context, journal, results, item);
    }

    /// <summary>A package entry carrying <paramref name="content"/>, as the restore side sees it.</summary>
    private async Task<PackageEntry> Entry(string content, DateTime modified)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var ms = new MemoryStream();
        await using (var writer = await PackageWriter.CreateAsync(ms, new PackageHeader(), null, default))
        {
            await writer.WriteFileAsync("test/file.txt", "test:1", new MemoryStream(bytes), new FileMeta(FileAttributes.Normal, modified, modified, bytes.Length));
            await writer.CompleteAsync();
        }
        ms.Position = 0;
        var reader = await PackageReader.OpenAsync(ms, null, ownsInput: true, default);
        readers.Add(reader);
        return (await reader.ReadNextAsync(default))!;
    }

    private string Existing(string name, string content, DateTime modified)
    {
        var path = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modified);
        return path;
    }

    [Fact]
    public async Task NewFilesAreWrittenWithTheirTimestampsAndUndoRemovesThem()
    {
        var (context, journal, results, item) = Context(ConflictPolicy.KeepBoth);
        var target = Path.Combine(folder, @"deep\new\file.txt");
        var outcome = await context.Files.RestoreAsync(await Entry("hello", OldTime), item, target, default);

        Assert.Equal(FileOutcome.Written, outcome);
        Assert.Equal("hello", File.ReadAllText(target));
        Assert.Equal(OldTime, File.GetLastWriteTimeUtc(target));
        Assert.Equal(1, results.For(item).FilesDone);

        journal.Complete(UndoStatus.Completed);
        journal.Dispose();
        var report = await UndoEngine.UndoAsync(journal.Id, null, default);
        Assert.False(report.HasFailures);
        Assert.False(File.Exists(target));
        Assert.False(Directory.Exists(Path.Combine(folder, "deep")));
        Assert.Equal(UndoStatus.Undone, UndoEngine.Find(journal.Id)!.Status);
    }

    [Fact]
    public async Task KeepBothSavesAlongsideAndDoesNotPileUpOnRerun()
    {
        var existing = Existing("report.docx", "mine", NewTime);
        var (context, _, _, item) = Context(ConflictPolicy.KeepBoth);

        Assert.Equal(FileOutcome.KeptBoth, await context.Files.RestoreAsync(await Entry("theirs", OldTime), item, existing, default));
        Assert.Equal("mine", File.ReadAllText(existing));
        Assert.Equal("theirs", File.ReadAllText(Path.Combine(folder, "report (from OLDPC).docx")));

        Assert.Equal(FileOutcome.SkippedIdentical, await context.Files.RestoreAsync(await Entry("theirs", OldTime), item, existing, default));
        Assert.False(File.Exists(Path.Combine(folder, "report (from OLDPC 2).docx")));
    }

    [Fact]
    public async Task OverwriteBacksUpTheOriginalForUndo()
    {
        var existing = Existing("settings.ini", "original", NewTime);
        var (context, journal, _, item) = Context(ConflictPolicy.Overwrite);

        Assert.Equal(FileOutcome.Replaced, await context.Files.RestoreAsync(await Entry("migrated", OldTime), item, existing, default));
        Assert.Equal("migrated", File.ReadAllText(existing));

        journal.Dispose();
        await UndoEngine.UndoAsync(journal.Id, null, default);
        Assert.Equal("original", File.ReadAllText(existing));
    }

    [Theory]
    [InlineData(ConflictPolicy.OverwriteIfNewer, "2026", FileOutcome.Replaced, "incoming")]
    [InlineData(ConflictPolicy.OverwriteIfNewer, "2023", FileOutcome.SkippedExisting, "current")]
    [InlineData(ConflictPolicy.Skip, "2026", FileOutcome.SkippedExisting, "current")]
    public async Task ConflictPoliciesDecideWhoWins(ConflictPolicy policy, string incomingYear, FileOutcome expected, string winner)
    {
        var existing = Existing($"c-{policy}-{incomingYear}.txt", "current", new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var (context, _, _, item) = Context(policy);
        var incomingTime = incomingYear == "2026" ? NewTime : OldTime;
        Assert.Equal(expected, await context.Files.RestoreAsync(await Entry("incoming", incomingTime), item, existing, default));
        Assert.Equal(winner, File.ReadAllText(existing));
    }

    [Fact]
    public async Task IdenticalFilesAreSkipped()
    {
        var existing = Existing("same.txt", "same", OldTime);
        var (context, _, results, item) = Context(ConflictPolicy.Overwrite);
        Assert.Equal(FileOutcome.SkippedIdentical, await context.Files.RestoreAsync(await Entry("same", OldTime), item, existing, default));
        Assert.Equal(1, results.For(item).FilesSkipped);
    }

    [Fact]
    public async Task DryRunChangesNothing()
    {
        var (context, _, results, item) = Context(ConflictPolicy.Overwrite, dryRun: true);
        var target = Path.Combine(folder, @"dry\file.txt");
        await context.Files.RestoreAsync(await Entry("hello", OldTime), item, target, default);
        Assert.False(Directory.Exists(Path.Combine(folder, "dry")));
        Assert.Equal(1, results.For(item).FilesDone);
    }

    [Fact]
    public async Task TextSettingsGetTheNewUsersPaths()
    {
        var (context, _, _, item) = Context(ConflictPolicy.Overwrite);
        var remap = new PathRemapper().Add(@"C:\Users\old", @"C:\Users\new");
        var target = Path.Combine(folder, "config.json");
        await context.Files.RestoreAsync(await Entry(@"{""downloads"":""C:\\Users\\old\\Downloads""}", OldTime), item, target, default, remap, remapText: true);
        Assert.Equal(@"{""downloads"":""C:\\Users\\new\\Downloads""}", File.ReadAllText(target));
    }

    [Fact]
    public async Task RegistryChangesAreUndoneExactly()
    {
        var name = $"test-{Guid.NewGuid():N}";
        var root = RegistryRoot.Sandbox(name, "HKCU");
        try
        {
            using (var key = root.Key.CreateSubKey(@"App\Settings"))
                key.SetValue("Keep", "old");

            var export = new RegistryExport
            {
                Root = "HKCU",
                Path = @"App\Settings",
                Keys =
                [
                    new RegKeyData
                    {
                        Path = "",
                        Values =
                        [
                            new RegValueData { Name = "Keep", Kind = RegistryValueKind.String, S = "new" },
                            new RegValueData { Name = "Folder", Kind = RegistryValueKind.ExpandString, S = @"C:\Users\old\Data" },
                            new RegValueData { Name = "Count", Kind = RegistryValueKind.DWord, N = 42 },
                        ],
                    },
                    new RegKeyData { Path = "Sub", Values = [new RegValueData { Name = "Flag", Kind = RegistryValueKind.Binary, B = "AQID" }] },
                ],
            };
            var journal = RollbackJournal.Create("OLDPC", []);
            var result = RegistrySnapshot.Apply(root, export, journal, dryRun: false, new PathRemapper().Add(@"C:\Users\old", @"C:\Users\new"));
            Assert.Empty(result.Errors);
            using (var key = root.Key.OpenSubKey(@"App\Settings")!)
            {
                Assert.Equal("new", key.GetValue("Keep"));
                Assert.Equal(@"C:\Users\new\Data", key.GetValue("Folder", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
                Assert.Equal(42, key.GetValue("Count"));
            }
            using (var sub = root.Key.OpenSubKey(@"App\Settings\Sub")!)
                Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])sub.GetValue("Flag")!);

            journal.Dispose();
            var report = await UndoEngine.UndoAsync(journal.Id, null, default);
            Assert.False(report.HasFailures);
            using (var key = root.Key.OpenSubKey(@"App\Settings")!)
            {
                Assert.Equal("old", key.GetValue("Keep"));
                Assert.Null(key.GetValue("Folder"));
                Assert.Null(key.GetValue("Count"));
                Assert.Null(key.OpenSubKey("Sub"));
            }

            // A capture of what's there now round-trips through JSON.
            var captured = RegistrySnapshot.Capture(root.Key, "HKCU", "App")!;
            var copy = Json.FromBytes<RegistryExport>(Json.ToBytes(captured));
            Assert.Equal("old", copy.Keys.Single(k => k.Path == "Settings").Values.Single(v => v.Name == "Keep").S);
        }
        finally
        {
            root.Key.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\FerrySandbox\{name}", throwOnMissingSubKey: false);
            using var sandbox = Registry.CurrentUser.OpenSubKey(@"Software\FerrySandbox");
            if (sandbox is { SubKeyCount: 0, ValueCount: 0 })
                Registry.CurrentUser.DeleteSubKey(@"Software\FerrySandbox", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void AlternateNamesKeepTheExtension()
    {
        Assert.Equal(@"C:\x\a (from PC).txt", FileRestorer.AlternateName(@"C:\x\a.txt", "PC"));
        Assert.Equal(@"C:\x\a (from PC 3)", FileRestorer.AlternateName(@"C:\x\a", "PC", 3));
    }
}
