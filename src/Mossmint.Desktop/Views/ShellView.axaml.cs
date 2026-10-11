using Avalonia.Controls;
using Mossmint.Desktop.ViewModels;

namespace Mossmint.Desktop.Views;

public partial class ShellView : UserControl
{
    // Below this width the page needs the room more than the labels do (WCAG 1.4.10).
    private const double Narrow = 1200;

    public ShellView()
    {
        InitializeComponent();
    }

    // Only when the width crosses the line (or first lays out narrow), so the toggle still
    // works either side of it.
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < Narrow;
        var crossed = e.PreviousSize.Width == 0 ? narrow : narrow != e.PreviousSize.Width < Narrow;
        if (crossed && DataContext is ShellViewModel shell)
        {
            shell.IsSidebarCollapsed = narrow;
        }
    }
}
