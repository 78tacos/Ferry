using System.Runtime.InteropServices;
using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Ferry.Core.Packaging;
using Microsoft.Win32;

namespace Ferry.Core.Migrators.Windows;

/// <summary>
/// User and system environment variables. Variables are only added (never overwrite what the new PC has),
/// and PATH entries are merged in when their folder exists on the new PC. Applied at the very end, after
/// programs have been reinstalled and files copied, so the folders they point to are there.
/// </summary>
public sealed partial class EnvironmentMigrator : MigratorBase
{
    public override string Id => "environment";
    public override MigrationCategory Category => MigrationCategory.WindowsSettings;
    public override int Order => 85;

    private const string UserKey = "Environment";
    private const string SystemKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// <summary>Variables Windows, the hardware or other installers own.</summary>
    private static readonly HashSet<string> ManagedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "TEMP", "TMP", "OneDrive", "OneDriveCommercial", "OneDriveConsumer", "ComSpec", "DriverData", "NUMBER_OF_PROCESSORS", "OS",
        "PATHEXT", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "PROCESSOR_LEVEL", "PROCESSOR_REVISION", "PSModulePath",
        "USERNAME", "windir", "ZES_ENABLE_SYSMAN", "POWERSHELL_DISTRIBUTION_CHANNEL", "__PSLockDownPolicy", "configsetroot",
        "OnlineServices", "platformcode", "RegionCode", "FPS_BROWSER_APP_PROFILE_STRING", "FPS_BROWSER_USER_PROFILE_STRING",
    };

    public override Task ScanAsync(ScanContext context, CancellationToken ct)
    {
        foreach (var user in context.Users)
        {
            var hive = context.UserRegistry(user);
            using var env = hive is null ? null : RegistrySnapshot.OpenRead(hive, UserKey);
            var names = env?.GetValueNames().Where(n => !ManagedNames.Contains(n)).ToList();
            if (names is null || names.Count == 0)
                continue;
            var item = NewItem($"{user.Sid}:user", user.AccountName, "Environment variables", user.Sid);
            item.Description = string.Join(", ", names.Take(8)) + (names.Count > 8 ? ", …" : "");
            item.SizeBytes = 1024;
            context.Add(item);
        }

        using var system = RegistrySnapshot.OpenRead(Registry.LocalMachine, SystemKey);
        var systemNames = system?.GetValueNames().Where(n => !ManagedNames.Contains(n)).ToList();
        if (systemNames is { Count: > 0 })
        {
            var item = NewItem("system", "System", "System environment variables");
            item.Description = "Adds variables and PATH folders the new PC doesn't have; never changes existing ones.";
            item.SizeBytes = 1024;
            context.Add(item);
        }
        return Task.CompletedTask;
    }

    public override async Task CaptureAsync(CaptureContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            var isSystem = item.UserSid is null;
            var baseKey = isSystem ? Registry.LocalMachine : context.UserRegistry(item);
            if (baseKey is null)
                continue;
            await context.CaptureRegistryAsync(item, baseKey, isSystem ? "HKLM" : "HKCU", isSystem ? SystemKey : UserKey,
                Entry(isSystem ? "system.json" : $"{item.UserSid}.json"), ct, valueFilter: (_, name) => !ManagedNames.Contains(name), recursive: false)
                .ConfigureAwait(false);
            context.Results.FileDone(item, 0);
        }
    }

    private sealed class Pending
    {
        public readonly List<(InventoryItem Item, RegistryExport Export)> Exports = [];
    }

    public override async Task RestoreEntryAsync(RestoreContext context, InventoryItem item, PackageEntry entry, CancellationToken ct)
    {
        var export = await entry.ReadJsonAsync<RegistryExport>(ct).ConfigureAwait(false);
        var pending = context.State<Pending>(Id);
        lock (pending)
            pending.Exports.Add((item, export));
    }

    public override Task CompleteRestoreAsync(RestoreContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct)
    {
        var pending = context.State<Pending>(Id);
        var changed = false;
        foreach (var (item, export) in pending.Exports)
        {
            RegistryRoot? root;
            string path;
            PathRemapper remap;
            if (item.UserSid is null)
            {
                root = context.MachineRegistry();
                path = SystemKey;
                remap = new PathRemapper();
            }
            else
            {
                if (!context.TryGetTargetUser(item, out var target))
                    continue;
                root = context.UserRegistry(item, target);
                path = UserKey;
                remap = context.RemapperFor(item);
            }
            if (root is null)
                continue;
            changed |= Merge(context, item, root, path, export, remap);
        }
        if (changed && !context.SimulateCommands)
            BroadcastEnvironmentChange();
        return Task.CompletedTask;
    }

    private static bool Merge(RestoreContext context, InventoryItem item, RegistryRoot root, string path, RegistryExport export, PathRemapper remap)
    {
        var values = export.Keys.FirstOrDefault()?.Values ?? [];
        using var existing = RegistrySnapshot.OpenRead(root.Key, path);
        var changed = false;
        foreach (var value in values)
        {
            if (value.Kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString) || value.S is null)
                continue;
            var incoming = remap.Remap(value.S);
            var current = existing?.GetValue(value.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

            if (value.Name.Equals("Path", StringComparison.OrdinalIgnoreCase))
            {
                var merged = MergePath(current, incoming, out var added);
                if (added.Count == 0)
                    continue;
                if (!context.DryRun)
                    RegistrySnapshot.SetValue(root, path, value.Name, merged, RegistryValueKind.ExpandString, context.Journal);
                context.Results.Info(item, $"Added to PATH: {string.Join("; ", added)}");
                changed = true;
            }
            else if (current is null)
            {
                if (!context.DryRun)
                    RegistrySnapshot.SetValue(root, path, value.Name, incoming, value.Kind, context.Journal);
                context.Results.Info(item, $"Added {value.Name}.");
                changed = true;
            }
            else if (!string.Equals(current, incoming, StringComparison.OrdinalIgnoreCase))
            {
                context.Results.Info(item, $"{value.Name} already set on this PC to \"{current}\"; kept it (the old PC had \"{incoming}\").");
            }
        }
        context.Results.FileDone(item, 0);
        return changed;
    }

    /// <summary>Appends incoming PATH folders that are missing and exist here (or depend on a variable).</summary>
    internal static string MergePath(string? current, string incoming, out List<string> added)
    {
        var entries = (current ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var known = new HashSet<string>(entries.Select(NormalizeDir), StringComparer.OrdinalIgnoreCase);
        added = [];
        foreach (var entry in incoming.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!known.Add(NormalizeDir(entry)))
                continue;
            var expanded = Environment.ExpandEnvironmentVariables(entry);
            if (!expanded.Contains('%') && !Directory.Exists(expanded))
                continue;
            entries.Add(entry);
            added.Add(entry);
        }
        return string.Join(';', entries);
    }

    private static string NormalizeDir(string dir) => dir.Trim().TrimEnd('\\');

    private static void BroadcastEnvironmentChange()
    {
        // Lets Explorer and newly started programs see the new variables without a sign-out.
        SendMessageTimeoutW(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Environment", 0x0002, 5000, out _);
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
