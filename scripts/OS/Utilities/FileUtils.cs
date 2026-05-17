using System;
using System.IO;
using Godot;

public static class FileUtils
{
    
    public static bool PathExists(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            // GD.PrintErr("FileUtils: Provided path is null or empty.");
            return false;
        }

        if (path.StartsWith("res://") || path.StartsWith("user://"))
        {
            return DirAccess.DirExistsAbsolute(path) || Godot.FileAccess.FileExists(path);
        }

        return Directory.Exists(path) || File.Exists(path);
    }


    public static bool DirectoryExists(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        if (path.StartsWith("res://") || path.StartsWith("user://"))
        {
            return DirAccess.DirExistsAbsolute(path);
        }

        return Directory.Exists(path);
    }

    public static bool FileExists(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        if (path.StartsWith("res://") || path.StartsWith("user://"))
        {
            return Godot.FileAccess.FileExists(path);
        }

        return File.Exists(path);
    }


    public static string GetOrCreatePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            GD.PrintErr("FileUtils: Provided path is null or empty.");
            return string.Empty;
        }

        if (path.StartsWith("res://") && !OS.HasFeature("editor"))
        {
            GD.PrintErr($"FileUtils: Cannot create directory in 'res://' in a exported build: {path}");
            return string.Empty;
        }

        string globalPath = (path.StartsWith("res://") || path.StartsWith("user://")) 
            ? ProjectSettings.GlobalizePath(path) 
            : path;

        if (string.IsNullOrEmpty(globalPath))
        {
            GD.PrintErr($"FileUtils: Failed to globalize path '{path}'.");
            return string.Empty;
        }

        string directory = Path.GetDirectoryName(globalPath);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"FileUtils: Failed to create directory '{directory}'. Exception: {ex.Message}");
                return string.Empty;
            }
        }

        return globalPath;
    }


    public static string CombinePaths(string basePath, string relativePath)
    {
        if (string.IsNullOrEmpty(basePath)) return relativePath;
        if (string.IsNullOrEmpty(relativePath)) return basePath;

        basePath = basePath.Replace('\\', '/');
        relativePath = relativePath.Replace('\\', '/').TrimStart('/');

        if (basePath.EndsWith("/"))
        {
            return basePath + relativePath;
        }
        
        return basePath + "/" + relativePath;
    }
}