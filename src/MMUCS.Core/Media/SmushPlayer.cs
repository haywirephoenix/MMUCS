using System;
using System.Collections.Generic;
using System.Threading;
using MMUCS.Core.Smush;

namespace MMUCS.Core.Media;

/// <summary>
/// Shows a SMUSH video frame by frame against a clock. Codec 47 builds each frame from the ones before, so
/// frames are decoded in order: showing a later frame decodes forward to it, an earlier one starts again
/// from the first frame (all of COMI's opening decodes in about two seconds). Not thread-safe: use it
/// from one thread at a time.
/// </summary>
public sealed class SmushPlayer
{
    private SmushDecoder _decoder;
    private IEnumerator<SmushFrame> _frames;
    // The palette as 32-bit BGRA, rebuilt only when a frame changed it.
    private readonly uint[] _bgra = new uint[256];
    private bool _paletteDirty = true;

    public SmushPlayer(SmushVideo video)
    {
        Video = video;
        Restart();
    }

    public SmushVideo Video { get; }
    public int FrameCount => Video.Frames.Count;
    public double FrameRate => Video.Header.FrameRate;
    public TimeSpan Duration => TimeSpan.FromSeconds(FrameCount / FrameRate);

    /// <summary>The frame on screen, or null before the first.</summary>
    public SmushFrame Current { get; private set; }
    public int CurrentIndex => Current?.Index ?? -1;

    /// <summary>The frame to show at a time.</summary>
    public int FrameAt(TimeSpan time)
    {
        return Math.Clamp((int)Math.Floor(time.TotalSeconds * FrameRate), 0, Math.Max(0, FrameCount - 1));
    }

    /// <summary>Decodes up to <paramref name="index"/> (from the start when it lies behind). True when the picture changed.</summary>
    public bool ShowFrame(int index, CancellationToken token = default)
    {
        index = Math.Clamp(index, 0, Math.Max(0, FrameCount - 1));
        if (index == CurrentIndex) return false;
        if (index < CurrentIndex) Restart();
        while (CurrentIndex < index)
        {
            token.ThrowIfCancellationRequested();
            if (!_frames.MoveNext()) break;
            Current = _frames.Current;
            if (Current.ImageChanged || Current.PaletteChanged) PictureChanged = true;
            if (Current.PaletteChanged) _paletteDirty = true;
        }
        return true;
    }

    /// <summary>
    /// True when the picture may differ from the last <see cref="CopyBgra"/>: a frame drew or changed the
    /// palette since. Frames that do neither show the same picture again.
    /// </summary>
    public bool PictureChanged { get; private set; } = true;

    /// <summary>
    /// The current frame as opaque 32-bit pixels, blue in the low byte (BGRA in memory on little-endian
    /// machines: Skia's native order, so drawing needs no conversion).
    /// </summary>
    public void CopyBgra(Span<uint> pixels)
    {
        PictureChanged = false;
        var f = Current;
        if (f == null) { pixels.Clear(); return; }
        var lut = _bgra;
        if (_paletteDirty)
        {
            var pal = f.Palette;
            for (int c = 0; c < 256; c++)
                lut[c] = 0xFF000000u | ((uint)pal[c * 3] << 16) | ((uint)pal[c * 3 + 1] << 8) | pal[c * 3 + 2];
            _paletteDirty = false;
        }
        var px = f.Pixels;
        int n = Math.Min(px.Length, pixels.Length);
        for (int i = 0; i < n; i++) pixels[i] = lut[px[i]];
    }

    private void Restart()
    {
        _frames?.Dispose();
        _decoder = new SmushDecoder(Video);
        _frames = _decoder.Frames().GetEnumerator();
        Current = null;
        PictureChanged = _paletteDirty = true;
    }
}
