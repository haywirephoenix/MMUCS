using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using MMUCS.Core.Diagnostics;

namespace MMUCS.Core.Smush;

/// <summary>What one frame of a video did, as <see cref="SmushDecoder"/> plays it.</summary>
public sealed class SmushFrame
{
    public int Index { get; internal set; }
    /// <summary>The screen after this frame: width × height palette indices. Reused for the next frame.</summary>
    public byte[] Pixels { get; internal set; }
    /// <summary>The palette after this frame. Reused for the next frame.</summary>
    public byte[] Palette { get; internal set; }
    /// <summary>A frame object drew, or a stored screen was fetched.</summary>
    public bool ImageChanged { get; internal set; }
    public bool PaletteChanged { get; internal set; }
    /// <summary>The screen is the one stored earlier (FTCH): a held picture.</summary>
    public bool Fetched { get; internal set; }
    /// <summary>Codec 47's compression mode for this frame, or -1.</summary>
    public int Compression { get; internal set; } = -1;
    public int AudioChunks { get; internal set; }
    public int TextChunks { get; internal set; }
}

/// <summary>
/// Plays a <see cref="SmushVideo"/> frame by frame into one indexed screen. Frames must be decoded in
/// order: codec 47 builds each frame from the ones before it.
/// <list type="bullet">
/// <item>NPAL / XPAL change the palette (XPAL fades it).</item>
/// <item>FOBJ (and zlib-packed ZFOB) draw a frame object at its position: codec 47, or codec 1/3 RLE.</item>
/// <item>STOR keeps the screen as it is at the end of the frame; FTCH shows the kept screen again.</item>
/// <item>IACT (audio) and TEXT (subtitles) are counted, not decoded yet.</item>
/// </list>
/// </summary>
public sealed class SmushDecoder
{
    private readonly SmushVideo _video;
    private readonly SmushPalette _palette;
    private readonly byte[] _screen;
    private readonly byte[] _objectBuffer;
    private byte[] _stored;
    private Codec47 _codec47;
    private readonly HashSet<string> _warned = new();

    public SmushDecoder(SmushVideo video)
    {
        _video = video;
        _palette = new SmushPalette(video.Header.Palette);
        _screen = new byte[video.Width * video.Height];
        _objectBuffer = new byte[video.Width * video.Height];
    }

    /// <summary>Frames that failed or used something unsupported; each kind is logged once.</summary>
    public int Problems { get; private set; }
    /// <summary>The most bytes any block-coded frame left unread (see <see cref="Codec47.UnreadBytes"/>).</summary>
    public int MaxUnreadBytes { get; private set; }
    /// <summary>Block-coded frames that left more than one byte unread.</summary>
    public int FramesWithUnreadBytes { get; private set; }
    public int OutsideCopies => _codec47?.OutsideCopies ?? 0;

    public IEnumerable<SmushFrame> Frames(CancellationToken token = default)
    {
        var frame = new SmushFrame { Pixels = _screen, Palette = _palette.Rgb };
        var data = _video.Data;
        for (int i = 0; i < _video.Frames.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            frame.Index = i;
            frame.ImageChanged = frame.PaletteChanged = frame.Fetched = false;
            frame.Compression = -1;
            frame.AudioChunks = frame.TextChunks = 0;
            bool store = false;

            foreach (var chunk in _video.Frames[i].Children(data))
            {
                try
                {
                    if (chunk.Tag == SmushTag.FOBJ) DrawObject(chunk.Data(data), frame);
                    else if (chunk.Tag == SmushTag.ZFOB) DrawObject(Inflate(chunk.Data(data)), frame);
                    else if (chunk.Tag == SmushTag.NPAL) { _palette.Set(chunk.Data(data)); frame.PaletteChanged = true; }
                    else if (chunk.Tag == SmushTag.XPAL)
                    {
                        if (_palette.ApplyDelta(chunk.Data(data))) frame.PaletteChanged = true;
                        else Problem(i, "an XPAL step before any XPAL deltas");
                    }
                    else if (chunk.Tag == SmushTag.STOR) store = true;
                    else if (chunk.Tag == SmushTag.FTCH)
                    {
                        if (_stored == null) { Problem(i, "FTCH with nothing stored"); continue; }
                        Buffer.BlockCopy(_stored, 0, _screen, 0, _screen.Length);
                        frame.ImageChanged = frame.Fetched = true;
                    }
                    else if (chunk.Tag == SmushTag.IACT) frame.AudioChunks++;
                    else if (chunk.Tag == SmushTag.TEXT) frame.TextChunks++;
                    else Problem(i, $"chunk {chunk.TagName}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Problem(i, $"{chunk.TagName}: {ex.Message}");
                }
            }

            if (store)
            {
                _stored ??= new byte[_screen.Length];
                Buffer.BlockCopy(_screen, 0, _stored, 0, _screen.Length);
            }
            yield return frame;
        }
    }

    private void DrawObject(ReadOnlySpan<byte> fobj, SmushFrame frame)
    {
        var header = FrameObject.ReadHeader(fobj);
        var payload = fobj[FrameObject.HeaderSize..];
        int sw = _video.Width, sh = _video.Height;

        switch (header.Codec)
        {
            case 47:
            {
                if (_codec47 == null || _codec47.Width != header.Width || _codec47.Height != header.Height)
                    _codec47 = new Codec47(header.Width, header.Height);
                var pixels = _objectBuffer.AsSpan(0, header.Width * header.Height);
                frame.Compression = _codec47.Decode(payload, pixels);
                if (frame.Compression == 2)
                {
                    MaxUnreadBytes = Math.Max(MaxUnreadBytes, _codec47.UnreadBytes);
                    if (_codec47.UnreadBytes > 1) FramesWithUnreadBytes++;
                }
                Blit(pixels, header);
                break;
            }
            case 1:
            case 3:
                SmushRle.DrawCodec1(payload, _screen, sw, sh, header.Left, header.Top, header.Width, header.Height);
                break;
            default:
                Problem(frame.Index, $"codec {header.Codec}");
                return;
        }
        frame.ImageChanged = true;
    }

    /// <summary>Copies a decoded object onto the screen at its position, clipped to the screen.</summary>
    private void Blit(ReadOnlySpan<byte> pixels, FrameObject at)
    {
        int sw = _video.Width, sh = _video.Height;
        int x0 = Math.Max(0, at.Left), x1 = Math.Min(sw, at.Left + at.Width);
        if (x1 <= x0) return;
        for (int row = 0; row < at.Height; row++)
        {
            int y = at.Top + row;
            if (y < 0 || y >= sh) continue;
            pixels.Slice(row * at.Width + (x0 - at.Left), x1 - x0).CopyTo(_screen.AsSpan(y * sw + x0));
        }
    }

    private static byte[] Inflate(ReadOnlySpan<byte> zfob)
    {
        int size = (int)BinaryPrimitives.ReadUInt32BigEndian(zfob);
        var result = new byte[size];
        using var z = new ZLibStream(new MemoryStream(zfob[4..].ToArray()), CompressionMode.Decompress);
        z.ReadExactly(result);
        return result;
    }

    private void Problem(int frame, string what)
    {
        Problems++;
        if (_warned.Add(what)) Log.Warn($"{_video.Name}.san frame {frame}: {what}");
    }
}
