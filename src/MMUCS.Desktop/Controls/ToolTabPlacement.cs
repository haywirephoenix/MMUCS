using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Dock.Avalonia.Controls;

namespace MMUCS.Desktop.Controls;

/// <summary>
/// Puts tool panel tabs along the top, like the document tabs. The Fluent theme docks the tab strip
/// at the bottom in its template, and template values outrank app styles, so it's moved here once
/// the template is applied.
/// </summary>
public static class ToolTabPlacement
{
    public static void Install() =>
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<ToolControl>((control, e) =>
        {
            if (e.NameScope.Find<ToolTabStrip>("PART_TabStrip") is { } strip)
                DockPanel.SetDock(strip, Avalonia.Controls.Dock.Top);
        });
}
