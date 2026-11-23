using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MMUCS.Core.Smush;

/// <summary>A line of cutscene text and the frames it is on screen (<see cref="To"/> is the last one).</summary>
public sealed record SubtitleCue(int From, int To, SmushText Text);

/// <summary>
/// Collects a cutscene's lines into cues as its frames are decoded: a line repeated on consecutive frames
/// (the same chunk, chunk for chunk) is one cue. Written as <c>video/&lt;name&gt;/subtitles.json</c>, the
/// <c>video/&lt;name&gt;/subtitles</c> asset of docs/bundle-format.md, pointing at the <c>text/</c> IDs.
/// </summary>
public sealed class SubtitleTrack
{
    private readonly Dictionary<SmushText, SubtitleCue> _open = [];
    private int _lastFrame = -1;

    public List<SubtitleCue> Cues { get; } = [];

    /// <summary>Adds a frame's lines; frames must come in order.</summary>
    public void Add(int frame, IReadOnlyList<SmushText> texts)
    {
        var still = new Dictionary<SmushText, SubtitleCue>();
        foreach (var t in texts)
        {
            if (still.ContainsKey(t)) continue;
            if (_open.TryGetValue(t, out var cue) && cue.To == _lastFrame && frame == _lastFrame + 1)
            {
                var longer = cue with { To = frame };
                Cues[Cues.IndexOf(cue)] = longer;
                still[t] = longer;
            }
            else
            {
                var started = new SubtitleCue(frame, frame, t);
                Cues.Add(started);
                still[t] = started;
            }
        }
        _open.Clear();
        foreach (var (k, v) in still) _open[k] = v;
        _lastFrame = frame;
    }

    public void Write(string path, SmushVideo video)
    {
        using var fs = File.Create(path);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        w.WriteStartObject();
        w.WriteString("id", $"video/{video.Name}/subtitles");
        w.WriteNumber("frameRate", video.Header.FrameRate);
        w.WriteString("frames", "from and to are frame numbers, both shown");
        w.WriteString("flags", "1 centre, 2 right, 4 wrap (inside a 10 px margin), 8 subtitle (hidden when subtitles are off)");
        w.WriteStartArray("cues");
        foreach (var c in Cues)
        {
            var t = c.Text;
            w.WriteStartObject();
            w.WriteNumber("from", c.From);
            w.WriteNumber("to", c.To);
            if (t.TextId != null) w.WriteString("text", t.TextId);
            w.WriteString("english", t.Text.TrimEnd('\n', ' '));
            w.WriteNumber("x", t.X);
            w.WriteNumber("y", t.Y);
            w.WriteNumber("flags", (int)t.Flags);
            w.WriteNumber("font", t.Font);
            w.WriteNumber("color", t.Color);
            w.WriteStartArray("clip");
            w.WriteNumberValue(t.ClipLeft); w.WriteNumberValue(t.ClipTop); w.WriteNumberValue(t.ClipWidth); w.WriteNumberValue(t.ClipHeight);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }
}
