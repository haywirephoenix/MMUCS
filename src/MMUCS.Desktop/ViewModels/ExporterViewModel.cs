using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMUCS.Core.Scumm;
using MMUCS.Desktop.Services;

namespace MMUCS.Desktop.ViewModels;

public enum ExportStep { Start = 0, Customize = 1, Path = 2, Process = 3, Complete = 4 }

public sealed record CostumeChoice(ScummBlock Block, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class StepItem(ExportStep step, string title) : ObservableObject
{
    public ExportStep Step { get; } = step;
    public string Title { get; } = title;
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>
/// Export wizard (port of the Godot ExporterContent): choose content → tune costume shading on a
/// live preview → pick a folder → run with progress/cancel → summary.
/// </summary>
public sealed partial class ExporterViewModel : ObservableObject
{
    private readonly ScummBlock _root;
    private readonly AppSettings _settings;
    private CancellationTokenSource _cts;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanGoNext))] private ExportStep _step;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanGoNext))] private bool _exportBackgrounds;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanGoNext))] private bool _exportObjects;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanGoNext))] private bool _exportCostumes;
    [ObservableProperty] private bool _allBackgroundPalettes;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanGoNext))] private bool _exportScripts;
    [ObservableProperty] private bool _scriptOffsets;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanGoNext))] private string _outputFolder;

    [ObservableProperty] private CostumeChoice _selectedCostume;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private double _progressMaximum = 1;
    [ObservableProperty] private string _progressText;
    [ObservableProperty] private string _currentItem;
    [ObservableProperty] private string _summaryText;
    [ObservableProperty] private bool _hasFailures;

    public string SurveyText { get; }
    public IReadOnlyList<StepItem> Steps { get; } = new[]
    {
        new StepItem(ExportStep.Start, "1  Content") { IsCurrent = true },
        new StepItem(ExportStep.Customize, "2  Costume shading"),
        new StepItem(ExportStep.Path, "3  Destination"),
        new StepItem(ExportStep.Process, "4  Export"),
        new StepItem(ExportStep.Complete, "5  Done"),
    };

    /// <summary>Costume preview reuses the viewer's model (shader options, palette, cel list).</summary>
    public AkosViewModel Preview { get; } = new();
    public ObservableCollection<CostumeChoice> Costumes { get; } = new();

    public ExporterViewModel(ScummBlock root, AppSettings settings)
    {
        _root = root;
        _settings = settings;

        _exportBackgrounds = settings.ExportBackgrounds;
        _exportObjects = settings.ExportObjects;
        _exportCostumes = settings.ExportCostumes;
        _allBackgroundPalettes = settings.ExportAllBackgroundPalettes;
        _exportScripts = settings.ExportScripts;
        _scriptOffsets = settings.ExportScriptOffsets;
        _outputFolder = settings.LastExportFolder;

        Preview.AlphaEnabled = settings.CelAlphaEnabled;
        Preview.ShadowColorEnabled = settings.CelShadowColorEnabled;
        Preview.ShadowAmount = settings.CelShadowAmount;
        Preview.LinearFilter = settings.CelLinearFilter;

        var (rooms, objects, costumes, scripts) = ScummExporters.Survey(root);
        SurveyText = $"{rooms} rooms · {objects} objects · {costumes} costumes · {scripts:N0} scripts in the loaded project";

        foreach (var lflf in root.FindChildrenRecursive(ScummTag.LFLF))
        {
            RoomModel.TryCreate(lflf, out var room);
            string roomLabel = room == null ? "?" : room.RoomNo > 0 ? $"{room.RoomNo}. {room.Name}" : $"@0x{room.Room.Offset:X8}";
            foreach (var akos in lflf.Children.Where(c => c.Tag == ScummTag.AKOS))
                Costumes.Add(new CostumeChoice(akos, $"{roomLabel}  —  AKOS @0x{akos.Offset:X8}"));
        }
    }

    public bool IsStart => Step == ExportStep.Start;
    public bool IsCustomize => Step == ExportStep.Customize;
    public bool IsPath => Step == ExportStep.Path;
    public bool IsProcess => Step == ExportStep.Process;
    public bool IsComplete => Step == ExportStep.Complete;
    public bool IsRunning => Step == ExportStep.Process;
    public bool ShowBack => Step is ExportStep.Customize or ExportStep.Path;
    public bool ShowNext => Step is ExportStep.Start or ExportStep.Customize or ExportStep.Path;
    public string NextLabel => Step == ExportStep.Path ? "Export" : "Next";

    public bool CanGoNext => Step switch
    {
        ExportStep.Start => ExportBackgrounds || ExportObjects || ExportCostumes || ExportScripts,
        ExportStep.Customize => true,
        ExportStep.Path => !string.IsNullOrWhiteSpace(OutputFolder),
        _ => false,
    };

    partial void OnStepChanged(ExportStep value)
    {
        foreach (var name in new[] { nameof(IsStart), nameof(IsCustomize), nameof(IsPath), nameof(IsProcess),
                     nameof(IsComplete), nameof(IsRunning), nameof(ShowBack), nameof(ShowNext), nameof(NextLabel) })
            OnPropertyChanged(name);

        foreach (var s in Steps) s.IsCurrent = s.Step == value;

        if (value == ExportStep.Customize && SelectedCostume == null)
            SelectedCostume = Costumes.FirstOrDefault();
        if (value != ExportStep.Customize)
            Preview.Stop();
    }

    partial void OnSelectedCostumeChanged(CostumeChoice value)
    {
        if (value != null) _ = Preview.LoadAsync(value.Block);
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (!CanGoNext) return;
        switch (Step)
        {
            case ExportStep.Start:
                // Shading only affects costumes, so skip that step when they aren't exported.
                Step = ExportCostumes ? ExportStep.Customize : ExportStep.Path;
                break;
            case ExportStep.Customize:
                Step = ExportStep.Path;
                break;
            case ExportStep.Path:
                await RunAsync();
                break;
        }
    }

    [RelayCommand]
    private void Back()
    {
        Step = Step switch
        {
            ExportStep.Customize => ExportStep.Start,
            ExportStep.Path => ExportCostumes ? ExportStep.Customize : ExportStep.Start,
            _ => Step,
        };
    }

    [RelayCommand]
    private void Restart() => Step = ExportStep.Start;

    [RelayCommand]
    public void Cancel() => _cts?.Cancel();

    private async Task RunAsync()
    {
        SaveSettings();
        Step = ExportStep.Process;
        ProgressValue = 0;
        ProgressMaximum = 1;
        ProgressText = "Preparing…";
        CurrentItem = null;

        var options = new ExportOptions
        {
            Backgrounds = ExportBackgrounds,
            Objects = ExportObjects,
            Costumes = ExportCostumes,
            AllBackgroundPalettes = AllBackgroundPalettes,
            Scripts = ExportScripts,
            ScriptOffsets = ScriptOffsets,
            CelConfig = new CelExportConfig
            {
                AlphaEnabled = Preview.AlphaEnabled,
                ShadowColorEnabled = Preview.ShadowColorEnabled,
                ShadowAmount = (float)Preview.ShadowAmount,
                UseLinearFilter = Preview.LinearFilter,
            },
        };

        _cts = new CancellationTokenSource();
        // Progress<T> captures the UI context, so reports arrive on the UI thread.
        var progress = new Progress<ExportProgress>(p =>
        {
            ProgressMaximum = Math.Max(1, p.Total);
            ProgressValue = p.Done;
            ProgressText = $"{p.Done:N0} / {p.Total:N0}";
            CurrentItem = p.Current;
        });

        var summary = await Task.Run(() =>
            new ScummExporters().ExportProjectAsync(_root, OutputFolder, options, progress, _cts.Token));

        HasFailures = summary.Failures > 0;
        string verb = summary.Cancelled ? "Cancelled after writing" : "Wrote";
        SummaryText = $"{verb} {summary.FilesWritten:N0} files in {summary.Elapsed.TotalSeconds:0.0} s." +
                      (summary.Failures > 0 ? $"\n{summary.Failures:N0} items failed (see the debug log)." : "");
        Step = ExportStep.Complete;
    }

    public void SaveSettings()
    {
        _settings.ExportBackgrounds = ExportBackgrounds;
        _settings.ExportObjects = ExportObjects;
        _settings.ExportCostumes = ExportCostumes;
        _settings.ExportAllBackgroundPalettes = AllBackgroundPalettes;
        _settings.ExportScripts = ExportScripts;
        _settings.ExportScriptOffsets = ScriptOffsets;
        _settings.LastExportFolder = OutputFolder;
        _settings.CelAlphaEnabled = Preview.AlphaEnabled;
        _settings.CelShadowColorEnabled = Preview.ShadowColorEnabled;
        _settings.CelShadowAmount = Preview.ShadowAmount;
        _settings.CelLinearFilter = Preview.LinearFilter;
        _settings.Save();
    }
}
