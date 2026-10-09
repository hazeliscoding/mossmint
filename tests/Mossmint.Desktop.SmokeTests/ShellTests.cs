using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mossmint.Desktop.ViewModels;
using Mossmint.Desktop.Views;

namespace Mossmint.Desktop.SmokeTests;

public class ShellTests
{
    [AvaloniaFact]
    public void A_clean_install_opens_on_the_welcome_screen()
    {
        var (window, _) = OpenMainWindow();

        window.GetVisualDescendants().OfType<WelcomeView>().Should().ContainSingle();
        VisibleTexts(window).Should().Contain("Make room to grow.");
    }

    [AvaloniaTheory]
    [InlineData("Start fresh", "Overview")]
    [InlineData("Import my finances", "Imports")]
    public void Each_welcome_choice_opens_its_page(string choice, string expectedTitle)
    {
        var (window, _) = OpenMainWindow();

        var button = window.GetVisualDescendants().OfType<Button>()
            .Single(b => AutomationName(b) == choice);
        button.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        PageTitle(window).Should().Be(expectedTitle);
    }

    [AvaloniaFact]
    public void Every_destination_navigates_and_renders_its_page()
    {
        var (window, vm) = OpenMainWindow();
        vm.OpenApp(Destination.Overview);

        foreach (var item in vm.Shell.NavItems)
        {
            vm.Shell.SelectedItem = item;
            Dispatcher.UIThread.RunJobs();

            PageTitle(window).Should().Be(item.Label);
        }

        vm.Shell.NavItems.Select(i => i.Destination).Should().BeEquivalentTo(Enum.GetValues<Destination>());
    }

    [AvaloniaFact]
    public void Ctrl_number_shortcuts_switch_sections()
    {
        var (window, vm) = OpenMainWindow();
        vm.OpenApp(Destination.Overview);
        window.Focus();

        window.KeyPressQwerty(PhysicalKey.Digit3, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        vm.Shell.SelectedItem!.Destination.Should().Be(Destination.Paychecks);
        PageTitle(window).Should().Be("Paychecks");
    }

    [AvaloniaFact]
    public void The_sidebar_collapses_to_icons()
    {
        var (window, vm) = OpenMainWindow();
        vm.OpenApp(Destination.Overview);

        vm.Shell.ToggleSidebarCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var sidebar = window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("sidebar"));
        sidebar.Bounds.Width.Should().Be(48);
    }

    internal static (MainWindow Window, MainWindowViewModel ViewModel) OpenMainWindow()
    {
        var window = ((App)Application.Current!).CreateMainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, (MainWindowViewModel)window.DataContext!);
    }

    private static string? AutomationName(Control control) =>
        Avalonia.Automation.AutomationProperties.GetName(control);

    private static IEnumerable<string?> VisibleTexts(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text);

    private static string? PageTitle(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>().SingleOrDefault(t => t.Classes.Contains("page-title"))?.Text;
}
