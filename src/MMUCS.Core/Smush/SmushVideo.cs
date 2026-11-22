using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MMUCS.Core.Smush;

/// <summary>
/// The AHDR of a SMUSH file. Version 2 (COMI) adds the frame rate, the largest frame's size and the
/// audio sample rate after the palette.
/// </summary>
public sealed record SmushHeader(int Version, int FrameCount, byte[] Palette, int FrameRate, int MaxFrameSize, int SampleRate)
{
    public const int PaletteSize = 0x300;
    /// <summary>COMI's videos all say 12; earlier games had no field and played at 15.</summary>
    public const int DefaultFrameRate = 15;

    public static SmushHeader Parse(ReadOnlySpan<byte> ahdr)
    {
        if (ahdr.Length < 6 + PaletteSize) throw new InvalidDataException($"AHDR is {ahdr.Length} bytes, too short");
        int version = ahdr[0] | (ahdr[1] << 8);
        int frames = ahdr[2] | (ahdr[3] << 8);
        byte[] palette = ahdr.Slice(6, PaletteSize).ToArray();
        int rate = 0, maxFrame = 0, sampleRate = 0;
        var v2 = ahdr[(6 + PaletteSize)..];
        if (version >= 2 && v2.Length >= 12)
        {
            rate = (int)U32(v2, 0);
            maxFrame = (int)U32(v2, 4);
            sampleRate = (int)U32(v2, 8);
        }
        return new SmushHeader(version, frames, palette, rate > 0 ? rate : DefaultFrameRate, maxFrame, sampleRate);
    }

    private static uint U32(ReadOnlySpan<byte> s, int o)
    {
        return (uint)(s[o] | (s[o + 1] << 8) | (s[o + 2] << 16) | (s[o + 3] << 24));
    }
}

/// <summary>
/// A SMUSH animation (.san cutscene, also the container of a .nut font) loaded into memory: its
/// header and where each frame is. Frames are decoded by <see cref="SmushDecoder"/>.
/// </summary>
public sealed class SmushVideo
{
    public string Path { get; }
    /// <summary>The file name without extension, lowercased: the video's asset name (<c>video/opening</c>).</summary>
    public string Name { get; }
    public byte[] Data { get; }
    public SmushHeader Header { get; }
    public IReadOnlyList<SmushChunk> Frames { get; }
    /// <summary>The screen size: the largest extent any frame object reaches (640×480 in COMI).</summary>
    public int Width { get; }
    public int Height { get; }

    private SmushVideo(string path, byte[] data)
    {
        Path = path;
        Name = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        Data = data;

        if (!SmushChunk.TryRead(data, 0, data.Length, out var anim) || anim.Tag != SmushTag.ANIM)
            throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is not a SMUSH file (no ANIM)");

        var frames = new List<SmushChunk>();
        SmushHeader header = null;
        foreach (var chunk in anim.Children(data))
        {
            if (chunk.Tag == SmushTag.AHDR) header = SmushHeader.Parse(chunk.Data(data));
            else if (chunk.Tag == SmushTag.FRME) frames.Add(chunk);
        }
        Header = header ?? throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} has no AHDR");
        Frames = frames;

        int w = 0, h = 0;
        foreach (var frame in frames)
            foreach (var obj in frame.Children(data))
            {
                if (obj.Tag != SmushTag.FOBJ || obj.Length < FrameObject.HeaderSize) continue;
                var fo = FrameObject.ReadHeader(obj.Data(data));
                w = Math.Max(w, fo.Left + fo.Width);
                h = Math.Max(h, fo.Top + fo.Height);
            }
        Width = w > 0 ? w : 640;
        Height = h > 0 ? h : 480;
    }

    public static SmushVideo Load(string path)
    {
        return new(path, File.ReadAllBytes(path));
    }

    /// <summary>Reads only the header, for counting frames without loading a whole file.</summary>
    public static SmushHeader ReadHeader(string path)
    {
        using var f = File.OpenRead(path);
        var buf = new byte[SmushChunk.HeaderSize * 2 + 6 + SmushHeader.PaletteSize + 20];
        int n = f.Read(buf, 0, buf.Length);
        if (!SmushChunk.TryRead(buf, 0, n, out var anim) || anim.Tag != SmushTag.ANIM)
            throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is not a SMUSH file (no ANIM)");
        if (!SmushChunk.TryRead(buf, anim.DataOffset, n, out var ahdr) || ahdr.Tag != SmushTag.AHDR)
            throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} does not start with an AHDR");
        return SmushHeader.Parse(ahdr.Data(buf));
    }

    /// <summary>
    /// The game's cutscenes: every .san beside the resource file or in its resource folder (any case),
    /// sorted by name.
    /// </summary>
    public static IReadOnlyList<string> FindAll(string gameFile)
    {
        return GameFiles.Find(gameFile, ".san");
    }
}

/// <summary>The 14-byte header of a frame object (FOBJ): codec, position and size.</summary>
public readonly record struct FrameObject(int Codec, int Left, int Top, int Width, int Height)
{
    public const int HeaderSize = 14;

    public static FrameObject ReadHeader(ReadOnlySpan<byte> d)
    {
        return new(
        d[0] | (d[1] << 8),
        (short)(d[2] | (d[3] << 8)),
        (short)(d[4] | (d[5] << 8)),
        d[6] | (d[7] << 8),
        d[8] | (d[9] << 8));
    }
}
