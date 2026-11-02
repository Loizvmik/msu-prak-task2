using TspGenetic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Globalization;

namespace TspDistributed.App;

public sealed class RouteView : Control
{
    private static readonly IPen RoutePen = new Pen(new SolidColorBrush(Color.Parse("#2F6FB3")), 2);
    private static readonly IBrush CityBrush = new SolidColorBrush(Color.Parse("#D2442F"));
    private static readonly IBrush StartBrush = new SolidColorBrush(Color.Parse("#1E8C45"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#333333"));
    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#FAFAF7"));

    private DistanceMatrix? _matrix;
    private IReadOnlyList<int>? _order;

    public void Show(DistanceMatrix? matrix, Route? route)
    {
        _matrix = matrix;
        _order = route?.StartingFrom(0);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Background, bounds);
        if (_matrix == null) return;

        var points = Layout(_matrix, bounds.Deflate(28));
        if (_order != null)
        {
            for (int i = 0; i < _order.Count; i++)
                context.DrawLine(RoutePen, points[_order[i]], points[_order[(i + 1) % _order.Count]]);
        }

        bool labels = _matrix.Size <= 60;
        var typeface = new Typeface(FontFamily.Default);
        for (int i = 0; i < points.Length; i++)
        {
            double r = i == 0 ? 6 : 4;
            context.DrawEllipse(i == 0 ? StartBrush : CityBrush, null, points[i], r, r);
            if (!labels) continue;
            var text = new FormattedText(_matrix.Names[i], CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, 11, TextBrush);
            context.DrawText(text, points[i] + new Point(6, -text.Height - 1));
        }
    }

    private static Point[] Layout(DistanceMatrix matrix, Rect area)
    {
        int n = matrix.Size;
        var result = new Point[n];
        if (matrix.Points == null)
        {
            var c = area.Center;
            double radius = Math.Min(area.Width, area.Height) / 2;
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n - Math.PI / 2;
                result[i] = new Point(c.X + radius * Math.Cos(a), c.Y + radius * Math.Sin(a));
            }
            return result;
        }

        var pts = matrix.Points;
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
        double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        double w = Math.Max(maxX - minX, 1e-9), h = Math.Max(maxY - minY, 1e-9);

        double scale = Math.Min(area.Width / w, area.Height / h);
        double offX = area.X + (area.Width - w * scale) / 2;
        double offY = area.Y + (area.Height - h * scale) / 2;
        for (int i = 0; i < n; i++)
            result[i] = new Point(offX + (pts[i].X - minX) * scale, offY + (pts[i].Y - minY) * scale);
        return result;
    }
}
