using System.Net;
using System.Net.Sockets;
using System.Text;
using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Migrators;
using Ferry.Core.Model;
using Ferry.Core.Packaging;
using Ferry.Core.Reporting;
using Ferry.Core.Transport;

namespace Ferry.Core.Tests;

/// <summary>A migrator that writes each item's name as data and records what restore was asked to do.</summary>
internal sealed class FakeMigrator : MigratorBase
{
    public override string Id => "fake";
    public override MigrationCategory Category => MigrationCategory.Files;
    public override int Order => 1;

    public List<string> Restored { get; } = [];
    public List<string> Completed { get; } = [];

    public override Task ScanAsync(ScanContext context, CancellationToken ct)
    {
        var alpha = NewItem("a", "Group", "Alpha");
        alpha.SizeBytes = 100;
        context.Add(alpha);
        var beta = NewItem("b", "Group", "Beta");
        beta.SelectedByDefault = false;
        beta.SizeBytes = 200;
        context.Add(beta);
        return Task.CompletedTask;
    }

    public override async Task CaptureAsync(CaptureContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            await context.Writer.WriteDataAsync(Entry(item.Id[5..]), item.Id, Encoding.UTF8.GetBytes(item.Name), ct: ct);
            context.Results.FileDone(item, item.Name.Length);
        }
    }

    public override async Task RestoreEntryAsync(RestoreContext context, InventoryItem item, PackageEntry entry, CancellationToken ct)
    {
        Restored.Add(Encoding.UTF8.GetString(await entry.ReadAllBytesAsync(ct)));
        context.Results.FileDone(item, 1);
    }

    public override Task CompleteRestoreAsync(RestoreContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct)
    {
        Completed.AddRange(items.Select(i => i.Name));
        return Task.CompletedTask;
    }
}

public class EngineTests
{
    [Fact]
    public async Task CaptureThenRestoreOnlyAppliesWhatWasChosen()
    {
        var fake = new FakeMigrator();
        var engine = new MigrationEngine([fake]);
        var inventory = await engine.ScanAsync(new ScanOptions { QueryWinget = false }, null, default);
        Assert.Equal(["Alpha", "Beta"], inventory.Items.Select(i => i.Name));
        Assert.Equal(["fake:a"], Selection.Defaults(inventory).ItemIds);

        using var package = new MemoryStream();
        await using (var writer = await PackageWriter.CreateAsync(package, new PackageHeader { SourceComputer = "OLDPC" }, "pw", default))
        {
            var capture = await engine.CaptureAsync(inventory, Selection.All(inventory), new CaptureOptions { UseShadowCopy = false }, writer, null, default);
            Assert.All(capture.Items, i => Assert.Equal(ItemStatus.Succeeded, i.Status));
        }

        package.Position = 0;
        await using var reader = await PackageReader.OpenAsync(package, _ => "pw", ownsInput: false, default);
        var (_, manifest, captureInfo) = await reader.ReadManifestAsync(default);
        Assert.Equal(2, manifest.Items.Count);
        Assert.Equal(["fake:a", "fake:b"], captureInfo!.IncludedItemIds);

        var onlyBeta = new Selection { ItemIds = { "fake:b" } };
        var report = await engine.RestoreAsync(reader, manifest, onlyBeta, new RestoreOptions { CreateRestorePoint = false }, null, default);
        Assert.Equal(["Beta"], fake.Restored);
        Assert.Equal(["Beta"], fake.Completed);
        Assert.Single(report.Items);
        Assert.Null(report.FatalError);
        // The fake changes nothing on disk, so no (empty) undo data is kept.
        Assert.Null(report.UndoId);

        var html = HtmlReport.Render(report);
        Assert.Contains("Beta", html);
    }

    [Fact]
    public void ReportsEscapeWhateverTheyShow()
    {
        var report = new MigrationReport { Kind = OperationKind.Restore, Source = new MachineInfo { ComputerName = "<script>" } };
        report.ManualSteps.Add("Install <b>this</b> & that");
        var html = HtmlReport.Render(report);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;b&gt;this&lt;/b&gt; &amp; that", html);
    }
}

public class PlanTests
{
    private static Inventory Inventory(string sid) => new()
    {
        Users = [new UserProfileInfo { Sid = sid, AccountName = @"OLDPC\joe", UserName = "joe", ProfilePath = @"C:\Users\joe" }],
        Items =
        [
            new() { Id = $"files:{sid}:kf:Documents", MigratorId = "files", Category = MigrationCategory.Files, Name = "Documents", UserSid = sid },
            new() { Id = $"files:{sid}:kf:Downloads", MigratorId = "files", Category = MigrationCategory.Files, Name = "Downloads", UserSid = sid },
            new() { Id = "wifi:Home", MigratorId = "wifi", Category = MigrationCategory.WindowsSettings, Name = "Home" },
            new() { Id = "apps:abc", MigratorId = "apps", Category = MigrationCategory.Applications, Name = "Notepad++", AvailableModes = ["winget", "copy"], DefaultMode = "winget" },
            new() { Id = "apps:def", MigratorId = "apps", Category = MigrationCategory.Applications, Name = "OldTool", AvailableModes = ["copy"], DefaultMode = "copy", SelectedByDefault = false },
        ],
    };

    [Fact]
    public void RulesAddAndRemoveItems()
    {
        var plan = new MigrationPlan { Include = ["category:Files", "name:Old*"], Skip = ["name:Downloads"], Apps = "copy" };
        var selection = plan.BuildSelection(Inventory("S-1-5-21-1"));
        Assert.True(selection.ItemIds.SetEquals(["files:S-1-5-21-1:kf:Documents", "apps:def"]), string.Join(", ", selection.ItemIds));
        Assert.Equal("copy", selection.Modes["apps:def"]);
    }

    [Fact]
    public void DefaultsApplyWhenNothingIsIncluded()
    {
        var selection = new MigrationPlan().BuildSelection(Inventory("S-1-5-21-1"));
        Assert.DoesNotContain("apps:def", selection.ItemIds);
        Assert.Contains("wifi:Home", selection.ItemIds);
        Assert.Equal(5, new MigrationPlan().BuildSelection(Inventory("S-1-5-21-1"), everythingByDefault: true).ItemIds.Count);
    }

    [Fact]
    public void SavedChoicesCarryOverToAnotherPcWithDifferentSids()
    {
        var first = Inventory("S-1-5-21-111");
        var chosen = Selection.Defaults(first);
        chosen.ItemIds.Remove("files:S-1-5-21-111:kf:Downloads");
        chosen.Modes["apps:abc"] = "copy";
        var plan = Json.FromText<MigrationPlan>(Json.ToText(MigrationPlan.FromSelection(first, chosen)));

        var other = Inventory("S-1-5-21-999");
        var applied = plan.BuildSelection(other);
        Assert.Contains("files:S-1-5-21-999:kf:Documents", applied.ItemIds);
        Assert.DoesNotContain("files:S-1-5-21-999:kf:Downloads", applied.ItemIds);
        Assert.DoesNotContain("apps:def", applied.ItemIds);
        Assert.Equal("copy", applied.Modes["apps:abc"]);
    }

    [Fact]
    public void UsersMapByNameThenToTheSignedInUser()
    {
        var source = Inventory("S-1-5-21-1");
        var targets = new List<UserProfileInfo>
        {
            new() { Sid = "S-1-12-1-5", AccountName = @"AzureAD\JoeSmith", UserName = "JoeSmith", IsInteractiveUser = true },
            new() { Sid = "S-1-5-21-9", AccountName = @"NEWPC\admin", UserName = "admin" },
        };
        Assert.Equal("S-1-12-1-5", new MigrationPlan().ToRestoreOptions(source, targets).UserMap["S-1-5-21-1"]);

        var explicitMap = new MigrationPlan { UserMap = { [@"OLDPC\joe"] = "admin" } };
        Assert.Equal("S-1-5-21-9", explicitMap.ToRestoreOptions(source, targets).UserMap["S-1-5-21-1"]);

        var missing = new MigrationPlan { UserMap = { ["joe"] = "nobody" } };
        Assert.Throws<InvalidDataException>(() => missing.ToRestoreOptions(source, targets));
    }
}

public class SecureChannelTests
{
    private static async Task<(SecureChannel Server, SecureChannel Client)> PairAsync(string serverCode, string clientCode)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serverTask = Task.Run(async () => await SecureChannel.AcceptAsync(await listener.AcceptTcpClientAsync(), serverCode, default));
            var client = await SecureChannel.ConnectAsync("127.0.0.1", port, clientCode, default);
            return (await serverTask, client);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task PairedPcsExchangeMessagesAndStreams()
    {
        var (server, client) = await PairAsync("ABCDE-FGHJK", "abcde fghjk");
        await using (server)
        await using (client)
        {
            await client.SendJsonAsync(MessageType.Status, new StatusMessage("scanning"), default);
            await client.SendJsonAsync(MessageType.Hello, new HelloMessage { Role = "sender" }, default);
            var statuses = new List<string>();
            var hello = await server.ReceiveJsonAsync<HelloMessage>(MessageType.Hello, default, statuses.Add);
            Assert.Equal("sender", hello.Role);
            Assert.Equal(["scanning"], statuses);

            var payload = new byte[3_000_000];
            new Random(7).NextBytes(payload);
            var send = Task.Run(async () =>
            {
                var writer = server.OpenDataWriter();
                await writer.WriteAsync(payload);
                await writer.CompleteAsync(default);
            });
            using var received = new MemoryStream();
            await client.OpenDataReader().CopyToAsync(received);
            await send;
            Assert.Equal(payload, received.ToArray());
        }
    }

    [Fact]
    public async Task WrongCodeFailsOnBothSides()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () => await SecureChannel.AcceptAsync(await listener.AcceptTcpClientAsync(), "ABCDE-FGHJK", default));
        await Assert.ThrowsAsync<PairingFailedException>(() => SecureChannel.ConnectAsync("127.0.0.1", port, "ZZZZZ-ZZZZZ", default));
        await Assert.ThrowsAsync<PairingFailedException>(() => serverTask);
        listener.Stop();
    }

    [Fact]
    public async Task ReceiverStreamsARestoreFromTheSender()
    {
        // The full network flow: NetworkReceiver + SenderSession over loopback, restoring through the engine.
        var senderFake = new FakeMigrator();
        var receiverFake = new FakeMigrator();
        await using var receiver = NetworkReceiver.Start("TESTC-ODE22", port: 0, bindAddress: IPAddress.Loopback);
        var receiverTask = Task.Run(async () =>
        {
            await using var session = await receiver.WaitForSenderAsync(default);
            var inventory = await session.ReceiveInventoryAsync(null, default);
            var selection = Selection.All(inventory);
            return await session.RestoreAsync(new MigrationEngine([receiverFake]), selection, new CaptureOptions { UseShadowCopy = false },
                new RestoreOptions { CreateRestorePoint = false }, null, default);
        });

        await using var sender = await SenderSession.ConnectAsync("127.0.0.1", receiver.Port, "TESTC-ODE22", default);
        var (capture, restore) = await sender.RunAsync(new MigrationEngine([senderFake]), new ScanOptions { QueryWinget = false }, null, default);
        var receiverReport = await receiverTask;

        Assert.Null(capture.FatalError);
        Assert.Equal(["Alpha", "Beta"], receiverFake.Restored);
        Assert.NotNull(restore);
        Assert.Equal(receiverReport.Items.Count, restore!.Items.Count);
    }
}
