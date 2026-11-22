using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MMUCS.Core.Audio;

namespace MMUCS.Desktop.Controls;

/// <summary>
/// An iMUSE sound's map along its length: regions as alternating bars, markers as labelled ticks above
/// them, jumps as arcs from where they leave to where they land (hooked jumps in another colour, with
/// their hook), and the play position. Click or drag to seek.
/// </summary>
public sealed class MediaTimeline : Control
{
    public static readonly StyledProperty<ImusSound> SoundProperty =
        AvaloniaProperty.Register<MediaTimeline, ImusSound>(nameof(Sound));
    public static readonly StyledProperty<long> PositionProperty =
        AvaloniaProperty.Register<MediaTimeline, long>(nameof(Position));

    public ImusSound Sound { get => GetValue(SoundProperty); set => SetValue(SoundProperty, value); }
    /// <summary>The play position in sample frames.</summary>
    public long Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }

    /// <summary>The user clicked or dragged to a frame.</summary>
    public event Action<long> SeekRequested;

    private const double BarTop = 64, BarHeight = 28, Pad = 8;
    private bool _dragging;
    private IBrush _regionA, _regionB, _hooked, _unhooked, _text, _subtle, _playhead, _back;

    static MediaTimeline() => AffectsRender<MediaTimeline>(SoundProperty, PositionProperty);

    public MediaTimeline()
    {
        ClipToBounds = true;
        MinHeight = BarTop + BarHeight + 28;
        this.OnThemeChanged(() => { _back = null; InvalidateVisual(); });
    }

    private void EnsureBrushes()
    {
        if (_back != null) return;
        _back = this.FindBrush("ListBackgroundBrush");
        _regionA = new SolidColorBrush(this.FindColor("TagRoomBrush"), 0.55);
        _regionB = new SolidColorBrush(this.FindColor("TagScriptBrush"), 0.55);
        _hooked = this.FindBrush("TagCostumeBrush");
        _unhooked = this.FindBrush("StrongTextBrush");
        _text = this.FindBrush("StrongTextBrush");
        _subtle = this.FindBrush("SubtleTextBrush");
        _playhead = this.FindBrush("ReviewMarkBrush");
    }

    private double Width0 => Math.Max(1, Bounds.Width - Pad * 2);
    private double X(long frame, long total) => Pad + Width0 * frame / Math.Max(1, total);

    public override void Render(DrawingContext ctx)
    {
        EnsureBrushes();
        ctx.FillRectangle(_back, new Rect(Bounds.Size));
        var s = Sound;
        if (s == null) return;
        long total = Math.Max(1, s.FrameCount);
        long F(long offset) => s.ToFrame(offset);

        // Regions.
        for (int i = 0; i < s.Regions.Count; i++)
        {
            var r = s.Regions[i];
            double x0 = X(F(r.Offset), total), x1 = X(F(r.Offset + r.Length), total);
            var rect = new Rect(x0, BarTop, Math.Max(1, x1 - x0 - 1), BarHeight);
            ctx.DrawRectangle(i % 2 == 0 ? _regionA : _regionB, null, rect, 3, 3);
            if (x1 - x0 > 30) Label(ctx, $"{i + 1}", new Point(x0 + 5, BarTop + 6), _text, 11);
        }

        // Jumps: an arc above the bars from where they leave to where they land.
        foreach (var j in s.Jumps)
        {
            double from = X(F(j.Offset), total), to = X(F(j.Destination), total);
            double lift = Math.Clamp(Math.Abs(to - from) / 3, 14, BarTop - 22);
            var pen = new Pen(j.Hook == 0 ? _unhooked : _hooked, 1.5, j.Hook == 0 ? null : new DashStyle(new double[] { 4, 3 }, 0));
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(from, BarTop), false);
                g.CubicBezierTo(new Point(from, BarTop - lift), new Point(to, BarTop - lift), new Point(to, BarTop));
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, pen, geo);
            ctx.DrawLine(pen, new Point(to - 4, BarTop - 6), new Point(to, BarTop));
            ctx.DrawLine(pen, new Point(to + 4, BarTop - 6), new Point(to, BarTop));
            Label(ctx, j.Hook == 0 ? $"{j.FadeMs} ms" : $"hook {j.Hook}", new Point((from + to) / 2 - 18, BarTop - lift * 0.75 - 14), j.Hook == 0 ? _subtle : _hooked, 10);
        }

        // Markers below the bars (the scripts' trigger points), skipping labels that would overlap.
        double lastLabelEnd = double.NegativeInfinity;
        var tick = new Pen(_subtle, 1);
        foreach (var m in s.Markers)
        {
            if (m.Text.Trim('*').Length == 0) continue;
            double x = X(F(m.Offset), total);
            ctx.DrawLine(tick, new Point(x, BarTop + BarHeight), new Point(x, BarTop + BarHeight + 6));
            if (x < lastLabelEnd) continue;
            var t = Text(m.Text, _subtle, 10);
            ctx.DrawText(t, new Point(x + 2, BarTop + BarHeight + 6));
            lastLabelEnd = x + t.Width + 8;
        }

        // Play position.
        double px = X(Math.Clamp(Position, 0, total), total);
        ctx.DrawLine(new Pen(_playhead, 2), new Point(px, 4), new Point(px, BarTop + BarHeight + 4));
    }

    private static FormattedText Text(string s, IBrush brush, double size) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);

    private static void Label(DrawingContext ctx, string s, Point at, IBrush brush, double size) => ctx.DrawText(Text(s, brush, size), at);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Sound == null) return;
        _dragging = true;
        e.Pointer.Capture(this);
        Seek(e.GetPosition(this).X);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) Seek(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void Seek(double x)
    {
        long total = Math.Max(1, Sound.FrameCount);
        long frame = (long)Math.Clamp((x - Pad) / Width0 * total, 0, total - 1);
        SeekRequested?.Invoke(frame);
    }
}
