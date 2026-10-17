using System.Diagnostics;
using System.Net;
using System.Text;
using Ferry.Core.Engine;
using Ferry.Core.Migrators;
using Ferry.Core.Model;
using Ferry.Core.Packaging;
using Ferry.Core.Rollback;
using Ferry.Core.Transport;

namespace Ferry.Core.Tests;

/// <summary>Regression tests for problems found in review.</summary>
public class ReviewRegressionTests
{
    [Fact]
    public async Task RerunningAPartialUndoNeverDeletesRestoredOriginals()
    {
        var folder = TestEnvironment.NewFolder("undo-rerun");
        var replaced = Path.Combine(folder, "settings.ini");
        var created = Path.Combine(folder, "new.txt");
        await File.WriteAllTextAsync(replaced, "original");

        var journal = RollbackJournal.Create("OLDPC", []);
        journal.BackupFile(replaced);
        await File.WriteAllTextAsync(replaced, "migrated");
        journal.FileCreated(created);
        await File.WriteAllTextAsync(created, "new");
        journal.Complete(UndoStatus.Completed);
        journal.Dispose();

        // First attempt: the new file is open elsewhere, so only part of the undo succeeds.
        MigrationReport first;
        await using (new FileStream(created, FileMode.Open, FileAccess.Read, FileShare.None))
            first = await UndoEngine.UndoAsync(journal.Id, null, default);
        Assert.True(first.Items.Sum(i => i.FilesFailed) > 0);
        Assert.Equal("original", await File.ReadAllTextAsync(replaced));
        Assert.Equal(UndoStatus.PartiallyUndone, UndoEngine.Find(journal.Id)!.Status);

        // Second attempt finishes the job and must leave the restored original alone.
        var second = await UndoEngine.UndoAsync(journal.Id, null, default);
        Assert.False(second.HasFailures);
        Assert.Equal("original", await File.ReadAllTextAsync(replaced));
        Assert.False(File.Exists(created));
        Assert.Equal(UndoStatus.Undone, UndoEngine.Find(journal.Id)!.Status);
    }

    [Fact]
    public async Task ABackupRecordWithoutABackupLeavesTheFileAlone()
    {
        // The journal records a backup before moving the original; if the move never happened, undo must not delete it.
        var folder = TestEnvironment.NewFolder("undo-intent");
        var path = Path.Combine(folder, "keep.txt");
        await File.WriteAllTextAsync(path, "original");
        var journal = RollbackJournal.Create("OLDPC", []);
        Directory.CreateDirectory(Path.Combine(journal.Directory, "backup"));
        journal.BackupFile(path);
        // Simulate the crash between record and move by putting the original back by hand.
        File.Move(Directory.EnumerateFiles(Path.Combine(journal.Directory, "backup")).Single(), path);
        journal.Dispose();
        await UndoEngine.UndoAsync(journal.Id, null, default);
        Assert.Equal("original", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(@"\\nas\projects\2024", @"From OLDPC\nas_projects\2024")]
    [InlineData(@"Q:\Archive", @"From OLDPC\Q\Archive")]
    public void FoldersFromMissingDrivesOrSharesGoUnderTheSystemDrive(string source, string expectedTail)
    {
        var target = FoldersMigrator.DefaultTarget(source, [new DriveSummary { Name = @"C:\" }], "OLDPC");
        Assert.Equal(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, expectedTail), target);
    }

    [Fact]
    public void FoldersOnDrivesThatExistKeepTheirPath() =>
        Assert.Equal(@"C:\Data", FoldersMigrator.DefaultTarget(@"C:\Data", [new DriveSummary { Name = @"C:\" }], "OLDPC"));

    /// <summary>Writes lots of data so the transfer is still going when the new PC cancels.</summary>
    private sealed class BulkMigrator(Action? onFirstRestore = null) : MigratorBase
    {
        public override string Id => "bulk";
        public override MigrationCategory Category => MigrationCategory.Files;
        public override int Order => 1;

        public override Task ScanAsync(ScanContext context, CancellationToken ct)
        {
            var item = NewItem("all", "Bulk", "Bulk data");
            item.SizeBytes = 200L << 20;
            context.Add(item);
            return Task.CompletedTask;
        }

        public override async Task CaptureAsync(CaptureContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct)
        {
            var noise = new byte[1 << 20];
            new Random(1).NextBytes(noise);
            for (var i = 0; i < 200; i++)
                await context.Writer.WriteDataAsync(Entry(i.ToString()), items[0].Id, noise, ct: ct);
        }

        public override async Task RestoreEntryAsync(RestoreContext context, InventoryItem item, PackageEntry entry, CancellationToken ct)
        {
            await entry.SkipAsync(ct);
            onFirstRestore?.Invoke();
            onFirstRestore = null;
            // Slow reader: lets the sender fill the connection's buffers before the cancel lands.
            await Task.Delay(50, ct);
        }
    }

    [Fact]
    public async Task CancellingOnTheNewPcDoesNotLeaveTheOldPcHanging()
    {
        using var cancel = new CancellationTokenSource();
        await using var receiver = NetworkReceiver.Start("CANCE-LTEST", port: 0, bindAddress: IPAddress.Loopback);
        var receiverTask = Task.Run(async () =>
        {
            await using var session = await receiver.WaitForSenderAsync(default);
            var inventory = await session.ReceiveInventoryAsync(null, default);
            return await session.RestoreAsync(new MigrationEngine([new BulkMigrator(() => cancel.CancelAfter(200))]), Selection.All(inventory),
                new CaptureOptions { UseShadowCopy = false }, new RestoreOptions { CreateRestorePoint = false }, null, cancel.Token);
        });

        var clock = Stopwatch.StartNew();
        await using var sender = await SenderSession.ConnectAsync("127.0.0.1", receiver.Port, "CANCE-LTEST", default);
        var run = sender.RunAsync(new MigrationEngine([new BulkMigrator()]), new ScanOptions { QueryWinget = false }, null, default);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(run, finished);
        var (capture, restore) = await run;
        Assert.True(capture.Cancelled || capture.FatalError is not null, "The old PC should report that the transfer stopped.");
        Assert.Null(restore);

        var receiverReport = await receiverTask;
        Assert.True(receiverReport.Cancelled);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ACancelFromTheOldPcStopsTheNewPcInsteadOfHanging()
    {
        // The old PC's stream ends with Cancel mid-way; every later read must fail rather than wait.
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () => await SecureChannel.AcceptAsync(await listener.AcceptTcpClientAsync(), "ABCDE-FGHJK", default));
        await using var client = await SecureChannel.ConnectAsync("127.0.0.1", port, "ABCDE-FGHJK", default);
        await using var server = await serverTask;
        listener.Stop();

        await client.SendAsync(MessageType.Data, Encoding.UTF8.GetBytes("partial"), default);
        await client.SendAsync(MessageType.Cancel, ReadOnlyMemory<byte>.Empty, default);
        var stream = server.OpenDataReader();
        var buffer = new byte[100];
        Assert.Equal(7, await stream.ReadAtLeastAsync(buffer, 7));
#pragma warning disable CA2022 // Only the exception matters here, not how much is read.
        await Assert.ThrowsAsync<RemoteException>(async () => await stream.ReadAsync(buffer));
        var second = stream.ReadAsync(buffer).AsTask();
#pragma warning restore CA2022
        Assert.Same(second, await Task.WhenAny(second, Task.Delay(2000)));
        await Assert.ThrowsAsync<RemoteException>(() => second);
    }
}
