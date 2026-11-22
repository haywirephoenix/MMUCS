using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MMUCS.Core.Audio;

/// <summary>
/// A Digital iMUSE sound (iMUS): a MAP of events, then DATA holding the PCM. Every event's position is
/// a byte offset into the whole iMUS file (so the first region starts where DATA's samples do).
/// <list type="bullet">
/// <item>FRMT: sample size, rate and channels.</item>
/// <item>REGN: a stretch of audio. Playback runs region to region.</item>
/// <item>JUMP: at a position, go to another one when the hook matches (music loops and transitions), with a crossfade.</item>
/// <item>TEXT: a marker that fires a trigger in the scripts; the first one names the track.</item>
/// <item>SYNC: lip-sync data for speech.</item>
/// <item>STOP: where playback ends.</item>
/// </list>
/// </summary>
public sealed class ImusSound
{
    private const uint IMUS = 0x694D5553, MAP = 0x4D415020, DATA = 0x44415441;
    private const uint FRMT = 0x46524D54, REGN = 0x5245474E, JUMP = 0x4A554D50, TEXT = 0x54455854,
        SYNC = 0x53594E43, STOP = 0x53544F50;

    public readonly record struct Region(long Offset, long Length);
    public readonly record struct Jump(long Offset, long Destination, int Hook, int FadeMs);
    public readonly record struct Marker(long Offset, string Text);
    public readonly record struct Sync(long Offset, uint[] Data);

    public enum EventKind { Region, Jump, Marker, Sync, Stop }
    /// <summary>A map event in map order: its kind, position, and index into that kind's list (<see cref="Regions"/>, …).</summary>
    public readonly record struct Event(EventKind Kind, long Offset, int Index);

    public int Bits { get; private set; } = 16;
    public int SampleRate { get; private set; } = 22050;
    public int Channels { get; private set; } = 1;
    /// <summary>Where the samples start and how many bytes of them there are.</summary>
    public int DataOffset { get; private set; }
    public int DataLength { get; private set; }
    public long? Stop { get; private set; }
    public List<Region> Regions { get; } = [];
    public List<Jump> Jumps { get; } = [];
    public List<Marker> Markers { get; } = [];
    public List<Sync> Syncs { get; } = [];
    /// <summary>MAP events this parser doesn't know, by tag.</summary>
    public List<string> UnknownEvents { get; } = [];
    /// <summary>Every region, jump, marker, sync and stop in map order: the order playback handles events that share a position.</summary>
    public List<Event> Events { get; } = [];

    public byte[] Bytes { get; }
    public ReadOnlySpan<byte> Samples => Bytes.AsSpan(DataOffset, DataLength);
    public int BytesPerFrame => Math.Max(1, Channels * Bits / 8);
    public long FrameCount => DataLength / BytesPerFrame;

    /// <summary>A position in the file as a sample frame from the start of the data (what a WAV uses).</summary>
    public long ToFrame(long offset)
    {
        return (offset - DataOffset) / BytesPerFrame;
    }

    /// <summary>The track's own name, the first TEXT that isn't a row of asterisks.</summary>
    public string Title
    {
        get
        {
            foreach (var m in Markers)
                if (m.Offset == DataOffset && m.Text.Length > 0 && m.Text.Trim('*').Length > 0) return m.Text;
            return null;
        }
    }

    private ImusSound(byte[] bytes) => Bytes = bytes;

    public static ImusSound Parse(byte[] bytes)
    {
        var s = new ImusSound(bytes);
        var span = bytes.AsSpan();
        if (span.Length < 16 || BinaryPrimitives.ReadUInt32BigEndian(span) != IMUS)
            throw new InvalidDataException("not an iMUS sound");

        int pos = 8;
        while (pos + 8 <= span.Length)
        {
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(span[pos..]);
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(pos + 4)..]);
            int body = pos + 8;
            if (tag == MAP) s.ParseMap(span.Slice(body, Math.Min(size, span.Length - body)));
            else if (tag == DATA)
            {
                s.DataOffset = body;
                s.DataLength = Math.Min(size, span.Length - body);
                break;
            }
            pos = body + size;
        }
        if (s.DataOffset == 0) throw new InvalidDataException("iMUS sound has no DATA");
        return s;
    }

    private void ParseMap(ReadOnlySpan<byte> map)
    {
        int pos = 0;
        while (pos + 8 <= map.Length)
        {
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(map[pos..]);
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(map[(pos + 4)..]);
            if (size < 0 || pos + 8 + size > map.Length) break;
            var b = map.Slice(pos + 8, size);

            if (tag == FRMT) { Bits = (int)Field(b, 8); SampleRate = (int)Field(b, 12); Channels = (int)Field(b, 16); }
            else if (tag == REGN) { Events.Add(new Event(EventKind.Region, Field(b, 0), Regions.Count)); Regions.Add(new Region(Field(b, 0), Field(b, 4))); }
            else if (tag == JUMP) { Events.Add(new Event(EventKind.Jump, Field(b, 0), Jumps.Count)); Jumps.Add(new Jump(Field(b, 0), Field(b, 4), (int)Field(b, 8), (int)Field(b, 12))); }
            else if (tag == STOP) { Stop = Field(b, 0); Events.Add(new Event(EventKind.Stop, Field(b, 0), 0)); }
            else if (tag == TEXT)
            {
                var chars = b.Length > 4 ? b[4..] : [];
                int end = chars.IndexOf((byte)0);
                string text = Encoding.Latin1.GetString(end < 0 ? chars : chars[..end]);
                // The last marker of some tracks is padding: no text, at an offset past the file.
                if (text.Length > 0 || Field(b, 0) <= Bytes.Length)
                {
                    Events.Add(new Event(EventKind.Marker, Field(b, 0), Markers.Count));
                    Markers.Add(new Marker(Field(b, 0), text));
                }
            }
            else if (tag == SYNC)
            {
                var data = new uint[Math.Max(0, (b.Length - 4) / 4)];
                for (int i = 0; i < data.Length; i++) data[i] = Field(b, 4 + i * 4);
                Events.Add(new Event(EventKind.Sync, Field(b, 0), Syncs.Count));
                Syncs.Add(new Sync(Field(b, 0), data));
            }
            else UnknownEvents.Add(Encoding.ASCII.GetString(map.Slice(pos, 4)));

            pos += 8 + size;
        }
    }

    private static uint Field(ReadOnlySpan<byte> b, int i)
    {
        return i + 4 <= b.Length ? BinaryPrimitives.ReadUInt32BigEndian(b[i..]) : 0;
    }
}
