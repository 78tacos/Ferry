using Ferry.Core.Infrastructure;
using Ferry.Core.Model;
using Microsoft.Win32;

namespace Ferry.Core.Engine;

public sealed class ScanContext
{
    private readonly List<InventoryItem> items = [];
    private readonly Action<string> status;

    internal ScanContext(ScanOptions options, MachineInfo machine, IReadOnlyList<UserProfileInfo> users, HiveCache hives,
        ExclusionRules exclusions, Action<string> status)
    {
        Options = options;
        Machine = machine;
        Users = users;
        Hives = hives;
        Exclusions = exclusions;
        this.status = status;
    }

    public ScanOptions Options { get; }
    public MachineInfo Machine { get; }

    /// <summary>The users being scanned.</summary>
    public IReadOnlyList<UserProfileInfo> Users { get; }
    public HiveCache Hives { get; }
    public ExclusionRules Exclusions { get; }

    /// <summary>Problems that affect the whole scan rather than one item (e.g. "not running as administrator").</summary>
    public List<string> Warnings { get; } = [];

    public IReadOnlyList<InventoryItem> Items
    {
        get
        {
            lock (items)
                return items.ToList();
        }
    }

    public void Add(InventoryItem item)
    {
        lock (items)
        {
            if (items.Any(i => i.Id == item.Id))
            {
                Log.Debug($"Duplicate inventory id {item.Id} ignored");
                return;
            }
            items.Add(item);
        }
    }

    public void Status(string text) => status(text);

    /// <summary>The user's registry hive, or null (logged) when it can't be opened.</summary>
    public RegistryKey? UserRegistry(UserProfileInfo user) => Hives.Get(user, out _)?.Root;

    public void Warn(string text)
    {
        lock (Warnings)
        {
            if (!Warnings.Contains(text))
                Warnings.Add(text);
        }
        Log.Warn(text);
    }
}
