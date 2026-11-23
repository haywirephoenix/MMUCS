using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using MMUCS.Core.Fonts;
using MMUCS.Core.Smush;
using SkiaSharp;

namespace MMUCS.Player.Text;

/// <summary>
/// One frame's cutscene text, ready to draw over the picture in video pixels (the canvas's own matrix
/// scales it with the picture). Bitmap-font text is a few small images (drawn pixel-sharp); a
/// replacement typeface is text blobs, drawn as vectors at whatever size the picture is shown.
/// Owned by whatever shows it (the media player's video frame, a game frame).
/// </summary>
public sealed class SubtitleOverlay : IDisposable
{
    internal readonly List<(SKImage Image, int X, int Y)> Images = [];
    internal readonly List<(SKTextBlob Blob, SKColor Fill, SKColor? Outline, float Stroke)> Runs = [];

    public bool IsEmpty => Images.Count == 0 && Runs.Count == 0;

    public void Draw(SKCanvas canvas, bool smooth)
    {
        var sampling = new SKSamplingOptions(smooth ? SKFilterMode.Linear : SKFilterMode.Nearest, SKMipmapMode.None);
        foreach (var (image, x, y) in Images)
            canvas.DrawImage(image, x, y, sampling);
        if (Runs.Count == 0) return;
        using var paint = new SKPaint { IsAntialias = true };
        foreach (var (blob, fill, outline, stroke) in Runs)
        {
            if (outline is { } o)
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = stroke;
                paint.StrokeJoin = SKStrokeJoin.Round;
                paint.Color = o;
                canvas.DrawText(blob, 0, 0, paint);
            }
            paint.Style = SKPaintStyle.Fill;
            paint.Color = fill;
            canvas.DrawText(blob, 0, 0, paint);
        }
    }

    public void Dispose()
    {
        foreach (var (image, _, _) in Images) image.Dispose();
        foreach (var r in Runs) r.Blob.Dispose();
        Images.Clear();
        Runs.Clear();
    }
}

/// <summary>Where the cutscene text's letters come from.</summary>
public enum SubtitleFontKind
{
    /// <summary>The game's NUT fonts.</summary>
    Game,
    /// <summary>An unpacked font folder (<see cref="FontPack"/>): edited or replaced glyph PNGs.</summary>
    Folder,
    /// <summary>A TrueType/OpenType file, drawn as vectors in the colours the game font would have had.</summary>
    Typeface,
}

/// <summary>
/// Lays out and draws a cutscene frame's lines (<see cref="SmushFrame.Texts"/>) with the chosen fonts,
/// in the colours of the palette on screen. Immutable once made, so frames can be drawn on worker threads;
/// changing the font makes a new renderer. Its Skia objects are left to the finalizer: a frame being
/// drawn in the background may still be using the old renderer when it's replaced.
/// </summary>
public sealed class SubtitleRenderer
{
    private readonly GlyphFont[] _gameFonts;
    private readonly GlyphFont[] _fonts;
    private readonly SKTypeface _typeface;
    private readonly Dictionary<int, TypefaceFont> _typefaceFonts = [];

    private SubtitleRenderer(GlyphFont[] gameFonts, GlyphFont[] fonts, SKTypeface typeface, bool subtitles, string description, float scale)
    {
        _gameFonts = gameFonts;
        _fonts = fonts;
        _typeface = typeface;
        ShowSubtitles = subtitles;
        Description = description;
        Scale = scale > 0 ? scale : 1;
    }

    /// <summary>Draw lines flagged as subtitles (credits and captions always show).</summary>
    public bool ShowSubtitles { get; }
    /// <summary>What the fonts are, for the player's readout.</summary>
    public string Description { get; }
    /// <summary>A multiplier on a replacement typeface's size (1: capitals as tall as the game font's).</summary>
    public float Scale { get; }

    /// <param name="gameFonts">The game's fonts by number (<see cref="NutFont.LoadAll"/>); replacements measure against them.</param>
    /// <param name="path">The font folder or typeface file, for those kinds.</param>
    /// <param name="scale">A multiplier on a typeface's matched size (for a future subtitle scale setting).</param>
    public static SubtitleRenderer Create(GlyphFont[] gameFonts, SubtitleFontKind kind, string path, bool subtitles, float scale = 1)
    {
        switch (kind)
        {
            case SubtitleFontKind.Folder:
            {
                var fonts = (GlyphFont[])gameFonts.Clone();
                var found = FontPack.Find(path);
                foreach (var (n, dir) in found)
                {
                    if (n >= fonts.Length) Array.Resize(ref fonts, n + 1);
                    fonts[n] = FontPack.Load(dir, n < gameFonts.Length ? gameFonts[n] : null);
                }
                string what = found.Count == 0 ? "no font folders found" : $"font {string.Join(", ", found.Keys)}";
                return new SubtitleRenderer(gameFonts, fonts, null, subtitles, $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(path))} ({what})", scale);
            }
            case SubtitleFontKind.Typeface:
            {
                var typeface = SKTypeface.FromFile(path) ?? throw new InvalidDataException($"{Path.GetFileName(path)} isn't a font Skia can read");
                return new SubtitleRenderer(gameFonts, gameFonts, typeface, subtitles, typeface.FamilyName, scale);
            }
            default:
                return new SubtitleRenderer(gameFonts, gameFonts, null, subtitles, "game fonts", scale);
        }
    }

    /// <summary>The frame's lines as an overlay, or null when there's nothing to draw.</summary>
    public SubtitleOverlay Build(SmushFrame frame, int width, int height)
    {
        if (frame == null || frame.Texts.Count == 0) return null;
        var overlay = new SubtitleOverlay();
        Span<uint> lut = stackalloc uint[256];
        var pal = frame.Palette;
        for (int i = 0; i < 256; i++)
            lut[i] = 0xFF000000u | ((uint)pal[i * 3] << 16) | ((uint)pal[i * 3 + 1] << 8) | pal[i * 3 + 2];

        foreach (var text in frame.Texts)
        {
            if (text.IsSubtitle && !ShowSubtitles) continue;
            AddText(overlay, text, frame.Palette, lut, width, height, TextColorMode.Smush);
        }
        if (overlay.IsEmpty) { overlay.Dispose(); return null; }
        return overlay;
    }

    /// <summary>
    /// One line of text the scripts draw in a room (blast-text), coloured as room text is
    /// (<see cref="TextColorMode.Room"/>) against <paramref name="palette"/> (256 RGB triplets); null when nothing shows.
    /// </summary>
    public SubtitleOverlay BuildText(SmushText text, byte[] palette, int width, int height)
    {
        if (text == null || string.IsNullOrEmpty(text.Text) || palette == null) return null;
        var overlay = new SubtitleOverlay();
        Span<uint> lut = stackalloc uint[256];
        for (int i = 0; i < 256; i++)
            lut[i] = i * 3 + 2 < palette.Length ? 0xFF000000u | ((uint)palette[i * 3] << 16) | ((uint)palette[i * 3 + 1] << 8) | palette[i * 3 + 2] : 0xFF000000u;
        AddText(overlay, text, palette, lut, width, height, TextColorMode.Room);
        if (overlay.IsEmpty) { overlay.Dispose(); return null; }
        return overlay;
    }

    private void AddText(SubtitleOverlay overlay, SmushText text, byte[] palette, ReadOnlySpan<uint> lut, int width, int height, TextColorMode mode)
    {
        var font = Font(_fonts, text.Font);
        if (font == null) return;
        if (_typeface != null) AddTypefaceText(overlay, text, font, width, height, lut, mode);
        else AddBitmapText(overlay, text, font, palette, width, height, mode);
    }

    private static GlyphFont Font(GlyphFont[] fonts, int n)
    {
        return n >= 0 && n < fonts.Length && fonts[n] != null ? fonts[n] : Array.Find(fonts, f => f != null);
    }

    private static void AddBitmapText(SubtitleOverlay overlay, SmushText text, GlyphFont font, byte[] palette, int width, int height, TextColorMode mode)
    {
        var layout = TextLayouter.Layout(text, font, width, height);
        var image = TextRasterizer.Draw(layout, font, palette, mode);
        if (image == null) return;
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var handle = GCHandle.Alloc(image.Pixels, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), image.Width * 4);
            overlay.Images.Add((SKImage.FromPixelCopy(pixmap), image.Left, image.Top));
        }
        finally { handle.Free(); }
    }

    private void AddTypefaceText(SubtitleOverlay overlay, SmushText text, GlyphFont gameFont, int width, int height, ReadOnlySpan<uint> lut, TextColorMode mode)
    {
        TypefaceFont tf;
        lock (_typefaceFonts)
        {
            if (!_typefaceFonts.TryGetValue(text.Font, out tf))
                _typefaceFonts[text.Font] = tf = new TypefaceFont(_typeface, gameFont, Scale);
        }
        var layout = TextLayouter.Layout(text, tf, width, height);
        if (layout.Chars.Count == 0) return;

        // One blob per colour run (a ^c code mid-line starts another).
        int start = 0;
        while (start < layout.Chars.Count)
        {
            int color = layout.Chars[start].Color, end = start;
            while (end < layout.Chars.Count && layout.Chars[end].Color == color) end++;
            var chars = new char[end - start];
            var points = new SKPoint[end - start];
            for (int i = start; i < end; i++)
            {
                var c = layout.Chars[i];
                chars[i - start] = c.Code;
                points[i - start] = new SKPoint(c.X, c.Y + tf.Baseline);
            }
            var blob = tf.Blob(chars, points);
            if (blob != null)
            {
                uint fill = TextRasterizer.InkColor(gameFont, color, lut, mode);
                uint? outline = TextRasterizer.OutlineColor(gameFont, color, lut, mode);
                overlay.Runs.Add((blob, new SKColor(fill), outline is { } o ? new SKColor(o) : null, tf.Stroke));
            }
            start = end;
        }
    }

    /// <summary>
    /// A typeface sized to stand in for one game font. Its capitals are made as tall as the game font's
    /// (typefaces differ a lot in how much of their line spacing the letters fill, so matching the line
    /// height made some look far bigger), and its baseline sits on the game font's, so the game's
    /// positions (made for that font) still work. <paramref name="scale"/> multiplies the size and the
    /// line height.
    /// </summary>
    private sealed class TypefaceFont : IGlyphMetrics
    {
        // Measured at a large size, where hinting and rounding don't skew the proportions.
        private const float MeasureSize = 200;
        private readonly SKFont _font;
        private readonly float[] _widths = new float[256];

        public TypefaceFont(SKTypeface typeface, GlyphFont game, float scale)
        {
            _font = new SKFont(typeface, MeasureSize) { Subpixel = true, Edging = SKFontEdging.Antialias };
            float cap = CapHeight(_font);
            if (cap <= 0)
            {
                _font.GetFontMetrics(out var m);
                cap = m.CapHeight > 0 ? m.CapHeight : (m.Descent - m.Ascent) * 0.66f;
            }
            _font.Size = MeasureSize * game.CapHeight / cap * scale;
            LineHeight = game.LineHeight * scale;
            Baseline = game.Baseline * scale;
            // The game's outlines are one pixel outside the letters; a stroke is centred on the edge.
            Stroke = 2 * scale;
            for (int c = 0; c < 256; c++)
                _widths[c] = c < 32 ? 0 : _font.MeasureText(((char)c).ToString());
        }

        /// <summary>The height of the capitals' ink above the baseline (H, I and E, as the game font is measured).</summary>
        private static float CapHeight(SKFont font)
        {
            float top = 0;
            foreach (char c in "HIE")
            {
                font.MeasureText(c.ToString(), out var bounds);
                top = Math.Min(top, bounds.Top);
            }
            return -top;
        }

        public float LineHeight { get; }
        public float Baseline { get; }
        public float Stroke { get; }

        public float CharWidth(char c) => c < 256 ? _widths[c] : _font.MeasureText(c.ToString());
        public float CharHeight(char c) => LineHeight;

        public SKTextBlob Blob(char[] chars, SKPoint[] points)
        {
            lock (_font)
            {
                return SKTextBlob.CreatePositioned(new string(chars), _font, points);
            }
        }

    }
}
