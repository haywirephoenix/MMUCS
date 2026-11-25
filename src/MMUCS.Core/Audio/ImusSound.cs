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
/// <item>FRMT: sample size, rate and channels. 12-bit samples are widened to 16-bit as the sound is parsed.</item>
/// <item>REGN: a stretch of audio. Playback runs region to region.</item>
/// <item>JUMP: at a position, go to another one when the hook matches (music loops and transitions), with a crossfade.</item>
/// <item>TEXT: a marker that fires a trigger in the scripts; the first one names the track.</item>
/// <item>SYNC: lip-sync data for speech: mouth width and height over time (<see cref="LipSync"/>).</item>
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

    /// <summary>
    /// The mouth's width and height (0–127 each) at <paramref name="ms"/> into the sound, from the SYNC block
    /// <paramref name="sync"/> (ScummVM's tracksLipSync). Each 32-bit entry holds a time in 60 Hz ticks (16 ms)
    /// in its top half, then the width and the height a byte each; the entry is the last one at or before
    /// the time. (0, 0) without that block, or for a time past what a tick count holds.
    /// </summary>
    public (int Width, int Height) LipSync(int sync, int ms)
    {
        if (ms < 0 || sync < 0 || sync >= Syncs.Count || ((ms >> 4) & 0xFFFF0000) != 0) return (0, 0);
        var data = Syncs[sync].Data;
        if (data.Length == 0) return (0, 0);
        uint ticks = (uint)(ms >> 4);
        int i = 0;
        while (i < data.Length && data[i] >> 16 < ticks) i++;
        // Past the end, or on an entry later than the time: the one before it.
        if (i == data.Length || data[i] >> 16 > ticks) i = Math.Max(0, i - 1);
        return ((int)(data[i] >> 8) & 0x7F, (int)data[i] & 0x7F);
    }

    private ImusSound(byte[] bytes) => Bytes = bytes;

    /// <summary>
    /// A 16-bit iMUS sound made from PCM (a music mod's track): <paramref name="regions"/>,
    /// <paramref name="jumps"/> and <paramref name="markers"/> are in sample frames from the start of the audio
    /// and go in the map by position, with a stop at <paramref name="stop"/> when given. <paramref name="fill"/>
    /// writes the <paramref name="frames"/> frames of samples into the buffer from the offset it's given.
    /// </summary>
    public static ImusSound Build(int sampleRate, int channels, long frames, IEnumerable<Region> regions, IEnumerable<Jump> jumps,
        IEnumerable<Marker> markers, long? stop, Action<byte[], int> fill)
    {
        int bpf = channels * 2;
        var map = new MemoryStream();
        void Chunk(uint tag, params uint[] fields) => Write(map, tag, fields, null);
        Chunk(FRMT, 0, 0, 16, (uint)sampleRate, (uint)channels);
        var regionList = new List<Region>(regions);
        var jumpList = new List<Jump>(jumps);
        var markerList = new List<Marker>(markers);
        // Positions are bytes into the whole file; the header before DATA is fixed by the map's size.
        int mapSize = (int)map.Length + regionList.Count * 16 + jumpList.Count * 24 + (stop != null ? 12 : 0);
        foreach (var m in markerList) mapSize += 8 + 4 + Encoding.Latin1.GetByteCount(m.Text) + 1;
        int dataOffset = 8 + 8 + mapSize + 8;
        uint At(long frame) => (uint)(dataOffset + frame * bpf);
        // In position order; where several share one, markers, then jumps, then the region that plays on, then the stop.
        var events = new List<(long Frame, int Rank, Action Write)>();
        foreach (var m in markerList) events.Add((m.Offset, 0, () => Write(map, TEXT, [At(m.Offset)], m.Text)));
        foreach (var j in jumpList) events.Add((j.Offset, 1, () => Chunk(JUMP, At(j.Offset), At(j.Destination), (uint)j.Hook, (uint)j.FadeMs)));
        foreach (var r in regionList) events.Add((r.Offset, 2, () => Chunk(REGN, At(r.Offset), (uint)(r.Length * bpf))));
        if (stop is { } s) events.Add((s, 3, () => Chunk(STOP, At(s))));
        events.Sort((a, b) => a.Frame != b.Frame ? a.Frame.CompareTo(b.Frame) : a.Rank.CompareTo(b.Rank));
        foreach (var e in events) e.Write();

        long dataLength = frames * bpf;
        if (dataOffset + dataLength > Array.MaxLength) throw new InvalidDataException("track too long for one iMUS sound");
        var bytes = new byte[dataOffset + dataLength];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, IMUS);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], (uint)(bytes.Length - 8));
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], MAP);
        BinaryPrimitives.WriteUInt32BigEndian(span[12..], (uint)map.Length);
        map.ToArray().CopyTo(span[16..]);
        BinaryPrimitives.WriteUInt32BigEndian(span[(dataOffset - 8)..], DATA);
        BinaryPrimitives.WriteUInt32BigEndian(span[(dataOffset - 4)..], (uint)dataLength);
        fill(bytes, dataOffset);
        return Parse(bytes);
    }

    private static void Write(MemoryStream map, uint tag, uint[] fields, string text)
    {
        var textBytes = text == null ? [] : Encoding.Latin1.GetBytes(text + "\0");
        var word = new byte[4];
        void Word(uint v) { BinaryPrimitives.WriteUInt32BigEndian(word, v); map.Write(word); }
        Word(tag);
        Word((uint)(fields.Length * 4 + textBytes.Length));
        foreach (var f in fields) Word(f);
        map.Write(textBytes);
    }

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
        return s.Bits == 12 ? Widen12(s) : s;
    }

    /// <summary>
    /// 12-bit samples (the sound resources' effects) as 16-bit: every 3 bytes hold two unsigned samples
    /// centred on 0x800, the first in byte 0 and byte 1's low nibble, the second in byte 2 and byte 1's high
    /// nibble (ScummVM's mixBits12). The map's positions in the samples grow by 4/3 with them.
    /// </summary>
    private static ImusSound Widen12(ImusSound s)
    {
        var src = s.Samples;
        int pairs = src.Length / 3;
        var bytes = new byte[s.DataOffset + pairs * 4];
        s.Bytes.AsSpan(0, s.DataOffset).CopyTo(bytes);
        for (int i = 0, o = s.DataOffset; i < pairs; i++, o += 4)
        {
            int b0 = src[i * 3], b1 = src[i * 3 + 1], b2 = src[i * 3 + 2];
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(o), (short)(((b0 | (b1 & 0x0F) << 8) - 0x800) << 4));
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(o + 2), (short)(((b2 | (b1 & 0xF0) << 4) - 0x800) << 4));
        }

        long At(long offset) => offset <= s.DataOffset ? offset : s.DataOffset + (offset - s.DataOffset) * 4 / 3;
        var w = new ImusSound(bytes)
        {
            Bits = 16, SampleRate = s.SampleRate, Channels = s.Channels,
            DataOffset = s.DataOffset, DataLength = pairs * 4,
            Stop = s.Stop is { } stop ? At(stop) : null,
        };
        foreach (var r in s.Regions) w.Regions.Add(new Region(At(r.Offset), At(r.Offset + r.Length) - At(r.Offset)));
        foreach (var j in s.Jumps) w.Jumps.Add(j with { Offset = At(j.Offset), Destination = At(j.Destination) });
        foreach (var m in s.Markers) w.Markers.Add(m with { Offset = At(m.Offset) });
        foreach (var y in s.Syncs) w.Syncs.Add(y with { Offset = At(y.Offset) });
        foreach (var e in s.Events) w.Events.Add(e with { Offset = At(e.Offset) });
        w.UnknownEvents.AddRange(s.UnknownEvents);
        return w;
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
