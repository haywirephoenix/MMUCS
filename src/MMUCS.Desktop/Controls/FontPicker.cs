using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using MMUCS.Desktop.ViewModels;

namespace MMUCS.Desktop.Controls;

/// <summary>
/// The font controls the viewers' toolbars share (media player, game window): a list of the fonts the
/// game's text can be drawn in, and Folder… / Font file… to add one. Acts on its <see cref="Chooser"/>.
/// </summary>
public sealed class FontPicker : StackPanel
{
    public static readonly StyledProperty<FontChooser> ChooserProperty =
        AvaloniaProperty.Register<FontPicker, FontChooser>(nameof(Chooser));

    public FontChooser Chooser { get => GetValue(ChooserProperty); set => SetValue(ChooserProperty, value); }

    public FontPicker()
    {
        Orientation = Orientation.Horizontal;
        var list = new ComboBox
        {
            Width = 190, Margin = new Thickness(0, 0, 4, 0),
        };
        list.Bind(ItemsControl.ItemsSourceProperty, new Binding($"{nameof(Chooser)}.{nameof(FontChooser.Choices)}") { Source = this });
        ToolTip.SetTip(list, "The font the game's text is drawn in: its NUT fonts, an unpacked font folder (Export ▸ Fonts, edited or not) or a TrueType/OpenType file");
        list.Bind(SelectingItemsControl.SelectedItemProperty, new Binding($"{nameof(Chooser)}.{nameof(FontChooser.Selected)}") { Source = this, Mode = BindingMode.TwoWay });

        var folder = new Button { Content = "Folder…", Margin = new Thickness(0, 0, 4, 0) };
        ToolTip.SetTip(folder, "Use a font folder: an export's font/NNNN folders (or the export root), with edited or replaced glyph PNGs");
        folder.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Font folder (font/NNNN, or an export holding font/)" });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) Chooser?.UseFontFolder(path);
        };

        var file = new Button { Content = "Font file…" };
        ToolTip.SetTip(file, "Use a TrueType/OpenType font, sized to each game font and coloured as the game colours it");
        file.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Font file",
                FileTypeFilter = [new FilePickerFileType("Fonts") { Patterns = ["*.ttf", "*.otf", "*.ttc"] }],
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) Chooser?.UseTypeface(path);
        };

        Children.Add(list);
        Children.Add(folder);
        Children.Add(file);
    }
}
