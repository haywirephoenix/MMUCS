using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MMUCS.scripts.OS.Panels;

public partial class MainCanvas : VBoxContainer
{
    [Export] public bool AutoLoadLastFile = true;
    [Export] public Control _canvas;
    [Export] public MMenuBar _menuBar;
    [Export] public StatusBar _statusBar;
    
    [Export] public BlockHierarchyPanel _blockHierarchyPanel;
    [Export] public HexPanel _hexPanel;
    [Export] public RoomPreviewPanel _roomPanel;
    [Export] public MetadataPanel _metadataPanel;
    [Export] public AkosViewerPanel _akosPanel;
    [Export] public OBIMViewerPanel _obimPanel;
    [Export] public OptionsPanel _optionsPanel;

    private static MainCanvas Instance;
    private readonly ScummResourceParser _scummResourceParser = new();
    
    private Task<ScummBlock> _bootLoadTask;
    private ScummBlock _loadedVirtualRoot = null;
    
    private bool _isBootSequenceFinished = false;

    public override void _EnterTree()
    {
        Instance = this;
        PhysicsServer2D.Singleton.SetActive(false);
        NavigationServer2D.Singleton.SetActive(false);
        PhysicsServer3D.Singleton.SetActive(false);
        NavigationServer3D.Singleton.SetActive(false);
        OS.LowProcessorUsageMode = true;
    }

    public override async void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        EventBus.Instance.FileParsed += OnScummblockLoaded;

        EventBus.Instance.StartupComplete += OnBootScreenFinished;
    
        HideAllWindows();

        if (AutoLoadLastFile && ConfigManager.GUISettings.AutoLoadLastFile)
        {
            string lastPath = ConfigManager.GUISettings.LastFilePath;
            if (FileUtils.PathExists(lastPath))
            {
                _bootLoadTask = LoadScummFileAsync(lastPath);
            }
        }

        if (_bootLoadTask != null)
        {
            _loadedVirtualRoot = await _bootLoadTask;
        }

        if (BootScreen.IsBooting && BootScreen.IsEnabled)
        {
            BootScreen.RequestExit();
        }
        else
        {
            OnBootScreenFinished();
        }
    }

    private void OnBootScreenFinished()
    {
        _isBootSequenceFinished = true;

        if (_loadedVirtualRoot != null)
        {
            EventBus.Instance.EmitSignal(EventBus.SignalName.FileParsed, _loadedVirtualRoot);
        }
        else if (!ConfigManager.GUISettings.AutoLoadLastFile || !FileUtils.PathExists(ConfigManager.GUISettings.LastFilePath))
        {
            MMenuBar.OpenFileBrowser();
        }
    }

    public async Task<ScummBlock> LoadScummFileAsync(string path)
    {
        string dir = path.GetBaseDir();
        string fileName = path.GetFile();
        string baseName = GetBaseName(fileName);

        List<string> dataFiles = FindRelatedFiles(dir, fileName);
        if (dataFiles.Count == 0)
        {
            StatusBar.SetStatus("No files found.");
            return null;
        }

        GetWindow().Title = $"MMUCS — {baseName} (loading…)";
        StatusBar.SetStatus($"Found {dataFiles.Count} file(s) for {baseName} — parsing…");
        StatusBar.SetProgressVisible(true);
        StatusBar.SetProgress(0f);

        ScummBlock virtualRoot = null;

        await Task.Run(() =>
        {
            try
            {
                var roots = new List<ScummBlock>();
                int fileCount = dataFiles.Count;

                for (int i = 0; i < fileCount; i++)
                {
                    string filePath = dataFiles[i];
                    int fileIndex = i;

                    StatusBar.SetStatus($"Parsing {Path.GetFileName(filePath)} ({i + 1} / {fileCount})…");

                    _scummResourceParser.ProgressChanged += (cur, total) =>
                    {
                        float filePct = total > 0 ? (float)cur / total : 0f;
                        float overallPct = ((fileIndex + filePct) / fileCount) * 100f;
                        StatusBar.SetProgress(overallPct);
                    };

                    ScummBlock fileRoot = _scummResourceParser.Parse(filePath);
                    roots.Add(fileRoot);
                }

                virtualRoot = new ScummBlock
                {
                    TagName = baseName,
                    FullName = $"SCUMM Data — {baseName}"
                };

                foreach (ScummBlock fileRoot in roots)
                {
                    fileRoot.Parent = virtualRoot;
                    virtualRoot.Children.Add(fileRoot);
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"Parse error: {ex.Message}\n{ex.StackTrace}");
            }
        });

        StatusBar.SetProgressVisible(false);

        if (virtualRoot == null || virtualRoot.Children.Count == 0)
        {
            StatusBar.SetStatus("Failed to parse resource data.");
            GetWindow().Title = "MMUCS";
            return null;
        }

        int blockCount = CountBlocks(virtualRoot);
        GetWindow().Title = $"MMUCS — {baseName}";
        StatusBar.SetStatus($"Loaded {baseName} — {dataFiles.Count} files, {blockCount} blocks");

        ConfigManager.GUISettings.LastFilePath = path;
        return virtualRoot;
    }

    private int CountBlocks(ScummBlock block)
    {
        int count = 1;
        foreach (var child in block.Children)
            count += CountBlocks(child);
        return count;
    }

    private List<string> FindRelatedFiles(string dir, string fileName)
    {
        string baseName = GetBaseName(fileName);
        string ext = fileName.Length > baseName.Length ? fileName.Substring(baseName.Length + 1) : string.Empty;

        if (!ext.StartsWith("LA", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string> { Path.Combine(dir, fileName) };
        }

        var candidates = Directory.GetFiles(dir, $"{baseName}.LA*", SearchOption.TopDirectoryOnly)
            .Where(f =>
            {
                string e = Path.GetExtension(f).TrimStart('.').ToUpperInvariant();
                return e.StartsWith("LA") && e.Length > 2 && e.Substring(2).All(char.IsDigit);
            });

        return candidates.OrderBy(f =>
        {
            string e = Path.GetExtension(f).TrimStart('.').ToUpperInvariant();
            if (int.TryParse(e.Substring(2), out int la)) return la;
            return int.MaxValue;
        }).ToList();
    }

    private static string GetBaseName(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName.Substring(0, dot) : fileName;
    }

    public void OnScummblockLoaded(ScummBlock block)
    {
        _blockHierarchyPanel.LoadBlocks(block);
        OpenAllWindows();
    }

    private void HideAllWindows()
    {
        for (int i = 0; i <= Consts.MAIN_PANEL_MAX_INDEX; i++)
        {
            var panel = GetPanelByID(i);
            if (panel != null) panel.Visible = false;
        }
    }

    private void OpenAllWindows()
    {
        float delayStep = 0.05f;
        for (int i = 0; i <= Consts.MAIN_PANEL_MAX_INDEX; i++)
        {
            var panel = GetPanelByID(i);
            panel?.PlayOpenAnimation(i * delayStep);
        }
    }

    public FloatingPanel GetPanelByID(long id) => id switch
    {
        Consts.TREE_PNL_ID => _blockHierarchyPanel,
        Consts.HEX_PNL_ID => _hexPanel,
        Consts.ROOM_PNL_ID => _roomPanel,
        Consts.META_PNL_ID => _metadataPanel,
        Consts.AKOS_PNL_ID => _akosPanel,
        Consts.OBIM_PNL_ID => _obimPanel,
        Consts.OPTS_PNL_ID => _optionsPanel,
        _ => null,
    };
    
    public void ToggleVisible(int panelID)
    {
        var panel = GetPanelByID(panelID);
        panel?.AnimateVisible(!panel.Visible);
    }

    public async void OnFileSelected(string path)
    {
        _bootLoadTask = LoadScummFileAsync(path);
        ScummBlock resultRoot = await _bootLoadTask;
        if (resultRoot != null)
        {
            EventBus.Instance.EmitSignal(EventBus.SignalName.FileParsed, resultRoot);
        }
    }
}