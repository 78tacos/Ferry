using System.Collections.Concurrent;
using System.Diagnostics;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Ferry.Core.Rollback;

namespace Ferry.Core.Engine;

public sealed class RestoreContext
{
    private readonly Action<string?, string?> status;
    private readonly Stopwatch statusClock = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<string, PathRemapper> remappers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> targetFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> state = new(StringComparer.Ordinal);

    internal RestoreContext(Inventory source, Selection selection, RestoreOptions options, MachineInfo target, IReadOnlyList<UserProfileInfo> targetUsers,
        HiveCache hives, IChangeJournal journal, ResultSet results, Action<string?, string?> status)
    {
        Source = source;
        Selection = selection;
        Options = options;
        Target = target;
        TargetUsers = targetUsers;
        Hives = hives;
        Journal = journal;
        Results = results;
        this.status = status;
        Files = new FileRestorer(this);
    }

    public Inventory Source { get; }

    /// <summary>What was chosen on this PC, including any change of handling (e.g. reinstall instead of copy).</summary>
    public Selection Selection { get; }
    public RestoreOptions Options { get; }
    public MachineInfo Target { get; }
    public IReadOnlyList<UserProfileInfo> TargetUsers { get; }
    public HiveCache Hives { get; }
    public IChangeJournal Journal { get; }
    public ResultSet Results { get; }
    public FileRestorer Files { get; }

    public bool DryRun => Options.DryRun;
    public bool IsSandboxed => Options.SandboxRoot is not null;

    /// <summary>True when changes outside files and the registry (installs, netsh, printers) must not really run.</summary>
    public bool SimulateCommands => DryRun || IsSandboxed;

    public string SourceComputer => Source.Machine.ComputerName;

    /// <summary>Per-restore scratch state for migrators that collect entries before acting on them.</summary>
    public T State<T>(string key) where T : class, new() => (T)state.GetOrAdd(key, _ => new T());

    public void Status(InventoryItem? item, string? detail, bool force = false)
    {
        if (!force && statusClock.ElapsedMilliseconds < 150)
            return;
        statusClock.Restart();
        status(item?.Name, detail);
    }

    public UserProfileInfo? SourceUser(InventoryItem item) => Source.FindUser(item.UserSid);

    /// <summary>The user on this PC that the item's source user maps to, or null when unmapped.</summary>
    public UserProfileInfo? TargetUser(InventoryItem item)
    {
        if (item.UserSid is null || !Options.UserMap.TryGetValue(item.UserSid, out var targetSid))
            return null;
        return TargetUsers.FirstOrDefault(u => string.Equals(u.Sid, targetSid, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Looks up the target user and records a skip when there is none.</summary>
    public bool TryGetTargetUser(InventoryItem item, out UserProfileInfo target)
    {
        target = TargetUser(item)!;
        if (target is not null)
            return true;
        var source = SourceUser(item);
        Results.MarkSkipped(item, $"No user on this PC was chosen to receive {source?.AccountName ?? "this user"}'s data.");
        return false;
    }

    public RegistryRoot? UserRegistry(InventoryItem item, UserProfileInfo target)
    {
        if (IsSandboxed)
            return RegistryRoot.Sandbox($"USER_{target.Sid}", "HKCU");
        var hive = Hives.Get(target, out var error);
        if (hive is null)
        {
            Results.Error(item, $"Could not open {target.AccountName}'s registry: {error}");
            return null;
        }
        return RegistryRoot.User(hive, target.ProfilePath);
    }

    public RegistryRoot MachineRegistry() => IsSandboxed ? RegistryRoot.Sandbox("HKLM", "HKLM") : RegistryRoot.Machine();

    /// <summary>Where a known folder (Documents, Desktop…) is for a user on this PC, honouring OneDrive and folder redirection.</summary>
    public string TargetFolder(UserProfileInfo target, KnownFolder folder) =>
        targetFolders.GetOrAdd($"{target.Sid}|{folder.Key}", _ =>
            KnownFolders.Resolve(folder, target.ProfilePath, Hives.Get(target, out string? _)?.Root));

    /// <summary>Rewrites the source user's paths to the target user's for strings in settings, shortcuts and registry values.</summary>
    public PathRemapper RemapperFor(InventoryItem item) => remappers.GetOrAdd(item.UserSid ?? "", sid =>
    {
        var remap = new PathRemapper();
        foreach (var (from, to) in Options.FolderMap)
            remap.Add(from, to);

        var source = Source.FindUser(sid);
        var target = TargetUser(item);
        if (source is null || target is null)
            return remap;
        foreach (var (key, sourcePath) in source.FolderPaths)
        {
            var folder = KnownFolders.ByKey(key);
            if (folder is not null)
                remap.Add(sourcePath, TargetFolder(target, folder));
        }
        remap.Add(source.ProfilePath, target.ProfilePath);
        return remap;
    });

    /// <summary>The path actually written: the real path, or its stand-in under the sandbox root for test restores.</summary>
    public string Redirect(string path)
    {
        if (Options.SandboxRoot is not { } sandbox)
            return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return Path.Combine(sandbox, "UNC", path.TrimStart('\\'));
        return path.Length >= 3 && path[1] == ':' ? Path.Combine(sandbox, path[..1], path[3..]) : Path.Combine(sandbox, path.TrimStart('\\'));
    }

    /// <summary>
    /// Runs an external command that changes the system, journaling how to undo it. In dry runs and sandboxed
    /// restores the command is only logged.
    /// </summary>
    public async Task<ProcessResult> RunAsync(InventoryItem item, string description, string fileName, string arguments,
        string? undoFileName, string? undoArguments, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (SimulateCommands)
        {
            Log.Info($"[{item.Name}] Would run: {fileName} {arguments}");
            return new ProcessResult(0, "", "");
        }
        var result = await ProcessRunner.RunRawAsync(fileName, arguments, ct, timeout ?? TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        if (result.Succeeded && undoFileName is not null)
            Journal.CommandRun(description, undoFileName, undoArguments ?? "");
        return result;
    }

    /// <summary>Like <see cref="RunAsync"/> but for a PowerShell script; the undo is also a PowerShell script.</summary>
    public async Task<ProcessResult> RunPowerShellAsync(InventoryItem item, string description, string script, string? undoScript,
        CancellationToken ct, TimeSpan? timeout = null)
    {
        if (SimulateCommands)
        {
            Log.Info($"[{item.Name}] Would run PowerShell: {script}");
            return new ProcessResult(0, "", "");
        }
        var result = await PowerShellRunner.RunAsync(script, ct, timeout).ConfigureAwait(false);
        if (result.Succeeded && undoScript is not null)
        {
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop';" + undoScript));
            Journal.CommandRun(description, Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}");
        }
        return result;
    }
}
