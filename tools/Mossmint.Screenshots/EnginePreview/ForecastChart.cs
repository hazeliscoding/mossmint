using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Mossmint.Engine.CashFlow;

namespace Mossmint.Screenshots.EnginePreview;

// A plain checking-balance chart for the engine preview: closing balance as a
// line over a soft fill, the reserve floor dashed, paydays marked, and the
// lowest intraday point called out. The app's own chart arrives in M4.
internal sealed class ForecastChart(IReadOnlyList<CashFlowDay> days, decimal floor, IReadOnlyList<DateOnly> paydays, LowPoint lowest) : Control
{
    private const double Left = 64, Right = 16, Top = 22, Bottom = 26;

    public override void Render(DrawingContext context)
    {
        var accent = Brush("Accent");
        var fill = Brush("AccentSubtle");
        var grid = Brush("BorderHairline");
        var faint = Brush("TextFaint");
        var info = Brush("SignalInfo");
        var warning = Brush("SignalWarning");
        var mono = new Typeface(new FontFamily("avares://Mossmint.Desktop/Assets/Fonts#IBM Plex Mono"));

        var width = Bounds.Width - Left - Right;
        var height = Bounds.Height - Top - Bottom;
        var values = days.SelectMany(d => new[] { d.Low.Amount, d.Closing.Amount }).Append(floor).ToList();
        var (min, max) = ((double)Math.Min(0m, values.Min()), (double)values.Max() * 1.08);
        double X(int i) => Left + (width * i / Math.Max(1, days.Count - 1));
        double Y(decimal v) => Top + (height * (1 - (((double)v - min) / (max - min))));

        for (var step = 0; step <= 4; step++)
        {
            var value = min + ((max - min) * step / 4);
            var y = Y((decimal)value);
            context.DrawLine(new Pen(grid, 1), new Point(Left, y), new Point(Left + width, y));
            Text(context, $"${value / 1000:0.0}k", new Point(8, y - 8), faint, mono, 11);
        }

        for (var i = 0; i < days.Count; i += 7)
        {
            Text(context, days[i].Date.ToString("d MMM", CultureInfo.InvariantCulture), new Point(X(i) - 14, Top + height + 8), faint, mono, 11);
        }

        foreach (var payday in paydays)
        {
            var i = days.ToList().FindIndex(d => d.Date == payday);
            if (i < 0)
            {
                continue;
            }

            context.DrawLine(new Pen(info, 1), new Point(X(i), Top), new Point(X(i), Top + height));
            Text(context, "PAY", new Point(X(i) + 4, Top - 2), info, mono, 10);
        }

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            a.BeginFigure(new Point(X(0), Y((decimal)min)), true);
            for (var i = 0; i < days.Count; i++)
            {
                // Each day dips to its low before closing, so the line shows intraday gaps.
                var low = new Point(X(i), Y(days[i].Low.Amount));
                var close = new Point(X(i), Y(days[i].Closing.Amount));
                if (i == 0)
                {
                    l.BeginFigure(new Point(X(0), Y(days[0].Opening.Amount)), false);
                }

                l.LineTo(low);
                l.LineTo(close);
                a.LineTo(low);
                a.LineTo(close);
            }

            a.LineTo(new Point(X(days.Count - 1), Y((decimal)min)));
            a.EndFigure(true);
            l.EndFigure(false);
        }

        context.DrawGeometry(fill, null, area);
        context.DrawGeometry(null, new Pen(accent, 2, lineJoin: PenLineJoin.Round), line);

        var floorY = Y(floor);
        context.DrawLine(new Pen(faint, 1.2, new DashStyle([4, 4], 0)), new Point(Left, floorY), new Point(Left + width, floorY));
        Text(context, $"${floor:0} cushion", new Point(Left + width - 92, floorY - 18), faint, mono, 11);

        var lowIndex = days.ToList().FindIndex(d => d.Date == lowest.Date);
        var lowPoint = new Point(X(lowIndex), Y(lowest.Amount.Amount));
        var diamond = new StreamGeometry();
        using (var d = diamond.Open())
        {
            d.BeginFigure(lowPoint + new Point(0, -6), true);
            d.LineTo(lowPoint + new Point(6, 0));
            d.LineTo(lowPoint + new Point(0, 6));
            d.LineTo(lowPoint + new Point(-6, 0));
            d.EndFigure(true);
        }

        context.DrawGeometry(warning, null, diamond);
        Text(context, $"Lowest {Format.Money(lowest.Amount)} · {lowest.Date:d MMM}", lowPoint + new Point(10, 4), warning, mono, 11);
    }

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Magenta;

    private static void Text(DrawingContext context, string text, Point at, IBrush brush, Typeface typeface, double size) =>
        context.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush), at);
}
