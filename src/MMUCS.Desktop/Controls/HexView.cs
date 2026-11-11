using System;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MMUCS.Desktop.Controls;

/// <summary>
/// Virtualized hex inspector: only the visible rows are laid out and drawn, so it handles
/// multi-hundred-MB resource files without allocation spikes. Pair with a ScrollBar bound to
/// <see cref="TopRow"/>, <see cref="MaxTopRow"/> and <see cref="VisibleRows"/>.
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

    private const int BytesPerRow = 16;
    private int _selected = -1;

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");
    private static readonly IBrush OffsetBrush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
    private static readonly IBrush HexBrush = new SolidColorBrush(Color.FromRgb(220, 220, 225));
    private static readonly IBrush ZeroBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
    private static readonly IBrush AsciiBrush = new SolidColorBrush(Color.FromRgb(150, 200, 255));
    private static readonly IBrush HeaderBg = new SolidColorBrush(Color.FromArgb(50, 255, 170, 0));
    private static readonly IBrush SelectBg = new SolidColorBrush(Color.FromArgb(120, 60, 140, 255));
    private static readonly IBrush StripeBg = new SolidColorBrush(Color.FromArgb(10, 255, 255, 255));

    static HexView()
    {
        AffectsRender<HexView>(DataProperty, BaseOffsetProperty, TopRowProperty, HeaderLengthProperty, FontSizeProperty);
        FocusableProperty.OverrideDefaultValue<HexView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<HexView>(true);
    }

    private long TotalRows => (Data.Length + BytesPerRow - 1) / BytesPerRow;
    private Typeface Typeface => new(Mono);
    private double LineHeight => Math.Ceiling(FontSize * 1.45);
    private double CharWidth => new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, FontSize, null).WidthIncludingTrailingWhitespace;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataProperty)
        {
            _selected = -1;
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

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        TopRow -= e.Delta.Y * 3;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.PageDown: TopRow += VisibleRows - 1; break;
            case Key.PageUp: TopRow -= VisibleRows - 1; break;
            case Key.Home: TopRow = 0; break;
            case Key.End: TopRow = MaxTopRow; break;
            case Key.Down: TopRow += 1; break;
            case Key.Up: TopRow -= 1; break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetPosition(this);
        double cw = CharWidth;
        double hexX = Pad + 10 * cw;
        int row = (int)(p.Y / LineHeight) + (int)TopRow;
        double rel = p.X - hexX;
        if (rel < 0) return;
        int col = rel < 8 * 3 * cw
            ? (int)Math.Floor(rel / (3 * cw))
            : 8 + (int)Math.Floor((rel - (8 * 3 + 1) * cw) / (3 * cw));
        if (col < 0 || col >= BytesPerRow) return;

        int index = row * BytesPerRow + col;
        if (index < 0 || index >= Data.Length) return;
        _selected = index;
        UpdateSelectionInfo();
        InvalidateVisual();
    }

    private void UpdateSelectionInfo()
    {
        var span = Data.Span;
        int i = _selected;
        if (i < 0 || i >= span.Length) { SelectionInfo = null; return; }

        var sb = new StringBuilder();
        sb.Append($"@0x{BaseOffset + i:X8}  u8 {span[i]}  i8 {(sbyte)span[i]}");
        if (i + 2 <= span.Length)
            sb.Append($"  u16le {BitConverter.ToUInt16(span.Slice(i, 2))}");
        if (i + 4 <= span.Length)
        {
            sb.Append($"  u32le {BitConverter.ToUInt32(span.Slice(i, 4))}");
            sb.Append($"  u32be {System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(span.Slice(i, 4))}");
        }
        SelectionInfo = sb.ToString();
    }

    private const double Pad = 8;

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var span = Data.Span;
        if (span.Length == 0) return;

        double lh = LineHeight, cw = CharWidth;
        double offsetX = Pad, hexX = Pad + 10 * cw, asciiX = hexX + (BytesPerRow * 3 + 2) * cw;
        long top = (long)TopRow;
        int rows = (int)Math.Ceiling(Bounds.Height / lh) + 1;
        var tf = Typeface;
        var hex = new StringBuilder(BytesPerRow * 3);
        var ascii = new StringBuilder(BytesPerRow);

        for (int r = 0; r < rows; r++)
        {
            long row = top + r;
            long start = row * BytesPerRow;
            if (start >= span.Length) break;
            int count = (int)Math.Min(BytesPerRow, span.Length - start);
            double y = r * lh;

            if ((row & 1) == 1) ctx.FillRectangle(StripeBg, new Rect(0, y, Bounds.Width, lh));

            // Header tint and selection highlight behind the glyphs.
            for (int c = 0; c < count; c++)
            {
                long idx = start + c;
                IBrush bg = idx == _selected ? SelectBg : idx < HeaderLength ? HeaderBg : null;
                if (bg == null) continue;
                ctx.FillRectangle(bg, new Rect(ByteX(hexX, cw, c) - cw * 0.25, y, cw * 2.5, lh));
                ctx.FillRectangle(bg, new Rect(asciiX + c * cw, y, cw, lh));
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

    /// <summary>X of byte column c; one extra char of gap separates the two 8-byte groups.</summary>
    private static double ByteX(double hexX, double cw, int c) => hexX + c * 3 * cw + (c >= 8 ? cw : 0);

    private void DrawText(DrawingContext ctx, string text, double x, double y, Typeface tf, IBrush brush)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, FontSize, brush);
        ctx.DrawText(ft, new Point(x, y + (LineHeight - ft.Height) / 2));
    }
}
