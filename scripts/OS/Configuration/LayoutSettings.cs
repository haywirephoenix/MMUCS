using System.Collections.Generic;
using Godot;

public readonly struct LayoutSettings(Vector2 position, Vector2 size, bool isOpen)
{
    private static bool LayoutsMarkedForReset = false;
    public readonly Vector2 Position = position;
    public readonly Vector2 Size = size;
    public readonly bool IsOpen = isOpen;
    
    private static ConfigFile _layoutCache;
    
    

    public void ApplyTo(FloatingPanel panel, bool invokeOpen, bool tween = true)
    {
        if (!tween)
        {
            panel.Position = Position;
            panel.Size = Size;
        }
        else
        {
            panel.TweenPositionSize(Position, Size);
        }
        
        if (invokeOpen)
            panel.Open(IsOpen, true);
        else
            panel.SetIsOpenNoEvent(IsOpen);
    }

    private static float GetCurrentScale() =>
        Engine.GetMainLoop() is SceneTree tree ? tree.Root.ContentScaleFactor : 1.0f;

    private static bool _layoutApplied;

    private static bool TryReadLayout(ConfigFile cfg, string section, out LayoutSettings layout)
    {
        layout = default;

        if (!cfg.HasSection(section))
            return false;

        float scale = GetCurrentScale();

        layout = new LayoutSettings(
            position: new Vector2(
                (float)cfg.GetValue(section, SettingKeys.Layout.X),
                (float)cfg.GetValue(section, SettingKeys.Layout.Y)
            ) * scale,
            size: new Vector2(
                (float)cfg.GetValue(section, SettingKeys.Layout.Width),
                (float)cfg.GetValue(section, SettingKeys.Layout.Height)
            ) * scale,
            isOpen: (bool)cfg.GetValue(section, SettingKeys.Layout.IsOpen, true)
        );

        if (!_layoutApplied)
        {
            string layoutMode = (string)cfg.GetValue(SettingKeys.Layout.Global, 
                SettingKeys.Layout.LayoutMode, SettingKeys.Layout.Unified);
        
            LayoutModes.SetLayoutMode(LayoutModes.StringToLayoutMode(layoutMode));

            _layoutApplied = true;
        }

        

        return true;
    }

    public static void SavePanelLayouts(IEnumerable<FloatingPanel> panels)
    {
        _layoutCache = null;
        var cfg = new ConfigFile();
        float scale = GetCurrentScale();

        cfg.SetValue(SettingKeys.Layout.Global, SettingKeys.Layout.LayoutMode, LayoutModes.CurrentLayoutMode);
        
        foreach (var panel in panels)
        {
            Vector2 normPos  = panel.Position / scale;
            Vector2 normSize = panel.Size / scale;

            cfg.SetValue(panel.PanelId, SettingKeys.Layout.X,      normPos.X);
            cfg.SetValue(panel.PanelId, SettingKeys.Layout.Y,      normPos.Y);
            cfg.SetValue(panel.PanelId, SettingKeys.Layout.Width,  normSize.X);
            cfg.SetValue(panel.PanelId, SettingKeys.Layout.Height, normSize.Y);
            cfg.SetValue(panel.PanelId, SettingKeys.Layout.IsOpen, panel.IsOpen);
        }

        cfg.Save(SettingKeys.LayoutPath);
    }
    
    private static bool defaultTemplateApplied;
    
    public static bool TryLoadTemplateLayout(string templateName, string panelId, out LayoutSettings layout)
    {
        layout = default;
        return TryLoadTemplate(templateName, out ConfigFile cfg) && 
            TryApplyTemplate(cfg, panelId, out layout);
    }

    public static bool TryLoadPanelLayout(string panelId, out LayoutSettings layout)
    {
        layout = default;

        if (LayoutsMarkedForReset) return false;

        if (_layoutCache == null)
        {
            _layoutCache = new ConfigFile();
            if (_layoutCache.Load(SettingKeys.LayoutPath) != Error.Ok)
            {
                _layoutCache = null;
                StatusBar.SetStatus("Layout config loading failed.");
                return false;
            }

            if (!_layoutCache.HasSection(SettingKeys.Layout.Global))
            {
                ResetPanelLayouts();
                return false;
            }
        }

        if (!TryReadLayout(_layoutCache, panelId, out layout))
        {
            StatusBar.SetStatus($"Panel {panelId} has no entry.");
            return false;
        }

        return true;
    }

    public static bool TryLoadTemplate(string templateName, out ConfigFile cfg)
    {
        cfg = new ConfigFile();
        string path = FileUtils.CombinePaths(SettingKeys.TemplateLayoutsDir, $"{templateName}.cfg");

        if (!FileUtils.FileExists(path) || cfg.Load(path) != Error.Ok)
        {
            StatusBar.SetStatus($"Template '{templateName}' not found.");
            return false;
        }

        return true;
    }

    public static bool TryApplyTemplate(ConfigFile cfg, string panelId, out LayoutSettings layout) =>
        TryReadLayout(cfg, panelId, out layout);

    public static void ResetPanelLayouts()
    {
        _layoutCache = null;
        LayoutsMarkedForReset = true;
        using var dir = DirAccess.Open(SettingKeys.UserDir);
        if (dir != null && dir.FileExists(SettingKeys.LayoutPath))
            dir.Remove(SettingKeys.LayoutPath);
    }
}