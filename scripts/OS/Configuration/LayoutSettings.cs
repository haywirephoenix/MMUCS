using System.Collections.Generic;
using Godot;

public readonly struct LayoutSettings(Vector2 position, Vector2 size, bool isOpen)
{
    private static bool LayoutsMarkedForReset = false;
    public readonly Vector2 Position = position;
    public readonly Vector2 Size = size;
    public readonly bool IsOpen = isOpen;
    
    
    public static void SavePanelLayouts(IEnumerable<FloatingPanel> panels)
    {
        var cfg = new ConfigFile();
    
        float currentScale = Engine.GetMainLoop() is SceneTree tree 
            ? tree.Root.ContentScaleFactor 
            : 1.0f;

        foreach (var panel in panels)
        {
            Vector2 normalizedPos = panel.Position / currentScale;
            Vector2 normalizedSize = panel.Size / currentScale;

            cfg.SetValue(panel.PanelId, SettingKeys.Layout.X, normalizedPos.X);
            cfg.SetValue(panel.PanelId, SettingKeys.Layout.Y, normalizedPos.Y);
            cfg.SetValue(panel.PanelId, SettingKeys.Layout.Width, normalizedSize.X);
            cfg.SetValue(panel.PanelId, SettingKeys.Layout.Height, normalizedSize.Y);
            cfg.SetValue(panel.PanelId, SettingKeys.Layout.IsOpen, panel.IsOpen);
        }
        cfg.Save(SettingKeys.LayoutPath);
    }
    
    public static bool TryLoadPanelLayout(string panelId, out LayoutSettings layout)
    {
        layout = default;
        var cfg = new ConfigFile();
        if (LayoutsMarkedForReset) return false;

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

        // Grab the current scale factor
        float currentScale = Engine.GetMainLoop() is SceneTree tree 
            ? tree.Root.ContentScaleFactor 
            : 1.0f;

        // Read the normalized values
        Vector2 savedPos = new Vector2(
            (float)cfg.GetValue(panelId, SettingKeys.Layout.X), 
            (float)cfg.GetValue(panelId, SettingKeys.Layout.Y)
        );
        Vector2 savedSize = new Vector2(
            (float)cfg.GetValue(panelId, SettingKeys.Layout.Width), 
            (float)cfg.GetValue(panelId, SettingKeys.Layout.Height)
        );

        // Inflate them matching the active scale factor
        layout = new LayoutSettings(
            position: savedPos * currentScale,
            size: savedSize * currentScale,
            isOpen: (bool)cfg.GetValue(panelId, SettingKeys.Layout.IsOpen, true)
        );
    
        return true;
    }
    
    public static void ResetPanelLayouts()
    {
        LayoutsMarkedForReset = true;
        using var dir = DirAccess.Open(SettingKeys.UserDir);
        if (dir != null && dir.FileExists(SettingKeys.LayoutPath)) 
        {
            dir.Remove(SettingKeys.LayoutPath);
        }
    }
}