using MMUCS.Core.Fonts;
using Xunit;

namespace MMUCS.Desktop.Tests;

/// <summary>Room text colours (blast-text): value 1 takes the text's colour, the rest are palette indices as they are.</summary>
public class RoomTextColorTests
{
    [Fact]
    public void Value_1_is_the_colour_and_the_rest_stay()
    {
        Assert.Equal(19, TextRasterizer.MapValue(1, 0, 19, TextColorMode.Room));
        Assert.Equal(225, TextRasterizer.MapValue(225, 0, 19, TextColorMode.Room));
        Assert.Equal(-1, TextRasterizer.MapValue(0, 0, 19, TextColorMode.Room));
        // Cutscene text keeps its own rule: font 1's ink (1) isn't the colour code's negation, so it stays 1.
        Assert.Equal(1, TextRasterizer.MapValue(1, 0, 19));
    }
}
