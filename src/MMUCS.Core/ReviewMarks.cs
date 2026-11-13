using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMUCS.Core;

/// <summary>
/// What MMUCS worked out for a marked costume or object when it was marked, so a later run (after a
/// code change) can say what changed. Fields that don't apply are null.
/// </summary>
public sealed class ReviewSnapshot
{
    [JsonPropertyName("placed")] public bool Placed { get; set; }
    [JsonPropertyName("actor")] public int? Actor { get; set; }
    [JsonPropertyName("x")] public int? X { get; set; }
    [JsonPropertyName("y")] public int? Y { get; set; }
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("elevation")] public int? Elevation { get; set; }
    [JsonPropertyName("scale")] public int? Scale { get; set; }
    [JsonPropertyName("zplane")] public int? ZPlane { get; set; }
    [JsonPropertyName("dressed_by")] public int? DressedBy { get; set; }
    /// <summary>The object a costume belongs to (its hotspot), as matched.</summary>
    [JsonPropertyName("owner")] public string Owner { get; set; }
    /// <summary>An object image's state on entering the room (0 = hidden).</summary>
    [JsonPropertyName("entry_state")] public int? EntryState { get; set; }
}

/// <summary>
/// A costume or object marked in the room viewer as possibly wrong (position, scale, draw order …),
/// for tools/mroom's review command.
/// </summary>
public sealed class ReviewMark
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("created")] public string Created { get; set; }
    [JsonPropertyName("room")] public int Room { get; set; }
    [JsonPropertyName("room_name")] public string RoomName { get; set; }
    /// <summary>"costume" or "object".</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; }
    [JsonPropertyName("costume")] public int? Costume { get; set; }
    /// <summary>The costume's AKOS block offset, "0x…": tells copies apart.</summary>
    [JsonPropertyName("akos")] public string Akos { get; set; }
    /// <summary>For an object mark, the object's name.</summary>
    [JsonPropertyName("object")] public string Object { get; set; }
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("note")] public string Note { get; set; }
    [JsonPropertyName("snapshot")] public ReviewSnapshot Snapshot { get; set; } = new();
}

/// <summary>
/// The review marks, kept in review.json beside the app's settings (%LocalAppData%/MMUCS on Windows).
/// Every change reads the file first, so marks cleared by tools/mroom while the app runs stay cleared.
/// </summary>
public static class ReviewMarks
{
    /// <summary>What a mark can say looks wrong. A mark is reviewed in full whatever it's tagged with.</summary>
    public static readonly IReadOnlyList<(string Tag, string Label)> Tags = new[]
    {
        ("position", "Position"),
        ("scale", "Scale"),
        ("order", "Draw order (in front of / behind)"),
        ("zplane", "Z-plane (cut by the wrong foreground)"),
        ("object", "Wrong object / owner"),
        ("frame", "Wrong frame or animation"),
        ("missing", "Not placed / not shown"),
    };

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class Document
    {
        [JsonPropertyName("format")] public int Format { get; set; } = 1;
        [JsonPropertyName("marks")] public List<ReviewMark> Marks { get; set; } = new();
    }

    /// <summary>The file the marks live in; tests point it elsewhere.</summary>
    public static string FilePath { get; set; }

    private static string PathOrDefault => FilePath ?? Path.Combine(CorePaths.Root, "review.json");

    /// <summary>Where the marks are read from and saved to.</summary>
    public static string CurrentPath => PathOrDefault;

    public static List<ReviewMark> Load()
    {
        try
        {
            string path = PathOrDefault;
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Options)?.Marks ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Diagnostics.Log.Warn($"Review marks unreadable: {ex.Message}");
            return new();
        }
    }

    private static void Save(List<ReviewMark> marks)
    {
        string path = PathOrDefault;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new Document { Marks = marks }, Options));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Adds a mark (replacing one for the same thing) and returns it with its id.</summary>
    public static ReviewMark Add(ReviewMark mark)
    {
        var marks = Load();
        var same = marks.FirstOrDefault(m => SameTarget(m, mark));
        if (same != null) marks.Remove(same);
        mark.Id = same?.Id ?? (marks.Count == 0 ? 1 : marks.Max(m => m.Id) + 1);
        mark.Created ??= DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        marks.Add(mark);
        Save(marks.OrderBy(m => m.Id).ToList());
        return mark;
    }

    public static bool Remove(int id)
    {
        var marks = Load();
        if (marks.RemoveAll(m => m.Id == id) == 0) return false;
        Save(marks);
        return true;
    }

    /// <summary>
    /// Whether two marks are about the same thing: the same object, or the same costume in the same
    /// room at the same spot (one costume can stand in several places, like the fortbase chickens).
    /// </summary>
    public static bool SameTarget(ReviewMark a, ReviewMark b) =>
        a.Room == b.Room && a.Kind == b.Kind && (a.Kind == "object"
            ? a.Object == b.Object
            : a.Akos == b.Akos && a.Snapshot?.X == b.Snapshot?.X && a.Snapshot?.Y == b.Snapshot?.Y);
}
