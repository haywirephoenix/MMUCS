using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MMUCS.Core;

/// <summary>
/// Mods to download: a repository is a <c>mods.json</c> anyone can host (a GitHub Pages site made from the template
/// in <c>mods-repo-template/</c>), listing each mod's id (its folder under _mods), name, category, version,
/// description, and a zip of its folder with that zip's SHA-256 and size:
/// <code>
/// { "name": "MMUCS mods", "mods": [
///   { "id": "WideScreenMod", "name": "Wide screen", "category": "Images", "version": "1.0.0",
///     "description": "…", "zip": "zips/WideScreenMod-1.0.0.zip", "sha256": "…", "size": 123456 } ] }
/// </code>
/// A relative <c>zip</c> is from the mods.json's address. <c>mprobe mods --pack</c> writes the zips and mods.json.
/// An installed mod's mod.json keeps the version it came as (<c>"version"</c>), so an update shows.
/// </summary>
public static class ModRepository
{
    /// <summary>The repository the Mods view starts with.</summary>
    public const string DefaultUrl = "https://haywirephoenix.github.io/MMUCS-mods/mods.json";

    /// <summary>A mod a repository offers.</summary>
    public sealed record Entry(string Id, string Name, string Category, string Version, string Description, Uri Zip, string Sha256, long Size);

    /// <summary>A repository's mods.json: its name and the mods it offers.</summary>
    public sealed record Index(string Name, List<Entry> Mods);

    /// <summary>A mod in the game's _mods folder: its folder, its mod.json's name, version and description, whether it's on, and what kind it is.</summary>
    public sealed record Installed(string Id, string Folder, string Name, string Version, string Description, bool Enabled, string Category);

    /// <summary>Reads a mods.json (<paramref name="json"/>) found at <paramref name="url"/>; entries without an id or zip are left out.</summary>
    public static Index Parse(string json, Uri url)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        string Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var mods = new List<Entry>();
        if (root.TryGetProperty("mods", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var m in list.EnumerateArray())
            {
                string id = Str(m, "id"), zip = Str(m, "zip");
                if (!IsFolderName(id) || string.IsNullOrEmpty(zip) || !Uri.TryCreate(url, zip, out var zipUrl)) continue;
                long size = m.TryGetProperty("size", out var s) && s.TryGetInt64(out long n) ? n : 0;
                mods.Add(new Entry(id, Str(m, "name") ?? id, Str(m, "category") ?? "Other", Str(m, "version") ?? "", Str(m, "description") ?? "",
                    zipUrl, Str(m, "sha256")?.ToLowerInvariant(), size));
            }
        return new Index(Str(root, "name") ?? url.Host, mods);
    }

    /// <summary>Fetches and reads the mods.json at <paramref name="url"/>.</summary>
    public static async Task<Index> FetchAsync(HttpClient http, Uri url, CancellationToken token = default)
    {
        string json = await http.GetStringAsync(url, token);
        return Parse(json, url);
    }

    /// <summary>Whether <paramref name="id"/> can be a folder under _mods: a plain name, no path in it.</summary>
    public static bool IsFolderName(string id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 100 && id.IndexOfAny(['/', '\\', ':']) < 0 && id != "." && id != ".." &&
        id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !id.StartsWith('.');

    /// <summary>The mods in the _mods folder beside <paramref name="gameFile"/> that have a mod.json, in name order.</summary>
    public static List<Installed> Scan(string gameFile)
    {
        var mods = new List<Installed>();
        foreach (var mod in GameMods.Load(gameFile))
        {
            var root = mod.Json.RootElement;
            string Str(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            mods.Add(new Installed(mod.Name, mod.Folder, Str("name") ?? mod.Name, Str("version") ?? "", Str("description") ?? "", mod.Enabled, CategoryOf(mod)));
        }
        return mods;
    }

    /// <summary>What a mod holds, by its files: Hi-res (a scale), Music (tracks), Images (rooms/), or Other.</summary>
    public static string CategoryOf(GameMods.Mod mod)
    {
        if (mod.Scale > 1) return "Hi-res";
        bool music = mod.Json.RootElement.ValueKind == JsonValueKind.Object && mod.Json.RootElement.TryGetProperty("tracks", out _);
        bool images = Directory.Exists(Path.Combine(mod.Folder, "rooms"));
        return music && images ? "Music, Images" : music ? "Music" : images ? "Images" : "Other";
    }

    /// <summary>Turns the mod in <paramref name="folder"/> on or off (mod.json's "enabled"), keeping the rest of its mod.json.</summary>
    public static void SetEnabled(string folder, bool enabled)
    {
        string file = Path.Combine(folder, "mod.json");
        var root = GameMods.ReadForEdit(file);
        root["enabled"] = enabled;
        GameMods.WriteEdited(file, root);
    }

    /// <summary>The _mods folder beside <paramref name="gameFile"/>, made if it isn't there.</summary>
    public static string CreateFolder(string gameFile)
    {
        string folder = GameMods.Folder(gameFile) ?? throw new InvalidOperationException("No game is open");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// Downloads <paramref name="entry"/>'s zip, checks its SHA-256 (when the repository gives one) and installs it
    /// (<see cref="Install"/>). <paramref name="progress"/> hears the share downloaded (0–1) when the size is known.
    /// </summary>
    public static async Task<string> DownloadAsync(HttpClient http, Entry entry, string gameFile, IProgress<double> progress = null, CancellationToken token = default)
    {
        string modsFolder = CreateFolder(gameFile);
        string temp = Path.Combine(modsFolder, $".{entry.Id}.download");
        try
        {
            using (var response = await http.GetAsync(entry.Zip, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? entry.Size;
                await using var from = await response.Content.ReadAsStreamAsync(token);
                await using var to = File.Create(temp);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await from.ReadAsync(buffer, token)) > 0)
                {
                    await to.WriteAsync(buffer.AsMemory(0, read), token);
                    done += read;
                    if (total > 0) progress?.Report(Math.Min(1, done / (double)total));
                }
            }
            if (entry.Sha256 is { Length: > 0 } want)
            {
                string got;
                await using (var f = File.OpenRead(temp)) got = Convert.ToHexString(await SHA256.HashDataAsync(f, token)).ToLowerInvariant();
                if (got != want) throw new InvalidDataException($"{entry.Id}: the download's SHA-256 is {got}, not the repository's {want}");
            }
            using (var zip = ZipFile.OpenRead(temp)) Install(zip, entry.Id, modsFolder, entry.Version);
            return Path.Combine(modsFolder, entry.Id);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>
    /// Unpacks <paramref name="zip"/> as mod <paramref name="id"/> in <paramref name="modsFolder"/>, in place of the one
    /// there: its files are written to a new folder first, then that's swapped in, so a broken zip leaves the old one.
    /// The zip may hold the mod's folder or just its files. A file that would land outside the mod's folder stops it.
    /// The mod stays off or on as it was (on when it's new), and its mod.json gets <paramref name="version"/>.
    /// </summary>
    public static void Install(ZipArchive zip, string id, string modsFolder, string version = null)
    {
        if (!IsFolderName(id)) throw new InvalidDataException($"{id}: not a mod folder name");
        string dest = Path.Combine(modsFolder, id);
        string fresh = Path.Combine(modsFolder, $".{id}.new"), old = Path.Combine(modsFolder, $".{id}.old");
        foreach (var dir in new[] { fresh, old }) if (Directory.Exists(dir)) Directory.Delete(dir, true);
        // A zip of the folder ("WideScreenMod/mod.json", …) or of its files ("mod.json", …).
        var files = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        string prefix = files.Any(e => e.FullName.Replace('\\', '/') == "mod.json") ? "" :
            files.Select(e => e.FullName.Replace('\\', '/')).FirstOrDefault(n => n.EndsWith("/mod.json") && n.Count(c => c == '/') == 1) is { } top
                ? top[..(top.Length - "mod.json".Length)] : null;
        if (prefix == null) throw new InvalidDataException($"{id}: the zip has no mod.json");
        string root = Path.GetFullPath(fresh) + Path.DirectorySeparatorChar;
        try
        {
            foreach (var e in files)
            {
                string name = e.FullName.Replace('\\', '/');
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string to = Path.GetFullPath(Path.Combine(fresh, name[prefix.Length..]));
                if (!to.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException($"{id}: the zip's {e.FullName} would land outside the mod's folder");
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                e.ExtractToFile(to, overwrite: true);
            }
            // As it was: a mod turned off stays off through an update.
            bool enabled = GameMods.ReadForEdit(Path.Combine(dest, "mod.json"))["enabled"]?.GetValueKind() != JsonValueKind.False;
            string map = Path.Combine(fresh, "mod.json");
            var json = GameMods.ReadForEdit(map);
            json["enabled"] = enabled;
            if (!string.IsNullOrEmpty(version)) json["version"] = version;
            GameMods.WriteEdited(map, json);
            if (Directory.Exists(dest)) Directory.Move(dest, old);
            Directory.Move(fresh, dest);
            if (Directory.Exists(old)) Directory.Delete(old, true);
        }
        catch
        {
            if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
            if (!Directory.Exists(dest) && Directory.Exists(old)) Directory.Move(old, dest);
            throw;
        }
    }

    /// <summary>
    /// Whether <paramref name="available"/> is newer than <paramref name="installed"/>: compared as versions
    /// ("1.10.0" after "1.9.2") when both read as one, else when they differ at all.
    /// </summary>
    public static bool IsNewer(string available, string installed)
    {
        if (string.IsNullOrEmpty(available)) return false;
        if (string.IsNullOrEmpty(installed)) return true;
        return Version.TryParse(available.TrimStart('v'), out var a) && Version.TryParse(installed.TrimStart('v'), out var b)
            ? a > b : !string.Equals(available, installed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Zips the mod folder <paramref name="folder"/> into <paramref name="zipFile"/> (its files under the mod's folder name)
    /// and gives its mods.json entry, with the zip's address relative to the mods.json in <paramref name="indexFolder"/>.
    /// </summary>
    public static JsonObject Pack(string folder, string zipFile, string indexFolder)
    {
        string id = Path.GetFileName(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipFile)));
        if (File.Exists(zipFile)) File.Delete(zipFile);
        using (var zip = ZipFile.Open(zipFile, ZipArchiveMode.Create))
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                zip.CreateEntryFromFile(file, $"{id}/{Path.GetRelativePath(folder, file).Replace('\\', '/')}", CompressionLevel.Optimal);
        string sha;
        using (var f = File.OpenRead(zipFile)) sha = Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
        var mod = GameMods.ReadForEdit(Path.Combine(folder, "mod.json"));
        string Str(string name, string fallback) => mod[name]?.GetValueKind() == JsonValueKind.String ? mod[name].GetValue<string>() : fallback;
        var probe = new GameMods.Mod(id, folder, true, JsonDocument.Parse(mod.ToJsonString()));
        return new JsonObject
        {
            ["id"] = id,
            ["name"] = Str("name", id),
            ["category"] = Str("category", CategoryOf(probe)),
            ["version"] = Str("version", "1.0.0"),
            ["description"] = Str("description", ""),
            ["zip"] = Path.GetRelativePath(indexFolder, zipFile).Replace('\\', '/'),
            ["sha256"] = sha,
            ["size"] = new FileInfo(zipFile).Length,
        };
    }
}
