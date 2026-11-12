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

/// <summary>
/// A node in the chore tree: Anim → Dir (a chore: its limbs play together) → Limb → frame step.
/// </summary>
public sealed class ChoreNode
{
    public string Label { get; init; }
    public ObservableCollection<ChoreNode> Children { get; } = new();
    /// <summary>The chore (AKCH index) this node belongs to, or -1.</summary>
    public int Chore { get; init; } = -1;
    /// <summary>For a step node: its AKSQ position (drawn on its own); otherwise -1.</summary>
    public int StepPos { get; init; } = -1;
    /// <summary>Kept on the node so the tree reopens as it was when its tab is shown again.</summary>
    public bool IsExpanded { get; set; }
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

    /// <summary>The viewer's own panels (Cels, Preview, Chores, Resolved palette).</summary>
    public Docking.CostumeDockFactory DockFactory { get; }
    [ObservableProperty] private Dock.Model.Controls.IRootDock _layout;

    public AkosViewModel()
    {
        DockFactory = new Docking.CostumeDockFactory(this);
        UseLayout(DockFactory.CreateLayout());
    }

    /// <summary>Applies a saved arrangement of the viewer's panels (the default if it doesn't fit).</summary>
    public void RestoreLayout(Docking.DockLayoutStore.Node saved) => UseLayout(DockFactory.TryRestore(saved) ?? DockFactory.CreateLayout());

    public void ResetLayout() => UseLayout(DockFactory.CreateLayout());

    private void UseLayout(Dock.Model.Controls.IRootDock layout)
    {
        DockFactory.InitLayout(layout);
        Layout = layout;
    }
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
            var data = await Task.Run(() => _parser.Parse(akosBlock));
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

    private static readonly string[] AnimNames = { "", "init", "walk", "stand", "talk start", "talk stop" };

    private void BuildChoreTree(AkosData data)
    {
        int count = AkosFrames.ChoreCount(data);
        var dirNames = AkosFrames.DirectionNames(data);
        for (int anim = 0; anim * data.DirectionCount < count; anim++)
        {
            string name = anim < AnimNames.Length && AnimNames[anim].Length > 0 ? $"Anim {anim}  ({AnimNames[anim]})" : $"Anim {anim}";
            var animNode = new ChoreNode { Label = name, Chore = -1 };
            for (int dir = 0; dir < data.DirectionCount; dir++)
            {
                int chore = AkosFrames.ChoreIndex(data, anim, dir);
                if (!AkosFrames.HasChore(data, chore)) continue;
                var limbs = AkosFrames.GetLimbs(data, chore);
                var dirNode = new ChoreNode
                {
                    Label = $"Dir {dir} {dirNames[dir]}  ({limbs.Count} limb{(limbs.Count == 1 ? "" : "s")}, {AkosFrames.CycleLength(data, chore)} frames)",
                    Chore = chore,
                };
                foreach (var limb in limbs)
                {
                    var steps = AkosFrames.GetFrameSteps(data, limb);
                    var limbNode = new ChoreNode { Label = $"Limb {limb.Index}  ({steps.Count} frame{(steps.Count == 1 ? "" : "s")})", Chore = chore };
                    foreach (int pos in steps)
                        limbNode.Children.Add(new ChoreNode { Label = StepLabel(data, pos), Chore = chore, StepPos = pos });
                    dirNode.Children.Add(limbNode);
                }
                animNode.Children.Add(dirNode);
            }
            if (animNode.Children.Count > 0) Chores.Add(animNode);
        }
    }

    private static string StepLabel(AkosData data, int pos)
    {
        var frame = new AkosFrame();
        int xm = 0, ym = 0;
        AkosFrames.AddStep(data, pos, frame, ref xm, ref ym);
        return frame.Cels.Count switch
        {
            0 => $"0x{pos:X4}  empty",
            1 => $"0x{pos:X4}  cel {frame.Cels[0].Cel}",
            _ => $"0x{pos:X4}  cels {string.Join(", ", frame.Cels.Select(c => c.Cel))}",
        };
    }

    partial void OnSelectedCelChanged(CelItem value)
    {
        _frameChore = -1;
        _ = RefreshCelAsync();
    }

    private bool _suppressPaletteReload;

    partial void OnPaletteIndexChanged(int value)
    {
        if (_suppressPaletteReload || Akos == null || Block == null) return;
        _parser.UpdatePaletteOnly(Akos, Block, value);
        UpdatePalette();
        // The shader path resolves colours on the GPU; only the CPU "Raw" image needs a refresh.
        if (_frameChore >= 0) ShowChoreFrame(_playPos);
        else _ = RefreshCelAsync();
    }

    partial void OnSelectedChoreNodeChanged(ChoreNode value)
    {
        if (value == null || Akos == null) return;
        Stop();
        if (value.StepPos >= 0)
        {
            // One step of one limb: its cels, at their offsets.
            var frame = new AkosFrame();
            int xm = 0, ym = 0;
            AkosFrames.AddStep(Akos, value.StepPos, frame, ref xm, ref ym);
            ShowFrame(frame, null, $"Step 0x{value.StepPos:X4}");
        }
        else if (ChoreOf(value) is int chore and >= 0)
        {
            _frameChore = chore;
            _frameBounds = AkosFrames.CycleBounds(Akos, chore, AkosFrames.CycleLength(Akos, chore));
            ShowChoreFrame(0);
        }
    }

    /// <summary>The chore a node plays: its own, or (for an Anim node) its first direction's.</summary>
    private static int ChoreOf(ChoreNode node) => node.Chore >= 0 ? node.Chore : node.Children.FirstOrDefault()?.Chore ?? -1;

    private int _frameChore = -1;
    private (int, int, int, int) _frameBounds;

    private void ShowChoreFrame(int step)
    {
        if (Akos == null || _frameChore < 0) return;
        var frame = AkosFrames.Compose(Akos, _frameChore, step);
        int cycle = AkosFrames.CycleLength(Akos, _frameChore);
        ShowFrame(frame, _frameBounds, $"Chore {_frameChore}  frame {step % cycle + 1}/{cycle}");
    }

    /// <summary>Shows a composed frame in place of a single cel.</summary>
    private void ShowFrame(AkosFrame frame, (int, int, int, int)? bounds, string label)
    {
        var akos = Akos;
        _cts?.Cancel();
        if (akos == null || frame == null) return;
        var surface = AkosFrames.ComposeSurface(akos, frame, bounds);
        if (surface == null) { DataImage = RawImage = null; CelInfo = $"{label}\n(nothing drawn)"; return; }
        DataImage = IndexedRenderer.CreateDataImage(surface, akos)?.ToSkImage();
        RawImage = IndexedRenderer.CreateImage(surface, akos)?.ToSkImage();
        CelInfo = $"{label}  —  {surface.Width} × {surface.Height} px\n" +
                  string.Join("\n", frame.Cels.Select(c => $"cel {c.Cel}  at {c.X},{c.Y}  {c.Width}×{c.Height}"));
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
        if (Akos == null || SelectedChoreNode == null || ChoreOf(SelectedChoreNode) is not (int chore and >= 0))
        {
            Log.Info("Select an animation or direction to play.");
            return;
        }

        _frameChore = chore;
        _frameBounds = AkosFrames.CycleBounds(Akos, chore, AkosFrames.CycleLength(Akos, chore));
        _playPos = 0;
        _timer ??= new DispatcherTimer();
        _timer.Interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(Fps, 1, 60));
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
        IsPlaying = true;
        ShowChoreFrame(0);
    }

    private void OnTick(object sender, EventArgs e)
    {
        if (_frameChore < 0) { Stop(); return; }
        _playPos++;
        ShowChoreFrame(_playPos);
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
