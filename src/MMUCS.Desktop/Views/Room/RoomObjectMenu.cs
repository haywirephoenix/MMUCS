using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using MMUCS.Core;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.ViewModels;

namespace MMUCS.Desktop.Views.Room;

/// <summary>The right-click menu for room objects, the same in the room and in the Objects list.</summary>
internal static class RoomObjectMenu
{
    /// <summary>
    /// Shows the menu for <paramref name="obj"/> (or, when null, for all objects) at <paramref name="target"/>:
    /// show / hide, show only, select, link a costume, review marks.
    /// </summary>
    public static void Show(RoomViewModel vm, RoomObject obj, Control target)
    {
        if (vm?.Room == null)
        {
            return;
        }

        static MenuItem Item(string header, System.Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            return item;
        }

        var menu = new ContextMenu();
        var items = obj == null ? null : vm.ActionItems(vm.ItemFor(obj));
        if (items is { Count: > 1 })
        {
            int n = items.Count;
            menu.Items.Add(Item($"Show {n} selected", () => vm.SetVisible(items, true)));
            menu.Items.Add(Item($"Hide {n} selected", () => vm.SetVisible(items, false)));
            menu.Items.Add(Item($"Show only the {n} selected", () => vm.ShowOnly(items)));
            AddReviewItems(menu, vm, items, Item, target);
            menu.Items.Add(new Separator());
        }
        else if (obj != null)
        {
            // Underscores in names would otherwise be read as access keys.
            string name = obj.ToString().Replace("_", "__");
            bool visible = vm.IsVisible(obj);
            menu.Items.Add(Item(visible ? $"Hide {name}" : $"Show {name}", () => vm.SetVisible(obj, !visible)));
            menu.Items.Add(Item($"Show only {name}", () => vm.ShowOnly(obj)));
            menu.Items.Add(Item("Select", () => vm.SelectedObject = obj));
            if (CostumeMenu(vm, obj) is { } costumeMenu)
            {
                menu.Items.Add(costumeMenu);
            }

            if (vm.ItemFor(obj) is { } entry)
            {
                AddReviewItems(menu, vm, [entry], Item, target);
            }

            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Item("Show all objects", () => vm.SetAllVisible(true), vm.AllObjectsVisible != true));
        menu.Items.Add(Item("Hide all objects", () => vm.SetAllVisible(false), vm.AllObjectsVisible != false));
        menu.Open(target);
    }

    /// <summary>Mark for review… (one mark for all of <paramref name="items"/>), and clearing their marks.</summary>
    private static void AddReviewItems(ContextMenu menu, RoomViewModel vm, IReadOnlyList<RoomObjectItem> items,
        System.Func<string, System.Action, bool, MenuItem> item, Control target)
    {
        var markable = items.Where(i => vm.TargetOf(i) != null).ToList();
        if (markable.Count == 0)
        {
            return;
        }

        var existing = vm.MarkFor(markable);
        string what = markable.Count > 1 ? $" {markable.Count} together" : "";
        menu.Items.Add(new Separator());
        menu.Items.Add(item(existing != null ? $"Edit review mark{what}…" : $"Mark{what} for review…", () => _ = MarkAsync(target, vm, markable, existing), true));
        int marks = markable.SelectMany(i => i.ReviewMarks).Select(m => m.Id).Distinct().Count();
        if (marks > 0)
        {
            menu.Items.Add(item(marks == 1 ? "Clear review mark" : $"Clear {marks} review marks", () => vm.ClearReviewMarks(markable), true));
        }
    }

    /// <summary>Asks what looks wrong (tags and a note) and saves one mark for the entries, for mroom's review command.</summary>
    private static async System.Threading.Tasks.Task MarkAsync(Control target, RoomViewModel vm, List<RoomObjectItem> items, ReviewMark existing)
    {
        if (TopLevel.GetTopLevel(target) is not Window owner)
        {
            return;
        }

        string name = items.Count == 1 ? items[0].Name : $"{items.Count} together: " + string.Join(", ", items.Select(i => i.Name));
        var result = await ReviewMarkDialog.Ask(owner, name, existing);
        if (result is { } r)
        {
            vm.MarkForReview(items, r.Tags, r.Note);
        }
    }

    /// <summary>
    /// "Link costume" for an object (or "Link to object" for an unclaimed costume): for costumes the
    /// matching can't place, such as an inventory item's placeholder hotspot. Links are saved.
    /// </summary>
    private static MenuItem CostumeMenu(RoomViewModel vm, RoomObject obj)
    {
        static string Esc(string s) => s.Replace("_", "__");
        MenuItem Radio(string header, bool isChecked, System.Action action)
        {
            var item = new MenuItem { Header = Esc(header), ToggleType = MenuItemToggleType.Radio, IsChecked = isChecked };
            item.Click += (_, _) => action();
            return item;
        }

        var costumes = vm.RoomCostumes;
        if (costumes.Count == 0 || vm.Room == null)
        {
            return null;
        }

        if (obj.Index < 0)
        {
            // An unclaimed costume: pick the object it belongs to.
            var actor = vm.ItemFor(obj)?.Costume;
            if (actor == null)
            {
                return null;
            }

            var targets = vm.Room.Objects.Where(o => o.ImageCount == 0).OrderBy(o => o.Name).ToList();
            if (targets.Count == 0)
            {
                return null;
            }

            var toObject = new MenuItem { Header = "Link to object" };
            foreach (var o in targets)
            {
                toObject.Items.Add(Radio(o.ToString(), false, () => _ = vm.SetCostumeLinkAsync(o, actor.Costume)));
            }

            return toObject;
        }

        int? link = vm.GetCostumeLink(obj);
        var current = vm.ItemFor(obj)?.Costume;
        var menu = new MenuItem { Header = "Link costume" };
        menu.Items.Add(Radio(current != null && link == null ? $"Automatic (costume {current.Costume})" : "Automatic", link == null, () => _ = vm.SetCostumeLinkAsync(obj, null)));
        menu.Items.Add(Radio("None", link == 0, () => _ = vm.SetCostumeLinkAsync(obj, 0)));
        menu.Items.Add(new Separator());
        foreach (var c in costumes)
        {
            string owner = c.Object != null && !ReferenceEquals(c.Object.Block, obj.Block) ? $"   — {c.Object}" : "";
            menu.Items.Add(Radio(c.Label + owner, link == c.Costume, () => _ = vm.SetCostumeLinkAsync(obj, c.Costume)));
        }
        return menu;
    }
}
