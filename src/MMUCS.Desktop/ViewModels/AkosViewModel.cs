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
    public ObservableCollection<ChoreNode> Children { get; } = [];
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

    [ObservableProperty] public partial AkosData Akos { get; set; }
    [ObservableProperty] public partial ScummBlock Block { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = "No costume selected";
    [ObservableProperty] public partial string Info { get; set; } = "";
    [ObservableProperty] public partial CelItem SelectedCel { get; set; }
    [ObservableProperty] public partial SKImage DataImage { get; set; }
    [ObservableProperty] public partial SKImage RawImage { get; set; }
    [ObservableProperty] public partial SKImage PaletteImage { get; set; }
    [ObservableProperty] public partial ScummColor[] PaletteColors { get; set; }
    [ObservableProperty] public partial string CelInfo { get; set; }
    [ObservableProperty] public partial int PaletteIndex { get; set; }
    [ObservableProperty] public partial int MaxPaletteIndex { get; set; }

    [ObservableProperty] public partial CelViewMode Mode { get; set; } = CelViewMode.Shader;
    [ObservableProperty] public partial bool AlphaEnabled { get; set; } = true;
    [ObservableProperty] public partial bool ShadowColorEnabled { get; set; } = true;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("MvvmToolkit", "MVVMTK0042", Justification = "Static: one CelInfo toggle shared by every costume viewer.")] [ObservableProperty] private static bool _celInfoEnabled = true;
    [ObservableProperty] public partial double ShadowAmount { get; set; } = 0.4;
    [ObservableProperty] public partial bool LinearFilter { get; set; }

    [ObservableProperty] public partial ChoreNode SelectedChoreNode { get; set; }
    [ObservableProperty] public partial bool IsPlaying { get; set; }
    [ObservableProperty] public partial double Fps { get; set; } = 10;
    /// <summary>
    /// Also show what's shown here in the Room viewer, where the room places this costume: the animation while it
    /// plays, the chore's frame, step or cel selected (a cel where the first step that draws it puts it).
    /// </summary>
    [ObservableProperty] public partial bool PlayInPlace { get; set; } = true;

    /// <summary>A frame played in place, in the palette this viewer shows (null when playback in place ends).</summary>
    public event Action<ScummBlock, AkosFrame, int> LiveFrameChanged;
    private bool _liveShown;

    /// <summary>Sends step <paramref name="step"/> of the playing chore as the engine shows it on an actor: over
    /// the limbs the costume's init animation started.</summary>
    private void SendLiveFrame(int step)
    {
        if (Akos == null || _frameChore < 0)
        {
            return;
        }

        _liveShown = true;
        LiveFrameChanged?.Invoke(Block, AkosFrames.ComposeAfterInit(Akos, _frameChore, step), PaletteIndex);
    }

    /// <summary>The selected cel is what's shown (here, and in place), not a step or a chore's frame.</summary>
    public bool ShowsCel => _frameChore < 0 && _shownStep < 0;

    /// <summary>The selected cel's mod cel, while the costume shows the mods' cels; else null.</summary>
    public AkosModCel SelectedModCel => SelectedCel is { } cel ? Akos?.ModCels?.GetValueOrDefault(cel.Index) : null;

    /// <summary>
    /// Moves cel <paramref name="cel"/>'s mod cel to offset <paramref name="x"/>,<paramref name="y"/> in this costume
    /// (unsaved: loading the costume again reads meta.json), and shows what's shown again with it, here and in place.
    /// </summary>
    public void PreviewCelOffset(int cel, int x, int y)
    {
        if (Akos?.ModCels?.GetValueOrDefault(cel) is not { } mod) return;
        var cels = new Dictionary<int, AkosModCel>(Akos.ModCels) { [cel] = mod with { OffsetX = x, OffsetY = y } };
        Akos.ModCels = cels;
        if (_frameChore >= 0) ShowChoreFrame(_playPos);
        else if (_shownStep >= 0)
        {
            var frame = StepFrame(_shownStep);
            ShowFrame(frame, null, $"Step 0x{_shownStep:X4}");
            SendLiveFrame(frame);
        }
        else
        {
            _ = RefreshCelAsync();
            if (SelectedCel is { } selected && PlayInPlace) SendLiveFrame(AkosFrames.CelInPlace(Akos, selected.Index, _lastChore));
        }
    }

    /// <summary>Sends a frame shown here (a step, a cel) to be shown in place, when that's on.</summary>
    private void SendLiveFrame(AkosFrame frame)
    {
        if (Akos == null || !PlayInPlace || frame is not { IsEmpty: false })
        {
            ClearLiveFrame();
            return;
        }

        _liveShown = true;
        LiveFrameChanged?.Invoke(Block, frame, PaletteIndex);
    }

    private void ClearLiveFrame()
    {
        if (!_liveShown)
        {
            return;
        }

        _liveShown = false;
        LiveFrameChanged?.Invoke(Block, null, PaletteIndex);
    }

    partial void OnPlayInPlaceChanged(bool value)
    {
        if (!value) ClearLiveFrame();
        else if (_frameChore >= 0) SendLiveFrame(_playPos);
        else if (_shownStep >= 0) SendLiveFrame(StepFrame(_shownStep));
        else if (SelectedCel is { } cel) SendLiveFrame(AkosFrames.CelInPlace(Akos, cel.Index, _lastChore));
    }

    /// <summary>The step shown (a chore node's step), or -1; the chore last shown, which a cel is placed by first.</summary>
    private int _shownStep = -1, _lastChore = -1;

    /// <summary>One step of one limb: its cels, at their offsets.</summary>
    private AkosFrame StepFrame(int pos)
    {
        var frame = new AkosFrame();
        int xm = 0, ym = 0;
        if (Akos != null) AkosFrames.AddStep(Akos, pos, frame, ref xm, ref ym);
        return frame;
    }

    public ObservableCollection<CelItem> Cels { get; } = [];

    /// <summary>The viewer's own panels (Cels, Preview, Chores, Resolved palette).</summary>
    public Docking.ViewerDock ViewerLayout { get; }

    public AkosViewModel() => ViewerLayout = new Docking.ViewerDock(new Docking.CostumeDockFactory(this));
    public ObservableCollection<ChoreNode> Chores { get; } = [];
    public IReadOnlyList<CelViewMode> Modes { get; } = Enum.GetValues<CelViewMode>();

    public bool HasCostume => Akos != null;

    /// <summary>The mods' costume cels (set when a game is opened), loaded in place of the game's while <see cref="UseMods"/> is on.</summary>
    public CostumeCelMods CostumeMods { get; set; }

    private bool _useMods = true;

    /// <summary>Whether the costume shows the mods' cels (the Room viewer's Mod images); a change loads it again.</summary>
    public bool UseMods
    {
        get => _useMods;
        set
        {
            if (_useMods == value) return;
            _useMods = value;
            OnPropertyChanged();
            if (Block is { } block)
            {
                Block = null;
                _ = LoadAsync(block);
            }
        }
    }

    public async Task LoadAsync(ScummBlock akosBlock)
    {
        if (akosBlock == null || ReferenceEquals(akosBlock, Block))
        {
            return;
        }

        Stop();
        _shownStep = _lastChore = -1;
        OnPropertyChanged(nameof(ShowsCel));

        Block = akosBlock;
        Title = $"AKOS @0x{akosBlock.Offset:X8}";
        Info  = "";
        Cels.Clear();
        Chores.Clear();
        DataImage = RawImage = null;

        try
        {
            var mods = UseMods ? CostumeMods : null;
            var data = await Task.Run(() =>
            {
                var d = AkosParser.Parse(akosBlock);
                if (mods == null) return d;
                mods.Apply(d, akosBlock);
                lock (mods.Problems)
                {
                    foreach (var problem in mods.Problems) Log.Warn($"Costume mods: {problem}");
                    mods.Problems.Clear();
                }
                return d;
            });
            if (!ReferenceEquals(Block, akosBlock))
            {
                return;
            }

            Akos = data;
            OnPropertyChanged(nameof(HasCostume));
            Title = $"AKOS @0x{akosBlock.Offset:X8}";
            Info  = $"{data.Header.CelsCount} cels, {data.AnimCount} anims × {data.DirectionCount} dirs, codec {data.Header.Codec}";

            var apals = akosBlock.Parent?.FindChild(ScummTag.ROOM)?.FindChild(ScummTag.PALS)?.FindChild(ScummTag.WRAP)?.FindChildrenRecursive(ScummTag.APAL);
            MaxPaletteIndex = Math.Max(0, (apals?.Count ?? 1) - 1);
            _suppressPaletteReload = true;
            PaletteIndex = data.DefaultPaletteIndex;
            _suppressPaletteReload = false;
            UpdatePalette();

            for (int i = 0; i < data.CelInfos.Length && i < data.CelOffsets.Length; i++)
            {
                // A mod's cel at its own size.
                var size = data.ModCels?.GetValueOrDefault(i)?.Surface is { } mod ? (mod.Width, mod.Height) : (data.CelInfos[i].Width, data.CelInfos[i].Height);
                Cels.Add(new CelItem(i, size.Item1, size.Item2));
            }

            BuildChoreTree(data);
            var foundCel = Cels.FirstOrDefault();
            if (foundCel != null)
            {
                SelectedCel = foundCel;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"AKOS parse failed: {ex.Message}");
        }
    }

    private static readonly string[] AnimNames = ["", "init", "walk", "stand", "talk start", "talk stop"];

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
                if (!AkosFrames.HasChore(data, chore))
                {
                    continue;
                }

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
                    {
                        limbNode.Children.Add(new ChoreNode { Label = StepLabel(data, pos), Chore = chore, StepPos = pos });
                    }

                    dirNode.Children.Add(limbNode);
                }
                animNode.Children.Add(dirNode);
            }
            if (animNode.Children.Count > 0)
            {
                Chores.Add(animNode);
            }
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
        _shownStep = -1;
        OnPropertyChanged(nameof(ShowsCel));
        _ = RefreshCelAsync();
        if (value != null && Akos != null && PlayInPlace) SendLiveFrame(AkosFrames.CelInPlace(Akos, value.Index, _lastChore));
    }

    private bool _suppressPaletteReload;

    partial void OnPaletteIndexChanged(int value)
    {
        if (_suppressPaletteReload || Akos == null || Block == null) return;
        AkosParser.UpdatePaletteOnly(Akos, Block, value);
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
            var frame = StepFrame(value.StepPos);
            _frameChore = -1;
            _shownStep = value.StepPos;
            OnPropertyChanged(nameof(ShowsCel));
            if (ChoreOf(value) is int owner and >= 0) _lastChore = owner;
            ShowFrame(frame, null, $"Step 0x{value.StepPos:X4}");
            SendLiveFrame(frame);
        }
        else if (ChoreOf(value) is int chore and >= 0)
        {
            _frameChore = chore;
            _lastChore = chore;
            _shownStep = -1;
            OnPropertyChanged(nameof(ShowsCel));
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
        if (Akos == null || _frameChore < 0)
        {
            return;
        }

        var frame = AkosFrames.Compose(Akos, _frameChore, step);
        if (PlayInPlace)
        {
            SendLiveFrame(step);
        }

        int cycle = AkosFrames.CycleLength(Akos, _frameChore);
        ShowFrame(frame, _frameBounds, $"Chore {_frameChore}  frame {step % cycle + 1}/{cycle}");
    }

    /// <summary>Shows a composed frame in place of a single cel.</summary>
    private void ShowFrame(AkosFrame frame, (int, int, int, int)? bounds, string label)
    {
        var akos = Akos;
        _cts?.Cancel();
        if (akos == null || frame == null)
        {
            return;
        }

        var surface = AkosFrames.ComposeSurface(akos, frame, bounds);
        if (surface == null) { DataImage = RawImage = null; CelInfo = $"{label}\n(nothing drawn)"; return; }
        DataImage = IndexedRenderer.CreateDataImage(surface, akos)?.ToSkImage();
        RawImage = IndexedRenderer.CreateImage(surface, akos)?.ToSkImage();
        CelInfo = $"{label}  —  {surface.Width} × {surface.Height} px\n" +
                  string.Join("\n", frame.Cels.Select(c => $"cel {c.Cel}  at {c.X},{c.Y}  {c.Width}×{c.Height}"));
    }

    private void SelectCel(int index)
    {
        if (index >= 0 && index < Cels.Count)
        {
            SelectedCel = Cels[index];
        }
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
        string modded = akos.ModCels?.GetValueOrDefault(cel.Index) is { } m
            ? $"\nMod cel {System.IO.Path.GetFileName(m.File)}: {m.Surface.Width} × {m.Surface.Height} px at offset {m.OffsetX},{m.OffsetY}" : "";
        CelInfo = $"Cel {cel.Index}  —  {ci.Width} × {ci.Height} px{modded}\n" +
                  $"RelX {ci.RelX}   RelY {ci.RelY}\nMoveX {ci.MoveX}   MoveY {ci.MoveY}\n" +
                  $"AKCD 0x{off.AkcdOffset:X8}   AKCI 0x{off.AkciOffset:X4}";

        try
        {
            var result = await AkosCelCache.GetCachedCelAsync(akos, cel.Index, bakeImages: true, cacheDisabled: false, token);
            if (token.IsCancellationRequested || result.Surface == null)
            {
                return;
            }

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
        OnPropertyChanged(nameof(ShowsCel));
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
        ClearLiveFrame();
    }

    public async Task ExportCelsAsync(string folder)
    {
        if (Akos == null)
        {
            return;
        }

        var config = new CelExportConfig
        {
            AlphaEnabled = AlphaEnabled,
            ShadowColorEnabled = ShadowColorEnabled,
            ShadowAmount = (float)ShadowAmount,
            UseLinearFilter = LinearFilter,
        };
        string dir = System.IO.Path.Combine(folder, $"akos_{Akos.Offset:X8}");
        await ScummExporters.ExportAkosCelsAsync(Akos, config, dir);
        Log.Info($"Exported {Akos.CelOffsets.Length} cels to {dir}");
    }
}
