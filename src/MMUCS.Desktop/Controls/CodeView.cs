using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.VisualTree;
using MMUCS.Desktop.Services;

namespace MMUCS.Desktop.Controls;

/// <summary>
/// Read-only monospace code view that only lays out and draws the lines in view, so scripts with
/// tens of thousands of lines stay responsive. Shows <see cref="Tokens"/> in colour (offset runs
/// dropped when <see cref="ShowOffsets"/> is off), or <see cref="Text"/> when there are no tokens.
/// Place it in a ScrollViewer. Drag to select, double-click for a word, Ctrl+C / Ctrl+A.
/// </summary>
public sealed class CodeView : Control
{
    public static readonly StyledProperty<IReadOnlyList<ScriptToken>> TokensProperty =
        AvaloniaProperty.Register<CodeView, IReadOnlyList<ScriptToken>>(nameof(Tokens));
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<CodeView, string>(nameof(Text));
    public static readonly StyledProperty<bool> ShowOffsetsProperty =
        AvaloniaProperty.Register<CodeView, bool>(nameof(ShowOffsets), true);
    public static readonly StyledProperty<IBrush> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<CodeView>();
    public static readonly StyledProperty<FontFamily> FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner<CodeView>();
    public static readonly StyledProperty<double> FontSizeProperty = TextElement.FontSizeProperty.AddOwner<CodeView>();
    public static readonly StyledProperty<IBrush> SelectionBrushProperty =
        AvaloniaProperty.Register<CodeView, IBrush>(nameof(SelectionBrush), new SolidColorBrush(Color.FromArgb(0x80, 0x26, 0x4F, 0x78)));

    public IReadOnlyList<ScriptToken> Tokens { get => GetValue(TokensProperty); set => SetValue(TokensProperty, value); }
    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool ShowOffsets { get => GetValue(ShowOffsetsProperty); set => SetValue(ShowOffsetsProperty, value); }
    public IBrush Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public IBrush SelectionBrush { get => GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }

    /// <summary>One line: its text and coloured spans (start, length, brush).</summary>
    private sealed record Line(string Text, (int Start, int Length, ScriptTokenKind Kind)[] Spans);

    private List<Line> _lines = new();
    private bool _dirty = true;
    private int _maxLength;
    private double _lineHeight, _charWidth;
    private Typeface _typeface;
    private bool _metricsValid;
    private readonly Dictionary<int, FormattedText> _cache = new();
    private IBrush[] _brushes;
    private ScrollViewer _scroll;

    // Selection as (line, column) positions; anchor is where the drag started.
    private (int Line, int Col) _anchor, _caret;
    private bool _selecting;

    static CodeView()
    {
        AffectsMeasure<CodeView>(TokensProperty, TextProperty, ShowOffsetsProperty, FontFamilyProperty, FontSizeProperty);
        AffectsRender<CodeView>(ForegroundProperty, SelectionBrushProperty);
        FocusableProperty.OverrideDefaultValue<CodeView>(true);
        CursorProperty.OverrideDefaultValue<CodeView>(new Cursor(StandardCursorType.Ibeam));
    }

    public CodeView()
    {
        var copy = new MenuItem { Header = "Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        copy.Click += (_, _) => Copy();
        var all = new MenuItem { Header = "Select all", InputGesture = new KeyGesture(Key.A, KeyModifiers.Control) };
        all.Click += (_, _) => SelectAll();
        ContextMenu = new ContextMenu { Items = { copy, all } };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TokensProperty || change.Property == TextProperty || change.Property == ShowOffsetsProperty)
        {
            _dirty = true;
            _cache.Clear();
            if (change.Property != ShowOffsetsProperty)
            {
                _anchor = _caret = default;
                if (_scroll != null) _scroll.Offset = default; // a new script starts at the top
            }
            else _anchor = _caret = default; // columns shift when the gutter comes and goes
        }
        else if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty || change.Property == ForegroundProperty)
        {
            _metricsValid = false;
            _cache.Clear();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroll = this.FindAncestorOfType<ScrollViewer>();
        if (_scroll != null) _scroll.ScrollChanged += OnScrollChanged;
        _brushes = null;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_scroll != null) _scroll.ScrollChanged -= OnScrollChanged;
        _scroll = null;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) => InvalidateVisual();

    // ── Content ──

    private void Rebuild()
    {
        _dirty = false;
        _lines = new List<Line>();
        _maxLength = 0;
        var tokens = Tokens;
        if (tokens == null)
        {
            foreach (var l in (Text ?? "").Replace("\r\n", "\n").Split('\n')) AddLine(l, Array.Empty<(int, int, ScriptTokenKind)>());
            return;
        }
        bool offsets = ShowOffsets;
        var sb = new StringBuilder();
        var spans = new List<(int, int, ScriptTokenKind)>();
        foreach (var t in tokens)
        {
            if (t.Kind == ScriptTokenKind.Offset && !offsets) continue;
            var text = t.Text;
            int from = 0;
            while (true)
            {
                int nl = text.IndexOf('\n', from);
                int end = nl < 0 ? text.Length : nl;
                if (end > from)
                {
                    if (t.Kind != ScriptTokenKind.Plain) spans.Add((sb.Length, end - from, t.Kind));
                    sb.Append(text, from, end - from);
                }
                if (nl < 0) break;
                AddLine(sb.ToString(), spans.ToArray());
                sb.Clear();
                spans.Clear();
                from = nl + 1;
            }
        }
        if (sb.Length > 0) AddLine(sb.ToString(), spans.ToArray());
    }

    private void AddLine(string text, (int, int, ScriptTokenKind)[] spans)
    {
        _lines.Add(new Line(text, spans));
        _maxLength = Math.Max(_maxLength, text.Length);
    }

    private void EnsureMetrics()
    {
        if (_metricsValid) return;
        _metricsValid = true;
        _typeface = new Typeface(FontFamily ?? FontFamily.Default);
        var probe = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, Brushes.White);
        _charWidth = probe.WidthIncludingTrailingWhitespace / 10;
        _lineHeight = Math.Ceiling(probe.Height);
    }

    private IBrush BrushFor(ScriptTokenKind kind)
    {
        if (_brushes == null)
        {
            var kinds = Enum.GetValues<ScriptTokenKind>();
            _brushes = new IBrush[kinds.Length];
            foreach (var k in kinds)
                if (k != ScriptTokenKind.Plain && this.TryFindResource($"Script{k}Brush", out var r) && r is IBrush b)
                    _brushes[(int)k] = b;
        }
        return _brushes[(int)kind];
    }

    private FormattedText Layout(int index)
    {
        if (_cache.TryGetValue(index, out var ft)) return ft;
        if (_cache.Count > 4000) _cache.Clear(); // only lines that were in view are kept; drop them now and then
        var line = _lines[index];
        ft = new FormattedText(line.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, Foreground ?? Brushes.White);
        foreach (var (start, length, kind) in line.Spans)
            if (BrushFor(kind) is { } brush) ft.SetForegroundBrush(brush, start, length);
        _cache[index] = ft;
        return ft;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_dirty) Rebuild();
        EnsureMetrics();
        return new Size(_maxLength * _charWidth + _charWidth, _lines.Count * _lineHeight);
    }

    // ── Drawing ──

    public override void Render(DrawingContext context)
    {
        if (_dirty) Rebuild();
        EnsureMetrics();
        // Transparent fill so the whole area takes pointer input.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_lines.Count == 0) return;

        // The part of this control the ScrollViewer shows.
        double top = 0, height = Bounds.Height;
        if (_scroll != null && this.TranslatePoint(default, _scroll) is { } origin)
        {
            top = Math.Max(0, -origin.Y);
            height = _scroll.Viewport.Height;
        }
        int first = Math.Clamp((int)(top / _lineHeight), 0, _lines.Count - 1);
        int last = Math.Clamp((int)((top + height) / _lineHeight) + 1, 0, _lines.Count - 1);

        var (s, e) = Ordered();
        bool hasSelection = s != e;
        for (int i = first; i <= last; i++)
        {
            double y = i * _lineHeight;
            if (hasSelection && i >= s.Line && i <= e.Line)
            {
                int c0 = i == s.Line ? s.Col : 0;
                int c1 = i == e.Line ? e.Col : _lines[i].Text.Length + 1; // +1: the line break
                if (c1 > c0) context.FillRectangle(SelectionBrush, new Rect(c0 * _charWidth, y, (c1 - c0) * _charWidth, _lineHeight));
            }
            if (_lines[i].Text.Length > 0) context.DrawText(Layout(i), new Point(0, y));
        }
    }

    // ── Selection ──

    private ((int Line, int Col) Start, (int Line, int Col) End) Ordered() =>
        _anchor.Line < _caret.Line || (_anchor.Line == _caret.Line && _anchor.Col <= _caret.Col) ? (_anchor, _caret) : (_caret, _anchor);

    private (int Line, int Col) HitTest(Point p)
    {
        if (_lines.Count == 0) return default;
        int line = Math.Clamp((int)(p.Y / _lineHeight), 0, _lines.Count - 1);
        int col = Math.Clamp((int)Math.Round(p.X / _charWidth), 0, _lines[line].Text.Length);
        return (line, col);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        var hit = HitTest(point.Position);
        if (e.ClickCount == 2) SelectWord(hit);
        else if (e.ClickCount >= 3) { _anchor = (hit.Line, 0); _caret = (hit.Line, _lines.Count > 0 ? _lines[hit.Line].Text.Length : 0); }
        else
        {
            if ((e.KeyModifiers & KeyModifiers.Shift) == 0) _anchor = hit;
            _caret = hit;
            _selecting = true;
            e.Pointer.Capture(this);
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_selecting) return;
        var p = e.GetPosition(this);
        _caret = HitTest(p);
        // Drag past the visible edge: scroll along.
        if (_scroll != null && this.TranslatePoint(p, _scroll) is { } q)
        {
            if (q.Y < 0) _scroll.Offset = _scroll.Offset.WithY(Math.Max(0, _scroll.Offset.Y - _lineHeight));
            else if (q.Y > _scroll.Viewport.Height) _scroll.Offset = _scroll.Offset.WithY(_scroll.Offset.Y + _lineHeight);
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_selecting) return;
        _selecting = false;
        e.Pointer.Capture(null);
    }

    private void SelectWord((int Line, int Col) at)
    {
        if (_lines.Count == 0) return;
        var t = _lines[at.Line].Text;
        static bool Word(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '$' or '.';
        int a = Math.Min(at.Col, t.Length), b = a;
        while (a > 0 && Word(t[a - 1])) a--;
        while (b < t.Length && Word(t[b])) b++;
        _anchor = (at.Line, a);
        _caret = (at.Line, b);
    }

    public void SelectAll()
    {
        if (_dirty) Rebuild();
        if (_lines.Count == 0) return;
        _anchor = (0, 0);
        _caret = (_lines.Count - 1, _lines[^1].Text.Length);
        InvalidateVisual();
    }

    /// <summary>The selected text, or everything when nothing is selected.</summary>
    public string SelectedText()
    {
        if (_dirty) Rebuild();
        var (s, e) = Ordered();
        if (s == e) { s = (0, 0); e = (_lines.Count - 1, _lines.Count > 0 ? _lines[^1].Text.Length : 0); }
        var sb = new StringBuilder();
        for (int i = s.Line; i <= e.Line && i < _lines.Count; i++)
        {
            var t = _lines[i].Text;
            int c0 = i == s.Line ? Math.Min(s.Col, t.Length) : 0;
            int c1 = i == e.Line ? Math.Min(e.Col, t.Length) : t.Length;
            sb.Append(t, c0, c1 - c0);
            if (i < e.Line) sb.Append(Environment.NewLine);
        }
        return sb.ToString();
    }

    public async void Copy()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(SelectedText());
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (ctrl && e.Key == Key.C) { Copy(); e.Handled = true; }
        else if (ctrl && e.Key == Key.A) { SelectAll(); e.Handled = true; }
    }
}
