using System;
using System.Text;
using Godot;


public partial class HexPanel : FloatingPanel
{
    public override string PanelTitle => "Hex View";
    [Export] public RichTextLabel _offsetView;
    [Export] public RichTextLabel _hexView;
    [Export] public RichTextLabel _asciiView;
    [Export] public Label _offsetLabel;
    [Export] public HScrollBar _hScroll;
    [Export] public VScrollBar _vScroll;

    private ScummBlock currentBlock;
    private StringBuilder offsetBb = new();
    private StringBuilder hexBb = new();
    private StringBuilder asciiBb = new();
    // private byte[] _data;
    private int _highlightStart = -1;
    private int _highlightLength;
    private int _scrollOffset;

    private const int BytesPerRow = 16;
    private const int RowHeight = 18;
    
    private const string s_codeTagStart = "[code]";
    private const string s_codeTagEnd = "[/code]";
    private const string s_colorTagStart = "[color=";
    private const string s_colorTagStart66 = "[color=#666666]";
    private const string s_colorTagEnd = "[/color]";
    private const string s_colorTagEndSpace = "[/color] ";
    private const string s_X2 = "X2";
    private const string s_color44 = "#444444";
    private const string s_colorcc = "#cccccc";
    private const string s_hexHighlColorStart = "[bgcolor=#2a4a6a][color=#88ccff]";
    private const string s_hexHighlColorEnd = "[/color][/bgcolor] ";
    private const string s_asciiHighlColorStart = "[bgcolor=#2a4a6a][color=#88ccff]";
    private const string s_asciiHighlColorEnd = "[/color][/bgcolor]";
    private const string s_tab = "   ";
    private const char s_space = ' ';
    private const char s_closeSqBrace = ']';
    private const char s_dot = '.';
    private const char s_newline = '\n';

    private static readonly NodePath PathAsciiView = "Layout/MarginContainer/ContentRoot/Container/hbox/HSplit/asciiView";
    private static readonly NodePath PathOffsetView = "Layout/MarginContainer/ContentRoot/Container/hbox/HSplit/offsetView";
    private static readonly NodePath PathhexView = "Layout/MarginContainer/ContentRoot/Container/hbox/HSplit/hexView";
    private static readonly NodePath PathOffsetLabel = "Layout/MarginContainer/ContentRoot/Container/MarginContainer/OffsetLabel";
    private static readonly NodePath PathvScroll = "Layout/MarginContainer/ContentRoot/Container/hbox/vScroll";

    protected override void OnReady()
    {
        _hexView.GuiInput += _OnHexViewInput;
        _offsetView.GuiInput += _OnOffsetInput;
        _asciiView.GuiInput += _OnAsciiInput;
        _vScroll.ValueChanged += _OnScroll;
        EventBus.Instance.BlockSelected += _OnBlockSelected;
    }
    
    public override void AssignNodes()
    {
        base.AssignNodes();
        _offsetView = GetNode<RichTextLabel>(PathOffsetView);
        _hexView = GetNode<RichTextLabel>(PathhexView);
        _asciiView = GetNode<RichTextLabel>(PathAsciiView);
        _offsetLabel = GetNode<Label>(PathOffsetLabel);
        _vScroll = GetNode<VScrollBar>(PathvScroll);
    }

    private void _OnBlockSelected(ScummBlock block)
    {
        // GD.Print($"[HEX] Showing {block.Tag}");

        currentBlock = block;

        ReadOnlySpan<byte> rawData = block.DataSpan;

        if (rawData == null)
        {
            // GD.Print("[HEX] No raw data");
            return;
        }

        SetTitle($"Hex View — {block.Tag} @ 0x{block.Offset:X8}");
        _offsetLabel.Text = $"Offset: 0x{block.Offset:X8}   Size: {block.Size} bytes   Path: {block.FullPath}";

        // Lazy-load raw data from block
        // _data =rawData;
        _highlightStart = 0;
        // _highlightLength = block.Size;
        _scrollOffset = 0;

        // if (_data != null)
        // {
        _vScroll.MaxValue = (block.DataLength / BytesPerRow) * RowHeight;
        _Render();
        //}
    }

    public void ScrollToOffset(int offset)
    {
        if (currentBlock.DataSpan == null) return;
        var row = offset / BytesPerRow;
        _vScroll.Value = row * RowHeight;
    }

    public void HighlightRange(int start, int length)
    {
        _highlightStart = start;
        _highlightLength = length;
        _Render();
    }
    private void _Render()
    {
    var _data = currentBlock.DataSpan;
    if (_data == null) return;

    offsetBb.Clear();
    hexBb.Clear();
    asciiBb.Clear(); 

    // Start the code block
    offsetBb.Append(s_codeTagStart);
    hexBb.Append(s_codeTagStart);
    asciiBb.Append(s_codeTagStart);

    int startRow = (int)(_vScroll.Value / RowHeight);
    int visibleRows = (int)(_contentRoot.Size.Y / RowHeight) + 2;
    int endRow = Mathf.Min(startRow + visibleRows, (currentBlock.DataLength + BytesPerRow - 1) / BytesPerRow);

    for (int row = startRow; row < endRow; row++)
    {
        int rowOffset = row * BytesPerRow;
        offsetBb.Append($"{s_colorTagStart66}{rowOffset:X8}{s_colorTagEnd}\n");

        for (int col = 0; col < BytesPerRow; col++)
        {
            int idx = rowOffset + col;

            if (idx >= _data.Length)
            {
                hexBb.Append(s_tab); // 3 spaces
                asciiBb.Append(s_space);
                continue;
            }

            byte b = _data[idx];
            string hex = b.ToString(s_X2);
            char ascii = (b >= 32 && b < 127) ? (char)b : s_dot;
            bool inHighlight = _highlightStart > -1 && _highlightLength > 0 &&
                               idx >= _highlightStart && idx < _highlightStart + _highlightLength;

            if (inHighlight)
            {
                // Hex: [bgcolor]XX[/bgcolor] plus one trailing space
                hexBb.Append(s_hexHighlColorStart).Append(hex).Append(s_hexHighlColorEnd);
                asciiBb.Append(s_asciiHighlColorStart).Append(ascii).Append(s_asciiHighlColorEnd);
            }
            else
            {
                string color = b == 0 ? s_color44 : s_colorcc;
                // Hex: [color]XX[/color] plus one trailing space
                hexBb.Append(s_colorTagStart).Append(color).Append(s_closeSqBrace)
                     .Append(hex).Append(s_colorTagEnd).Append(' ');
                
                asciiBb.Append(s_colorTagStart).Append(color).Append(s_closeSqBrace)
                      .Append(ascii).Append(s_colorTagEnd);
            }

            if (col == 7) hexBb.Append(s_space); // The mid-row gap
        }

        hexBb.Append(s_newline);
        asciiBb.Append(s_newline);
    }

    // Close the code block
    offsetBb.Append(s_codeTagEnd);
    hexBb.Append(s_codeTagEnd);
    asciiBb.Append(s_codeTagEnd);

    _offsetView.Text = offsetBb.ToString();
    _hexView.Text = hexBb.ToString();
    _asciiView.Text = asciiBb.ToString();
}

    private void _OnScroll(double value)
    {
        _Render();
    }

    private void CheckMouseScroll(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true } mb)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                _hexView.GrabFocus();
            }
            if (mb.ButtonIndex == MouseButton.WheelDown)
                _vScroll.Value += RowHeight * 3;
            else if (mb.ButtonIndex == MouseButton.WheelUp)
                _vScroll.Value -= RowHeight * 3;
        }
    }

    private void CheckCopy(InputEvent @event, RichTextLabel label)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false } keyEvent)
        {
            // 4. Was Control held when C was hit?
            if (keyEvent.CtrlPressed && keyEvent.Keycode == Key.C)
            {
                OnCopyKeyPressed(label);
            
                // Mark as handled so the event doesn't "leak" to other UI nodes
                GetViewport().SetInputAsHandled();
            }
        }
    }
    
    private void _OnOffsetInput(InputEvent @event)
    {
        CheckCopy(@event, _offsetView);
        
        CheckMouseScroll(@event);   
    }
    private void _OnAsciiInput(InputEvent @event)
    {
        CheckCopy(@event, _asciiView);
        
        CheckMouseScroll(@event);
    }

    private void _OnHexViewInput(InputEvent @event)
    {
        
        CheckCopy(@event, _hexView);
        
        CheckMouseScroll(@event);

        
        /*
       if (@event is InputEventMouseButton { Pressed: true } mb)
       {
           // Rough hit-test: map click Y to row, X to column
           int row = (int)(mb.Position.Y / RowHeight) + (int)(_vScroll.Value / RowHeight);
           int col = (int)((mb.Position.X - 90) / 24); // 90px = gutter, 24px per byte
           col = Mathf.Clamp(col, 0, BytesPerRow - 1);

           int offset = row * BytesPerRow + col;
           if (_data != null && offset < _data.Length)
               EventBus.Instance.EmitSignal(EventBus.SignalName.HexOffsetSelected, offset, 1);
       }*/
        
    }

    private void OnCopyKeyPressed(RichTextLabel label)
    {
        string selected = label.GetSelectedText();
        DisplayServer.ClipboardSet(selected.TrimEnd());
        StatusBar.SetStatus("hex data copied to clipboard.");
    }
}