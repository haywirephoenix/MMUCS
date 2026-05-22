using System.Collections.Generic;
using Godot;

public partial class WindowManager : Node
{
    public readonly struct PanelLayout(Vector2 position, Vector2 size, bool visible)
    {
        public readonly Vector2 Position = position;
        public readonly Vector2 Size = size;
        public readonly bool Visible = visible;
    }
    
    public enum DockZone { None, Left, Right } //Top, Bottom
    
    public const float DefaultThickness = 260f;
    public const float SnapDistance     = 1f;
    public const float DockHoldTime  = 0.1f;
    private static DockZone _hoverZone    = DockZone.None;
    private static ulong    _hoverStartMs = 0;
    private static int _previewInsertIndex = -1;
    
    private static readonly Dictionary<DockZone, List<FloatingPanel>> _zones = new()
    {
        { DockZone.Left,   new() },
        { DockZone.Right,  new() },
        // { DockZone.Top,    new() },
        // { DockZone.Bottom, new() },
    };
        
    private static readonly List<FloatingPanel> _registeredPanels = new();
    private static Control _canvas;
    private static Panel   _preview;
   
    
    private static WindowManager Instance;

    public override void _EnterTree()
    {
        Instance = this;
    }
    
    public override void _ExitTree()
    {
        if (IsInstanceValid(_canvas)) _canvas.Resized -= _OnCanvasResized;
        _canvas = null;

        if (IsInstanceValid(_preview)) _preview.QueueFree();
        _preview = null;

        foreach (var list in _zones.Values) list.Clear();
        _registeredPanels.Clear();

        _hoverZone    = DockZone.None;
        _hoverStartMs = 0;
    
        Instance = null;
    }
    
    public static void RegisterPanel(FloatingPanel panel)
    {
        if (Instance == null) { GD.PushWarning("WindowManager not ready"); return; }

        _registeredPanels.Add(panel);
    
        // if (!ConfigManager.TryLoadPanelLayout(panel.PanelId, out PanelLayout layout)) return;

        // var pos = ClampToCanvas(layout.Position, layout.Size, panel.GetParent<Control>());
        // panel.CallDeferred(Control.MethodName.SetPosition, layout.Position);
        // panel.CallDeferred(Control.MethodName.SetSize, layout.Size);
        // panel.Visible = layout.Visible;

        Instance.RegisterCanvas(panel.GetParent<Control>());
    }

    public static void SaveLayout() => ConfigManager.SavePanelLayouts(_registeredPanels);

    private static Vector2 ClampToCanvas(Vector2 pos, Vector2 size, Control canvas)
    {
        if (canvas == null) return pos;
    
        var canvasSize = canvas.Size;
    
        pos.X = Mathf.Clamp(pos.X, 0, Mathf.Max(0, canvasSize.X - size.X));
        pos.Y = Mathf.Clamp(pos.Y, 0, Mathf.Max(0, canvasSize.Y - size.Y));
    
        return pos;
    }
    
    public static void UnregisterPanel(FloatingPanel panel)
    {
        _registeredPanels.Remove(panel);
    }
    
    
    private void RegisterCanvas(Control canvas)
    {
        if (_canvas == canvas) return;
        if (_canvas != null) _canvas.Resized -= _OnCanvasResized;
        _canvas = canvas;
        _canvas.Resized += _OnCanvasResized;
        _BuildPreview();
    }

    private void _BuildPreview()
    {
        _preview?.QueueFree();
        _preview = new Panel
        {
            Name        = "__DockPreview",
            Visible     = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ZIndex      = -1,
        };
        var sb = new StyleBoxFlat { BgColor = new Color(0.25f, 0.55f, 1f, 0.22f) };
        _preview.AddThemeStyleboxOverride("panel", sb);
        _canvas.AddChild(_preview);
    }


    public static void Dock(FloatingPanel panel, DockZone zone)
    {
        panel.SaveFloatingRect();
        _RemoveFrom(panel);
        if (zone == DockZone.None) return;
        var list = _zones[zone];

        if (_previewInsertIndex < 0 ||
            _previewInsertIndex > list.Count)
        {
            list.Add(panel);
        }
        else
        {
            list.Insert(_previewInsertIndex, panel);
        }
        RefreshZoneLayout(zone);
    }

    public static void Undock(FloatingPanel panel)
    {
        var z = ZoneOf(panel);
        _RemoveFrom(panel);
        panel.RestoreFloatingRect();
        if (z != DockZone.None) RefreshZoneLayout(z);
    }

    public static DockZone ZoneOf(FloatingPanel panel)
    {
        foreach (var kv in _zones)
            if (kv.Value.Contains(panel)) return kv.Key;
        return DockZone.None;
    }

    // ── Layout ────────────────────────────────────────────────────────────────
    
    public static Rect2 GetDockedRect(FloatingPanel panel)
    {
        if (_canvas == null || !IsInstanceValid(_canvas)) return default;
        var zone = ZoneOf(panel);
        if (zone == DockZone.None) return default;

        var   list  = _zones[zone];
        int   idx   = list.IndexOf(panel);
        int   count = list.Count;
        float cw    = _canvas.Size.X;
        float ch    = _canvas.Size.Y;
        float thick = panel.DockedThickness;

        return zone switch
        {
            DockZone.Left   => new Rect2(0,            ch / count * idx, thick,        ch / count),
            DockZone.Right  => new Rect2(cw - thick,   ch / count * idx, thick,        ch / count),
            // DockZone.Top    => new Rect2(cw / count * idx, 0,            cw / count,   thick),
            // DockZone.Bottom => new Rect2(cw / count * idx, ch - thick,   cw / count,   thick),
            _               => default,
        };
    }


    public static DockZone UpdatePreview(
        FloatingPanel draggingPanel,
        Vector2 panelPos, 
        Vector2 panelSize,
        Vector2 mousePos,
        float thickness)
    {
        if (_canvas == null || !IsInstanceValid(_canvas)) return DockZone.None;

        Rect2 canvasRect = _canvas.GetRect(); 
        Vector2 canvasSize = canvasRect.Size; 

        DockZone zone = DockZone.None;

        if (mousePos.X <= canvasRect.Position.X + SnapDistance) 
            zone = DockZone.Left;
        else if (mousePos.X >= canvasSize.X - SnapDistance) 
            zone = DockZone.Right;
        // else if (mousePos.Y <= thickness) 
        //     zone = DockZone.Top;
        // else if (mousePos.Y >= canvasSize.Y - thickness) 
        //     zone = DockZone.Bottom;


        if (zone == DockZone.None)
        {
            if (_hoverZone != DockZone.None) { _hoverZone = DockZone.None; HidePreview(); }
            return DockZone.None;
        }

        if (zone != _hoverZone)
        {
            _hoverZone    = zone;
            _hoverStartMs = Time.GetTicksMsec();
            HidePreview();
            return DockZone.None;
        }

        float elapsed = (Time.GetTicksMsec() - _hoverStartMs) / 1000f;
        if (elapsed < DockHoldTime) return DockZone.None;
    
        _ShowPreview(_PreviewRect(zone, draggingPanel, mousePos));
        return zone;
    }
    
    public static void ResetHover()
    {
        _hoverZone = DockZone.None;
        _hoverStartMs = 0;
        _previewInsertIndex = -1;

        HidePreview();
    }

   
    
    public static void OnDockResize(
        FloatingPanel source, 
        DockZone zone, 
        InputEvent @event, 
        Vector2 direction, 
        float newThickness)
    {
        if (zone == DockZone.None) return;

        var list = _zones[zone];
        foreach (var panel in list)
        {
            if (panel == source) continue;
        
            panel.SyncResize(@event, direction, newThickness);
        }
    }
    
    public static bool IsInnerHandle(Vector2 dir, DockZone zone) => zone switch
    {
        DockZone.Left   => dir is { X: > 0, Y: 0 },
        DockZone.Right  => dir is { X: < 0, Y: 0 },
        // DockZone.Top    => dir is { X: 0, Y: > 0 },
        // DockZone.Bottom => dir is { X: 0, Y: < 0 },
        _               => false,
    };
    
    public static void RefreshZoneLayout(DockZone zone)
    {
        if (zone == DockZone.None) return;
        foreach (var p in _zones[zone])
            if (IsInstanceValid(p)) p.ApplyDockedLayout();
    }
    
    
    private void _OnCanvasResized()
    {
        if (Instance == null) return;
        foreach (var zone in _zones.Keys) RefreshZoneLayout(zone);
    }

    private static void _RemoveFrom(FloatingPanel p)
    {
        foreach (var list in _zones.Values) list.Remove(p);
    }

    public static float MouseDeltaToDock(Vector2 mouseDiff, DockZone _currentDock)
        => (_currentDock, mouseDiff) switch
        {
            (DockZone.Left,   var diff) => diff.X,
            (DockZone.Right,  var diff) => -diff.X,
            // (DockZone.Top,    var diff) => diff.Y,
            // (DockZone.Bottom, var diff) => -diff.Y,
            _ => 0f,
        };
    
   

    private static DockZone _CandidateZone(Vector2 pos, Vector2 size)
    {
        Vector2 cs   = _canvas.Size;
        float   dL   = pos.X,              dR = cs.X - (pos.X + size.X);
        float   dT   = pos.Y,              dB = cs.Y - (pos.Y + size.Y);
        float   best = SnapDistance;
        DockZone zone = DockZone.None;
        if (dL < best) { best = dL; zone = DockZone.Left; }
        if (dR < best) { best = dR; zone = DockZone.Right; }
        // if (dT < best) { best = dT; zone = DockZone.Top; }
        // if (dB < best) { best = dB; zone = DockZone.Bottom; }
        return zone;
    }

    private static Rect2 _ZoneFullRect(DockZone zone, float thickness)
    {
        Vector2 cs = _canvas.Size;
        return zone switch
        {
            DockZone.Left   => new Rect2(0,             0, thickness,    cs.Y),
            DockZone.Right  => new Rect2(cs.X-thickness,0, thickness,    cs.Y),
            // DockZone.Top    => new Rect2(0,             0, cs.X,         thickness),
            // DockZone.Bottom => new Rect2(0, cs.Y-thickness, cs.X,        thickness),
            _               => default,
        };
    }
    
    private static Rect2 _PreviewRect(
        DockZone zone,
        FloatingPanel dragging,
        Vector2 mousePos)
    {
        if (_canvas == null || !IsInstanceValid(_canvas) || zone == DockZone.None)
            return default;

        Vector2 cs = _canvas.Size;

        var list = _zones[zone];

        int count = list.Count + 1;

        float thick = dragging.DockedThickness;

        switch (zone)
        {
            case DockZone.Left:
                {
                    float segment = cs.Y / count;

                    _previewInsertIndex = Mathf.Clamp(
                        Mathf.FloorToInt(mousePos.Y / segment),
                        0,
                        count - 1);

                    return new Rect2(
                        0,
                        segment * _previewInsertIndex,
                        thick,
                        segment);
                }

            case DockZone.Right:
                {
                    float segment = cs.Y / count;

                    _previewInsertIndex = Mathf.Clamp(
                        Mathf.FloorToInt(mousePos.Y / segment),
                        0,
                        count - 1);

                    return new Rect2(
                        cs.X - thick,
                        segment * _previewInsertIndex,
                        thick,
                        segment);
                }
        }

        return default;
    }

    private static void _ShowPreview(Rect2 r)
    {
        if (_preview == null || !IsInstanceValid(_preview)) return;
        _preview.Position = r.Position;
        _preview.Size     = r.Size;
        _preview.Visible  = true;
        _preview.ZIndex = 1000;
    }
    
    public static void HidePreview()
    {
        if (_preview == null || !IsInstanceValid(_preview)) return;
        _preview.Visible = false;
    }
    
}