using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Ferry.Core.Packaging;

namespace Ferry.Core.Migrators.Windows;

/// <summary>Each user's persistent mapped network drives (Z: → \\server\share). They reconnect at the next sign-in.</summary>
public sealed class MappedDrivesMigrator : MigratorBase
{
    public override string Id => "drives";
    public override MigrationCategory Category => MigrationCategory.WindowsSettings;
    public override int Order => 12;

    public override Task ScanAsync(ScanContext context, CancellationToken ct)
    {
        foreach (var user in context.Users)
        {
            var hive = context.UserRegistry(user);
            using var network = hive is null ? null : RegistrySnapshot.OpenRead(hive, "Network");
            if (network is null)
                continue;
            foreach (var letter in network.GetSubKeyNames())
            {
                using var key = RegistrySnapshot.OpenRead(network, letter);
                if (key?.GetValue("RemotePath") is not string remote)
                    continue;
                var item = NewItem($"{user.Sid}:{letter}", "Mapped network drives", $"{letter.ToUpperInvariant()}: → {remote}", user.Sid);
                item.Description = $"For {user.UserName}";
                item.Properties["letter"] = letter;
                item.Properties["remote"] = remote;
                context.Add(item);
            }
        }
        return Task.CompletedTask;
    }

    public override async Task CaptureAsync(CaptureContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            var hive = context.UserRegistry(item);
            if (hive is null)
                continue;
            var letter = item.Prop("letter")!;
            if (await context.CaptureRegistryAsync(item, hive, "HKCU", $@"Network\{letter}", Entry(item.UserSid!, letter + ".json"), ct).ConfigureAwait(false))
                context.Results.FileDone(item, 0);
        }
    }

    public override async Task RestoreEntryAsync(RestoreContext context, InventoryItem item, PackageEntry entry, CancellationToken ct)
    {
        if (!context.TryGetTargetUser(item, out var target))
            return;
        var export = await entry.ReadJsonAsync<RegistryExport>(ct).ConfigureAwait(false);
        var root = context.UserRegistry(item, target);
        if (root is null)
            return;

        var letter = item.Prop("letter")!;
        using (var existing = RegistrySnapshot.OpenRead(root.Key, $@"Network\{letter}"))
        {
            if (existing?.GetValue("RemotePath") is string current)
            {
                if (string.Equals(current, item.Prop("remote"), StringComparison.OrdinalIgnoreCase))
                    context.Results.Info(item, "Already mapped on this PC.");
                else
                    context.Results.Warn(item, $"{letter.ToUpperInvariant()}: is already mapped to {current} for {target.UserName}; left as it is.");
                return;
            }
        }
        // Saved credentials can't move; the user is asked for them on first use if the share needs them.
        var result = RegistrySnapshot.Apply(root, export, context.Journal, context.DryRun);
        foreach (var error in result.Errors)
            context.Results.Warn(item, error);
        context.Results.Info(item, $"Reconnects the next time {target.UserName} signs in.");
        context.Results.FileDone(item, 0);
    }
}
