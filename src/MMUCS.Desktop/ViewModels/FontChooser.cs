using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MMUCS.Player.Text;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Fonts;
using MMUCS.Desktop.Rendering;
using MMUCS.Desktop.Services;

namespace MMUCS.Desktop.ViewModels;

/// <summary>A font for the game's text: the game's, a font folder or a typeface file.</summary>
public sealed record FontChoice(SubtitleFontKind Kind, string Path, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The font the game's text is drawn in, shared by the windows that draw it (the media player's cutscene
/// text, the game window's labels; the toolbar's Controls/FontPicker): the game's NUT fonts, the typefaces
/// shipped with MMUCS, or a font folder or file picked. The choice is kept in the settings.
/// </summary>
public sealed partial class FontChooser : ObservableObject
{
    private readonly AppSettings _settings;

    public FontChooser(AppSettings settings)
    {
        _settings = settings;
        Choices.Add(new FontChoice(SubtitleFontKind.Game, null, "Game fonts (NUT)"));
        string bundled = Path.Combine(AppContext.BaseDirectory, "fonts");
        if (Directory.Exists(bundled))
        {
            foreach (var file in Directory.EnumerateFiles(bundled, "*.*", SearchOption.AllDirectories)
                         .Where(IsTypefaceFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                Choices.Add(TypefaceChoice(file));
        }
        var selected = Choices[0];
        if (settings != null)
        {
            string path = settings.SubtitleFontPath;
            if (settings.SubtitleFontKind == "folder" && Directory.Exists(path)) selected = AddChoice(FolderChoice(path));
            else if (settings.SubtitleFontKind == "typeface" && File.Exists(path)) selected = AddChoice(TypefaceChoice(path));
        }
        _loading = true;
        Selected = selected;
        _loading = false;
    }

    private readonly bool _loading;

    public ObservableCollection<FontChoice> Choices { get; } = [];

    [ObservableProperty] public partial FontChoice Selected { get; set; }

    /// <summary>The choice changed: renderers made from it should be made again.</summary>
    public event Action Changed;

    public static bool IsTypefaceFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc";

    private static FontChoice FolderChoice(string path) =>
        new(SubtitleFontKind.Folder, path, $"Folder: {Path.GetFileName(Path.TrimEndingDirectorySeparator(path))}");

    private static FontChoice TypefaceChoice(string path) =>
        new(SubtitleFontKind.Typeface, path, $"Typeface: {Path.GetFileNameWithoutExtension(path)}");

    private FontChoice AddChoice(FontChoice choice)
    {
        var same = Choices.FirstOrDefault(c => c.Kind == choice.Kind && string.Equals(c.Path, choice.Path, StringComparison.Ordinal));
        if (same != null) return same;
        Choices.Add(choice);
        return choice;
    }

    /// <summary>Draws the text with an unpacked font folder (an export's font/NNNN, edited or not).</summary>
    public void UseFontFolder(string path) => Selected = AddChoice(FolderChoice(path));

    /// <summary>Draws the text with a TrueType/OpenType file.</summary>
    public void UseTypeface(string path) => Selected = AddChoice(TypefaceChoice(path));

    partial void OnSelectedChanged(FontChoice value)
    {
        if (value == null) return;
        if (_settings != null && !_loading)
        {
            _settings.SubtitleFontKind = value.Kind switch { SubtitleFontKind.Folder => "folder", SubtitleFontKind.Typeface => "typeface", _ => "game" };
            if (value.Path != null) _settings.SubtitleFontPath = value.Path;
            _settings.Save();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// A renderer for the chosen font over <paramref name="gameFonts"/>, with what it draws in
    /// (<paramref name="info"/>); the game fonts when the choice can't be used.
    /// </summary>
    public SubtitleRenderer CreateRenderer(GlyphFont[] gameFonts, bool subtitles, out string info)
    {
        var choice = Selected ?? Choices[0];
        try
        {
            var renderer = SubtitleRenderer.Create(gameFonts, choice.Kind, choice.Path, subtitles);
            info = $"Text: {renderer.Description}";
            return renderer;
        }
        catch (Exception ex)
        {
            info = $"Couldn't use {choice.Label}: {ex.Message}; showing the game fonts";
            Log.Warn($"Font {choice.Path}: {ex.Message}");
            return SubtitleRenderer.Create(gameFonts, SubtitleFontKind.Game, null, subtitles);
        }
    }
}
