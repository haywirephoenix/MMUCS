using System.Collections.Generic;
using Avalonia.Input;

namespace MMUCS.Desktop.Controls;

/// <summary>
/// Ctrl+Alt, pressed together in either order, from the keys' own down and up events. The modifiers a key
/// event carries can't be trusted for this: X11 reports the state before the key, so Alt's own press after
/// Ctrl says only Ctrl is down, and AltGr comes as Alt+Ctrl or on its own depending on the layout.
/// </summary>
public sealed class ReleaseChord
{
    private readonly HashSet<Key> _held = [];

    /// <summary>A key went down; true when it completes Ctrl+Alt.</summary>
    public bool Down(Key key)
    {
        if (!IsPart(key)) return false;
        _held.Add(key);
        return (_held.Contains(Key.LeftCtrl) || _held.Contains(Key.RightCtrl)) && (_held.Contains(Key.LeftAlt) || _held.Contains(Key.RightAlt));
    }

    public void Up(Key key) => _held.Remove(key);

    /// <summary>Forget what's held (the window lost the keyboard and won't see the keys come up).</summary>
    public void Reset() => _held.Clear();

    public static bool IsPart(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt;
}
