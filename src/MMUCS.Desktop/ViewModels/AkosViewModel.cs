using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Imaging;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.Controls;
using MMUCS.Desktop.Rendering;
using SkiaSharp;

namespace MMUCS.Desktop.ViewModels;

public sealed record CelItem(int Index, int Width, int Height)
{
    public override string ToString() => $"Cel {Index:D3}   {Width}×{Height}";
}

public sealed class ChoreStepItem
{
    public AkosChoreStep Step { get; init; }
    public string Label => Step.Kind switch
    {
        AkosStepKind.DrawSingle => $"0x{Step.Offset:X4}  Draw cel {Step.CelIndex}",
        AkosStepKind.DrawMany => $"0x{Step.Offset:X4}  DrawMany ({Step.MultiCels.Count})",
        AkosStepKind.End => $"0x{Step.Offset:X4}  End",
        AkosStepKind.Empty => $"0x{Step.Offset:X4}  Empty",
        _ => $"0x{Step.Offset:X4}  Control 0x{Step.RawCode:X4}",
    };
    public bool IsDraw => Step.Kind == AkosStepKind.DrawSingle;
}

public sealed class ChoreNode
{
    public string Label { get; init; }
    public ObservableCollection<ChoreNode> Children { get; } = new();
    public IReadOnlyList<ChoreStepItem> Steps { get; init; } = Array.Empty<ChoreStepItem>();
    public ChoreStepItem StepItem { get; init; }
}

public sealed partial class AkosViewModel : ObservableObject
{
    private readonly AkosParser _parser = new();
    private CancellationTokenSource _cts;
    private DispatcherTimer _timer;
    private int _playPos;

    [ObservableProperty] private AkosData _akos;
    [ObservableProperty] private ScummBlock _block;
    [ObservableProperty] private string _title = "No costume selected";
    [ObservableProperty] private CelItem _selectedCel;
    [ObservableProperty] private SKImage _dataImage;
    [ObservableProperty] private SKImage _rawImage;
    [ObservableProperty] private SKImage _paletteImage;
    [ObservableProperty] private ScummColor[] _paletteColors;
    [ObservableProperty] private string _celInfo;
    [ObservableProperty] private int _paletteIndex;
    [ObservableProperty] private int _maxPaletteIndex;

    [ObservableProperty] private CelViewMode _mode = CelViewMode.Shader;
    [ObservableProperty] private bool _alphaEnabled = true;
    [ObservableProperty] private bool _shadowColorEnabled = true;
    [ObservableProperty] private double _shadowAmount = 0.4;
    [ObservableProperty] private bool _linearFilter;

    [ObservableProperty] private ChoreNode _selectedChoreNode;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _fps = 10;

    public ObservableCollection<CelItem> Cels { get; } = new();
    public ObservableCollection<ChoreNode> Chores { get; } = new();
    public IReadOnlyList<CelViewMode> Modes { get; } = Enum.GetValues<CelViewMode>();

    public bool HasCostume => Akos != null;

    public async Task LoadAsync(ScummBlock akosBlock)
    {
        if (akosBlock == null || ReferenceEquals(akosBlock, Block)) return;
        Stop();

        Block = akosBlock;
        Title = $"AKOS @0x{akosBlock.Offset:X8}";
        Cels.Clear();
        Chores.Clear();
        DataImage = RawImage = null;

        try
        {
            var data = await Task.Run(() =>
            {
                var d = _parser.Parse(akosBlock);
                _parser._DecodeChores(d);
                return d;
            });
            if (!ReferenceEquals(Block, akosBlock)) return;

            Akos = data;
            OnPropertyChanged(nameof(HasCostume));
            Title = $"AKOS @0x{akosBlock.Offset:X8}  —  {data.Header.CelsCount} cels, {data.AnimCount} anims × {data.DirectionCount} dirs, codec {data.Header.Codec}";

            var apals = akosBlock.Parent?.FindChild(ScummTag.ROOM)?.FindChild(ScummTag.PALS)?.FindChild(ScummTag.WRAP)?.FindChildrenRecursive(ScummTag.APAL);
            MaxPaletteIndex = Math.Max(0, (apals?.Count ?? 1) - 1);
            _suppressPaletteReload = true;
            PaletteIndex = data.DefaultPaletteIndex;
            _suppressPaletteReload = false;
            UpdatePalette();

            for (int i = 0; i < data.CelInfos.Length && i < data.CelOffsets.Length; i++)
                Cels.Add(new CelItem(i, data.CelInfos[i].Width, data.CelInfos[i].Height));

            BuildChoreTree(data);
            SelectedCel = Cels.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Error($"AKOS parse failed: {ex.Message}");
        }
    }

    private void BuildChoreTree(AkosData data)
    {
        if (data.Chores == null) return;
        foreach (var chore in data.Chores)
        {
            var node = new ChoreNode { Label = chore.DisplayName };
            foreach (var dir in chore.Directions)
            {
                var steps = dir.Steps?.Select(s => new ChoreStepItem { Step = s }).ToList() ?? new List<ChoreStepItem>();
                var dirNode = new ChoreNode { Label = $"Dir {dir.DirIndex}  (seq 0x{dir.SequenceOffset:X4}, {steps.Count} steps)", Steps = steps };
                foreach (var s in steps)
                    dirNode.Children.Add(new ChoreNode { Label = s.Label, StepItem = s });
                node.Children.Add(dirNode);
            }
            Chores.Add(node);
        }
    }

    partial void OnSelectedCelChanged(CelItem value) => _ = RefreshCelAsync();

    private bool _suppressPaletteReload;

    partial void OnPaletteIndexChanged(int value)
    {
        if (_suppressPaletteReload || Akos == null || Block == null) return;
        _parser.UpdatePaletteOnly(Akos, Block, value);
        UpdatePalette();
        // The shader path resolves colours on the GPU; only the CPU "Raw" image needs a refresh.
        _ = RefreshCelAsync();
    }

    partial void OnSelectedChoreNodeChanged(ChoreNode value)
    {
        if (value?.StepItem is { IsDraw: true } step) SelectCel(step.Step.CelIndex);
        else if (value?.Steps.FirstOrDefault(s => s.IsDraw) is { } first) SelectCel(first.Step.CelIndex);
    }

    private void SelectCel(int index)
    {
        if (index >= 0 && index < Cels.Count) SelectedCel = Cels[index];
    }

    private void UpdatePalette()
    {
        PaletteColors = Akos?.ResolvedColors?.ToArray();
        PaletteImage = SkiaImages.CreatePaletteStrip(Akos?.ResolvedColors);
    }

    private async Task RefreshCelAsync()
    {
        var akos = Akos;
        var cel = SelectedCel;
        if (akos == null || cel == null) { DataImage = RawImage = null; CelInfo = null; return; }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var ci = akos.CelInfos[cel.Index];
        var off = akos.CelOffsets[cel.Index];
        CelInfo = $"Cel {cel.Index}  —  {ci.Width} × {ci.Height} px\n" +
                  $"RelX {ci.RelX}   RelY {ci.RelY}\nMoveX {ci.MoveX}   MoveY {ci.MoveY}\n" +
                  $"AKCD 0x{off.AkcdOffset:X8}   AKCI 0x{off.AkciOffset:X4}";

        try
        {
            var result = await AkosCelCache.GetCachedCelAsync(akos, cel.Index, bakeImages: true, cacheDisabled: false, token);
            if (token.IsCancellationRequested || result.Surface == null) return;
            DataImage = result.ShaderDataImage?.ToSkImage();
            RawImage = result.RawUnshadedImage?.ToSkImage();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error($"Cel decode failed: {ex.Message}"); }
    }

    // ── Chore playback ───────────────────────────────────────────────────────

    [RelayCommand]
    private void TogglePlay()
    {
        if (IsPlaying) { Stop(); return; }

        var dirNode = SelectedChoreNode?.Steps.Count > 0
            ? SelectedChoreNode
            : SelectedChoreNode?.Children.FirstOrDefault(c => c.Steps.Count > 0);
        var frames = dirNode?.Steps.Where(s => s.IsDraw).Select(s => s.Step.CelIndex).ToList();
        if (frames == null || frames.Count == 0)
        {
            Log.Info("Select an animation or direction with draw steps to play.");
            return;
        }

        _playPos = 0;
        _timer ??= new DispatcherTimer();
        _timer.Interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(Fps, 1, 60));
        _timer.Tick -= OnTick;
        _timer.Tag = frames;
        _timer.Tick += OnTick;
        _timer.Start();
        IsPlaying = true;
    }

    private void OnTick(object sender, EventArgs e)
    {
        if (_timer?.Tag is not List<int> frames || frames.Count == 0) { Stop(); return; }
        SelectCel(frames[_playPos % frames.Count]);
        _playPos++;
    }

    partial void OnFpsChanged(double value)
    {
        if (_timer != null) _timer.Interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(value, 1, 60));
    }

    public void Stop()
    {
        _timer?.Stop();
        IsPlaying = false;
    }

    public async Task ExportCelsAsync(string folder)
    {
        if (Akos == null) return;
        var config = new CelExportConfig
        {
            AlphaEnabled = AlphaEnabled,
            ShadowColorEnabled = ShadowColorEnabled,
            ShadowAmount = (float)ShadowAmount,
            UseLinearFilter = LinearFilter,
        };
        string dir = System.IO.Path.Combine(folder, $"akos_{Akos.Offset:X8}");
        await new ScummExporters().ExportAkosCelsAsync(Akos, config, dir);
        Log.Info($"Exported {Akos.CelOffsets.Length} cels to {dir}");
    }
}
