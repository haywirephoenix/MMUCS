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
    public int FrameAt(TimeSpan time) => Math.Clamp((int)Math.Floor(time.TotalSeconds * FrameRate), 0, Math.Max(0, FrameCount - 1));

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
        }
        return true;
    }

    /// <summary>The current frame as straight RGBA, for display.</summary>
    public void CopyRgba(Span<byte> rgba)
    {
        var f = Current;
        if (f == null) { rgba.Clear(); return; }
        var px = f.Pixels;
        var pal = f.Palette;
        int n = Math.Min(px.Length, rgba.Length / 4);
        for (int i = 0; i < n; i++)
        {
            int c = px[i] * 3;
            rgba[i * 4] = pal[c];
            rgba[i * 4 + 1] = pal[c + 1];
            rgba[i * 4 + 2] = pal[c + 2];
            rgba[i * 4 + 3] = 255;
        }
    }

    private void Restart()
    {
        _frames?.Dispose();
        _decoder = new SmushDecoder(Video);
        _frames = _decoder.Frames().GetEnumerator();
        Current = null;
    }
}
