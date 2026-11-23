using System.Text;
using MMUCS.Core.Fonts;
using MMUCS.Core.Imaging;
using MMUCS.Core.Smush;
using Xunit;

namespace MMUCS.Desktop.Tests;

/// <summary>NUT fonts, cutscene TEXT chunks, and laying out and drawing them.</summary>
public class SubtitleTests
{
    private static string Game => Environment.GetEnvironmentVariable("MMUCS_GAME") is { } g && File.Exists(g) ? g : null;

    private static byte[] U16(params int[] values) => values.SelectMany(v => new[] { (byte)v, (byte)(v >> 8) }).ToArray();

    /// <summary>A row: its byte length, then the skip/run pairs.</summary>
    private static byte[] Row(params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        return [.. U16(body.Length), .. body];
    }

    [Fact]
    public void Codec_44_and_codec_21_rows_decode_the_same()
    {
        // A 4×2 glyph: row 0 = _ 7 7 _, row 1 = 9 _ _ _ (_ untouched).
        // As the encoders write them: codec 44 ends each row with a 0 byte one past the width, codec 21 with a skip past it.
        byte[] c44 = [.. Row(U16(1), U16(1), [7, 7], U16(1), U16(0), [0]), .. Row(U16(0), U16(0), [9], U16(3), U16(0), [0]), .. Row(U16(4))];
        byte[] c21 = [.. Row(U16(1), U16(1), [7, 7], U16(2)), .. Row(U16(0), U16(0), [9], U16(4)), .. Row(U16(5))];
        foreach (var data in new[] { c44, c21 })
        {
            var px = Enumerable.Repeat((byte)2, 8).ToArray();
            NutFont.DecodeLines(data, px, 4, 2);
            Assert.Equal(new byte[] { 2, 7, 7, 2, 9, 2, 2, 2 }, px);
        }
    }

    [Fact]
    public void A_text_chunk_gives_its_place_code_font_colour_and_text()
    {
        byte[] chunk = [.. U16(320, 410, 13, 27, 65534, 593, 483, 0), .. Encoding.Latin1.GetBytes("/OPGT485/^f00^c031Captain's log, Andr\xe9 \r\n\0")];
        var t = SmushText.Parse(chunk);
        Assert.Equal((320, 410), (t.X, t.Y));
        Assert.Equal(SmushTextFlags.Center | SmushTextFlags.Wrap | SmushTextFlags.Subtitle, t.Flags);
        Assert.Equal((27, -2, 593, 483), (t.ClipLeft, t.ClipTop, t.ClipWidth, t.ClipHeight));
        Assert.Equal("OPGT485", t.Code);
        Assert.Equal("text/opgt485", t.TextId);
        Assert.Equal((0, 31), (t.Font, t.Color));
        Assert.Equal("Captain's log, Andr\xe9 \n", t.Text);
        Assert.Equal('\xe9', t.Text[19]);
    }

    /// <summary>Every character 10 wide, 20 high.</summary>
    private sealed class Fixed : IGlyphMetrics
    {
        public float CharWidth(char c) => 10;
        public float CharHeight(char c) => 20;
        public float LineHeight => 20;
    }

    private static SmushText Line(string text, int x, int y, SmushTextFlags flags) =>
        new() { X = x, Y = y, Flags = flags, Text = text, Color = 31 };

    [Fact]
    public void Centred_lines_are_centred_on_x_and_each_line_is_one_pixel_taller_than_its_letters()
    {
        var layout = TextLayouter.Layout(Line("abcd\nab", 320, 100, SmushTextFlags.Center), new Fixed(), 640, 480);
        Assert.Equal((300f, 100f), (layout.Chars[0].X, layout.Chars[0].Y));
        Assert.Equal((310f, 121f), (layout.Chars[4].X, layout.Chars[4].Y));
        Assert.Equal(new TextRect(0, 0, 640, 480), layout.Clip);
    }

    [Fact]
    public void Wrapped_text_breaks_at_spaces_and_is_pushed_inside_the_margin()
    {
        // 100 words of 5 letters can't fit in 620 px; y 470 is pushed up so the block fits, x 0 pushed right.
        string text = string.Join(" ", Enumerable.Repeat("abcde", 100));
        var layout = TextLayouter.Layout(Line(text, 0, 470, SmushTextFlags.Center | SmushTextFlags.Wrap), new Fixed(), 640, 480);
        Assert.Equal(new TextRect(10, 10, 630, 470), layout.Clip);
        // 620 px fits 10 words (590 px with spaces); the 11th would make it 650.
        var rows = layout.Chars.GroupBy(c => c.Y).ToList();
        Assert.Equal(10, rows.Count);
        Assert.Equal(50, rows[0].Count(c => c.Code != ' '));
        float left = layout.Chars.Min(c => c.X), right = layout.Chars.Max(c => c.X) + 10, bottom = layout.Chars.Max(c => c.Y) + 20;
        Assert.True(left >= 10 && right <= 630, $"{left}–{right}");
        // Pushed up to 470 − (10 lines × 21 + half a line).
        Assert.Equal(470 - (210 + 10), layout.Chars[0].Y);
        Assert.True(bottom <= 470);
    }

    [Fact]
    public void Glyph_values_are_coloured_as_COMI_colours_cutscene_text()
    {
        // ^c031: font 0's ink (225 = −31) becomes colour 255.
        Assert.Equal(255, TextRasterizer.MapValue(225, 2, 31));
        // ^c032: font 4's ink (224 = −32) becomes 255 and its outline (225) 0.
        Assert.Equal(255, TextRasterizer.MapValue(224, 2, 32));
        Assert.Equal(0, TextRasterizer.MapValue(225, 2, 32));
        // ^c001: the credits. 225 becomes 0 (black), 224 stays (red in the opening's palette).
        Assert.Equal(0, TextRasterizer.MapValue(225, 2, 1));
        Assert.Equal(224, TextRasterizer.MapValue(224, 2, 1));
        Assert.Equal(-1, TextRasterizer.MapValue(2, 2, 31));
    }

    private static GlyphFont TestFont()
    {
        // 'A' is 3×2 with ink 224 and outline 225; space is blank.
        var a = new Glyph { Width = 3, Height = 2, Indices = [225, 224, 225, 2, 224, 2], Transparent = 2 };
        var space = new Glyph { Width = 2, Height = 2, Indices = [2, 2, 2, 2], Transparent = 2 };
        var glyphs = Enumerable.Repeat(Glyph.Empty, 128).ToArray();
        glyphs['A'] = a;
        glyphs[' '] = space;
        return new GlyphFont("test", glyphs);
    }

    [Fact]
    public void Drawn_text_takes_its_colours_from_the_palette_on_screen()
    {
        var font = TestFont();
        Assert.Equal((224, (byte?)225), (font.Ink, font.Outline));
        var palette = new byte[768];
        palette[255 * 3] = 0xF0; // colour 255: red F0
        palette[224 * 3 + 2] = 0x80; // colour 224: blue 80
        var layout = TextLayouter.Layout(Line("A A", 10, 5, SmushTextFlags.None) with { Color = 32 }, font, 640, 480);
        var image = TextRasterizer.Draw(layout, font, palette);
        Assert.Equal((10, 5, 8, 2), (image.Left, image.Top, image.Width, image.Height));
        uint ink = 0xFFF00000, black = 0xFF000000;
        Assert.Equal(new uint[] { black, ink, black, 0, 0, black, ink, black }, image.Pixels[..8]);
        Assert.Equal(new uint[] { 0, ink, 0, 0, 0, 0, ink, 0 }, image.Pixels[8..]);
    }

    [Fact]
    public void A_font_folder_round_trips_and_full_colour_glyphs_take_the_text_colour()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mmucs-font-" + Guid.NewGuid().ToString("N"));
        try
        {
            var font = TestFont();
            FontPack.Export(font, 4, dir, "test.nut");
            string fontDir = FontPack.FolderOf(dir, 4);
            Assert.True(File.Exists(Path.Combine(fontDir, "glyph", "0065.png")));
            Assert.Equal(4, FontPack.NumberOf(fontDir));
            Assert.Equal(fontDir, FontPack.Find(dir)[4]);

            var back = FontPack.Load(fontDir);
            Assert.Equal(font['A'].Indices, back['A'].Indices);
            Assert.Equal(2, back['A'].Transparent);
            Assert.Equal((font.Ink, font.Outline, font.LineHeight), (back.Ink, back.Outline, back.LineHeight));

            // Replace 'A' with a white full-colour pixel 2 wide (and a clear one): it is drawn in the ink's colour.
            using (var fs = File.Create(Path.Combine(fontDir, "glyph", "0065.png")))
                PngWriter.Write(fs, 2, 1, new byte[] { 255, 255, 255, 255, 0, 0, 0, 0 });
            var modded = FontPack.Load(fontDir, font);
            Assert.False(modded['A'].IsIndexed);
            Assert.Equal(2f, modded.CharWidth('A'));
            var palette = new byte[768];
            palette[255 * 3 + 1] = 0xC0;
            var image = TextRasterizer.Draw(TextLayouter.Layout(Line("A", 0, 0, SmushTextFlags.None) with { Color = 32 }, modded, 640, 480), modded, palette);
            Assert.Equal(new uint[] { 0xFF00C000, 0 }, image.Pixels);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void The_png_reader_reads_what_the_writer_writes()
    {
        var ms = new MemoryStream();
        PngWriter.WriteIndexed(ms, 3, 2, new byte[] { 0, 1, 2, 2, 1, 0 }, Enumerable.Range(0, 768).Select(i => (byte)i).ToArray(),
            alpha: new byte[] { 0, 255, 255 });
        var indexed = PngReader.Read(ms.ToArray());
        Assert.Equal(new byte[] { 0, 1, 2, 2, 1, 0 }, indexed.Indices);
        Assert.Equal(0u, indexed.Palette[0] >> 24);
        Assert.Equal(0xFF030405u, indexed.Palette[1]);

        ms = new MemoryStream();
        PngWriter.Write(ms, 1, 2, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Equal(new uint[] { 0x04010203, 0x08050607 }, PngReader.Read(ms.ToArray()).Argb);
    }

    [Fact]
    public void The_games_fonts_decode_with_the_values_COMI_draws_them_with()
    {
        if (Game == null) return;
        var fonts = NutFont.LoadAll(Game);
        Assert.Equal(5, fonts.Length);
        Assert.All(fonts, f => Assert.Equal(256, f.Count));
        Assert.All(fonts, f => Assert.Single(f.Glyphs.Select(g => g.Height).Distinct()));
        Assert.Equal((225, (byte?)null, 27f), (fonts[0].Ink, fonts[0].Outline, fonts[0].LineHeight));
        Assert.Equal((1, (byte?)0), (fonts[1].Ink, fonts[1].Outline));
        // font3 is laid out as codec 21 but decodes the same.
        Assert.Equal((1, (byte?)null), (fonts[3].Ink, fonts[3].Outline));
        Assert.Equal((224, (byte?)225, 36f), (fonts[4].Ink, fonts[4].Outline, fonts[4].LineHeight));
        // What replacement typefaces are sized and placed by.
        Assert.Equal((15f, 21f), (fonts[0].CapHeight, fonts[0].Baseline));
        Assert.Equal((19f, 27f), (fonts[4].CapHeight, fonts[4].Baseline));
    }

    [Fact]
    public void The_openings_text_chunks_make_its_lines_and_credits()
    {
        if (Game == null) return;
        var video = SmushVideo.Load(SmushVideo.FindAll(Game).First(p => Path.GetFileNameWithoutExtension(p).Equals("opening", StringComparison.OrdinalIgnoreCase)));
        var track = new SubtitleTrack();
        int chunks = 0;
        foreach (var frame in new SmushDecoder(video).Frames())
        {
            chunks += frame.TextChunks;
            track.Add(frame.Index, frame.Texts);
        }
        Assert.Equal(11250, chunks);
        Assert.Equal(158, track.Cues.Select(c => c.Text).Distinct().Count());
        var first = track.Cues[0];
        Assert.Equal(("ds_004", 247, 287, 4, 32), (first.Text.Code, first.From, first.To, first.Text.Font, first.Text.Color));
        Assert.Contains(track.Cues, c => c.Text.Code == "OPGT485" && c.Text.IsSubtitle && c.From == 600 && c.To == 805);
    }
}
