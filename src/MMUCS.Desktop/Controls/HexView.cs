using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using MMUCS.Core.Diagnostics;

namespace MMUCS.Desktop.Controls;

/// <summary>
/// Virtualized hex inspector: only the visible rows are laid out and drawn, so it handles
/// multi-hundred-MB resource files without allocation spikes. Pair with a ScrollBar bound to
/// <see cref="TopRow"/>, <see cref="MaxTopRow"/> and <see cref="VisibleRows"/>.
///
/// Selection: drag in the hex or ASCII column (mirrored in the other), Shift+click / Shift+arrows
/// to extend, Ctrl+A to select all. Ctrl+C copies in the format of the column the selection was
/// made in; the right-click menu offers hex, ASCII, hex dump, C array and offset.
/// </summary>
public sealed class HexView : Control
{
    public static readonly StyledProperty<ReadOnlyMemory<byte>> DataProperty =
        AvaloniaProperty.Register<HexView, ReadOnlyMemory<byte>>(nameof(Data));
    public static readonly StyledProperty<long> BaseOffsetProperty =
        AvaloniaProperty.Register<HexView, long>(nameof(BaseOffset));
    public static readonly StyledProperty<int> HeaderLengthProperty =
        AvaloniaProperty.Register<HexView, int>(nameof(HeaderLength), 8);
    public static readonly StyledProperty<double> TopRowProperty =
        AvaloniaProperty.Register<HexView, double>(nameof(TopRow), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay,
            coerce: (o, v) => Math.Clamp(Math.Floor(v), 0, ((HexView)o).MaxTopRow));
    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<HexView, double>(nameof(FontSize), 12);

    public static readonly DirectProperty<HexView, double> MaxTopRowProperty =
        AvaloniaProperty.RegisterDirect<HexView, double>(nameof(MaxTopRow), o => o.MaxTopRow);
    public static readonly DirectProperty<HexView, double> VisibleRowsProperty =
        AvaloniaProperty.RegisterDirect<HexView, double>(nameof(VisibleRows), o => o.VisibleRows);
    public static readonly DirectProperty<HexView, string> SelectionInfoProperty =
        AvaloniaProperty.RegisterDirect<HexView, string>(nameof(SelectionInfo), o => o.SelectionInfo);

    public ReadOnlyMemory<byte> Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public long BaseOffset { get => GetValue(BaseOffsetProperty); set => SetValue(BaseOffsetProperty, value); }
    /// <summary>Number of leading bytes to tint as the block header (tag + size).</summary>
    public int HeaderLength { get => GetValue(HeaderLengthProperty); set => SetValue(HeaderLengthProperty, value); }
    public double TopRow { get => GetValue(TopRowProperty); set => SetValue(TopRowProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    private double _maxTopRow, _visibleRows;
    private string _selectionInfo;
    public double MaxTopRow { get => _maxTopRow; private set => SetAndRaise(MaxTopRowProperty, ref _maxTopRow, value); }
    public double VisibleRows { get => _visibleRows; private set => SetAndRaise(VisibleRowsProperty, ref _visibleRows, value); }
    public string SelectionInfo { get => _selectionInfo; private set => SetAndRaise(SelectionInfoProperty, ref _selectionInfo, value); }

    private enum Column { Hex, Ascii }

    private const int BytesPerRow = 16;
    private const double Pad = 8;
    /// <summary>Copies larger than this are refused (a 16 MB hex dump is already ~70 MB of text).</summary>
    private const int MaxCopyBytes = 16 * 1024 * 1024;

    // Selection is [min(anchor, caret), max(anchor, caret)]; -1 means none.
    private int _anchor = -1;
    private int _caret = -1;
    private Column _activeColumn = Column.Hex;
    private bool _dragging;

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");
    private static readonly IBrush OffsetBrush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
    private static readonly IBrush HexBrush = new SolidColorBrush(Color.FromRgb(220, 220, 225));
    private static readonly IBrush ZeroBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
    private static readonly IBrush AsciiBrush = new SolidColorBrush(Color.FromRgb(150, 200, 255));
    private static readonly IBrush HeaderBg = new SolidColorBrush(Color.FromArgb(50, 255, 170, 0));
    private static readonly IBrush SelectBg = new SolidColorBrush(Color.FromArgb(140, 60, 140, 255));
    private static readonly IBrush MirrorBg = new SolidColorBrush(Color.FromArgb(70, 60, 140, 255));
    private static readonly IBrush StripeBg = new SolidColorBrush(Color.FromArgb(10, 255, 255, 255));
    private static readonly IPen CaretPen = new Pen(new SolidColorBrush(Color.FromArgb(200, 140, 190, 255)), 1);

    static HexView()
    {
        AffectsRender<HexView>(DataProperty, BaseOffsetProperty, TopRowProperty, HeaderLengthProperty, FontSizeProperty);
        FocusableProperty.OverrideDefaultValue<HexView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<HexView>(true);
    }

    public HexView()
    {
        ContextMenu = BuildContextMenu();
    }

    private long TotalRows => (Data.Length + BytesPerRow - 1) / BytesPerRow;
    private Typeface Typeface => new(Mono);
    private double LineHeight => Math.Ceiling(FontSize * 1.45);
    private double CharWidth => new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, FontSize, null).WidthIncludingTrailingWhitespace;

    private bool HasSelection => _anchor >= 0 && _caret >= 0 && Data.Length > 0;
    private int SelStart => Math.Min(_anchor, _caret);
    private int SelEnd => Math.Max(_anchor, _caret);
    private int SelLength => HasSelection ? SelEnd - SelStart + 1 : 0;

    // ── Layout helpers ───────────────────────────────────────────────────────

    private (double offsetX, double hexX, double asciiX, double cw) Columns()
    {
        double cw = CharWidth;
        double hexX = Pad + 10 * cw;
        return (Pad, hexX, hexX + (BytesPerRow * 3 + 2) * cw, cw);
    }

    /// <summary>X of byte column c; one extra char of gap separates the two 8-byte groups.</summary>
    private static double ByteX(double hexX, double cw, int c) => hexX + c * 3 * cw + (c >= 8 ? cw : 0);

    /// <summary>
    /// Maps a point to a byte index. With <paramref name="clamp"/> (dragging) points outside the
    /// columns or above/below the view snap to the nearest byte instead of returning -1.
    /// </summary>
    private int HitTest(Point p, bool clamp, out Column column)
    {
        var (_, hexX, asciiX, cw) = Columns();
        column = p.X >= asciiX - cw ? Column.Ascii : Column.Hex;
        if (Data.Length == 0) return -1;

        long row = (long)Math.Floor(p.Y / LineHeight) + (long)TopRow;
        int col;
        if (column == Column.Ascii)
        {
            col = (int)Math.Floor((p.X - asciiX) / cw);
        }
        else
        {
            double rel = p.X - hexX;
            col = rel < 8 * 3 * cw
                ? (int)Math.Floor(rel / (3 * cw))
                : 8 + (int)Math.Floor((rel - (8 * 3 + 1) * cw) / (3 * cw));
        }

        if (!clamp && (col < 0 || col >= BytesPerRow || row < 0)) return -1;
        col = Math.Clamp(col, 0, BytesPerRow - 1);
        row = Math.Clamp(row, 0, TotalRows - 1);
        long index = row * BytesPerRow + col;
        if (index >= Data.Length) return clamp ? Data.Length - 1 : -1;
        return (int)index;
    }

    // ── Property / size changes ──────────────────────────────────────────────

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataProperty)
        {
            _anchor = _caret = -1;
            SelectionInfo = null;
            UpdateScrollMetrics();
            SetCurrentValue(TopRowProperty, 0d);
        }
        else if (change.Property == FontSizeProperty)
        {
            UpdateScrollMetrics();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateScrollMetrics();
    }

    private void UpdateScrollMetrics()
    {
        double visible = Math.Max(1, Math.Floor(Bounds.Height / LineHeight));
        VisibleRows = visible;
        MaxTopRow = Math.Max(0, TotalRows - visible);
        CoerceValue(TopRowProperty);
    }

    // ── Selection ────────────────────────────────────────────────────────────

    private void SetSelection(int anchor, int caret, bool scrollToCaret = true)
    {
        int max = Data.Length - 1;
        if (max < 0) return;
        _anchor = Math.Clamp(anchor, 0, max);
        _caret = Math.Clamp(caret, 0, max);
        if (scrollToCaret) EnsureVisible(_caret);
        UpdateSelectionInfo();
        InvalidateVisual();
    }

    public void SelectAll()
    {
        if (Data.Length == 0) return;
        SetSelection(0, Data.Length - 1, scrollToCaret: false);
    }

    public void ClearSelection()
    {
        _anchor = _caret = -1;
        UpdateSelectionInfo();
        InvalidateVisual();
    }

    private void EnsureVisible(int index)
    {
        long row = index / BytesPerRow;
        if (row < TopRow) TopRow = row;
        else if (row >= TopRow + VisibleRows) TopRow = row - VisibleRows + 1;
    }

    private void UpdateSelectionInfo()
    {
        if (!HasSelection) { SelectionInfo = null; return; }

        var span = Data.Span;
        int i = SelStart;
        var sb = new StringBuilder();
        if (SelLength > 1)
            sb.Append($"0x{BaseOffset + SelStart:X8}–0x{BaseOffset + SelEnd:X8}  ({SelLength:N0} bytes)   ");
        else
            sb.Append($"@0x{BaseOffset + i:X8}   ");

        sb.Append($"u8 {span[i]}  i8 {(sbyte)span[i]}");
        if (i + 2 <= span.Length)
            sb.Append($"  u16le {BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(i, 2))}");
        if (i + 4 <= span.Length)
        {
            sb.Append($"  u32le {BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(i, 4))}");
            sb.Append($"  u32be {BinaryPrimitives.ReadUInt32BigEndian(span.Slice(i, 4))}");
        }
        SelectionInfo = sb.ToString();
    }

    // ── Input ────────────────────────────────────────────────────────────────

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        TopRow -= e.Delta.Y * 3;
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        int index = HitTest(point.Position, clamp: false, out var column);

        if (point.Properties.IsRightButtonPressed)
        {
            // Right-clicking outside the selection selects that byte first, so the menu acts on it.
            if (index >= 0 && (!HasSelection || index < SelStart || index > SelEnd))
            {
                _activeColumn = column;
                SetSelection(index, index, scrollToCaret: false);
            }
            return; // the ContextMenu opens on release
        }

        if (!point.Properties.IsLeftButtonPressed) return;
        if (index < 0) { ClearSelection(); return; }

        _activeColumn = column;
        bool extend = e.KeyModifiers.HasFlag(KeyModifiers.Shift) && HasSelection;
        SetSelection(extend ? _anchor : index, index, scrollToCaret: false);
        _dragging = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;

        var p = e.GetPosition(this);
        // Auto-scroll while dragging past the top or bottom edge.
        if (p.Y < 0) TopRow -= 1;
        else if (p.Y > Bounds.Height) TopRow += 1;

        int index = HitTest(p, clamp: true, out _);
        if (index >= 0 && index != _caret) SetSelection(_anchor, index, scrollToCaret: false);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
        {
            _dragging = false;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl && e.Key == Key.C) { _ = CopyAsync(_activeColumn == Column.Ascii ? CopyFormat.Ascii : CopyFormat.Hex); e.Handled = true; return; }
        if (ctrl && e.Key == Key.A) { SelectAll(); e.Handled = true; return; }
        if (e.Key == Key.Escape) { ClearSelection(); e.Handled = true; return; }

        int page = (int)Math.Max(1, VisibleRows - 1) * BytesPerRow;
        int caret = HasSelection ? _caret : (int)TopRow * BytesPerRow;
        int target = e.Key switch
        {
            Key.Left => caret - 1,
            Key.Right => caret + 1,
            Key.Up => caret - BytesPerRow,
            Key.Down => caret + BytesPerRow,
            Key.PageUp => caret - page,
            Key.PageDown => caret + page,
            Key.Home => ctrl ? 0 : caret - caret % BytesPerRow,
            Key.End => ctrl ? Data.Length - 1 : caret - caret % BytesPerRow + BytesPerRow - 1,
            _ => int.MinValue,
        };
        if (target == int.MinValue || Data.Length == 0) return;

        target = Math.Clamp(target, 0, Data.Length - 1);
        SetSelection(shift && HasSelection ? _anchor : target, target);
        e.Handled = true;
    }

    // ── Copy ─────────────────────────────────────────────────────────────────

    private enum CopyFormat { Hex, HexCompact, Ascii, HexDump, CArray, Offset }

    private ContextMenu BuildContextMenu()
    {
        MenuItem Item(string header, CopyFormat format, string gesture = null)
        {
            var item = new MenuItem { Header = header };
            if (gesture != null) item.InputGesture = KeyGesture.Parse(gesture);
            item.Click += (_, _) => _ = CopyAsync(format);
            return item;
        }

        var selectAll = new MenuItem { Header = "Select all", InputGesture = KeyGesture.Parse("Ctrl+A") };
        selectAll.Click += (_, _) => SelectAll();

        var menu = new ContextMenu();
        menu.Items.Add(Item("Copy hex", CopyFormat.Hex, "Ctrl+C"));
        menu.Items.Add(Item("Copy hex (no spaces)", CopyFormat.HexCompact));
        menu.Items.Add(Item("Copy ASCII", CopyFormat.Ascii));
        menu.Items.Add(Item("Copy hex dump", CopyFormat.HexDump));
        menu.Items.Add(Item("Copy as C array", CopyFormat.CArray));
        menu.Items.Add(Item("Copy offset", CopyFormat.Offset));
        menu.Items.Add(new Separator());
        menu.Items.Add(selectAll);
        // Copy items need a selection; Select all needs data.
        menu.Opening += (_, _) =>
        {
            foreach (var o in menu.Items)
                if (o is MenuItem m) m.IsEnabled = m == selectAll ? Data.Length > 0 : HasSelection;
        };
        return menu;
    }

    private async System.Threading.Tasks.Task CopyAsync(CopyFormat format)
    {
        if (!HasSelection) return;
        if (SelLength > MaxCopyBytes)
        {
            Log.Error($"Selection is too large to copy ({SelLength:N0} bytes; limit {MaxCopyBytes / (1024 * 1024)} MB).");
            return;
        }

        string text = Format(format, Data.Span.Slice(SelStart, SelLength), BaseOffset + SelStart);
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        await clipboard.SetTextAsync(text);
        Log.Info(format == CopyFormat.Offset ? $"Copied offset {text}" : $"Copied {SelLength:N0} bytes as {Describe(format)}.");
    }

    private static string Describe(CopyFormat f) => f switch
    {
        CopyFormat.Hex => "hex",
        CopyFormat.HexCompact => "hex",
        CopyFormat.Ascii => "ASCII",
        CopyFormat.HexDump => "a hex dump",
        CopyFormat.CArray => "a C array",
        _ => "text",
    };

    private static char Printable(byte b) => b >= 0x20 && b < 0x7F ? (char)b : '.';

    private static string Format(CopyFormat format, ReadOnlySpan<byte> bytes, long startOffset)
    {
        var sb = new StringBuilder();
        switch (format)
        {
            case CopyFormat.Hex:
                for (int i = 0; i < bytes.Length; i++)
                {
                    if (i > 0) sb.Append(i % BytesPerRow == 0 ? '\n' : ' ');
                    sb.Append(bytes[i].ToString("X2"));
                }
                break;

            case CopyFormat.HexCompact:
                sb.Append(Convert.ToHexString(bytes));
                break;

            case CopyFormat.Ascii:
                for (int i = 0; i < bytes.Length; i++)
                {
                    if (i > 0 && i % BytesPerRow == 0) sb.Append('\n');
                    sb.Append(Printable(bytes[i]));
                }
                break;

            case CopyFormat.HexDump:
            {
                // Rows aligned to absolute 16-byte boundaries, like the view; bytes outside the selection are blank.
                long first = startOffset - startOffset % BytesPerRow;
                long end = startOffset + bytes.Length;
                for (long row = first; row < end; row += BytesPerRow)
                {
                    sb.Append(row.ToString("X8")).Append("  ");
                    var ascii = new StringBuilder(BytesPerRow);
                    for (int c = 0; c < BytesPerRow; c++)
                    {
                        long abs = row + c;
                        if (abs >= startOffset && abs < end)
                        {
                            byte b = bytes[(int)(abs - startOffset)];
                            sb.Append(b.ToString("X2")).Append(' ');
                            ascii.Append(Printable(b));
                        }
                        else
                        {
                            sb.Append("   ");
                            ascii.Append(' ');
                        }
                        if (c == 7) sb.Append(' ');
                    }
                    sb.Append(' ').Append(ascii.ToString().TrimEnd()).Append('\n');
                }
                break;
            }

            case CopyFormat.CArray:
                sb.Append($"// 0x{startOffset:X8}, {bytes.Length} bytes\n");
                sb.Append($"static const unsigned char data[{bytes.Length}] = {{\n");
                for (int i = 0; i < bytes.Length; i++)
                {
                    if (i % BytesPerRow == 0) sb.Append("    ");
                    sb.Append("0x").Append(bytes[i].ToString("X2"));
                    if (i < bytes.Length - 1) sb.Append(i % BytesPerRow == BytesPerRow - 1 ? ",\n" : ", ");
                }
                sb.Append("\n};");
                break;

            case CopyFormat.Offset:
                sb.Append($"0x{startOffset:X8}");
                break;
        }
        return sb.ToString().TrimEnd('\n');
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var span = Data.Span;
        if (span.Length == 0) return;

        var (offsetX, hexX, asciiX, cw) = Columns();
        double lh = LineHeight;
        long top = (long)TopRow;
        int rows = (int)Math.Ceiling(Bounds.Height / lh) + 1;
        var tf = Typeface;
        var hex = new StringBuilder(BytesPerRow * 3);
        var ascii = new StringBuilder(BytesPerRow);

        bool hasSel = HasSelection;
        int selStart = hasSel ? SelStart : -1, selEnd = hasSel ? SelEnd : -1;
        IBrush hexSelBg = _activeColumn == Column.Hex ? SelectBg : MirrorBg;
        IBrush asciiSelBg = _activeColumn == Column.Ascii ? SelectBg : MirrorBg;

        for (int r = 0; r < rows; r++)
        {
            long row = top + r;
            long start = row * BytesPerRow;
            if (start >= span.Length) break;
            int count = (int)Math.Min(BytesPerRow, span.Length - start);
            double y = r * lh;

            if ((row & 1) == 1) ctx.FillRectangle(StripeBg, new Rect(0, y, Bounds.Width, lh));

            // Header tint (per byte, only in the first row or so).
            for (int c = 0; c < count && start + c < HeaderLength; c++)
            {
                ctx.FillRectangle(HeaderBg, new Rect(ByteX(hexX, cw, c) - cw * 0.25, y, cw * 2.5, lh));
                ctx.FillRectangle(HeaderBg, new Rect(asciiX + c * cw, y, cw, lh));
            }

            // Selection: one continuous run per row in each column (the hex run spans the group gap).
            if (hasSel && selEnd >= start && selStart < start + count)
            {
                int c0 = (int)Math.Max(0, selStart - start);
                int c1 = (int)Math.Min(count - 1, selEnd - start);
                double hx0 = ByteX(hexX, cw, c0) - cw * 0.5;
                double hx1 = ByteX(hexX, cw, c1) + cw * 2.5;
                ctx.FillRectangle(hexSelBg, new Rect(hx0, y, hx1 - hx0, lh));
                ctx.FillRectangle(asciiSelBg, new Rect(asciiX + c0 * cw, y, (c1 - c0 + 1) * cw, lh));
            }

            // Caret: outline the byte at the moving end of the selection.
            if (hasSel && _caret >= start && _caret < start + count)
            {
                int c = (int)(_caret - start);
                ctx.DrawRectangle(null, CaretPen, new Rect(ByteX(hexX, cw, c) - cw * 0.5 + 0.5, y + 0.5, cw * 3 - 1, lh - 1));
                ctx.DrawRectangle(null, CaretPen, new Rect(asciiX + c * cw + 0.5, y + 0.5, cw - 1, lh - 1));
            }

            DrawText(ctx, (BaseOffset + start).ToString("X8"), offsetX, y, tf, OffsetBrush);

            hex.Clear(); ascii.Clear();
            bool allZero = true;
            for (int c = 0; c < count; c++)
            {
                byte b = span[(int)(start + c)];
                if (b != 0) allZero = false;
                hex.Append(b.ToString("X2")).Append(c == 7 ? "  " : " ");
                ascii.Append(b >= 0x20 && b < 0x7F ? (char)b : '·');
            }
            DrawText(ctx, hex.ToString(), hexX, y, tf, allZero ? ZeroBrush : HexBrush);
            DrawText(ctx, ascii.ToString(), asciiX, y, tf, AsciiBrush);
        }
    }

    private void DrawText(DrawingContext ctx, string text, double x, double y, Typeface tf, IBrush brush)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, FontSize, brush);
        ctx.DrawText(ft, new Point(x, y + (LineHeight - ft.Height) / 2));
    }
}
