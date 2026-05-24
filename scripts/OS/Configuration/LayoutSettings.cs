using System.Collections.Generic;
using Godot;

public readonly struct LayoutSettings(Vector2 position, Vector2 size, bool isOpen)
{
    public readonly Vector2 Position = position;
    public readonly Vector2 Size = size;
    public readonly bool IsOpen = isOpen;
    
    public static void SavePanelLayouts(IEnumerable<FloatingPanel> panels)
    {
        var cfg = new ConfigFile();
        foreach (var panel in panels)
        {
            cfg.SetValue(panel.PanelId, SettingKeys.Layout.X, panel.Position.X);
            cfg.SetValue(panel.PanelId, SettingKeys.Layout.Y, panel.Position.Y);
            cfg.SetValue(panel.PanelId, SettingKeys.Layout.Width, panel.Size.X);
            cfg.SetValue(panel.PanelId, SettingKeys.Layout.Height, panel.Size.Y);
            cfg.SetValue(panel.PanelId, SettingKeys.Layout.IsOpen, panel.IsOpen);
        }
        cfg.Save(SettingKeys.LayoutPath);
    }
    
    public static bool TryLoadPanelLayout(string panelId, out LayoutSettings layout)
    {
        layout = default;
        var cfg = new ConfigFile();

        if (cfg.Load(SettingKeys.LayoutPath) != Error.Ok)
        {
            StatusBar.SetStatus($"Layout config loading failed.");
            return false;
        }
        if (!cfg.HasSection(panelId))
        {
            StatusBar.SetStatus($"Panel {panelId} has no entry");
            return false;
        }

        layout = new LayoutSettings(
            position: new Vector2(
                (float)cfg.GetValue(panelId, SettingKeys.Layout.X), 
                (float)cfg.GetValue(panelId, SettingKeys.Layout.Y)
            ),
            size: new Vector2(
                (float)cfg.GetValue(panelId, SettingKeys.Layout.Width), 
                (float)cfg.GetValue(panelId, SettingKeys.Layout.Height)
            ),
            isOpen: (bool)cfg.GetValue(panelId, SettingKeys.Layout.IsOpen, true)
        );
        return true;
    }
    
    public static void ResetPanelLayouts()
    {
        using var dir = DirAccess.Open(SettingKeys.UserDir);
        if (dir != null && dir.FileExists(SettingKeys.LayoutPath)) 
            dir.Remove(SettingKeys.LayoutPath);
            
    }
}