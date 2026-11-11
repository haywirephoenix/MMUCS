using System;
using Godot;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public partial class AkosViewerContent : Control
{
    private void FilterOptionButtonOnItemSelected(long index)
    {
        bool islinear = index == 1;
        TextureFilterEnum selected = islinear ? TextureFilterEnum.Linear : TextureFilterEnum.Nearest;
        _celPreview.TextureFilter = selected;
        SetShaderParameter("use_linear_filter", islinear);
    }
    
    private void _OnShadowAmountChanged(double value)
    {
        SetShaderParameter("shadow_amount", value);
    }
    private void OnVectorToggled(bool toggledOn)
    {
        VectorToggle.OnVectorToggled(toggledOn);
    }
    private void ShadowColorToggleOnToggled(bool toggledOn)
    {
        SetShaderParameter("shadow_color_enabled", toggledOn);
    }

    private void AlphaToggleOnToggled(bool toggledOn)
    {
        SetShaderParameter("alpha_enabled", toggledOn);
    }
    private void SetShaderParameter(string paramName, Variant value)
    {
        if (_celPreview.Material is ShaderMaterial sm)
            sm.SetShaderParameter(paramName, value);
    }
    private void ToggleShader(bool toggledOn)
    {
        SetShaderParameter("is_active", toggledOn);
        _alphaToggle.Disabled       = !toggledOn;
        _shadowColorToggle.Disabled = !toggledOn;
        _vectorToggle.Disabled      = !toggledOn;
        _shadowSlider.Editable      = toggledOn;
    }
    private void _UpdateShaderPalette()
    {
        if (_akos.ResolvedColors == null) return;

        var img = Image.CreateEmpty(256, 1, false, Image.Format.Rgb8);
        for (int i = 0; i < 256; i++)
        {
            // Use resolved colors if available, otherwise transparent
            Color c = (i < _akos.ResolvedColors.Length) ? _akos.ResolvedColors[i] : Colors.Transparent;
            img.SetPixel(i, 0, c);
        }

        _paletteTextureCache = ImageTexture.CreateFromImage(img);
        SetShaderParameter("room_palette", _paletteTextureCache);
    }
    
    private void EnableMaterial()
    {
        _celPreview.Material = akosShaderMaterial;
    }
    
    private void DisableMaterial()
    {
        _celPreview.Material = akosShaderMaterial;
    }
}