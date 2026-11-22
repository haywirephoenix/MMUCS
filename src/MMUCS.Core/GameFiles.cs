using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MMUCS.Core;

/// <summary>Finds a game's loose data files (cutscenes, bundles, fonts) from its first resource file.</summary>
public static class GameFiles
{
    /// <summary>
    /// Every file with the extension beside <paramref name="gameFile"/> or in a "resource" folder next
    /// to it (any case), sorted by name.
    /// </summary>
    public static IReadOnlyList<string> Find(string gameFile, string extension)
    {
        if (string.IsNullOrEmpty(gameFile)) return [];
        string dir = Path.GetDirectoryName(Path.GetFullPath(gameFile));
        if (dir == null || !Directory.Exists(dir)) return [];

        var folders = new List<string> { dir };
        folders.AddRange(Directory.GetDirectories(dir)
            .Where(d => string.Equals(Path.GetFileName(d), "resource", StringComparison.OrdinalIgnoreCase)));

        return [.. folders
            .SelectMany(Directory.GetFiles)
            .Where(f => string.Equals(Path.GetExtension(f), extension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)];
    }
}
