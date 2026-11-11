using System;
using Godot;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public partial class AkosViewerContent : Control
{
    
    private void InfoToggleOnToggled(bool toggledOn)
    {
        _celInfoLabel.Visible = toggledOn;
    }
    private void OptionsToggleOnToggled(bool toggledOn)
    {
        _configGrid.Visible = toggledOn;
    }
    private void AutoZoomToggleOnToggled(bool toggledOn)
    {
        if (toggledOn)
            _previewContainer.ZoomToFitDeferred();
        else
            _previewContainer.ResetZoomDeferred();
    }

    private void ModeOptionButtonOnItemSelected(long index)
    {
        CurrentViewMode = (ViewMode)index;
        _RefreshCelPreview();
    }
    
    private void StretchOptionButtonOnItemSelected(long index)
    {
        _celPreview.StretchMode = index switch
        {
            0 => TextureRect.StretchModeEnum.KeepAspectCentered,
            1 => TextureRect.StretchModeEnum.KeepCentered,
            _ => _celPreview.StretchMode
        };
        _previewContainer.CenterCameraDeferred();
    }
    
    private void _PopulateCelList()
    {
        _celList.Clear();
        if (_akos.CelInfos == null) return;

        for (int i = 0; i < _akos.CelInfos.Length; i++)
        {
            var ci = _akos.CelInfos[i];
            _celList.AddItem($"Cel {i:D3}  {ci.Width}×{ci.Height}");
        }

        if (_akos.CelInfos.Length > 0)
            _celList.Select(0);
    }

    private void _OnCelSelected(long index)
    {
        _selectedCelIndex = (int)index;
        _RefreshCelPreview();
    }

    private async void _RefreshCelPreview()
    {
        if (_selectedCelIndex < 0 || _akos.CelInfos == null)
        {
            _celPreview.Texture = null;
            ClearPallette();
            return;
        }

        _decodeCts?.Cancel();
        _decodeCts?.Dispose();
        _decodeCts = new CancellationTokenSource();
        var token = _decodeCts.Token;

        int capturedIndex = _selectedCelIndex;
        int activePaletteIdx = _paletteSlider.CurrentIndex;

        _UpdateCelLabels(_akos.CelInfos[capturedIndex]);

        try
        {
            if (_currentBlock != null)
            {
                _parser.UpdatePaletteOnly(_akos, _currentBlock, activePaletteIdx);
            }

            var result = await AkosCelCache.GetCachedCelAsync(_akos, capturedIndex, bakeImages: true, CacheDisabled, token);
            if (result.Surface == null || token.IsCancellationRequested) return;

            if (_selectedCelIndex != capturedIndex) return;
        
            Image displayImage = (CurrentViewMode != ViewMode.Raw)
                                     ? result.ShaderDataImage
                                     : result.RawUnshadedImage;

            if (displayImage != null)
                _celPreview.Texture = ImageTexture.CreateFromImage(displayImage);
            
            UpdateUI();
            _PopulatePalette();
        }
        catch (OperationCanceledException) { }
    }

    private void UpdateUI()
    {
        bool shaderActive = (CurrentViewMode == ViewMode.Shader);
        if (CurrentViewMode == ViewMode.Raw)
        {
            if (_celPreview.Material != null)
                _celPreview.Material = null;
        }
        else
        {
            if (_celPreview.Material == null)
                _celPreview.Material = akosShaderMaterial;
            SetShaderParameter("is_active", shaderActive);

            _UpdateShaderPalette();
        }

        _alphaToggle.Disabled = !shaderActive;
        _shadowColorToggle.Disabled = !shaderActive;
        _shadowSlider.Editable = shaderActive;


        // _previewContainer.ZoomToFit();
        // _previewContainer.CenterCameraDeferred();
        ZoomCameraToFit();
    }

    private void ZoomCameraToFit()
    {
        if (_autoZoomToggle.ButtonPressed)
            _previewContainer.ZoomToFitDeferred();
        else
            _previewContainer.ZoomToFitOnceDeferred();
    }

    private void _UpdateCelLabels(AkosCelInfo ci)
    {
        _celInfoLabel.Text =
            $"Cel {_selectedCelIndex}  —  {ci.Width} × {ci.Height} px\n" +
            $"RelX: {ci.RelX}  RelY: {ci.RelY}\n" +
            $"MoveX: {ci.MoveX}  MoveY: {ci.MoveY}\n" +
            $"AKCD offset: 0x{_akos.CelOffsets?[_selectedCelIndex].AkcdOffset:X8}\n" +
            $"AKCI offset: 0x{_akos.CelOffsets?[_selectedCelIndex].AkciOffset:X4}";
    }
}