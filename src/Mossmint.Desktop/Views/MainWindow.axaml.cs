using Avalonia.Controls;

namespace Mossmint.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 1440×900 is taller than a 1080p screen at 125% (816 above the taskbar), which would
        // start the title bar off the top of the screen, so the first size fits the work area.
        // Not maximized: Avalonia 12 paints a window that starts maximized at its old size.
        if (Screens.Primary is { } screen)
        {
            Width = Math.Min(Width, (screen.WorkingArea.Width / screen.Scaling) - 32);
            Height = Math.Min(Height, (screen.WorkingArea.Height / screen.Scaling) - 32);
        }
    }
}
