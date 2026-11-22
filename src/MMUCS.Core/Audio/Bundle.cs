using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MMUCS.Core.Audio;

/// <summary>One file inside a bundle: its 8.3 name and where its bytes are.</summary>
public readonly record struct BundleEntry(int Index, string Name, long Offset, int Size);

/// <summary>
/// A Digital iMUSE bundle (.bun, tag LB83): COMI's music (musdisk1/2) and speech (voxdisk1/2).
/// The header points at a directory of 20-byte entries (8-byte name, 4-byte extension, offset,
/// size, all big-endian). Each entry is a COMP-compressed iMUS sound (see <see cref="BundleCodec"/>),
/// except musdisk's PRELOAD, a table of the first bytes of each track for a quick start.
/// Entries are read on demand, from any thread.
/// </summary>
public sealed class Bundle : IDisposable
{
    private const uint LB83 = 0x4C423833;
    private const int EntrySize = 20;
    private readonly SafeFileHandle _file;

    public string Path { get; }
    public IReadOnlyList<BundleEntry> Entries { get; }

    private Bundle(string path)
    {
        Path = path;
        _file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        long length = RandomAccess.GetLength(_file);

        Span<byte> header = stackalloc byte[12];
        if (RandomAccess.Read(_file, header, 0) < 12 || BinaryPrimitives.ReadUInt32BigEndian(header) != LB83)
        {
            _file.Dispose();
            throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is not an LB83 bundle");
        }
        long dirOffset = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        if (dirOffset + (long)count * EntrySize > length)
        {
            _file.Dispose();
            throw new InvalidDataException($"{System.IO.Path.GetFileName(path)}: directory runs past the end of the file");
        }

        var dir = new byte[count * EntrySize];
        RandomAccess.Read(_file, dir, dirOffset);
        var entries = new List<BundleEntry>(count);
        for (int i = 0; i < count; i++)
        {
            var e = dir.AsSpan(i * EntrySize, EntrySize);
            string name = Ascii(e[..8]);
            string ext = Ascii(e.Slice(8, 4));
            long offset = BinaryPrimitives.ReadUInt32BigEndian(e[12..]);
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(e[16..]);
            if (offset + size > length) size = (int)Math.Max(0, length - offset);
            entries.Add(new BundleEntry(i, ext.Length > 0 ? $"{name}.{ext}" : name, offset, size));
        }
        Entries = entries;
    }

    public static Bundle Open(string path)
    {
        return new(path);
    }

    /// <summary>The entry's stored (compressed) bytes.</summary>
    public byte[] Read(BundleEntry entry)
    {
        var data = new byte[entry.Size];
        int read = 0;
        while (read < data.Length)
        {
            int n = RandomAccess.Read(_file, data.AsSpan(read), entry.Offset + read);
            if (n <= 0) break;
            read += n;
        }
        return data;
    }

    public void Dispose()
    {
        _file.Dispose();
    }

    private static string Ascii(ReadOnlySpan<byte> s)
    {
        int end = s.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? s : s[..end]);
    }
}
