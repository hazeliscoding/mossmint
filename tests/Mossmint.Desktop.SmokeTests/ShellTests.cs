using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Mossmint.Desktop.ViewModels;
using Mossmint.Desktop.Views;

namespace Mossmint.Desktop.SmokeTests;

public class ShellTests
{
    [AvaloniaFact]
    public void A_clean_install_opens_on_the_welcome_screen()
    {
        using var app = new Harness();
        var window = app.Open();

        window.GetVisualDescendants().OfType<WelcomeView>().Should().ContainSingle();
        app.VisibleTexts().Should().Contain("Make room to grow.");
    }

    [AvaloniaTheory]
    [InlineData("Start fresh")]
    [InlineData("Import my finances")]
    public void Both_welcome_choices_start_by_creating_a_profile(string choice)
    {
        using var app = new Harness();
        var window = app.Open();

        window.GetVisualDescendants().OfType<Button>().Single(b => Visuals.AutomationName(b) == choice).Command!.Execute(null);
        Harness.Pump();

        app.ViewModel.CurrentScreen.Should().BeOfType<CreateProfileViewModel>();
        app.PageTitle().Should().Be("Create your profile");
    }

    [AvaloniaFact]
    public void With_a_profile_the_app_opens_on_the_unlock_screen()
    {
        using var app = new Harness();
        app.CreateProfile();
        var window = app.Open();

        window.GetVisualDescendants().OfType<UnlockView>().Should().ContainSingle();
        app.Screen<UnlockViewModel>().Profiles.Single().Name.Should().Be("Juniper");
        app.ViewModel.OpenAppCommand.CanExecute(Destination.Overview).Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Every_destination_navigates_once_unlocked()
    {
        using var app = new Harness();
        app.CreateProfile();
        app.Open();
        var unlock = app.Screen<UnlockViewModel>();
        unlock.Passphrase = Harness.Passphrase;

        await unlock.UnlockCommand.ExecuteAsync(null);
        Harness.Pump();

        foreach (var item in app.ViewModel.Shell.NavItems)
        {
            app.ViewModel.Shell.SelectedItem = item;
            Harness.Pump();
            app.PageTitle().Should().Be(item.Label);
        }

        app.ViewModel.Shell.NavItems.Select(i => i.Destination).Should().BeEquivalentTo(Enum.GetValues<Destination>());
    }

    [AvaloniaFact]
    public async Task Ctrl_number_shortcuts_switch_sections_only_while_unlocked()
    {
        using var app = new Harness();
        app.CreateProfile();
        var window = app.Open();

        window.KeyPressQwerty(PhysicalKey.Digit3, RawInputModifiers.Control);
        Harness.Pump();
        app.ViewModel.CurrentScreen.Should().BeOfType<UnlockViewModel>();

        var unlock = app.Screen<UnlockViewModel>();
        unlock.Passphrase = Harness.Passphrase;
        await unlock.UnlockCommand.ExecuteAsync(null);
        Harness.Pump();
        window.Focus();
        window.KeyPressQwerty(PhysicalKey.Digit3, RawInputModifiers.Control);
        Harness.Pump();

        app.ViewModel.Shell.SelectedItem!.Destination.Should().Be(Destination.Paychecks);
        app.PageTitle().Should().Be("Paychecks");
    }

    [AvaloniaFact]
    public async Task Locking_returns_to_the_unlock_screen_and_closes_the_profile()
    {
        using var app = new Harness();
        app.CreateProfile();
        app.Open();
        var unlock = app.Screen<UnlockViewModel>();
        unlock.Passphrase = Harness.Passphrase;
        await unlock.UnlockCommand.ExecuteAsync(null);
        app.Services.Context.IsUnlocked.Should().BeTrue();

        app.ViewModel.Shell.LockCommand.Execute(null);
        Harness.Pump();

        app.Services.Context.IsUnlocked.Should().BeFalse();
        app.ViewModel.CurrentScreen.Should().BeOfType<UnlockViewModel>();
        app.ViewModel.OpenAppCommand.CanExecute(Destination.Overview).Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task A_wrong_passphrase_says_so_and_stays_locked()
    {
        using var app = new Harness();
        app.CreateProfile();
        app.Open();
        var unlock = app.Screen<UnlockViewModel>();
        unlock.Passphrase = "moss under the old elm";

        await unlock.UnlockCommand.ExecuteAsync(null);
        Harness.Pump();

        unlock.Error.Should().Contain("doesn't match");
        app.Services.Context.IsUnlocked.Should().BeFalse();
        app.VisibleTexts().Should().Contain(unlock.Error);
    }

    [AvaloniaFact]
    public void This_pc_unlock_opens_a_single_profile_straight_away()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var app = new Harness();
        app.CreateProfile(thisDevice: true);
        app.Open();

        app.Services.Context.IsUnlocked.Should().BeTrue();
        app.ViewModel.CurrentScreen.Should().BeOfType<ShellViewModel>();
    }

    [AvaloniaFact]
    public async Task The_sidebar_collapses_to_icons()
    {
        using var app = new Harness();
        app.CreateProfile();
        var window = app.Open();
        var unlock = app.Screen<UnlockViewModel>();
        unlock.Passphrase = Harness.Passphrase;
        await unlock.UnlockCommand.ExecuteAsync(null);

        app.ViewModel.Shell.ToggleSidebarCommand.Execute(null);
        Harness.Pump();

        window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("sidebar")).Bounds.Width.Should().Be(48);
    }

    [AvaloniaFact]
    public async Task The_sidebar_collapses_itself_in_a_narrow_window()
    {
        using var app = new Harness();
        app.CreateProfile();
        var window = app.Open();
        window.Width = 1000;
        var unlock = app.Screen<UnlockViewModel>();
        unlock.Passphrase = Harness.Passphrase;
        await unlock.UnlockCommand.ExecuteAsync(null);
        Harness.Pump();
        var sidebar = window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("sidebar"));
        var widths = new List<double> { sidebar.Bounds.Width };

        window.Width = 1440;
        Harness.Pump();
        widths.Add(sidebar.Bounds.Width);
        app.ViewModel.Shell.ToggleSidebarCommand.Execute(null);
        window.Width = 1300;
        Harness.Pump();
        widths.Add(sidebar.Bounds.Width);

        // Collapsed when it starts narrow, open once wide, and a toggle holds above the line.
        widths.Should().Equal(48, 216, 48);
    }
}
