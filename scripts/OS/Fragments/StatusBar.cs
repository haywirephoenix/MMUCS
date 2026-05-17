using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;

public partial class StatusBar : Control
{
    [Export] public int statusShowDuration = 5;
    [Export] public MainCanvas _mainCanvas;
    [Export] public ProgressBar _progressBar;
    [Export] public Label _statusLabel;
    
    private static StatusBar Instance;

    public override void _EnterTree()
    {
        Instance = this;
    }

    private void _UpdateProgress(float pct)
    {
        _progressBar.Value = pct;
    }

    private CancellationTokenSource _statusCts;
    
    private async void _SetStatus(string text)
    {
        _statusCts?.Cancel();
        _statusCts?.Dispose();
        _statusCts = new CancellationTokenSource();
        var token = _statusCts.Token;

        _statusLabel.Text = text;
        try 
        {
            await Task.Delay(TimeSpan.FromSeconds(statusShowDuration), token);
            _statusLabel.Text = string.Empty;
        }
        catch (OperationCanceledException)
        {
            // a newer status message 
            // was sent, so we let that one handle the UI.
        }
    }

    public static void SetStatus(string text) => Instance._SetStatus(text);

    private int _CountBlocks(ScummBlock block)
    {
        int count = 1;
        foreach (var child in block.Children)
            count += _CountBlocks(child);
        return count;
    }

    // ── File loading

    readonly ScummResourceParser _scummResourceParser = new ScummResourceParser();
    
    //todo: move parts of this
    public async Task OnFileSelected(string path)
    {
        string dir      = path.GetBaseDir();
        string fileName = path.GetFile();
        string baseName = _GetBaseName(fileName);
        

        // Collect COMI.LA0, COMI.LA1
        List<string> dataFiles = _FindRelatedFiles(dir, fileName);
        if (dataFiles.Count == 0)
        {
            _SetStatus("No files found.");
            return;
        }
            
            //dataFiles.Add(path);

        _mainCanvas.GetWindow().Title = $"MMUCS — {baseName} (loading…)";
        _SetStatus($"Found {dataFiles.Count} file(s) for {baseName} — parsing…");
        _progressBar.Visible = true;
        _progressBar.Value   = 0;

        ScummBlock virtualRoot = null;

        await Task.Run(() =>
        {
            try
            {
                var roots     = new List<ScummBlock>();
                int fileCount = dataFiles.Count;

                for (int i = 0; i < fileCount; i++)
                {
                    string filePath  = dataFiles[i];
                    int    fileIndex = i;          // capture for lambda

                    CallDeferred(MethodName._SetStatus,
                        $"Parsing {Path.GetFileName(filePath)}  ({i + 1} / {fileCount})…");

                    
                    _scummResourceParser.ProgressChanged += (cur, total) =>
                    {
                        float filePct    = total > 0 ? (float)cur / total : 0f;
                        float overallPct = (fileIndex + filePct) / fileCount * 100f;
                        CallDeferred(MethodName._UpdateProgress, overallPct);
                    };

                    ScummBlock fileRoot = _scummResourceParser.Parse(filePath);
                    roots.Add(fileRoot);
                }

                // Wrap everything in a lightweight virtual root so the tree
                // panel needs no changes at all.
                virtualRoot = new ScummBlock
                {
                    Tag        = 0,
                    TagName    = baseName,
                    FullName   = $"SCUMM Data — {baseName}",
                    Offset     = 0,
                    Size       = 0,
                    DataOffset = 0,
                    DataLength = 0,
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

        _progressBar.Visible = false;

        if (virtualRoot == null || virtualRoot.Children.Count == 0)
        {
            _SetStatus("Failed to parse — check output log for details.");
            _mainCanvas.GetWindow().Title = "MMUCS";
            return;
        }

        int blockCount = _CountBlocks(virtualRoot);
        _mainCanvas.GetWindow().Title = $"MMUCS — {baseName}";
        _SetStatus($"Loaded {baseName}  —  {dataFiles.Count} files, {blockCount} blocks");

        ConfigManager.GUISettings.LastFilePath = path;

        EventBus.Instance.EmitSignal(EventBus.SignalName.FileParsed, virtualRoot);
    }
    
    
    private List<string> _FindRelatedFiles(string dir, string fileName)
    {
        string baseName = _GetBaseName(fileName);
        string ext      = fileName.Length > baseName.Length
                          ? fileName.Substring(baseName.Length + 1) 
                          : string.Empty;

        IEnumerable<string> candidates;

        if (ext.StartsWith("LA", StringComparison.OrdinalIgnoreCase))
        {
            // COMI-style: *.LA0, *.LA1, *.LA9 …
            candidates = Directory
                .GetFiles(dir, $"{baseName}.LA*", SearchOption.TopDirectoryOnly)
                .Where(f =>
                {
                    string e = Path.GetExtension(f).TrimStart('.').ToUpperInvariant();
                    // Must be "LA" followed by digits only
                    return e.StartsWith("LA") && e.Length > 2
                           && e.Substring(2).All(char.IsDigit);
                });
        }/*
        else if (ext.Length > 0 && ext.All(char.IsDigit))
        {
            // Numeric style: *.000, *.001, *.002 …
            candidates = Directory
                .GetFiles(dir, $"{baseName}.*", SearchOption.TopDirectoryOnly)
                .Where(f =>
                {
                    string e = Path.GetExtension(f).TrimStart('.');
                    return e.Length > 0 && e.All(char.IsDigit);
                });
        }*/
        else
        {
            // Unknown pattern — return just the selected file
            return new List<string> { Path.Combine(dir, fileName) };
        }

        // Sort by the numeric part of the extension
        List<string> sorted = candidates
            .OrderBy(f =>
            {
                string e = Path.GetExtension(f).TrimStart('.').ToUpperInvariant();
                if (e.StartsWith("LA") && int.TryParse(e.Substring(2), out int la))
                    return la;
                if (int.TryParse(e, out int n))
                    return n;
                return int.MaxValue;
            })
            .ToList();
        
        /*
        if (sorted.Count > 0)
            GD.Print($"[Discovery] {sorted.Count} file(s) for '{baseName}': "
                     + string.Join(", ", sorted.Select(Path.GetFileName)));*/

        return sorted;
    }
    
    private static string _GetBaseName(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName.Substring(0, dot) : fileName;
    }
}