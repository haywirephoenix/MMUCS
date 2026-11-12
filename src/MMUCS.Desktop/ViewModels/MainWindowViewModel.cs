using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Scumm;
using Dock.Model.Controls;
using MMUCS.Desktop.Services;
using MMUCS.Desktop.ViewModels.Docking;

namespace MMUCS.Desktop.ViewModels;

public sealed record MetadataRow(string Key, string Value);

public enum ViewerTab { Room = 0, Costume = 1, Script = 2 }

public sealed partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty] private string _title = $"MMUCS {AppInfo.Version}";
    [ObservableProperty] private string _status = "Open a SCUMM resource file (File ▸ Open…, or drag one onto the window).";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private BlockNodeViewModel _selectedNode;
    [ObservableProperty] private ReadOnlyMemory<byte> _hexData;
    [ObservableProperty] private long _hexBaseOffset;
    [ObservableProperty] private int _selectedViewerIndex;

    public ObservableCollection<BlockNodeViewModel> Roots { get; } = new();
    public ObservableCollection<MetadataRow> Metadata { get; } = new();

    public TreeOptions TreeOptions { get; } = new();

    public AppSettings Settings { get; }

    /// <summary>Most-recent-first list of opened files (mirrors <see cref="AppSettings.RecentFiles"/>).</summary>
    public ObservableCollection<string> RecentFiles { get; } = new();

    [ObservableProperty] private bool _reopenLastFileOnStartup;
    [ObservableProperty] private string _filterText;
    [ObservableProperty] private string _filterSummary;

    private ScummBlock _rootBlock;

    /// <summary>Root of the loaded project (virtual root when several files are loaded), or null.</summary>
    public ScummBlock RootBlock => _rootBlock;
    public bool HasProject => _rootBlock != null;
    private DispatcherTimer _filterDebounce;

    /// <summary>Expanding more matches than this would make the TreeView sluggish.</summary>
    private const int MaxAutoExpandMatches = 150;

    public RoomViewModel Room { get; } = new();
    public ScriptViewModel Script { get; } = new();
    public AkosViewModel Costume { get; } = new();

    /// <summary>Docking layout: panels are views over this view model (see Docking/DockLayout.cs).</summary>
    public MmucsDockFactory DockFactory { get; }
    [ObservableProperty] private IRootDock _layout;

    /// <summary>Main window position/size from the last run (null on first run).</summary>
    public WindowPlacement SavedMainWindow { get; }

    /// <summary>Persists the panel layout, floating windows and main window placement.</summary>
    public void SaveLayout(WindowPlacement mainWindow) => DockLayoutStore.Save(Layout, mainWindow, DockFactory.HiddenIn, Costume.Layout);

    /// <summary>Raised by Ctrl+F; the Blocks panel focuses its filter box wherever it is docked.</summary>
    public event Action FocusFilterRequested;
    public void RequestFilterFocus() => FocusFilterRequested?.Invoke();

    private static readonly HashSet<uint> ScriptTags = new()
    {
        ScummTag.SCRP, ScummTag.LSCR, ScummTag.ENCD, ScummTag.EXCD, ScummTag.VERB,
    };

    public MainWindowViewModel()
    {
        Settings = AppSettings.Load();
        foreach (var f in Settings.RecentFiles) RecentFiles.Add(f);
        _reopenLastFileOnStartup = Settings.ReopenLastFileOnStartup;
        TreeOptions.ShowRoomNames = Settings.ShowRoomNames;
        TreeOptions.ShowObjectNames = Settings.ShowObjectNames;
        TreeOptions.ColorizeTags = Settings.ColorizeTags;
        TreeOptions.PropertyChanged += (_, e) => OnTreeOptionChanged(e.PropertyName);

        DockFactory = new MmucsDockFactory(this);
        var saved = DockLayoutStore.Read();
        SavedMainWindow = saved?.MainWindow;
        _layout = DockLayoutStore.TryRestore(DockFactory, saved) ?? DockFactory.CreateLayout();
        Costume.RestoreLayout(saved?.Costume);
        DockFactory.InitLayout(_layout);

        DockFactory.ActiveDockableChanged += (_, e) => { if (e.Dockable is { IsModified: true } d) d.IsModified = false; };
        Room.BlockFocusRequested += RevealFromViewer;
        Script.BlockFocusRequested += RevealFromViewer;
        Script.ShowScumm = Settings.ScriptShowScumm;
        Script.ShowOffsets = Settings.ScriptShowOffsets;
        Script.FontSize = Math.Clamp(Settings.ScriptFontSize, Controls.CodeView.MinFontSize, Controls.CodeView.MaxFontSize);
        Script.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScriptViewModel.FontSize))
            {
                if (Settings.ScriptFontSize == Script.FontSize) return;
                Settings.ScriptFontSize = Script.FontSize;
                ScheduleSettingsSave(); // wheel steps come in bursts
                return;
            }
            if (e.PropertyName is not (nameof(ScriptViewModel.ShowScumm) or nameof(ScriptViewModel.ShowOffsets))) return;
            if (Settings.ScriptShowScumm == Script.ShowScumm && Settings.ScriptShowOffsets == Script.ShowOffsets) return;
            Settings.ScriptShowScumm = Script.ShowScumm;
            Settings.ScriptShowOffsets = Script.ShowOffsets;
            Settings.Save();
        };
        ApplyRoomViewOptions();
        Costume.PlayInPlace = Settings.CostumePlayInPlace;
        Costume.LiveFrameChanged += Room.ShowLiveFrame;
        Costume.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AkosViewModel.PlayInPlace) || Settings.CostumePlayInPlace == Costume.PlayInPlace) return;
            Settings.CostumePlayInPlace = Costume.PlayInPlace;
            Settings.Save();
        };
        Room.PropertyChanged += (_, e) => { if (RoomViewOptionNames.Contains(e.PropertyName)) SaveRoomViewOptions(); };
        RoomActors.Links = Settings.CostumeLinks;
        Room.LinksChanged += () =>
        {
            Settings.Save();
            // The room is re-analysed on its next use; relabel its AKOS blocks in the hierarchy.
            Dispatcher.UIThread.Post(() => { foreach (var r in Roots) r.RefreshLabels(); }, DispatcherPriority.Background);
        };

        // Warnings stay in the debug log so parser noise doesn't overwrite useful status text.
        Log.Message += (level, msg) =>
        {
            if (level == LogLevel.Warning) return;
            Dispatcher.UIThread.Post(() => Status = level == LogLevel.Error ? $"⚠ {msg}" : msg);
        };
    }

    partial void OnSelectedViewerIndexChanged(int value) => DockFactory.Activate(
        (ViewerTab)value switch
        {
            ViewerTab.Costume => MmucsDockFactory.CostumeId,
            ViewerTab.Script => MmucsDockFactory.ScriptId,
            _ => MmucsDockFactory.RoomId,
        }, Layout);

    /// <summary>Brings a panel (by dock id) to the front.</summary>
    /// <summary>
    /// Brings a viewer forward. The index alone isn't enough: a tab clicked by hand doesn't change
    /// it, so setting the same value again wouldn't switch back.
    /// </summary>
    private void ShowViewer(ViewerTab tab)
    {
        if (SelectedViewerIndex != (int)tab) SelectedViewerIndex = (int)tab; // activates via OnSelectedViewerIndexChanged
        else OnSelectedViewerIndexChanged((int)tab);
    }

    /// <summary>Brings a panel to the front, putting it back first if it was hidden or its window closed.</summary>
    public void ShowPanel(string id) => DockFactory.ShowPanel(id, Layout);

    public bool IsPanelOpen(string id) => DockFactory.IsPanelOpen(id, Layout);

    /// <summary>Hides a placed panel, or puts a hidden one back.</summary>
    public void TogglePanel(string id)
    {
        if (IsPanelOpen(id)) DockFactory.HidePanel(id, Layout);
        else ShowPanel(id);
    }

    /// <summary>Closes floating windows and rebuilds the default arrangement.</summary>
    public void ResetLayout()
    {
        if (Layout?.Windows != null)
            foreach (var w in Layout.Windows.ToList()) w.Exit();

        var layout = DockFactory.CreateLayout();
        DockFactory.InitLayout(layout);
        Layout = layout;
        Costume.ResetLayout();
        OnSelectedViewerIndexChanged(SelectedViewerIndex);
    }

    private void OnTreeOptionChanged(string property)
    {
        Settings.ShowRoomNames = TreeOptions.ShowRoomNames;
        Settings.ShowObjectNames = TreeOptions.ShowObjectNames;
        Settings.ColorizeTags = TreeOptions.ColorizeTags;
        Settings.Save();

        if (property is nameof(TreeOptions.ShowRoomNames) or nameof(TreeOptions.ShowObjectNames))
        {
            // The filter matches label text, so an active filter must be re-run.
            if (!string.IsNullOrWhiteSpace(FilterText)) ApplyFilter();
            else foreach (var r in Roots) r.RefreshLabels();
        }
    }

    [RelayCommand]
    private void ToggleReopenLastFileOnStartup() => ReopenLastFileOnStartup = !ReopenLastFileOnStartup;

    partial void OnReopenLastFileOnStartupChanged(bool value)
    {
        Settings.ReopenLastFileOnStartup = value;
        Settings.Save();
    }

    /// <summary>Opens the command-line file if given, otherwise the last file when that option is on.</summary>
    public Task StartupAsync(string[] args)
    {
        if (args is { Length: > 0 }) return OpenFileAsync(args[0]);
        if (ReopenLastFileOnStartup && Settings.LastFile is { } last && File.Exists(last)) return OpenFileAsync(last);
        return Task.CompletedTask;
    }

    public void ClearRecentFiles()
    {
        Settings.RecentFiles.Clear();
        Settings.Save();
        RecentFiles.Clear();
    }

    private void RememberRecent(string path)
    {
        Settings.AddRecent(path);
        Settings.Save();
        RecentFiles.Clear();
        foreach (var f in Settings.RecentFiles) RecentFiles.Add(f);
    }

    private void ForgetRecent(string path)
    {
        if (Settings.RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) == 0) return;
        Settings.Save();
        RecentFiles.Remove(path);
    }

    /// <summary>
    /// Opens a resource file. For "NAME.LAn" every sibling .LA file is loaded too, since LA0 holds
    /// the room names that LA1/LA2 rooms refer to.
    /// </summary>
    public async Task OpenFileAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Log.Error($"File not found: {path}");
            ForgetRecent(path);
            return;
        }

        IsLoading = true;
        var sw = Stopwatch.StartNew();
        try
        {
            var files = ScummProject.FindRelatedFiles(path);
            var root = await Task.Run(() =>
            {
                var r = ScummProject.Load(files, (i, n, f) =>
                    Dispatcher.UIThread.Post(() => Status = $"Parsing {Path.GetFileName(f)} ({i + 1}/{n})…"));
                // Match every room's costumes to objects so AKOS blocks are named in the hierarchy.
                try { RoomActors.AnalyzeAll(r); }
                catch (Exception ex) { Log.Warn($"Costume analysis failed: {ex.Message}"); }
                return r;
            });

            _rootBlock = root;
            _selectionKey = files.Count > 0 ? Path.GetFullPath(files[0]).ToLowerInvariant() : null;
            OnPropertyChanged(nameof(HasProject));
            SelectedNode = null;
            ApplyFilter();
            RestoreSelection();

            string names = string.Join(", ", files.Select(Path.GetFileName));
            Title = $"MMUCS {AppInfo.Version} — {names}";
            Status = $"Parsed {names} ({root.Size:N0} bytes, {CountBlocks(root):N0} blocks) in {sw.ElapsedMilliseconds} ms";
            SelectedNode ??= Roots.FirstOrDefault();
            RememberRecent(path);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to parse {path}: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── Tree filter ──────────────────────────────────────────────────────────

    partial void OnFilterTextChanged(string value)
    {
        // Debounce so typing doesn't rebuild the tree on every keystroke.
        if (_filterDebounce == null)
        {
            _filterDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _filterDebounce.Tick += (_, _) => { _filterDebounce.Stop(); ApplyFilter(); };
        }
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    /// <summary>
    /// Rebuilds the tree for the current filter (same rules as the Godot version):
    /// a 4-letter known tag such as "AKOS" matches that tag exactly; anything else is a
    /// case-insensitive substring match on the tag or label. Matches keep their whole subtree,
    /// and ancestors of matches stay visible.
    /// </summary>
    public void ApplyFilter()
    {
        _filterDebounce?.Stop();
        if (_rootBlock == null) return;
        var previouslySelected = SelectedNode?.Block;
        string q = FilterText?.Trim();
        List<ScummBlock> matches = null;

        if (string.IsNullOrEmpty(q))
        {
            TreeOptions.VisibleBlocks = null;
            FilterSummary = null;
        }
        else
        {
            uint? tag = null;
            string upper = q.ToUpperInvariant();
            if (q.Length == 4 && ScummTag.TryGetMeta(upper, out _)) tag = ScummTag.FromString(upper);

            var visible = new HashSet<ScummBlock>();
            var found = new List<ScummBlock>();

            bool IsMatch(ScummBlock b) => tag.HasValue
                ? b.Tag == tag.Value
                : (b.TagName?.Contains(q, StringComparison.OrdinalIgnoreCase) == true
                   || BlockNodeViewModel.LabelFor(b, TreeOptions).Contains(q, StringComparison.OrdinalIgnoreCase));

            bool Walk(ScummBlock b, bool ancestorMatched)
            {
                bool self = IsMatch(b);
                if (self) found.Add(b);
                bool shown = self || ancestorMatched;
                bool anyChild = false;
                foreach (var c in b.Children) anyChild |= Walk(c, shown);
                if (shown || anyChild) visible.Add(b);
                return shown || anyChild;
            }

            foreach (var c in _rootBlock.Children) Walk(c, false);
            visible.Add(_rootBlock);
            TreeOptions.VisibleBlocks = visible;
            matches = found;
            FilterSummary = found.Count == 0 ? "No matches"
                : found.Count == 1 ? "1 match"
                : found.Count > MaxAutoExpandMatches ? $"{found.Count:N0} matches (expand to browse)"
                : $"{found.Count:N0} matches";
        }

        // Rebuild the node tree; children materialise lazily against VisibleBlocks.
        Roots.Clear();
        var rootVm = new BlockNodeViewModel(_rootBlock, TreeOptions) { IsExpanded = true };
        Roots.Add(rootVm);

        if (matches == null)
        {
            // Default view: expand down to the LFLF level so rooms are visible immediately.
            foreach (var c in rootVm.Children)
            {
                c.IsExpanded = c.Block.Tag is 0 or ScummTag.LECF;
                if (c.Block.Tag == 0)
                    foreach (var cc in c.Children) cc.IsExpanded = cc.Block.Tag == ScummTag.LECF;
            }
        }
        else if (matches.Count <= MaxAutoExpandMatches)
        {
            foreach (var m in matches) Reveal(m, expandTarget: false);
        }

        SelectedNode = previouslySelected != null ? Reveal(previouslySelected, expandTarget: false) : null;
    }

    /// <summary>Expands the path to <paramref name="target"/> and returns its node (null if filtered out).</summary>
    public BlockNodeViewModel Reveal(ScummBlock target, bool expandTarget)
    {
        var root = Roots.FirstOrDefault();
        if (root == null || target == null) return null;

        var chain = new List<ScummBlock>();
        for (var b = target; b != null; b = b.Parent) chain.Add(b);
        chain.Reverse();
        if (!ReferenceEquals(chain[0], root.Block)) return null;

        var node = root;
        for (int i = 1; i < chain.Count; i++)
        {
            node.IsExpanded = true;
            node = node.Children.FirstOrDefault(c => ReferenceEquals(c.Block, chain[i]));
            if (node == null) return null;
        }
        if (expandTarget) node.IsExpanded = true;
        return node;
    }

    public void ClearFilter() => FilterText = string.Empty;

    /// <summary>True while a tree selection is routing to a viewer, which may select in it in turn.</summary>
    private bool _routingFromTree;
    /// <summary>True while a viewer is selecting in the tree: load what it shows, but don't switch viewers.</summary>
    private bool _selectingFromViewer;

    /// <summary>
    /// Selects <paramref name="block"/> in the hierarchy on behalf of a viewer (e.g. an object picked in
    /// the Room view's Objects panel). The current viewer stays in front; a costume is loaded into the
    /// Costume viewer in the background.
    /// </summary>
    public void RevealFromViewer(ScummBlock block)
    {
        if (_routingFromTree || block == null || ReferenceEquals(SelectedNode?.Block, block)) return;
        var node = Reveal(block, expandTarget: false);
        if (node == null) return;
        _selectingFromViewer = true;
        try { SelectedNode = node; }
        finally { _selectingFromViewer = false; }
    }

    private static int CountBlocks(ScummBlock b) => 1 + b.Children.Sum(CountBlocks);

    // ── Selection memory ──────────────────────────────────────────────────────

    private string _selectionKey;
    private ScummBlock _lastRoom;
    private DispatcherTimer _selectionSave;

    /// <summary>A block's place in the tree as child indices from the root ("2/0/14"), stable while the file is.</summary>
    private static string PathOf(ScummBlock b)
    {
        var parts = new List<int>();
        for (; b?.Parent != null; b = b.Parent) parts.Add(b.Parent.Children.IndexOf(b));
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static ScummBlock BlockAt(ScummBlock root, string path)
    {
        var b = root;
        if (string.IsNullOrEmpty(path)) return b;
        foreach (var part in path.Split('/'))
        {
            if (!int.TryParse(part, out int i) || i < 0 || i >= b.Children.Count) return null;
            b = b.Children[i];
        }
        return b;
    }

    /// <summary>
    /// Reopens the room the Room viewer showed and selects the block that was selected when this game
    /// was last open. The room comes first and separately: the selection may be a costume, which
    /// doesn't open a room, or belong to another room (then that one opens, as clicking it would).
    /// </summary>
    private void RestoreSelection()
    {
        if (_rootBlock == null || _selectionKey == null) return;
        Settings.LastRoom ??= new();
        if (Settings.LastRoom.TryGetValue(_selectionKey, out var roomPath)
            && BlockAt(_rootBlock, roomPath) is { Tag: ScummTag.LFLF } lflf && RoomModel.TryCreate(lflf, out var room))
        {
            _lastRoom = room.Lflf;
            Script.ShowRoom(lflf);
            _ = Room.LoadAsync(room);
        }
        if (Settings.LastSelection.TryGetValue(_selectionKey, out var path)
            && BlockAt(_rootBlock, path) is { } block && Reveal(block, expandTarget: false) is { } node)
            SelectedNode = node;
    }

    /// <summary>Remembers the room the Room viewer shows, for the next time this game is opened.</summary>
    private void RememberRoom(ScummBlock lflf)
    {
        if (_selectionKey == null || lflf == null) return;
        Settings.LastRoom ??= new();
        string path = PathOf(lflf);
        if (Settings.LastRoom.TryGetValue(_selectionKey, out var old) && old == path) return;
        Settings.LastRoom[_selectionKey] = path;
        ScheduleSettingsSave();
    }

    /// <summary>Remembers the selection (written after a short pause so arrowing through the tree doesn't write each step).</summary>
    private void RememberSelection(ScummBlock block)
    {
        if (_selectionKey == null || block == null) return;
        Settings.LastSelection[_selectionKey] = PathOf(block);
        ScheduleSettingsSave();
    }

    private void ScheduleSettingsSave()
    {
        if (_selectionSave == null)
        {
            _selectionSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _selectionSave.Tick += (_, _) => { _selectionSave.Stop(); Settings.Save(); };
        }
        _selectionSave.Stop();
        _selectionSave.Start();
    }

    // ── Tab update dots ───────────────────────────────────────────────────────

    /// <summary>
    /// Marks a viewer's tab when it was given new content while another tab is in front (Dock draws
    /// IsModified as a dot on the tab); bringing the tab forward clears it.
    /// </summary>
    private void MarkUpdated(string panelId)
    {
        // Checked after the routing finishes: the viewer may be brought to the front right after loading.
        Dispatcher.UIThread.Post(() =>
        {
            if (DockFactory.FindPanel(panelId, Layout) is { } panel && panel.Owner is Dock.Model.Core.IDock owner
                && !ReferenceEquals(owner.ActiveDockable, panel))
                panel.IsModified = true;
        }, DispatcherPriority.Background);
    }

    partial void OnSelectedNodeChanged(BlockNodeViewModel value)
    {
        Metadata.Clear();
        if (value == null)
        {
            HexData = ReadOnlyMemory<byte>.Empty;
            return;
        }

        var b = value.Block;
        RememberSelection(b);
        Metadata.Add(new("Tag", value.Tag));
        if (!string.IsNullOrEmpty(b.FullName)) Metadata.Add(new("Name", b.FullName));
        if (!string.IsNullOrEmpty(b.Description)) Metadata.Add(new("Description", b.Description));
        Metadata.Add(new("Offset", $"0x{b.Offset:X8}  ({b.Offset:N0})"));
        Metadata.Add(new("Size", $"{b.Size:N0} bytes"));
        Metadata.Add(new("Data", $"0x{b.DataOffset:X8} + {b.DataLength:N0}"));
        Metadata.Add(new("Children", b.Children.Count.ToString()));
        Metadata.Add(new("Path", b.FullPath));
        foreach (var kv in b.GetMetaDataDict())
            Metadata.Add(new(kv.Key.ToString(), kv.Value.ToString()));

        if (b.FileData != null)
        {
            // Root covers the whole file; the hex view is virtualized so that's fine.
            HexBaseOffset = b.Offset;
            HexData = new ReadOnlyMemory<byte>(b.FileData, b.Offset, b.Size);
        }

        _routingFromTree = true;
        try { RouteToViewer(b); }
        finally { _routingFromTree = false; }
    }

    /// <summary>Opens the most relevant viewer for the selected block.</summary>
    private void RouteToViewer(ScummBlock b)
    {
        var akos = b.Tag == ScummTag.AKOS ? b : b.FindParent(ScummTag.AKOS);
        if (akos != null)
        {
            _ = Costume.LoadAsync(akos);
            MarkUpdated(MmucsDockFactory.CostumeId);
            if (!_selectingFromViewer) ShowViewer(ViewerTab.Costume);
            return;
        }
        if (_selectingFromViewer) return; // the viewer already shows it

        if (ScriptTags.Contains(b.Tag))
        {
            Script.Show(b);
            ShowViewer(ViewerTab.Script);
            return;
        }
        if (Script.ShowRoom(b)) MarkUpdated(MmucsDockFactory.ScriptId); // the Script viewer lists the selected room's scripts

        if ((b.Tag == ScummTag.LFLF || b.FindParent(ScummTag.LFLF) != null) && RoomModel.TryCreate(b, out var room))
        {
            if (!ReferenceEquals(_lastRoom, room.Lflf)) MarkUpdated(MmucsDockFactory.RoomId);
            _lastRoom = room.Lflf;
            RememberRoom(room.Lflf);
            _ = Room.LoadAsync(room);
            if (b.Tag == ScummTag.OBIM || b.FindParent(ScummTag.OBIM) is { } obim)
            {
                var target = b.Tag == ScummTag.OBIM ? b : b.FindParent(ScummTag.OBIM);
                Room.SelectedObject = room.Objects.FirstOrDefault(o => ReferenceEquals(o.Block, target));
            }
            ShowViewer(ViewerTab.Room);
        }
    }

    // ── Room viewer display choices, kept across sessions ──

    private static readonly HashSet<string> RoomViewOptionNames = new()
    {
        nameof(RoomViewModel.ShowBackground), nameof(RoomViewModel.ShowObjects), nameof(RoomViewModel.ShowFullScreenObjects),
        nameof(RoomViewModel.ShowUnmatchedCostumes), nameof(RoomViewModel.ShowHotspots), nameof(RoomViewModel.ShowWalkboxes),
        nameof(RoomViewModel.ShowLabels), nameof(RoomViewModel.ShowBounds), nameof(RoomViewModel.ShowObjectsPanel),
    };
    private bool _applyingRoomView;

    private void ApplyRoomViewOptions()
    {
        var o = Settings.RoomView ??= new RoomViewOptions();
        _applyingRoomView = true;
        Room.ShowBackground = o.ShowBackground;
        Room.ShowObjects = o.ShowObjects;
        Room.ShowFullScreenObjects = o.ShowFullScreenObjects;
        Room.ShowUnmatchedCostumes = o.ShowUnmatchedCostumes;
        Room.ShowHotspots = o.ShowHotspots;
        Room.ShowWalkboxes = o.ShowWalkboxes;
        Room.ShowLabels = o.ShowLabels;
        Room.ShowBounds = o.ShowBounds;
        Room.ShowObjectsPanel = o.ShowObjectsPanel;
        _applyingRoomView = false;
    }

    private void SaveRoomViewOptions()
    {
        if (_applyingRoomView) return;
        var next = new RoomViewOptions
        {
            ShowBackground = Room.ShowBackground,
            ShowObjects = Room.ShowObjects,
            ShowFullScreenObjects = Room.ShowFullScreenObjects,
            ShowUnmatchedCostumes = Room.ShowUnmatchedCostumes,
            ShowHotspots = Room.ShowHotspots,
            ShowWalkboxes = Room.ShowWalkboxes,
            ShowLabels = Room.ShowLabels,
            ShowBounds = Room.ShowBounds,
            ShowObjectsPanel = Room.ShowObjectsPanel,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(next);
        if (json == System.Text.Json.JsonSerializer.Serialize(Settings.RoomView)) return;
        Settings.RoomView = next;
        Settings.Save();
    }
}
