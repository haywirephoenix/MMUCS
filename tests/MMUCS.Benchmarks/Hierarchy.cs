using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.Converters;
using MMUCS.Desktop.ViewModels;

namespace MMUCS.Benchmarks;

/// <summary>
/// The block hierarchy: what each row computes, and unfolding the largest room in a tree view with the
/// hierarchy's row layout (Avalonia's tree view creates every child row on unfold, on screen or not).
/// </summary>
public static class Hierarchy
{
    public static void Run() => Host.Run(async () =>
    {
        var options = new TreeOptions();
        var all = new List<ScummBlock>();
        void Walk(ScummBlock b) { all.Add(b); foreach (var c in b.Children) Walk(c); }
        Walk(Host.Root);
        var big = Host.Root.FindChildrenRecursive(ScummTag.LFLF).OrderByDescending(l => l.Children.Count).First();
        Console.WriteLine($"{all.Count:N0} blocks; largest room {BlockNodeViewModel.LabelFor(big, options)} with {big.Children.Count} children");

        var nodes = all.Select(b => new BlockNodeViewModel(b, options)).ToList();
        string s;
        object o;
        double PerRow(Action<BlockNodeViewModel> f)
        {
            var sw = Stopwatch.StartNew();
            foreach (var n in nodes) f(n);
            return sw.Ms() * 1000 / nodes.Count;
        }
        PerRow(n => s = n.Label); // warm
        Console.WriteLine($"per row, µs: label {PerRow(n => s = n.Label):0.00}, colour key {PerRow(n => s = n.TagBrushKey):0.00}, " +
                          $"colour lookup {PerRow(n => o = ThemeResourceConverter.Instance.Convert(n.TagBrushKey, typeof(IBrush), null, null)):0.00}, " +
                          $"tooltip {PerRow(n => s = n.ToolTip):0.00}, size {PerRow(n => s = n.SizeText):0.00}");

        foreach (bool colour in new[] { false, true, false, true })
        {
            var tree = new TreeView
            {
                ItemTemplate = new FuncTreeDataTemplate<BlockNodeViewModel>((n, _) =>
                {
                    var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                    ToolTip.SetTip(g, n.ToolTip);
                    var label = new TextBlock { Text = n.Label, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
                    if (colour && n.HasTagColor)
                        label.Foreground = (IBrush)ThemeResourceConverter.Instance.Convert(n.TagBrushKey, typeof(IBrush), null, null);
                    g.Children.Add(label);
                    var size = new TextBlock { Text = n.SizeText, FontSize = 11, Margin = new Thickness(8, 0, 4, 0) };
                    Grid.SetColumn(size, 1);
                    g.Children.Add(size);
                    return g;
                }, n => n.Children),
                ItemsSource = new[] { new BlockNodeViewModel(big, options) },
            };
            var window = new Window { Width = 400, Height = 900, Content = tree };
            window.Show();
            window.CaptureRenderedFrame();
            var sw = Stopwatch.StartNew();
            var roomRow = (TreeViewItem)tree.ContainerFromIndex(0);
            roomRow.IsExpanded = true;
            window.CaptureRenderedFrame();
            Console.WriteLine($"unfold {big.Children.Count} rows, colour {(colour ? "on " : "off")}: {sw.Ms():0} ms to the next frame, " +
                              $"{roomRow.GetRealizedContainers().Count()} rows created");
            window.Close();
            await Task.Delay(50);
        }
    });
}
