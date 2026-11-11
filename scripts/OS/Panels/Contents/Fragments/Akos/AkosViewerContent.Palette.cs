using System;
using Godot;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public partial class AkosViewerContent : Control
{
    // Tab 3 – Palette
    [Export] public GridContainer _paletteGrid;
    private ImageTexture _paletteTextureCache;
    
    private static readonly NodePath PathPaletteGrid = s_tabsPath + "/Palette/PaletteContainer/PaletteGrid";

    public void SetExternalPalette(Color[] palette)
    {
        _externalPalette = palette;
    }
    private void SetupPaletteSlider(ScummBlock block)
    {
        ScummBlock lflfBlock = block.Parent;

        if (lflfBlock == null)
        {
            GD.PrintErr("LFLF block not found");
            return;
        }
    
        var roomBlock = lflfBlock.FindChild(ScummTag.ROOM);
        var rmhdBlock = roomBlock.FindChild(ScummTag.RMHD);

        if (ScummDecoders.TryGetRoomInfo(rmhdBlock, roomBlock, out var roomInfo))
        {
            int totalPalettes = roomInfo.TotalPalettes;

            int startingIndex = Mathf.Clamp(_akos.DefaultPaletteIndex, 0, Mathf.Max(0, totalPalettes - 1));

            _paletteSlider.Setup(totalPalettes, startingIndex);
            return;
        }
    
        GD.PrintErr("Room info not found");
        _paletteSlider.Reset();
    }
    
    private void OnPaletteIndexChanged(int newIndex)
    {
        if (_akos != null) 
            _RefreshCelPreview();
    }
    
    private void ClearPallette()
    {
        foreach (Node child in _paletteGrid.GetChildren()) child.QueueFree();
    }

    private void _PopulatePalette()
    {
        ClearPallette();
        if (_akos.ResolvedColors == null) return;

        // ResolvedColors length (256)
        for (int i = 0; i < _akos.ResolvedColors.Length; i++)
        {

            Color c = _akos.ResolvedColors[i];
            // if (c.R == 1.0f && c.G == 0.0f && c.B == 1.0f) continue; //skip magenta

            var swatch = new ColorRect();
            swatch.Color             = c;
            swatch.CustomMinimumSize = new Vector2(20, 20);

            swatch.TooltipText = $"Global Index: {i}\nColor: {c}";
            _paletteGrid.AddChild(swatch);
        }
    }

    private void _PopulatePaletted()
    {
        foreach (Node child in _paletteGrid.GetChildren()) child.QueueFree();

        if (_akos.ResolvedColors == null) return;

        var paletteSpan = _akos.Palette.Span;

        for (int i = 0; i < _akos.ResolvedColors.Length; i++)
        {
            var swatch = new ColorRect();
            swatch.Color             = _akos.ResolvedColors[i];
            swatch.CustomMinimumSize = new Vector2(20, 20);
            swatch.TooltipText       = $"[{i}] AKPL:{paletteSpan[i]}  {_akos.ResolvedColors[i]}";
            _paletteGrid.AddChild(swatch);
        }
    }
}