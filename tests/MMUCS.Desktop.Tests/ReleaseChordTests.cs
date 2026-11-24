using Avalonia.Input;
using MMUCS.Desktop.Controls;
using Xunit;

namespace MMUCS.Desktop.Tests;

/// <summary>Ctrl+Alt lets the game window's mouse go, in either order, whichever Ctrl and Alt, from the keys themselves.</summary>
public class ReleaseChordTests
{
    [Theory]
    [InlineData(Key.LeftCtrl, Key.LeftAlt)]
    [InlineData(Key.LeftAlt, Key.LeftCtrl)]
    [InlineData(Key.RightCtrl, Key.LeftAlt)]
    [InlineData(Key.LeftCtrl, Key.RightAlt)] // AltGr
    public void Ctrl_and_Alt_together_complete_it_in_either_order(Key first, Key second)
    {
        var chord = new ReleaseChord();
        Assert.False(chord.Down(first));
        Assert.True(chord.Down(second));
    }

    [Fact]
    public void A_key_let_go_in_between_doesn_t_count_and_nor_do_other_keys()
    {
        var chord = new ReleaseChord();
        Assert.False(chord.Down(Key.LeftCtrl));
        chord.Up(Key.LeftCtrl);
        Assert.False(chord.Down(Key.LeftAlt));
        Assert.False(chord.Down(Key.A));
        Assert.True(chord.Down(Key.RightCtrl));

        chord.Reset(); // the window lost focus: nothing is held any more
        Assert.False(chord.Down(Key.LeftAlt));
    }
}
