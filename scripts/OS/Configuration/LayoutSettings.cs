using System.Collections.Generic;
using Godot;

public readonly struct LayoutSettings(Vector2 position, Vector2 size, bool isOpen)
{
    private static bool LayoutsMarkedForReset = false;
    public readonly Vector2 Position = position;
    public readonly Vector2 Size = size;
    public readonly bool IsOpen = isOpen;

    public const float RefWidth  = 1920f;
    public const float RefHeight = 1080f;
    
    private static ConfigFile _layoutCache;
    
    

    public void ApplyTo(FloatingPanel panel, bool invokeOpen, bool tween = true)
    {
        // float scale = GetCurrentScale();
        var pos = Position;
        var size = Size;
        if (!tween)
        {
            
            panel.Position = pos;
            panel.Size = size;
        }
        else
        {
            panel.TweenPositionSize(pos, size);
        }
        
        if (invokeOpen)
            panel.Open(IsOpen, tween);
        else
            panel.SetIsOpenNoEvent(IsOpen);
    }

    private static float GetCurrentScale()
    {
        float guiScale = ConfigManager.AppSettings.GuiScale;
        float dpiScale = ConfigManager.AppSettings.HiDPIEnabled 
            ? DisplayServer.ScreenGetMaxScale() 
            : 1.0f;
        return dpiScale * guiScale;
    }
    
    public static LayoutSettings ScaleToCanvas(LayoutSettings layout, Vector2 canvasSize)
    {
        float scaleX = canvasSize.X / RefWidth;
        float scaleY = canvasSize.Y / RefHeight;
        
        if (Mathf.Abs(scaleX - 1.0f) < 0.05f && Mathf.Abs(scaleY - 1.0f) < 0.05f)
            return layout;

        return new LayoutSettings(
            position: new Vector2(layout.Position.X * scaleX, layout.Position.Y * scaleY),
            size:     new Vector2(layout.Size.X     * scaleX, layout.Size.Y     * scaleY),
            isOpen:   layout.IsOpen
        );
    }

    private static bool _layoutApplied;
    
    private static bool TryReadUserLayout(ConfigFile cfg, string section, out LayoutSettings layout)
    {
        layout = default;
        if (!cfg.HasSection(section)) return false;

        float scale = GetCurrentScale();

        Vector2 rawPos = new Vector2(
            (float)cfg.GetValue(section, SettingKeys.Layout.X),
            (float)cfg.GetValue(section, SettingKeys.Layout.Y)
        );
        Vector2 rawSize = new Vector2(
            (float)cfg.GetValue(section, SettingKeys.Layout.Width),
            (float)cfg.GetValue(section, SettingKeys.Layout.Height)
        );

        layout = new LayoutSettings(
            position: Mathf.IsEqualApprox(scale, 1.0f) ? rawPos  : rawPos  * scale,
            size:     Mathf.IsEqualApprox(scale, 1.0f) ? rawSize : rawSize * scale,
            isOpen:   (bool)cfg.GetValue(section, SettingKeys.Layout.IsOpen, true)
        );
        return true;
    }

    private static bool TryReadTemplateLayout(ConfigFile cfg, string section, out LayoutSettings layout)
    {
        layout = default;
        if (!cfg.HasSection(section)) return false;

   
        layout = new LayoutSettings(
            position: new Vector2(
                (float)cfg.GetValue(section, SettingKeys.Layout.X),
                (float)cfg.GetValue(section, SettingKeys.Layout.Y)
            ),
            size: new Vector2(
                (float)cfg.GetValue(section, SettingKeys.Layout.Width),
                (float)cfg.GetValue(section, SettingKeys.Layout.Height)
            ),
            isOpen: (bool)cfg.GetValue(section, SettingKeys.Layout.IsOpen, true)
        );
        return true;
    }
    
    /*
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
    */
    public static bool TryLoadTemplateLayout(string templateName, string panelId, out LayoutSettings layout)
    {
        layout = default;
        return TryLoadTemplate(templateName, out ConfigFile cfg) &&
            TryReadTemplateLayout(cfg, panelId, out layout); // uses template reader
    }

    public static void SavePanelLayouts(IEnumerable<FloatingPanel> panels)
    {
        _layoutCache = null;
        var cfg = new ConfigFile();
        float scale = GetCurrentScale();

        cfg.SetValue(SettingKeys.Layout.Global, SettingKeys.Layout.LayoutMode, LayoutModes.CurrentLayoutMode);

        foreach (var panel in panels)
        {
            Vector2 normPos  = Mathf.IsEqualApprox(scale, 1.0f) ? panel.Position : panel.Position / scale;
            Vector2 normSize = Mathf.IsEqualApprox(scale, 1.0f) ? panel.Size     : panel.Size     / scale;

            cfg.SetValue(panel.PanelId, SettingKeys.Layout.X,      normPos.X);
            cfg.SetValue(panel.PanelId, SettingKeys.Layout.Y,      normPos.Y);
            cfg.SetValue(panel.PanelId, SettingKeys.Layout.Width,  normSize.X);
            cfg.SetValue(panel.PanelId, SettingKeys.Layout.Height, normSize.Y);
            cfg.SetValue(panel.PanelId, SettingKeys.Layout.IsOpen, panel.IsOpen);
        }

        cfg.Save(SettingKeys.LayoutPath);
    }
    
    private static bool defaultTemplateApplied;
    

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

            if (!_layoutApplied)
            {
                string layoutMode = (string)_layoutCache.GetValue(SettingKeys.Layout.Global,
                    SettingKeys.Layout.LayoutMode, SettingKeys.Layout.Unified);
                LayoutModes.SetLayoutMode(LayoutModes.StringToLayoutMode(layoutMode));
                _layoutApplied = true;
            }
        }

        if (!TryReadUserLayout(_layoutCache, panelId, out layout))
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
        
        Error result = cfg.Load(path);
    
        if (result != Error.Ok)
        {
            StatusBar.SetStatus($"Template '{templateName}' not found or failed to load. Error: {result}");
            return false;
        }

        return true;
    }

    public static bool TryApplyTemplate(ConfigFile cfg, string panelId, out LayoutSettings layout) =>
        TryReadTemplateLayout(cfg, panelId, out layout);

    public static void ResetPanelLayouts()
    {
        _layoutCache = null;
        _layoutApplied = false;
        LayoutsMarkedForReset = true;
        using var dir = DirAccess.Open(SettingKeys.UserDir);
        if (dir != null && dir.FileExists(SettingKeys.LayoutPath))
            dir.Remove(SettingKeys.LayoutPath);
    }
}