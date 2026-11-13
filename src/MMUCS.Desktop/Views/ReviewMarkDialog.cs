using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using MMUCS.Core;

namespace MMUCS.Desktop.Views;

/// <summary>
/// "Mark for review": what looks wrong about a costume or object (optional tags) and a note. The mark
/// is reviewed in full whatever it's tagged with; the tags say what was seen.
/// </summary>
public sealed class ReviewMarkDialog : Window
{
    public sealed record Result(IReadOnlyList<string> Tags, string Note);

    private readonly List<(string Tag, CheckBox Box)> _boxes = new();
    private readonly TextBox _note;
    private Result _result;

    private ReviewMarkDialog(string name, ReviewMark existing)
    {
        Title = "Mark for review";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = name, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock
        {
            Text = "What looks wrong? Optional: mroom review shows everything MMUCS works out for it either way.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 11, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 4),
        });
        foreach (var (tag, label) in ReviewMarks.Tags)
        {
            var box = new CheckBox { Content = label, IsChecked = existing?.Tags.Contains(tag) == true };
            _boxes.Add((tag, box));
            panel.Children.Add(box);
        }
        _note = new TextBox
        {
            Watermark = "Note: what you expected, e.g. \"should be behind the fence\"",
            Text = existing?.Note ?? "", AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 60, Margin = new Thickness(0, 6, 0, 0),
        };
        panel.Children.Add(_note);

        var ok = new Button { Content = existing == null ? "Mark" : "Save", IsDefault = true, MinWidth = 72 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 72 };
        ok.Click += (_, _) =>
        {
            _result = new Result(_boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Tag).ToList(), _note.Text);
            Close();
        };
        cancel.Click += (_, _) => Close();
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0), Children = { cancel, ok },
        });
        Content = panel;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>Shows the dialog; null when cancelled.</summary>
    public static async Task<Result> Ask(Window owner, string name, ReviewMark existing)
    {
        var dialog = new ReviewMarkDialog(name, existing);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
