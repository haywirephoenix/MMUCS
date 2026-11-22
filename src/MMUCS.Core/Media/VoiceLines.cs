using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Scumm;

namespace MMUCS.Core.Media;

/// <summary>
/// One spoken line as the scripts give it: <c>say-line 2 "/BCCB301/IT'S EL POLLO DIABLO!"</c>. The code
/// names the voice file (voice/bccb301) and the text after it is the English subtitle. Room is the room
/// whose local script, entry/exit code or object verb holds the line; 0 when only a global script has it.
/// </summary>
public sealed record VoiceLine(string Code, string Text, int Room, string RoomName);

/// <summary>
/// The speech in a game's scripts, and what its codes are made of. A code is a 2-character scene, a
/// 2-character speaker and a line number (BC·CB·301); the scene mostly matches a room, and GT is
/// Guybrush. Names for scenes and speakers come from Names/voice.json, where known.
/// </summary>
public static class VoiceLines
{
    /// <summary>Every line in the scripts with a /CODE/ prefix, by upper-case code.</summary>
    public static IReadOnlyDictionary<string, VoiceLine> Index(ScummBlock root)
    {
        var lines = new Dictionary<string, VoiceLine>(StringComparer.OrdinalIgnoreCase);
        if (root == null) return lines;

        foreach (var lflf in root.FindChildrenRecursive(ScummTag.LFLF))
        {
            RoomModel.TryCreate(lflf, out var room);
            // Global scripts are only stored in a room, so their lines don't say where they're spoken.
            foreach (var tag in new[] { ScummTag.LSCR, ScummTag.ENCD, ScummTag.EXCD, ScummTag.VERB, ScummTag.SCRP })
                foreach (var block in lflf.FindChildrenRecursive(tag))
                {
                    bool global = tag == ScummTag.SCRP;
                    foreach (var (code, text) in Strings(block.DataSpan))
                    {
                        var line = new VoiceLine(code, text, global ? 0 : room?.RoomNo ?? 0, global ? null : room?.Name);
                        // A line in a room beats the same line in a global script.
                        if (!lines.TryGetValue(code, out var seen) || (seen.Room == 0 && line.Room != 0)) lines[code] = line;
                    }
                }
        }
        return lines;
    }

    /// <summary>The /CODE/text strings in a block of script bytecode.</summary>
    public static IEnumerable<(string Code, string Text)> Strings(ReadOnlySpan<byte> data)
    {
        var found = new List<(string, string)>();
        for (int i = 0; i + 6 < data.Length; i++)
        {
            if (data[i] != '/') continue;
            int j = i + 1;
            while (j < data.Length && j - i - 1 < 10 && IsCodeChar(data[j])) j++;
            int len = j - i - 1;
            if (len < 4 || len > 9 || j >= data.Length || data[j] != '/') continue;
            int end = data[(j + 1)..].IndexOf((byte)0);
            if (end < 0) continue;
            found.Add((Encoding.ASCII.GetString(data.Slice(i + 1, len)), Clean(data.Slice(j + 1, end))));
            i = j + end;
        }
        return found;
    }

    private static bool IsCodeChar(byte b)
    {
        return b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'_';
    }

    /// <summary>The text without SCUMM's 0xFF control codes (waits, colours), as one line.</summary>
    private static string Clean(ReadOnlySpan<byte> s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            byte b = s[i];
            if (b == 0xFF) { i++; sb.Append(' '); continue; }
            sb.Append(b < 0x20 || b == 0x7F ? ' ' : (char)b);
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>A code's parts: BCCB301 → ("BC", "CB", 301). Underscores are skipped (PU_CP124).</summary>
    public static (string Scene, string Speaker, int Number) Split(string code)
    {
        string c = new string([.. (code ?? "").Where(char.IsAsciiLetterOrDigit)]).ToUpperInvariant();
        string scene = c.Length >= 2 ? c[..2] : c;
        string speaker = c.Length >= 4 ? c[2..4] : "";
        int digits = 0;
        while (digits < c.Length && char.IsAsciiDigit(c[^(digits + 1)])) digits++;
        int number = digits > 0 && int.TryParse(c[^digits..], out int n) ? n : 0;
        return (scene, speaker, number);
    }
}

/// <summary>
/// Names for the parts of speech codes: Names/voice.json (embedded), with a voice.json in the app data
/// folder adding to or overriding it.
/// </summary>
public static class VoiceNames
{
    public sealed class FileModel
    {
        public string Note { get; set; }
        public Dictionary<string, string> Speakers { get; set; }
        public Dictionary<string, string> Scenes { get; set; }
    }

    public static string LocalFile => Path.Combine(CorePaths.Root, "voice.json");

    private static readonly Lazy<(Dictionary<string, string> Speakers, Dictionary<string, string> Scenes)> _names = new(Load);

    public static string Speaker(string code)
    {
        return code != null && _names.Value.Speakers.TryGetValue(code, out var n) ? n : null;
    }

    public static string Scene(string code)
    {
        return code != null && _names.Value.Scenes.TryGetValue(code, out var n) ? n : null;
    }

    private static (Dictionary<string, string>, Dictionary<string, string>) Load()
    {
        var speakers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scenes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Apply(FileModel m)
        {
            foreach (var (k, v) in m?.Speakers ?? []) if (!string.IsNullOrWhiteSpace(v)) speakers[k] = v;
            foreach (var (k, v) in m?.Scenes ?? []) if (!string.IsNullOrWhiteSpace(v)) scenes[k] = v;
        }
        try
        {
            using var stream = typeof(VoiceNames).Assembly.GetManifestResourceStream("voice.json");
            if (stream != null) Apply(JsonSerializer.Deserialize<FileModel>(stream));
        }
        catch (Exception ex) { Log.Warn($"Built-in voice names ignored: {ex.Message}"); }
        try
        {
            if (File.Exists(LocalFile)) Apply(JsonSerializer.Deserialize<FileModel>(File.ReadAllText(LocalFile)));
        }
        catch (Exception ex) { Log.Warn($"{LocalFile} ignored: {ex.Message}"); }
        return (speakers, scenes);
    }
}
