using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MMUCS.Desktop.ViewModels;

/// <summary>
/// The Inspector's Position section: where the selection is drawn (an object image's top-left in the room, a
/// costume cel's offset from the game's cel). The game's own are shown and can't be changed; a mod's can, each
/// change shown at once in the viewers (<see cref="Show"/>'s preview), and Save writes it to the mod's meta.json,
/// Revert goes back to what's saved.
/// </summary>
public sealed partial class PositionViewModel : ObservableObject
{
    private Action<int, int> _preview;
    private Action<int, int> _save;
    private int _savedX, _savedY;
    private bool _showing;

    /// <summary>Section open (saved).</summary>
    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;

    /// <summary>Whether the selection has a position to show.</summary>
    [ObservableProperty] public partial bool IsAvailable { get; set; }

    /// <summary>What the position is of ("Object clring-a-haggis-caber, image 0").</summary>
    [ObservableProperty] public partial string Subject { get; set; }

    /// <summary>What X and Y mean and where they come from (the game, or the mod's file).</summary>
    [ObservableProperty] public partial string Source { get; set; }

    /// <summary>A mod's: X and Y can be changed and saved.</summary>
    [ObservableProperty] public partial bool IsModded { get; set; }

    [ObservableProperty] public partial decimal? X { get; set; }
    [ObservableProperty] public partial decimal? Y { get; set; }

    /// <summary>Changed since it was saved.</summary>
    public bool IsDirty => IsModded && ((int)(X ?? 0) != _savedX || (int)(Y ?? 0) != _savedY);

    /// <summary>
    /// Shows a position: <paramref name="modded"/> ones can be changed, each change going to
    /// <paramref name="preview"/>, and Save to <paramref name="save"/>.
    /// </summary>
    public void Show(string subject, string source, bool modded, int x, int y, Action<int, int> preview = null, Action<int, int> save = null)
    {
        _showing = true;
        Subject = subject;
        Source = source;
        IsModded = modded;
        (_savedX, _savedY) = (x, y);
        X = x;
        Y = y;
        _preview = modded ? preview : null;
        _save = modded ? save : null;
        IsAvailable = true;
        _showing = false;
        Changed();
    }

    /// <summary>Nothing selected has a position.</summary>
    public void Clear()
    {
        _showing = true;
        IsAvailable = false;
        IsModded = false;
        Subject = Source = null;
        X = Y = null;
        _preview = _save = null;
        _showing = false;
        Changed();
    }

    /// <summary>Moves a mod's position to <paramref name="x"/>,<paramref name="y"/> at once (one preview, as from the Room viewer's move tool).</summary>
    public void MoveTo(int x, int y)
    {
        if (!IsModded || ((int)(X ?? 0) == x && (int)(Y ?? 0) == y)) return;
        _showing = true;
        X = x;
        Y = y;
        _showing = false;
        Edited();
    }

    /// <summary>Moves a mod's position by <paramref name="dx"/>,<paramref name="dy"/> (the move tool's steps).</summary>
    public void MoveBy(int dx, int dy)
    {
        if (IsModded) MoveTo((int)(X ?? 0) + dx, (int)(Y ?? 0) + dy);
    }

    partial void OnXChanged(decimal? value) => Edited();
    partial void OnYChanged(decimal? value) => Edited();

    private void Edited()
    {
        if (_showing) return;
        _preview?.Invoke((int)(X ?? 0), (int)(Y ?? 0));
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Save()
    {
        int x = (int)(X ?? 0), y = (int)(Y ?? 0);
        try
        {
            _save?.Invoke(x, y);
            (_savedX, _savedY) = (x, y);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MMUCS.Core.Diagnostics.Log.Error($"Saving the position failed: {ex.Message}");
        }
        Changed();
    }

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Revert()
    {
        X = _savedX;
        Y = _savedY;
    }
}
