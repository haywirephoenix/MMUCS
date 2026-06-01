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
    [Export] public PopupMenu _editMenu;
    [Export] public HSplitContainer _hSplit;


    private ScummBlock currentBlock;
    private StringBuilder offsetBb = new();
    private StringBuilder hexBb = new();
    private StringBuilder asciiBb = new();
    private int _highlightStart = -1;
    private int _highlightLength;
    private int _scrollOffset;

    private int _bytesPerRow = 16;
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

    private const string s_contentRootPath = "Layout/MarginContainer/ContentRoot";
    private const string s_containerPath = s_contentRootPath + "/Container";

    private static readonly NodePath PathEditMenu = s_contentRootPath + "/MenuBar/Edit";
    
    private static readonly NodePath PathOffsetLabel = s_contentRootPath + "/MenuBar/OffsetLabel";
    private static readonly NodePath PathAsciiView = s_containerPath + "/hbox/HSplit/asciiView";
    private static readonly NodePath PathOffsetView = s_containerPath + "/hbox/HSplit/offsetView";
    private static readonly NodePath PathhexView = s_containerPath + "/hbox/HSplit/hexView";
    private static readonly NodePath PathvScroll = s_containerPath + "/hbox/vScroll";
    private static readonly NodePath PathHSplit= s_containerPath + "/hbox/HSplit";
    
    public override void _Notification(int what)
    {
        base._Notification(what);

        if (what == NotificationResized)
        {
            UpdateBytesPerRowBasedOnWidth();
        }
    }

    protected override void OnReady()
    {
        _hexView.GuiInput += _OnHexViewInput;
        _offsetView.GuiInput += _OnOffsetInput;
        _asciiView.GuiInput += _OnAsciiInput;
        _vScroll.ValueChanged += _OnScroll;
        _editMenu.IdPressed += EditMenuOnIdPressed;
        _hSplit.Resized += UpdateBytesPerRowBasedOnWidth;
        // _hSplit.Dragged += HSplitOnDragged;
    }
    private void HSplitOnDragged(long offset)
    {
        UpdateBytesPerRowBasedOnWidth();
    }
    private void EditMenuOnIdPressed(long id)
    {
        switch (id)
        {
            case 0: OnCopyHexPressed(); break;
            case 1: OnCopyAsciiPressed(); break;
            case 2: OnCopyAllPressed(); break;
        }
    }

    private void OnCopyAsciiPressed()
    {
        Copy(CopyType.Ascii);
    }
    private void OnCopyHexPressed()
    {
        Copy(CopyType.Hex);
    }
    
    private void OnCopyAllPressed()
    {
        Copy(CopyType.Combined);
    }

    enum CopyType { Hex,Ascii,Offsets,Combined }

    private void Copy(CopyType type)
    {
        ReadOnlySpan<byte> data = currentBlock.DataSpan;
        if (data == null || data.Length == 0)
        {
            StatusBar.SetStatus("No data available to copy.");
            return;
        }

        StringBuilder sb = new();

        switch (type)
        {
            case CopyType.Hex: CopyHex(data, sb); break;
            case CopyType.Ascii: CopyAscii(data, sb); break;
            case CopyType.Offsets: CopyOffsets(data, sb); break;
            case CopyType.Combined: CopyCombined(data, sb); break;
        }
    
        DisplayServer.ClipboardSet(sb.ToString().TrimEnd());
    }

    private void CopyHex(ReadOnlySpan<byte> data,StringBuilder sb)
    {
        for (int i = 0; i < data.Length; i++)
        {
            sb.Append(data[i].ToString(s_X2)).Append(' ');

            int col = i % _bytesPerRow;
            if (col == 7)
            {
                sb.Append(' ');
            }
            else if (col == 15 && i < data.Length - 1)
            {
                sb.Append('\n');
            }
        }
        
        StatusBar.SetStatus("All Hex data copied to clipboard.");
    }

    private void CopyOffsets(ReadOnlySpan<byte> data,StringBuilder sb)
    {
        int totalRows = (data.Length + _bytesPerRow - 1) / _bytesPerRow;
        for (int row = 0; row < totalRows; row++)
        {
            sb.Append($"{(row * _bytesPerRow):X8}\n");
        }
        
        StatusBar.SetStatus("All offsets copied to clipboard.");
    }

    private void CopyAscii(ReadOnlySpan<byte> data,StringBuilder sb)
    {
        
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            char ascii = (b >= 32 && b < 127) ? (char)b : s_dot;
            sb.Append(ascii);

            if ((i + 1) % _bytesPerRow == 0 && i < data.Length - 1)
            {
                sb.Append('\n');
            }
        }
        
        StatusBar.SetStatus("All ASCII data copied to clipboard.");
    }
    
    private void CopyCombined(ReadOnlySpan<byte> data, StringBuilder sb)
    {
        int totalRows = (data.Length + _bytesPerRow - 1) / _bytesPerRow;

        for (int row = 0; row < totalRows; row++)
        {
            int rowOffset = row * _bytesPerRow;

            sb.Append($"{rowOffset:X8}:  ");

            StringBuilder asciiRow = new();

            for (int col = 0; col < _bytesPerRow; col++)
            {
                int idx = rowOffset + col;

                if (idx < data.Length)
                {
                    byte b = data[idx];
                    sb.Append(b.ToString(s_X2)).Append(' ');

                    char ascii = (b >= 32 && b < 127) ? (char)b : s_dot;
                    asciiRow.Append(ascii);
                }
                else
                {
                    sb.Append("   "); 
                }

                if (col == 7)
                {
                    sb.Append(' ');
                }
            }

            sb.Append(" | ").Append(asciiRow).Append('\n');
        }

        StatusBar.SetStatus("Complete hex dump copied to clipboard.");
    }

    public override void AssignNodes()
    {
        base.AssignNodes();
        _editMenu = GetNode<PopupMenu>(PathEditMenu);
        _offsetView = GetNode<RichTextLabel>(PathOffsetView);
        _hexView = GetNode<RichTextLabel>(PathhexView);
        _asciiView = GetNode<RichTextLabel>(PathAsciiView);
        _offsetLabel = GetNode<Label>(PathOffsetLabel);
        _vScroll = GetNode<VScrollBar>(PathvScroll);
        _hSplit = GetNode<HSplitContainer>(PathHSplit);
    }

    protected override void _OnBlockSelected(ScummBlock block)
    {

        currentBlock = block;

        ReadOnlySpan<byte> rawData = block.DataSpan;

        if (rawData == null)
        {
            return;
        }

        SetTitle($"Hex View — {block.Tag} @ 0x{block.Offset:X8}");
        _offsetLabel.Text = $"Offset: 0x{block.Offset:X8}   Size: {block.Size} bytes   Path: {block.FullPath}";

        _highlightStart = 0;
        _scrollOffset = 0;

        _vScroll.MaxValue = (block.DataLength / _bytesPerRow) * RowHeight;
        _Render();
    }

    public void ScrollToOffset(int offset)
    {
        if (currentBlock.DataSpan == null) return;
        var row = offset / _bytesPerRow;
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
        if(currentBlock == null) return;
        var _data = currentBlock.DataSpan;
        if (_data == null) return;

        offsetBb.Clear();
        hexBb.Clear();
        asciiBb.Clear();

        offsetBb.Append(s_codeTagStart);
        hexBb.Append(s_codeTagStart);
        asciiBb.Append(s_codeTagStart);

        int startRow = (int)(_vScroll.Value / RowHeight);
        int visibleRows = (int)(_marginContainer.Size.Y / RowHeight) + 2;
        int endRow = Mathf.Min(startRow + visibleRows, (currentBlock.DataLength + _bytesPerRow - 1) / _bytesPerRow);

        for (int row = startRow; row < endRow; row++)
        {
            int rowOffset = row * _bytesPerRow;
            offsetBb.Append($"{s_colorTagStart66}{rowOffset:X8}{s_colorTagEnd}\n");

            for (int col = 0; col < _bytesPerRow; col++)
            {
                int idx = rowOffset + col;

                if (idx >= _data.Length)
                {
                    hexBb.Append(s_tab);
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
                    hexBb.Append(s_hexHighlColorStart).Append(hex).Append(s_hexHighlColorEnd);
                    asciiBb.Append(s_asciiHighlColorStart).Append(ascii).Append(s_asciiHighlColorEnd);
                }
                else
                {
                    string color = b == 0 ? s_color44 : s_colorcc;
                    hexBb.Append(s_colorTagStart).Append(color).Append(s_closeSqBrace)
                        .Append(hex).Append(s_colorTagEnd).Append(' ');

                    asciiBb.Append(s_colorTagStart).Append(color).Append(s_closeSqBrace)
                        .Append(ascii).Append(s_colorTagEnd);
                }

                if (_bytesPerRow == 16 && col == 7) 
                {
                    hexBb.Append(s_space);
                }
                else if (_bytesPerRow == 8 && col == 3)
                {
                    hexBb.Append(s_space);
                }
            }

            hexBb.Append(s_newline);
            asciiBb.Append(s_newline);
        }

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
            // if (mb.ButtonIndex == MouseButton.Left)
            // {
            //     _hexView.GrabFocus();
            // }
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
            if (keyEvent.CtrlPressed && keyEvent.Keycode == Key.C)
            {
                OnCopyKeyPressed(label);

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
    }

    private void OnCopyKeyPressed(RichTextLabel label)
    {
        string selected = label.GetSelectedText();
        DisplayServer.ClipboardSet(selected.TrimEnd());
        StatusBar.SetStatus("hex data copied to clipboard.");
    }
    
    
    private void UpdateBytesPerRowBasedOnWidth()
    {
        if (currentBlock == null || currentBlock.DataSpan == null) return;

        float totalAvailableWidth = _marginContainer.Size.X; 
        float charWidth = GetFontCharWidth(_hexView);
        int separation = _hSplit.GetThemeConstant("separation"); 
    
        float widthNeededFor16 = (73 * charWidth) + (separation * 2);
        float widthNeededFor8  = (40 * charWidth) + (separation * 2);

        int targetBytes = 16;
        if (totalAvailableWidth >= widthNeededFor16)
            targetBytes = 16;
        else if (totalAvailableWidth >= widthNeededFor8)
            targetBytes = 8;
        else
            targetBytes = 4;

        if (targetBytes != _bytesPerRow)
        {
            _bytesPerRow = targetBytes;
            _vScroll.MaxValue = ((currentBlock.DataLength + _bytesPerRow - 1) / _bytesPerRow) * RowHeight;
        }

        _Render();
    }
    
    private float GetFontCharWidth(RichTextLabel label)
    {
        Font font = label.GetThemeFont("normal_font");
        int fontSize = label.GetThemeFontSize("normal_font_size");
    
        return font.GetStringSize("X", HorizontalAlignment.Left, -1, fontSize).X;
    }
}