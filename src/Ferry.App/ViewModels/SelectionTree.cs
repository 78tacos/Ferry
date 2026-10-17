using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Ferry.App.Mvvm;
using Ferry.Core.Engine;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;

namespace Ferry.App.ViewModels;

public sealed record ModeOption(string Value, string Display);

/// <summary>A node in the "what to move" tree: a category, a group (user, Wi-Fi…) or an item. Checks cascade both ways.</summary>
public class SelectionNode : Observable
{
    private bool? isChecked;
    private bool isExpanded;
    private bool isVisible = true;
    private bool updating;

    public SelectionNode(string name, string? description, string icon, SelectionNode? parent)
    {
        Name = name;
        Description = description;
        Icon = icon;
        Parent = parent;
    }

    public string Name { get; }
    public string? Description { get; protected set; }
    public string Icon { get; }
    public SelectionNode? Parent { get; }
    public ObservableCollection<SelectionNode> Children { get; } = [];

    /// <summary>Raised on the root when any check changes, so totals can be recomputed.</summary>
    public event Action? Changed;

    public bool? IsChecked
    {
        get => isChecked;
        set => SetChecked(value ?? false, updateChildren: true, updateParent: true);
    }

    public bool IsExpanded
    {
        get => isExpanded;
        set => Set(ref isExpanded, value);
    }

    public bool IsVisible
    {
        get => isVisible;
        set => Set(ref isVisible, value);
    }

    public virtual long Size => Children.Where(c => c.IsChecked != false).Sum(c => c.Size);
    public virtual long TotalSize => Children.Sum(c => c.TotalSize);
    public string SizeText => TotalSize > 0 ? Format.Bytes(Size == TotalSize || IsChecked == false ? TotalSize : Size) : "";
    public int SelectedCount => Children.Sum(c => c is ItemNode ? (c.IsChecked == true ? 1 : 0) : c.SelectedCount);
    public int ItemCount => Children.Sum(c => c is ItemNode ? 1 : c.ItemCount);
    public string CountText => ItemCount == 0 ? "" : $"{SelectedCount} of {ItemCount}";
    public virtual bool HasWarnings => Children.Any(c => c.HasWarnings);

    private void SetChecked(bool? value, bool updateChildren, bool updateParent)
    {
        if (updating || value == isChecked)
            return;
        updating = true;
        isChecked = value;
        if (updateChildren && value.HasValue)
        {
            foreach (var child in Children)
                child.SetChecked(value, updateChildren: true, updateParent: false);
        }
        if (updateParent)
            Parent?.Recalculate();
        updating = false;
        OnPropertiesChanged(nameof(IsChecked), nameof(SizeText), nameof(CountText));
        Root.Changed?.Invoke();
    }

    /// <summary>Re-derives this node's tri-state from its children.</summary>
    internal void Recalculate()
    {
        bool? state = Children.Count == 0 ? isChecked
            : Children.All(c => c.IsChecked == true) ? true
            : Children.All(c => c.IsChecked == false) ? false
            : null;
        if (state != isChecked)
        {
            isChecked = state;
            OnPropertyChanged(nameof(IsChecked));
        }
        OnPropertiesChanged(nameof(SizeText), nameof(CountText));
        Parent?.Recalculate();
    }

    public SelectionNode Root => Parent?.Root ?? this;

    /// <summary>0 for the root, 1 for categories, 2 for groups, 3 for items under a group.</summary>
    public int Depth => Parent is null ? 0 : Parent.Depth + 1;

    /// <summary>Width of the name column, shrinking with indentation so the size column lines up at every level.</summary>
    public double NameWidth => Math.Max(220, 400 - (Depth - 1) * 20);

    /// <summary>Header rows have no mode column, so their name area also spans it.</summary>
    public double HeaderWidth => NameWidth + ModeColumnWidth;

    public const double ModeColumnWidth = 236;

    /// <summary>Sets the initial state while the tree is built; parents recalculate once at the end.</summary>
    internal void InitializeChecked(bool value) => isChecked = value;

    internal void NotifyTreeChanged() => Root.Changed?.Invoke();

    public IEnumerable<ItemNode> Items() => Children.SelectMany(c => c is ItemNode item ? [item] : c.Items());

    /// <summary>Shows only items whose name, group or description contains <paramref name="text"/>; expands to reveal them.</summary>
    public bool ApplyFilter(string text)
    {
        if (this is ItemNode item)
        {
            item.IsVisible = text.Length == 0 ||
                             item.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                             (item.Description?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false);
            return item.IsVisible;
        }
        var any = false;
        foreach (var child in Children)
            any |= child.ApplyFilter(text);
        IsVisible = any || text.Length == 0;
        if (text.Length > 0 && any)
            IsExpanded = true;
        return IsVisible;
    }
}

public sealed class ItemNode : SelectionNode
{
    private ModeOption? mode;

    public ItemNode(InventoryItem item, SelectionNode parent, bool isChecked, string? mode)
        : base(item.Name, item.Description, "", parent)
    {
        Item = item;
        Modes = item.AvailableModes.Select(m => new ModeOption(m, ItemModes.DisplayName(m))).ToList();
        this.mode = Modes.FirstOrDefault(m => m.Value == mode) ?? Modes.FirstOrDefault(m => m.Value == item.DefaultMode) ?? Modes.FirstOrDefault();
        InitializeChecked(isChecked);
    }

    public InventoryItem Item { get; }
    public IReadOnlyList<ModeOption> Modes { get; }
    public bool HasModes => Modes.Count > 1;

    public ModeOption? Mode
    {
        get => mode;
        set
        {
            if (Set(ref mode, value))
            {
                OnPropertyChanged(nameof(SizeText));
                Parent?.Recalculate();
                NotifyTreeChanged();
            }
        }
    }

    public string? ModeText => Modes.Count == 1 ? Modes[0].Display : null;

    public override long TotalSize => Preflight.EffectiveSize(Item, ModeSelection());
    public override long Size => IsChecked == true ? TotalSize : 0;
    public override bool HasWarnings => Item.Warnings.Count > 0;
    public string WarningText => string.Join(Environment.NewLine, Item.Warnings);
    public string? DetailText => Item.FileCount > 0 ? $"{Item.FileCount:N0} files" : null;

    private Selection ModeSelection()
    {
        var selection = new Selection();
        if (mode is not null)
            selection.Modes[Item.Id] = mode.Value;
        return selection;
    }
}

/// <summary>Builds the tree for an inventory and reads the resulting selection back out.</summary>
public sealed class SelectionTree
{
    private static readonly Dictionary<MigrationCategory, string> CategoryIcons = new()
    {
        [MigrationCategory.WindowsSettings] = "",
        [MigrationCategory.Applications] = "",
        [MigrationCategory.AppSettings] = "",
        [MigrationCategory.BrowsersAndEmail] = "",
        [MigrationCategory.Files] = "",
    };

    public SelectionTree(Inventory inventory, Selection initial)
    {
        Root = new SelectionNode("All", null, "", null);
        foreach (var category in inventory.Items.GroupBy(i => i.Category).OrderBy(g => g.Key))
        {
            var categoryNode = new SelectionNode(Categories.DisplayName(category.Key), Categories.Description(category.Key),
                CategoryIcons.GetValueOrDefault(category.Key, ""), Root) { IsExpanded = category.Key != MigrationCategory.AppSettings };
            Root.Children.Add(categoryNode);
            foreach (var group in category.GroupBy(i => i.Group))
            {
                var groupNode = new SelectionNode(group.Key, null, "", categoryNode) { IsExpanded = category.Key is MigrationCategory.Files or MigrationCategory.WindowsSettings };
                categoryNode.Children.Add(groupNode);
                foreach (var item in group)
                    groupNode.Children.Add(new ItemNode(item, groupNode, initial.Contains(item.Id), initial.Modes.GetValueOrDefault(item.Id)));
                groupNode.Recalculate();
            }
            // A category with a single group shows its items directly under it.
            if (categoryNode.Children.Count == 1 && string.IsNullOrEmpty(categoryNode.Children[0].Name))
                categoryNode.IsExpanded = true;
        }
    }

    public SelectionNode Root { get; }

    public Selection ToSelection()
    {
        var selection = new Selection();
        foreach (var item in Root.Items().Where(i => i.IsChecked == true))
        {
            selection.ItemIds.Add(item.Item.Id);
            if (item.Mode is { } mode)
                selection.Modes[item.Item.Id] = mode.Value;
        }
        return selection;
    }

    public void Apply(Func<InventoryItem, bool> shouldCheck)
    {
        foreach (var item in Root.Items())
            item.IsChecked = shouldCheck(item.Item);
    }
}
