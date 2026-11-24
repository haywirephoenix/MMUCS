using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MMUCS.Core.Audio;

/// <summary>
/// One row of COMI's music tables: what a music state or sequence plays and how it gets there. The
/// transition says how it takes over from the music playing (0 nothing; 2 and 3 crossfade, 3 keeping the
/// place when the attribute matches; 4 too, and the sequence ends at the track's "_end" marker; 8 only
/// sets the playing track's hook; 9 does that and ends at "_end"; 12 switches at the playing track's
/// "exit" marker). <see cref="AttribPos"/> and <see cref="HookId"/> pick which of a track's loops plays.
/// </summary>
public sealed record MusicCue(int Transition, int SoundId, string Name, int AttribPos, int HookId, int FadeOutDelay, string File)
{
    /// <summary>The number the track's file starts with ("2560-Zap4": 2560), which names its bundle entry; 0 for none.</summary>
    public int FileNumber
    {
        get
        {
            int digits = 0;
            while (digits < File.Length && char.IsAsciiDigit(File[digits])) digits++;
            return digits > 0 && int.TryParse(File.AsSpan(0, digits), out int n) ? n : 0;
        }
    }
}

/// <summary>
/// COMI's music states (1000 and up: the music a place plays) and sequences (2000 and up: music for an
/// event, played over the state's), as the game's own program holds them: the engine has them built in,
/// not in the data files. Each row is 76 bytes: the track's file name (32), the transition, the sound id
/// (u32 each), the name (24), the attribute position, the hook and the fade-out delay (u32 each); each table
/// starts with its null entry (STATE_NULL, 1000; SEQ_NULL, 2000).
/// </summary>
public sealed class MusicTable
{
    private const int RowSize = 76, NameAt = 40;

    public IReadOnlyList<MusicCue> States { get; private init; } = [];
    public IReadOnlyList<MusicCue> Sequences { get; private init; } = [];

    /// <summary>Reads the tables from the game's program, or null when it has none.</summary>
    public static MusicTable Read(ReadOnlySpan<byte> program)
    {
        var states = Rows(program, "STATE_NULL"u8, 1000, "state");
        var sequences = Rows(program, "SEQ_NULL"u8, 2000, "seq");
        return states.Count == 0 || sequences.Count == 0 ? null : new MusicTable { States = states, Sequences = sequences };
    }

    /// <summary>The tables from the program beside <paramref name="gameFile"/> (comi.exe there or in its install folder), or null.</summary>
    public static MusicTable Load(string gameFile)
    {
        if (FindProgram(gameFile) is not { } exe) return null;
        try { return Read(File.ReadAllBytes(exe)); }
        catch (IOException) { return null; }
    }

    /// <summary>The game's program: comi.exe beside the game file, in its install folder, or a folder up.</summary>
    public static string FindProgram(string gameFile)
    {
        if (string.IsNullOrEmpty(gameFile) || Path.GetDirectoryName(Path.GetFullPath(gameFile)) is not { } dir) return null;
        foreach (var folder in new[] { dir, Path.Combine(dir, "install"), Path.GetDirectoryName(dir) })
        {
            if (folder == null || !Directory.Exists(folder)) continue;
            var exe = Directory.EnumerateFiles(folder).FirstOrDefault(f => Path.GetFileName(f).Equals("comi.exe", StringComparison.OrdinalIgnoreCase));
            if (exe != null) return exe;
        }
        return null;
    }

    /// <summary>The rows from the null entry on, while their names have the table's prefix.</summary>
    private static List<MusicCue> Rows(ReadOnlySpan<byte> d, ReadOnlySpan<byte> nullName, int nullId, string prefix)
    {
        var rows = new List<MusicCue>();
        int from = 0;
        while (true)
        {
            int at = d[from..].IndexOf(nullName);
            if (at < 0) return rows;
            at += from;
            int start = at - NameAt;
            if (start >= 0 && at + nullName.Length < d.Length && d[at + nullName.Length] == 0 && BitConverter.ToInt32(d.Slice(start + 36, 4)) == nullId)
            {
                for (int row = start; row + RowSize <= d.Length; row += RowSize)
                {
                    var r = d.Slice(row, RowSize);
                    string name = Text(r.Slice(NameAt, 24));
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) break;
                    rows.Add(new MusicCue(
                        BitConverter.ToInt32(r.Slice(32, 4)), BitConverter.ToInt32(r.Slice(36, 4)), name,
                        BitConverter.ToInt32(r.Slice(64, 4)), BitConverter.ToInt32(r.Slice(68, 4)), BitConverter.ToInt32(r.Slice(72, 4)),
                        Text(r[..32])));
                }
                return rows;
            }
            from = at + 1;
        }
    }

    private static string Text(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? field : field[..end]);
    }
}
