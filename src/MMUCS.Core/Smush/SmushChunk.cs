using System;
using System.Collections.Generic;
using MMUCS.Core.Scumm;

namespace MMUCS.Core.Smush;

/// <summary>SMUSH chunk tags (big-endian FourCCs, as in the resource files).</summary>
public static class SmushTag
{
    public static readonly uint ANIM = ScummTag.FromString("ANIM");
    public static readonly uint AHDR = ScummTag.FromString("AHDR");
    public static readonly uint FRME = ScummTag.FromString("FRME");
    public static readonly uint FOBJ = ScummTag.FromString("FOBJ");
    /// <summary>A zlib-compressed FOBJ (not in COMI's files, but cheap to support).</summary>
    public static readonly uint ZFOB = ScummTag.FromString("ZFOB");
    public static readonly uint NPAL = ScummTag.FromString("NPAL");
    public static readonly uint XPAL = ScummTag.FromString("XPAL");
    public static readonly uint STOR = ScummTag.FromString("STOR");
    public static readonly uint FTCH = ScummTag.FromString("FTCH");
    public static readonly uint IACT = ScummTag.FromString("IACT");
    public static readonly uint TEXT = ScummTag.FromString("TEXT");
}

/// <summary>
/// One chunk of a SMUSH file: a FourCC, a big-endian length, then the data, padded to an even size.
/// Holds positions only; the bytes stay in the file buffer.
/// </summary>
public readonly record struct SmushChunk(uint Tag, int Offset, int DataOffset, int Length)
{
    public const int HeaderSize = 8;

    public string TagName => ScummTag.ToString(Tag);
    public int End => DataOffset + Length;
    /// <summary>Where the next sibling starts: chunks are padded to an even length.</summary>
    public int Next => End + (Length & 1);

    public ReadOnlySpan<byte> Data(byte[] file)
    {
        return file.AsSpan(DataOffset, Length);
    }

    /// <summary>Reads the chunk header at <paramref name="offset"/>; false when there isn't a whole one.</summary>
    public static bool TryRead(byte[] file, int offset, int limit, out SmushChunk chunk)
    {
        chunk = default;
        if (offset < 0 || offset + HeaderSize > limit) return false;
        uint tag = ScummTag.Read(file, offset);
        long length = ScummTag.Read(file, offset + 4);
        // A truncated last chunk keeps what is there rather than failing the whole file.
        length = Math.Min(length, limit - (offset + HeaderSize));
        chunk = new SmushChunk(tag, offset, offset + HeaderSize, (int)length);
        return true;
    }

    /// <summary>The chunks directly inside this one.</summary>
    public IEnumerable<SmushChunk> Children(byte[] file)
    {
        return Sequence(file, DataOffset, End);
    }

    /// <summary>The chunks laid end to end between <paramref name="start"/> and <paramref name="end"/>.</summary>
    public static IEnumerable<SmushChunk> Sequence(byte[] file, int start, int end)
    {
        int pos = start;
        while (TryRead(file, pos, end, out var chunk))
        {
            yield return chunk;
            pos = chunk.Next;
        }
    }
}
