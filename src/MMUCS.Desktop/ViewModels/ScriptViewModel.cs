using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MMUCS.Core.Scumm;

namespace MMUCS.Desktop.ViewModels;

/// <summary>
/// The Script viewer: the current room's scripts in a sidebar, and the selected one as SCUMM
/// source (decompiled) or as an instruction listing.
/// </summary>
public sealed partial class ScriptViewModel : ObservableObject
{
    public ObservableCollection<ScriptEntry> Scripts { get; } = new();

    [ObservableProperty] private ScriptEntry _selectedScript;
    [ObservableProperty] private string _text = "Select a room or a script in the hierarchy.";
    [ObservableProperty] private string _title = "No room";
    [ObservableProperty] private string _info;
    /// <summary>SCUMM source (true) or the instruction listing (false).</summary>
    [ObservableProperty] private bool _showScumm = true;
    [ObservableProperty] private bool _showScriptsPanel = true;

    /// <summary>A script picked in the sidebar: the hierarchy follows without switching viewers.</summary>
    public event Action<ScummBlock> BlockFocusRequested;

    private ScummBlock _lflf;
    private CancellationTokenSource _cts;
    private bool _selectingFromTree;

    public bool ShowInstructions
    {
        get => !ShowScumm;
        set => ShowScumm = !value;
    }

    partial void OnShowScummChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowInstructions));
        Refresh();
    }

    /// <summary>Lists the scripts of the room containing <paramref name="any"/>; false when it's the same room.</summary>
    public bool ShowRoom(ScummBlock any)
    {
        var lflf = any?.Tag == ScummTag.LFLF ? any : any?.FindParent(ScummTag.LFLF);
        if (ReferenceEquals(lflf, _lflf)) return false;
        _lflf = lflf;
        Scripts.Clear();
        if (lflf == null)
        {
            Title = "No room";
            return true;
        }
        foreach (var e in RoomScripts.List(lflf)) Scripts.Add(e);
        Title = RoomModel.TryCreate(lflf, out var room)
            ? $"Room {room.RoomNo}{(string.IsNullOrEmpty(room.Name) ? "" : $"  {room.Name}")}  —  {Scripts.Count} scripts"
            : $"{Scripts.Count} scripts";
        return true;
    }

    /// <summary>Shows a script block chosen in the hierarchy, selecting it in the sidebar.</summary>
    public void Show(ScummBlock block)
    {
        ShowRoom(block);
        var entry = RoomScripts.For(block);
        if (entry == null) return;
        var listed = Scripts.FirstOrDefault(s => ReferenceEquals(s.Block, entry.Block));
        if (listed == null)
        {
            // Outside a room (or not listed): show it on its own.
            listed = entry;
            Scripts.Add(entry);
        }
        _selectingFromTree = true;
        try
        {
            if (ReferenceEquals(SelectedScript, listed)) Refresh();
            else SelectedScript = listed;
        }
        finally { _selectingFromTree = false; }
    }

    partial void OnSelectedScriptChanged(ScriptEntry value)
    {
        Refresh();
        if (value != null && !_selectingFromTree) BlockFocusRequested?.Invoke(value.Block);
    }

    private async void Refresh()
    {
        _cts?.Cancel();
        var entry = SelectedScript;
        if (entry == null)
        {
            Text = Scripts.Count > 0 ? "Select a script." : "This room has no scripts.";
            Info = null;
            return;
        }

        var cts = _cts = new CancellationTokenSource();
        bool scumm = ShowScumm;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var text = await Task.Run(() => scumm
                ? ScummDecompiler.Format(RoomScripts.Decompile(entry))
                : RoomScripts.Disassemble(entry), cts.Token);
            if (cts.IsCancellationRequested) return;
            Text = text.Length > 0 ? text : "(empty)";
            Info = $"{entry.Detail}  ·  {text.Count(c => c == '\n'):N0} lines  ·  {sw.ElapsedMilliseconds} ms";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) Text = $"Couldn't decode this script: {ex.Message}";
        }
    }
}
