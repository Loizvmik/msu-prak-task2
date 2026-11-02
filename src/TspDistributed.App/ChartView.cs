using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Globalization;

namespace TspDistributed.App;

public sealed class ChartView : Control
{
    public sealed record Sample(int Generation, double Best, double Average);

    private static readonly IPen BestPen = new Pen(new SolidColorBrush(Color.Parse("#2F6FB3")), 2);
    private static readonly IPen AveragePen = new Pen(new SolidColorBrush(Color.Parse("#E08A2C")), 1.5);
    private static readonly IPen AxisPen = new Pen(new SolidColorBrush(Color.Parse("#999999")), 1);
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#E4E4E0")), 1);
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#555555"));
    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#FAFAF7"));

    private IReadOnlyList<Sample> _samples = [];

    public bool ShowAverage { get; set; } = true;

    public void Show(IReadOnlyList<Sample> samples)
    {
        _samples = samples;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Background, bounds);
        var typeface = new Typeface(FontFamily.Default);

        FormattedText Text(string s, IBrush? brush = null) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, brush ?? TextBrush);

        var plot = new Rect(bounds.X + 70, bounds.Y + 26, Math.Max(bounds.Width - 88, 10), Math.Max(bounds.Height - 56, 10));

        context.DrawText(Text("— лучший маршрут", BestPen.Brush), new Point(plot.X, 6));
        if (ShowAverage)
            context.DrawText(Text("— средняя по островам", AveragePen.Brush), new Point(plot.X + 140, 6));

        if (_samples.Count == 0)
        {
            context.DrawText(Text("Запустите поиск, чтобы увидеть график"), plot.Center - new Point(100, 6));
            return;
        }

        double min = _samples.Min(s => s.Best);
        double max = ShowAverage ? _samples.Max(s => s.Average) : _samples.Max(s => s.Best);
        if (max - min < 1e-9)
        {
            min -= 1;
            max += 1;
        }
        double pad = (max - min) * 0.05;
        min -= pad;
        max += pad;

        int g0 = _samples[0].Generation, g1 = _samples[^1].Generation;
        double gSpan = Math.Max(g1 - g0, 1);
        Point Map(int g, double v) => new(plot.X + (g - g0) / gSpan * plot.Width, plot.Bottom - (v - min) / (max - min) * plot.Height);

        const int ticks = 4;
        for (int i = 0; i <= ticks; i++)
        {
            double v = min + (max - min) * i / ticks;
            double y = plot.Bottom - plot.Height * i / ticks;
            context.DrawLine(GridPen, new Point(plot.X, y), new Point(plot.Right, y));
            var t = Text(v.ToString("0.#", CultureInfo.CurrentCulture));
            context.DrawText(t, new Point(plot.X - t.Width - 6, y - t.Height / 2));
        }
        context.DrawLine(AxisPen, plot.BottomLeft, plot.BottomRight);
        context.DrawLine(AxisPen, plot.BottomLeft, plot.TopLeft);

        var left = Text(g0.ToString(CultureInfo.CurrentCulture));
        var right = Text(g1.ToString(CultureInfo.CurrentCulture));
        context.DrawText(left, new Point(plot.X, plot.Bottom + 4));
        context.DrawText(right, new Point(plot.Right - right.Width, plot.Bottom + 4));
        var caption = Text("поколение");
        context.DrawText(caption, new Point(plot.Center.X - caption.Width / 2, plot.Bottom + 4));

        if (ShowAverage) DrawSeries(context, AveragePen, s => s.Average, Map);
        DrawSeries(context, BestPen, s => s.Best, Map);
    }

    private void DrawSeries(DrawingContext context, IPen pen, Func<Sample, double> value, Func<int, double, Point> map)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(map(_samples[0].Generation, value(_samples[0])), false);
            for (int i = 1; i < _samples.Count; i++)
                ctx.LineTo(map(_samples[i].Generation, value(_samples[i])));
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }
}
