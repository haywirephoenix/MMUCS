using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.Services;

namespace MMUCS.Desktop.ViewModels;

public sealed record MetadataRow(string Key, string Value);

public enum ViewerTab { Room = 0, Costume = 1, Script = 2 }

public sealed partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "MMUCS";
    [ObservableProperty] private string _status = "Open a SCUMM resource file (File ▸ Open…, or drag one onto the window).";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private BlockNodeViewModel _selectedNode;
    [ObservableProperty] private ReadOnlyMemory<byte> _hexData;
    [ObservableProperty] private long _hexBaseOffset;
    [ObservableProperty] private int _selectedViewerIndex;
    [ObservableProperty] private string _scriptText;

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
    private DispatcherTimer _filterDebounce;

    /// <summary>Expanding more matches than this would make the TreeView sluggish.</summary>
    private const int MaxAutoExpandMatches = 150;

    public RoomViewModel Room { get; } = new();
    public AkosViewModel Costume { get; } = new();

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

        // Warnings stay in the debug log so parser noise doesn't overwrite useful status text.
        Log.Message += (level, msg) =>
        {
            if (level == LogLevel.Warning) return;
            Dispatcher.UIThread.Post(() => Status = level == LogLevel.Error ? $"⚠ {msg}" : msg);
        };
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
            var root = await Task.Run(() => ScummProject.Load(files, (i, n, f) =>
                Dispatcher.UIThread.Post(() => Status = $"Parsing {Path.GetFileName(f)} ({i + 1}/{n})…")));

            _rootBlock = root;
            SelectedNode = null;
            ApplyFilter();

            string names = string.Join(", ", files.Select(Path.GetFileName));
            Title = $"MMUCS — {names}";
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

    private static int CountBlocks(ScummBlock b) => 1 + b.Children.Sum(CountBlocks);

    partial void OnSelectedNodeChanged(BlockNodeViewModel value)
    {
        Metadata.Clear();
        if (value == null)
        {
            HexData = ReadOnlyMemory<byte>.Empty;
            return;
        }

        var b = value.Block;
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

        RouteToViewer(b);
    }

    /// <summary>Opens the most relevant viewer for the selected block.</summary>
    private void RouteToViewer(ScummBlock b)
    {
        var akos = b.Tag == ScummTag.AKOS ? b : b.FindParent(ScummTag.AKOS);
        if (akos != null)
        {
            _ = Costume.LoadAsync(akos);
            SelectedViewerIndex = (int)ViewerTab.Costume;
            return;
        }

        if (ScriptTags.Contains(b.Tag))
        {
            ShowScript(b);
            SelectedViewerIndex = (int)ViewerTab.Script;
            return;
        }

        if ((b.Tag == ScummTag.LFLF || b.FindParent(ScummTag.LFLF) != null) && RoomModel.TryCreate(b, out var room))
        {
            _ = Room.LoadAsync(room);
            if (b.Tag == ScummTag.OBIM || b.FindParent(ScummTag.OBIM) is { } obim)
            {
                var target = b.Tag == ScummTag.OBIM ? b : b.FindParent(ScummTag.OBIM);
                Room.SelectedObject = room.Objects.FirstOrDefault(o => ReferenceEquals(o.Block, target));
            }
            SelectedViewerIndex = (int)ViewerTab.Room;
        }
    }

    private void ShowScript(ScummBlock b)
    {
        try
        {
            var lines = new ScummScriptDecoder().Disassemble(b);
            var sb = new System.Text.StringBuilder();
            foreach (var l in lines)
            {
                sb.Append($"{l.Address:X4}  {l.Opcode:X2}  {l.Name,-16}");
                if (!string.IsNullOrEmpty(l.DecodedString)) sb.Append("  ").Append(l.DecodedString);
                else if (l.Arguments is { Count: > 0 }) sb.Append("  ").Append(string.Join(", ", l.Arguments));
                sb.AppendLine();
            }
            ScriptText = sb.Length > 0 ? sb.ToString() : "(empty)";
        }
        catch (Exception ex)
        {
            ScriptText = $"Disassembly failed: {ex.Message}";
        }
    }
}
