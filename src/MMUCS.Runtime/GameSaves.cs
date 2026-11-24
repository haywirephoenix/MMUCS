using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MMUCS.Core;
using MMUCS.Core.Diagnostics;
using MMUCS.Runtime.Scripts;
using MMUCS.Runtime.Sound;

namespace MMUCS.Runtime;

/// <summary>
/// A game's saves folder (under the app's data, one per game): the F1 menu's numbered slots, each a save file
/// with its name in the header, and the registry settings the game keeps (the menu's page, the volumes, the
/// text speed), which the original kept in the Windows registry. <see cref="Attach"/> hands them to an engine.
/// </summary>
public sealed class GameSaves(string folder)
{
    private Dictionary<string, int> _registry;

    public string Folder { get; } = folder ?? throw new ArgumentNullException(nameof(folder));

    /// <summary>The saves folder for a game file: <c>saves/&lt;game&gt;</c> under the app's data.</summary>
    public static string FolderFor(string gameFile) =>
        Path.Combine(CorePaths.Root, "saves", Path.GetFileNameWithoutExtension(gameFile).ToLowerInvariant());

    public string SlotPath(int slot) => Path.Combine(Folder, $"slot-{slot:000}.mmucs-save");

    /// <summary>A slot's name, or null when the slot is empty.</summary>
    public string SlotName(int slot)
    {
        string path = SlotPath(slot);
        return File.Exists(path) ? ScriptedGame.ReadSaveName(path) ?? "" : null;
    }

    /// <summary>A slot's thumbnail (a PNG), or null when the slot is empty or its save has none.</summary>
    public byte[] SlotThumbnail(int slot)
    {
        string path = SlotPath(slot);
        return File.Exists(path) ? ScriptedGame.ReadSaveHeader(path)?.Thumbnail : null;
    }

    private string RegistryPath => Path.Combine(Folder, "registry.json");

    /// <summary>A registry setting the game wrote, or null when it hasn't.</summary>
    public int? Registry(string name)
    {
        _registry ??= LoadRegistry();
        return _registry.TryGetValue(name, out int value) ? value : null;
    }

    public void SetRegistry(string name, int value)
    {
        _registry ??= LoadRegistry();
        if (_registry.TryGetValue(name, out int old) && old == value) return;
        _registry[name] = value;
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(RegistryPath, JsonSerializer.Serialize(_registry, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Game registry: {ex.Message}");
        }
    }

    private Dictionary<string, int> LoadRegistry()
    {
        try
        {
            if (File.Exists(RegistryPath))
                return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(RegistryPath)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"Game registry: {ex.Message}");
        }
        return [];
    }

    /// <summary>
    /// Gives the engine the slots and the registry: the menu reads the slots' names, writes the F1 snapshot
    /// with the sounds and thumbnail as they were then (<paramref name="sounds"/>), and loads a slot with its sounds.
    /// </summary>
    public void Attach(FrameLoop loop, ScriptedGame.SnapshotExtras sounds, SoundEffects effects, VoicePlayer voice, ComiMusic music)
    {
        var vm = loop.Vm;
        vm.ReadRegistry = Registry;
        vm.WriteRegistry = SetRegistry;
        vm.SaveGameName = SlotName;
        vm.SaveGameWrite = (slot, name) =>
        {
            string path = SlotPath(slot), temp = path + ".new";
            try
            {
                Directory.CreateDirectory(Folder);
                using (var file = File.Create(temp))
                    ScriptedGame.WriteSave(file, vm.TemporaryState, sounds?.Music, sounds?.Speech, sounds?.Effects, name, sounds?.Thumbnail);
                File.Move(temp, path, overwrite: true);
                Log.Info($"Game: saved \"{name}\" in slot {slot} ({path})");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Log.Error($"Game: saving slot {slot}: {ex.Message}");
                return false;
            }
        };
        loop.LoadSlot = slot =>
        {
            string path = SlotPath(slot);
            if (!File.Exists(path)) return false;
            try
            {
                var missing = new List<string>();
                using (var file = File.OpenRead(path))
                    ScriptedGame.ReadSave(file, loop, effects, voice, music, missing);
                foreach (var what in missing) Log.Warn($"Game: loading slot {slot}: {what} isn't there any more");
                Log.Info($"Game: loaded slot {slot} \"{ScriptedGame.ReadSaveName(path)}\" into room {vm.CurrentRoom}");
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
            {
                Log.Error($"Game: loading slot {slot}: {ex.Message}");
                return false;
            }
        };
    }
}
