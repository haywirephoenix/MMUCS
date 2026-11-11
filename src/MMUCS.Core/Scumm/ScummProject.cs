using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MMUCS.Core.Scumm;

/// <summary>
/// Loads a game's resource set (e.g. COMI.LA0 + LA1 + LA2) as one tree. LA0 carries the room
/// name table (RNAM) that LA1/LA2 rooms refer to, so all files go through a single parser instance
/// in index order and end up under a virtual root block.
/// </summary>
public static class ScummProject
{
    /// <summary>
    /// For "NAME.LAn", returns every "NAME.LA&lt;digits&gt;" in the same folder ordered by number;
    /// otherwise just the file itself.
    /// </summary>
    public static IReadOnlyList<string> FindRelatedFiles(string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path).TrimStart('.');

        if (!IsLaExtension(ext, out _))
            return new[] { path };

        var files = Directory.GetFiles(dir, baseName + ".*", SearchOption.TopDirectoryOnly)
            .Select(f => (Path: f, Ok: IsLaExtension(Path.GetExtension(f).TrimStart('.'), out int n), Index: n))
            .Where(x => x.Ok && string.Equals(Path.GetFileNameWithoutExtension(x.Path), baseName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Index)
            .Select(x => x.Path)
            .ToList();

        return files.Count > 0 ? files : new[] { path };
    }

    private static bool IsLaExtension(string ext, out int index)
    {
        index = int.MaxValue;
        return ext.Length > 2
            && ext.StartsWith("LA", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(ext.AsSpan(2), out index);
    }

    /// <summary>Parses all files; a single file returns its own root, several get a virtual root.</summary>
    public static ScummBlock Load(IReadOnlyList<string> files, Action<int, int, string> progress = null)
    {
        if (files == null || files.Count == 0) throw new ArgumentException("No files", nameof(files));

        var parser = new ScummResourceParser(); // clears the shared room tables once
        var roots = new List<ScummBlock>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            progress?.Invoke(i, files.Count, files[i]);
            // LOFF offsets are file-relative; drop the previous file's before parsing the next.
            ScummStatics.RoomOffsets.Clear();
            roots.Add(parser.Parse(files[i]));
        }

        if (roots.Count == 1) return roots[0];

        string name = Path.GetFileNameWithoutExtension(files[0]);
        var root = new ScummBlock
        {
            Tag = 0,
            TagName = name,
            FullName = $"SCUMM Data — {files.Count} files",
            Description = string.Join(", ", files.Select(Path.GetFileName)),
            Size = roots.Sum(r => r.Size),
        };
        foreach (var r in roots)
        {
            r.Parent = root;
            root.Children.Add(r);
        }
        return root;
    }
}
