using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Mossmint.Desktop.Controls;

// Draws a Lucide outline (24-unit grid, see Themes/Icons.axaml) at any size,
// keeping the icon set's stroke proportions.
public sealed class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    public static readonly StyledProperty<double> StrokeWidthProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeWidth), 2);

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, ForegroundProperty, StrokeWidthProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeWidth
    {
        get => GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(16, 16);

    public override void Render(DrawingContext context)
    {
        if (Data is null || Foreground is null)
        {
            return;
        }

        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        var pen = new Pen(Foreground, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            context.DrawGeometry(null, pen, Data);
        }
    }
}
