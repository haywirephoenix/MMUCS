using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MMUCS.Runtime.Scripts;

namespace MMUCS.Runtime;

/// <summary>
/// An input recording (the game window's <c>--record</c>, mprobe boot's <c>--replay</c>): every input that reached
/// the engine, by the frame loop's jiffy, so a headless run can feed the same inputs at the same moments. A text
/// file, one line each: <c>boot N</c> first, then <c>JIFFY move X,Y</c> (the page's mouse, once a jiffy when it
/// changed), <c>JIFFY press left|right</c>, <c>JIFFY release left|right</c>, <c>JIFFY key CODE</c>, <c>JIFFY key-up CODE</c> (a key let go),
/// <c>JIFFY answer CODE</c> (a system message's answer: 0 for a click), <c>JIFFY skip-video</c>,
/// <c>JIFFY load FILE</c> (a save loaded: a copy beside the recording, so a later F6 doesn't change it) and
/// <c>JIFFY end</c>. <c>#</c> starts a comment. Jiffies count from boot, as both <see cref="FrameLoop.Jiffies"/>
/// and mprobe's clock do; the loop doesn't tick while a video plays or a system message waits.
/// </summary>
public sealed class InputRecording : IDisposable
{
    public const string Extension = ".mmucs-replay";

    /// <summary>Where the game window's <c>--record</c> writes when given no path.</summary>
    public static string DefaultFolder => System.IO.Path.Combine(MMUCS.Core.CorePaths.Root, "recordings");

    /// <summary>The newest recording in <see cref="DefaultFolder"/>, or null.</summary>
    public static string Latest() => Directory.Exists(DefaultFolder)
        ? new DirectoryInfo(DefaultFolder).GetFiles("*" + Extension).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName
        : null;

    public readonly record struct Event(long Jiffy, string Kind, int X = 0, int Y = 0, string File = null)
    {
        public MouseButton Button => X == 1 ? MouseButton.Right : MouseButton.Left;
    }

    private readonly StreamWriter _out;
    private (int X, int Y)? _mouse;
    private bool _left, _right;
    private int _loads;

    /// <summary>The recording's file.</summary>
    public string Path { get; }

    /// <summary>The last jiffy anything happened at.</summary>
    public long LastJiffy { get; private set; }

    /// <summary>Each line as it's written (not the mouse moves), for the host's log.</summary>
    public event Action<string> Recorded;

    public InputRecording(string path, int bootParam, string gameFile)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _out = new StreamWriter(Path, false) { AutoFlush = true }; // what's there survives a crash
        _out.WriteLine($"# MMUCS input recording, {DateTime.Now:yyyy-MM-dd HH:mm}{(gameFile == null ? "" : $", {gameFile}")}");
        _out.WriteLine($"# replay: mprobe boot --replay {Path}");
        _out.WriteLine($"boot {bootParam}");
    }

    /// <summary>The page's mouse, written when it moved since the last.</summary>
    public void Move(long jiffy, (int X, int Y) mouse)
    {
        if (_mouse == mouse) return;
        _mouse = mouse;
        Write(jiffy, $"move {mouse.X},{mouse.Y}", quiet: true);
    }

    public void Press(long jiffy, (int X, int Y) mouse, MouseButton button)
    {
        Move(jiffy, mouse);
        if (button == MouseButton.Left) _left = true; else _right = true;
        Write(jiffy, $"press {Name(button)}");
    }

    /// <summary>A button let go; only one that was pressed in the recording is written.</summary>
    public void Release(long jiffy, (int X, int Y) mouse, MouseButton button)
    {
        ref bool down = ref button == MouseButton.Left ? ref _left : ref _right;
        if (!down) return;
        down = false;
        Move(jiffy, mouse);
        Write(jiffy, $"release {Name(button)}");
    }

    private readonly HashSet<int> _keys = [];

    public void Key(long jiffy, int code)
    {
        _keys.Add(code);
        Write(jiffy, $"key {code}");
    }

    /// <summary>A key let go; only one that went down in the recording is written.</summary>
    public void KeyUp(long jiffy, int code)
    {
        if (_keys.Remove(code)) Write(jiffy, $"key-up {code}", quiet: true);
    }

    public void Answer(long jiffy, int code) => Write(jiffy, $"answer {code}");

    public void SkipVideo(long jiffy) => Write(jiffy, "skip-video");

    /// <summary>A save loaded: copied beside the recording, which names the copy.</summary>
    public void Load(long jiffy, string saveFile)
    {
        string copy = $"{System.IO.Path.GetFileNameWithoutExtension(Path)}.load{++_loads}.mmucs-save";
        File.Copy(saveFile, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, copy), overwrite: true);
        _mouse = null; // the next move is written again
        Write(jiffy, $"load {copy}");
    }

    public void Comment(string text) => _out.WriteLine($"# {text}");

    /// <summary>The end: the replay runs to here.</summary>
    public void End(long jiffy)
    {
        Write(jiffy, "end");
        _out.Flush();
    }

    public void Dispose() => _out.Dispose();

    private void Write(long jiffy, string what, bool quiet = false)
    {
        LastJiffy = Math.Max(LastJiffy, jiffy);
        string line = $"{jiffy} {what}";
        _out.WriteLine(line);
        if (!quiet) Recorded?.Invoke(line);
    }

    private static string Name(MouseButton button) => button == MouseButton.Left ? "left" : "right";

    /// <summary>A recording's boot parameter and events, in order; a load's file is made a full path.</summary>
    public static (int BootParam, List<Event> Events) Read(string path)
    {
        string folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        int boot = 0;
        var events = new List<Event>();
        int number = 0;
        foreach (string raw in File.ReadLines(path))
        {
            number++;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts[0] == "boot" && parts.Length == 2 && int.TryParse(parts[1], out boot)) continue;
            if (!long.TryParse(parts[0], out long jiffy) || parts.Length < 2) throw Bad();
            string arg = parts.Length > 2 ? parts[2] : "";
            events.Add(parts[1] switch
            {
                "move" when arg.Split(',') is [var x, var y] => new Event(jiffy, "move", Int(x), Int(y)),
                "press" or "release" => new Event(jiffy, parts[1], arg switch { "left" => 0, "right" => 1, _ => throw Bad() }),
                "key" or "key-up" or "answer" => new Event(jiffy, parts[1], Int(arg)),
                "skip-video" or "end" => new Event(jiffy, parts[1]),
                "load" when arg.Length > 0 => new Event(jiffy, "load", File: System.IO.Path.Combine(folder, arg)),
                _ => throw Bad(),
            });
        }
        return (boot, events);

        int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : throw Bad();
        Exception Bad() => new FormatException($"{path}:{number}: not a recording line");
    }
}
