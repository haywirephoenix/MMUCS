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

    public RoomViewModel Room { get; } = new();
    public AkosViewModel Costume { get; } = new();

    private static readonly HashSet<uint> ScriptTags = new()
    {
        ScummTag.SCRP, ScummTag.LSCR, ScummTag.ENCD, ScummTag.EXCD, ScummTag.VERB,
    };

    public MainWindowViewModel()
    {
        // Warnings stay in the debug log so parser noise doesn't overwrite useful status text.
        Log.Message += (level, msg) =>
        {
            if (level == LogLevel.Warning) return;
            Dispatcher.UIThread.Post(() => Status = level == LogLevel.Error ? $"⚠ {msg}" : msg);
        };
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
            return;
        }

        IsLoading = true;
        var sw = Stopwatch.StartNew();
        try
        {
            var files = ScummProject.FindRelatedFiles(path);
            var root = await Task.Run(() => ScummProject.Load(files, (i, n, f) =>
                Dispatcher.UIThread.Post(() => Status = $"Parsing {Path.GetFileName(f)} ({i + 1}/{n})…")));

            Roots.Clear();
            var rootVm = new BlockNodeViewModel(root, TreeOptions) { IsExpanded = true };
            Roots.Add(rootVm);
            // Expand down to the LFLF level so rooms are visible immediately.
            foreach (var c in rootVm.Children)
            {
                c.IsExpanded = c.Block.Tag is 0 or ScummTag.LECF;
                if (c.Block.Tag == 0)
                    foreach (var cc in c.Children) cc.IsExpanded = cc.Block.Tag == ScummTag.LECF;
            }

            string names = string.Join(", ", files.Select(Path.GetFileName));
            Title = $"MMUCS — {names}";
            Status = $"Parsed {names} ({root.Size:N0} bytes, {CountBlocks(root):N0} blocks) in {sw.ElapsedMilliseconds} ms";
            SelectedNode = rootVm;
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
