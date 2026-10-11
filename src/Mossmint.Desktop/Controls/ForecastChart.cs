using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Media;
using Mossmint.Application.Forecasting;
using Mossmint.Desktop.Services;
using Mossmint.Domain;

namespace Mossmint.Desktop.Controls;

// The spending account's balance: closing balance as a line over a soft fill,
// dipping to each day's low; the cushion dashed; paydays marked; the lowest point
// called out. The Cash Flow page has the same numbers as a table.
public sealed class ForecastChart : Control
{
    public static readonly StyledProperty<ProfileForecast?> ForecastProperty =
        AvaloniaProperty.Register<ForecastChart, ProfileForecast?>(nameof(Forecast));

    private const double Left = 80, Right = 16, Top = 22, Bottom = 26;

    static ForecastChart() => AffectsRender<ForecastChart>(ForecastProperty);

    // Colours are looked up while drawing, so redraw when the theme or a contrast theme changes them.
    public ForecastChart()
    {
        // An image to UI Automation, so Narrator reads its description; the table twin has the numbers.
        AutomationProperties.SetControlTypeOverride(this, AutomationControlType.Image);
        AutomationProperties.SetIsControlElementOverride(this, true);
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        ResourcesChanged += (_, _) => InvalidateVisual();
    }

    public ProfileForecast? Forecast
    {
        get => GetValue(ForecastProperty);
        set => SetValue(ForecastProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Forecast is not { Days.Count: > 0 } forecast)
        {
            return;
        }

        var days = forecast.Days;
        var culture = CultureInfo.CurrentCulture;
        var currency = forecast.Opening.Currency;
        var accent = Brush("Accent");
        var fill = Brush("AccentSubtle");
        var grid = Brush("BorderHairline");
        var faint = Brush("TextFaint");
        var info = Brush("SignalInfo");
        var warning = Brush("SignalWarning");
        var warningText = Brush("SignalWarningText");
        var secondary = Brush("TextSecondary");
        var mono = new Typeface(new FontFamily("avares://Mossmint.Desktop/Assets/Fonts#IBM Plex Mono"));

        var width = Bounds.Width - Left - Right;
        var height = Bounds.Height - Top - Bottom;
        var values = days.SelectMany(d => new[] { d.Low.Amount, d.Closing.Amount }).Append(forecast.Cushion.Amount).Append(forecast.Opening.Amount).ToList();
        var step = NiceStep((double)(values.Max() - Math.Min(0m, values.Min())) / 4);
        var (min, max) = (Math.Floor((double)Math.Min(0m, values.Min()) / step) * step, Math.Ceiling((double)values.Max() * 1.04 / step) * step);
        double X(int i) => Left + (width * i / Math.Max(1, days.Count - 1));
        double Y(decimal v) => Top + (height * (1 - (((double)v - min) / (max - min))));

        for (var tick = min; tick <= max + (step / 2); tick += step)
        {
            var y = Y((decimal)tick);
            context.DrawLine(new Pen(grid, 1), new Point(Left, y), new Point(Left + width, y));
            Text(context, MoneyInput.Format(new Money((decimal)tick, currency), culture, wholeUnits: true), new Point(4, y - 8), faint, mono, 11);
        }

        for (var i = 0; i < days.Count; i += 7)
        {
            Text(context, days[i].Date.ToString("d MMM", culture), new Point(X(i) - 14, Top + height + 8), faint, mono, 11);
        }

        for (var i = 0; i < days.Count; i++)
        {
            if (days[i].IsPayday)
            {
                context.DrawLine(new Pen(info, 1), new Point(X(i), Top), new Point(X(i), Top + height));
                Text(context, "PAY", new Point(X(i) + 4, Top - 2), info, mono, 11);
            }
        }

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            l.BeginFigure(new Point(X(0), Y(forecast.Opening.Amount)), false);
            a.BeginFigure(new Point(X(0), Y((decimal)min)), true);
            for (var i = 0; i < days.Count; i++)
            {
                // Each day dips to its low before closing, so the line shows the gap a bill opens before payday.
                var low = new Point(X(i), Y(days[i].Low.Amount));
                var close = new Point(X(i), Y(days[i].Closing.Amount));
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

        var cushionY = Y(forecast.Cushion.Amount);
        context.DrawLine(new Pen(faint, 1.2, new DashStyle([4, 4], 0)), new Point(Left, cushionY), new Point(Left + width, cushionY));
        // Secondary, not faint: it sits over the area fill, where faint fell to 4.2:1.
        var cushionLabel = Format($"{MoneyInput.Format(forecast.Cushion, culture)} cushion", secondary, mono, 11);
        context.DrawText(cushionLabel, new Point(Left + width - cushionLabel.Width - 4, cushionY - 18));

        var lowIndex = Math.Max(0, days.ToList().FindIndex(d => d.Date == forecast.LowestDate));
        var lowPoint = new Point(X(lowIndex), Y(forecast.Lowest.Amount));
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
        // Beside the point, on whichever side keeps it inside the plot.
        var lowLabel = Format($"Lowest {MoneyInput.Format(forecast.Lowest, culture)} · {forecast.LowestDate.ToString("d MMM", culture)}", warningText, mono, 11);
        var lowX = lowPoint.X + 10 + lowLabel.Width <= Left + width ? lowPoint.X + 10 : lowPoint.X - 10 - lowLabel.Width;
        context.DrawText(lowLabel, new Point(lowX, lowPoint.Y + 4));
    }

    // Gridlines at 1, 2, 2.5 or 5 times a power of ten, so the axis reads in round amounts.
    private static double NiceStep(double rough)
    {
        var power = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(rough, 1))));
        return new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * power).First(s => s >= rough);
    }

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Magenta;

    private static void Text(DrawingContext context, string text, Point at, IBrush brush, Typeface typeface, double size) =>
        context.DrawText(Format(text, brush, typeface, size), at);

    private static FormattedText Format(string text, IBrush brush, Typeface typeface, double size) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);
}
