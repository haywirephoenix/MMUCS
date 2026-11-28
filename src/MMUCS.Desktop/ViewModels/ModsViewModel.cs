using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMUCS.Core;
using MMUCS.Core.Diagnostics;
using MMUCS.Desktop.Services;

namespace MMUCS.Desktop.ViewModels;

/// <summary>
/// The game window's Mods view: the mods in the _mods folder beside the game and those a repository offers
/// (<see cref="ModRepository"/>), to install, update and switch on or off, and the music and image mods as a whole.
/// What it changes reaches the running game at once (<see cref="GameViewModel.ReloadMods"/>).
/// </summary>
public sealed partial class ModsViewModel : ObservableObject, IDisposable
{
    private static readonly HttpClient Http = CreateHttp();
    private readonly string _gameFile;
    private readonly AppSettings _settings;
    private readonly GameViewModel _game;
    private ModRepository.Index _index;
    private bool _fetched, _disposed;
    private CancellationTokenSource _cts = new();

    public ModsViewModel(string gameFile, AppSettings settings, GameViewModel game)
    {
        _gameFile = gameFile;
        _settings = settings;
        _game = game;
        RepositoryUrl = settings?.ModRepositoryUrl is { Length: > 0 } url ? url : ModRepository.DefaultUrl;
        _game.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GameViewModel.MusicModsEnabled)) { OnPropertyChanged(nameof(IsMusicModsEnabled)); Regate(); }
            if (e.PropertyName == nameof(GameViewModel.ImageModsEnabled)) { OnPropertyChanged(nameof(IsImageModsEnabled)); Regate(); }
        };
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MMUCS");
        return http;
    }

    /// <summary>Every mod known: installed, offered by the repository, or both.</summary>
    public ObservableCollection<ModItem> Mods { get; } = [];

    /// <summary>The mods the search and category filter leave.</summary>
    public IReadOnlyList<ModItem> FilteredMods => [.. Mods.Where(Shows)];

    /// <summary>What the category filter can pick.</summary>
    public IReadOnlyList<string> Categories { get; } = ["All", "Installed", "Updates", "Widescreen", "Music", "Images", "Hi-res", "Other"];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(FilteredMods))]
    public partial string SearchQuery { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(FilteredMods))]
    public partial string SelectedCategoryFilter { get; set; } = "All";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(InstallSelectedCommand))]
    public partial ModItem SelectedMod { get; set; }

    /// <summary>What happened last: a fetch, an install, or why it failed.</summary>
    [ObservableProperty] public partial string Status { get; set; }

    /// <summary>Whether a download or fetch is under way.</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(InstallSelectedCommand), nameof(RefreshRepoCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>The share of a download done (0–1).</summary>
    [ObservableProperty] public partial double Progress { get; set; }

    /// <summary>The repository's mods.json (kept in the settings).</summary>
    [ObservableProperty] public partial string RepositoryUrl { get; set; }

    /// <summary>The repository's name, as its mods.json gives it, once it's been read.</summary>
    [ObservableProperty] public partial string RepositoryName { get; set; }

    partial void OnRepositoryUrlChanged(string value)
    {
        if (_settings == null || _settings.ModRepositoryUrl == value) return;
        _settings.ModRepositoryUrl = value;
        _settings.Save();
    }

    /// <summary>The _mods folder beside the game.</summary>
    public string LocalModsPath => GameMods.Folder(_gameFile) ?? "(no game open)";

    /// <summary>Whether the _mods folder is there yet.</summary>
    public bool HasModsFolder => GameMods.Folder(_gameFile) is { } f && Directory.Exists(f);

    /// <summary>The music mods as a whole (the game window's setting).</summary>
    public bool IsMusicModsEnabled { get => _game.MusicModsEnabled; set => _game.MusicModsEnabled = value; }

    /// <summary>The image mods as a whole: object images, costume cels and hi-res pictures (the game window's setting).</summary>
    public bool IsImageModsEnabled { get => _game.ImageModsEnabled; set => _game.ImageModsEnabled = value; }

    /// <summary>The view came up: the installed mods are read again, and the repository the first time.</summary>
    public void Shown()
    {
        Rescan();
        if (!_fetched) _ = RefreshRepoAsync();
    }

    private bool Shows(ModItem m)
    {
        if (!string.IsNullOrWhiteSpace(SearchQuery) &&
            !(m.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) || m.Id.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
              m.Description.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase)))
            return false;
        return SelectedCategoryFilter switch
        {
            "Installed" => m.IsInstalled,
            "Updates" => m.HasUpdate,
            "Widescreen" or "Music" or "Images" or "Hi-res" or "Other" => m.Category.Contains(SelectedCategoryFilter, StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    /// <summary>Merges the installed mods with the repository's into <see cref="Mods"/>, keeping the selection.</summary>
    private void Rescan()
    {
        string selected = SelectedMod?.Id;
        var installed = ModRepository.Scan(_gameFile).ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var offered = (_index?.Mods ?? []).GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        Mods.Clear();
        foreach (string id in installed.Keys.Union(offered.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            Mods.Add(new ModItem(this, installed.GetValueOrDefault(id), offered.GetValueOrDefault(id)));
        Regate();
        OnPropertyChanged(nameof(FilteredMods));
        OnPropertyChanged(nameof(HasModsFolder));
        SelectedMod = Mods.FirstOrDefault(m => m.Id.Equals(selected, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether each mod's category is on as a whole (its checkbox is greyed when it isn't).</summary>
    private void Regate()
    {
        foreach (var m in Mods) m.IsCategoryGloballyEnabled = CategoryOn(m.Category);
    }

    /// <summary>Whether a mod of <paramref name="category"/> does anything with the switches as they are (a widescreen one always does).</summary>
    private bool CategoryOn(string category) =>
        category.Contains("Widescreen") ||
        (category.Contains("Music") && IsMusicModsEnabled) || ((category.Contains("Images") || category.Contains("Hi-res")) && IsImageModsEnabled) ||
        !(category.Contains("Music") || category.Contains("Images") || category.Contains("Hi-res"));

    /// <summary>A mod was switched on or off (its checkbox): written to its mod.json and shown in the game.</summary>
    internal void SetEnabled(ModItem mod, bool enabled)
    {
        if (mod.Folder == null) return;
        try
        {
            ModRepository.SetEnabled(mod.Folder, enabled);
            _game.ReloadMods();
            Status = $"{mod.Name} {(enabled ? "on" : "off")}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"{mod.Name}: couldn't write its mod.json: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanFetch))]
    private Task RefreshRepo() => RefreshRepoAsync();

    private bool CanFetch() => !IsBusy;

    private async Task RefreshRepoAsync()
    {
        if (!Uri.TryCreate(RepositoryUrl, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
        {
            Status = "The repository address isn't a web address (Settings)";
            return;
        }
        IsBusy = true;
        Status = $"Reading {url.Host}…";
        try
        {
            _index = await ModRepository.FetchAsync(Http, url, _cts.Token);
            _fetched = true;
            RepositoryName = _index.Name;
            Status = $"{_index.Name}: {_index.Mods.Count} mod{(_index.Mods.Count == 1 ? "" : "s")}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Status = $"Couldn't read the repository: {ex.Message}";
            Log.Warn($"Mods: {url}: {ex.Message}");
        }
        finally { IsBusy = false; }
        if (!_disposed) Rescan();
    }

    /// <summary>Settings' Test: reads the repository and says what it offers.</summary>
    [RelayCommand]
    private Task TestRepoConnection() => RefreshRepoAsync();

    /// <summary>Settings' Default: back to MMUCS's own repository.</summary>
    [RelayCommand]
    private void ResetRepository() => RepositoryUrl = ModRepository.DefaultUrl;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallSelected()
    {
        if (SelectedMod is not { Offered: { } entry } mod) return;
        IsBusy = true;
        Progress = 0;
        Status = $"Downloading {mod.Name} {entry.Version}…";
        try
        {
            await ModRepository.DownloadAsync(Http, entry, _gameFile, new Progress<double>(p => Progress = p), _cts.Token);
            Status = $"{mod.Name} {entry.Version} installed";
            _game.ReloadMods();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = $"Couldn't install {mod.Name}: {ex.Message}";
            Log.Warn($"Mods: {entry.Zip}: {ex.Message}");
        }
        finally { IsBusy = false; }
        if (!_disposed) Rescan();
    }

    private bool CanInstall() => !IsBusy && SelectedMod is { Offered: not null };

    /// <summary>Makes the _mods folder beside the game.</summary>
    [RelayCommand]
    private void CreateModsFolder()
    {
        try
        {
            Status = $"Made {ModRepository.CreateFolder(_gameFile)}";
            OnPropertyChanged(nameof(HasModsFolder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { Status = ex.Message; }
    }

    /// <summary>Reads the _mods folder again (after mods were copied in by hand).</summary>
    [RelayCommand]
    private void ReadFolder() { Rescan(); _game.ReloadMods(); Status = "Read the mods folder again"; }

    /// <summary>Back to the game.</summary>
    [RelayCommand]
    private void Close() => _game.ShowMods = false;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}

/// <summary>A mod in the Mods view: installed, offered by the repository, or both.</summary>
public sealed partial class ModItem : ObservableObject
{
    private readonly ModsViewModel _owner;
    private bool _enabled;

    public ModItem(ModsViewModel owner, ModRepository.Installed installed, ModRepository.Entry offered)
    {
        _owner = owner;
        Installed = installed;
        Offered = offered;
        _enabled = installed?.Enabled ?? false;
    }

    public ModRepository.Installed Installed { get; }
    public ModRepository.Entry Offered { get; }

    public string Id => Installed?.Id ?? Offered.Id;
    public string Name => Offered?.Name ?? Installed.Name;
    public string Category => Offered?.Category ?? Installed.Category;
    public string Description => Offered?.Description is { Length: > 0 } d ? d : Installed?.Description ?? "";
    public string Folder => Installed?.Folder;
    public bool IsInstalled => Installed != null;
    public bool HasUpdate => IsInstalled && Offered != null && ModRepository.IsNewer(Offered.Version, Installed.Version);

    /// <summary>The version installed, and the repository's when it's newer.</summary>
    public string Version => !IsInstalled ? Offered.Version : HasUpdate ? $"{Show(Installed.Version)} → {Offered.Version}" : Show(Installed.Version);

    private static string Show(string version) => string.IsNullOrEmpty(version) ? "—" : version;

    public string InstallStatus => !IsInstalled ? "Available" : HasUpdate ? "Update" : Offered == null ? "Local" : "Installed";

    /// <summary>What the install button says for it.</summary>
    public string InstallLabel => !IsInstalled ? "Install" : HasUpdate ? "Update" : "Reinstall";

    /// <summary>The download's size, when the repository gives it.</summary>
    public string Size => Offered is { Size: > 0 } o ? o.Size >= 1 << 20 ? $"{o.Size / (double)(1 << 20):0.#} MB" : $"{Math.Max(1, o.Size / 1024)} KB" : "";

    /// <summary>On or off (its mod.json's "enabled"): switching writes it and shows it in the game.</summary>
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value || !IsInstalled) return;
            _enabled = value;
            OnPropertyChanged();
            _owner.SetEnabled(this, value);
        }
    }

    /// <summary>Whether its kind of mod (music, images) is on as a whole, so switching it shows.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial bool IsCategoryGloballyEnabled { get; set; } = true;

    /// <summary>Whether its checkbox can be used: it's installed and its kind of mod is on.</summary>
    public bool CanToggle => IsInstalled && IsCategoryGloballyEnabled;
}
