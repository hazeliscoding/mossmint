using Avalonia.Headless.XUnit;
using Mossmint.Desktop.ViewModels;

namespace Mossmint.Desktop.SmokeTests;

// The shell outlives a lock, so anything a page had in progress for one
// profile must never show, or land, in the next one unlocked.
public class ProfileSwitchTests
{
    [AvaloniaFact]
    public async Task A_sync_preview_left_open_never_reaches_another_profile()
    {
        using var app = new Harness();
        app.CreateProfile("Juniper");
        app.CreateProfile("Willow");
        var shell = await Unlock(app, "Juniper");
        shell.Navigate(Destination.Connections);
        var page = (ConnectionsPageViewModel)shell.CurrentPage!;
        page.StartConnectCommand.Execute(null);
        page.SetupToken = FakeConnectorProvider.Token;
        await page.ConnectCommand.ExecuteAsync(null);
        foreach (var row in page.Linking)
        {
            row.Selected = row.Choices.Single(c => c.IsNew);
        }

        await page.ContinueCommand.ExecuteAsync(null);
        page.Preview.Should().NotBeNull();

        app.ViewModel.Lock();
        shell = await Unlock(app, "Willow");
        shell.Navigate(Destination.Connections);

        page.Preview.Should().BeNull();
        page.Linking.Should().BeEmpty();
        page.Connections.Should().BeEmpty();
        await page.ImportCommand.ExecuteAsync(null);
        app.Services.Context.Current.Data.Accounts().Should().BeEmpty();
        app.Services.Context.Current.Imports.Connections().Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task A_bank_file_preview_left_open_never_reaches_another_profile()
    {
        using var app = new Harness();
        app.CreateProfile("Juniper");
        app.CreateProfile("Willow");
        var shell = await Unlock(app, "Juniper");
        shell.Navigate(Destination.Imports);
        var page = (ImportsPageViewModel)shell.CurrentPage!;
        var path = Path.Combine(app.Root, "october.csv");
        await File.WriteAllTextAsync(path, "Date,Description,Amount\n2026-10-01,Paycheck,1200.00\n");
        app.Dialogs.NextBankFile = path;
        await page.ChooseBankFileCommand.ExecuteAsync(null);
        var row = page.BankFile!.Destinations.Single();
        (row.Selected, row.NewName) = (row.Choices.Single(c => c.IsNew), "Checking");
        await page.PreviewBankFileCommand.ExecuteAsync(null);
        page.Preview.Should().NotBeNull();

        app.ViewModel.Lock();
        shell = await Unlock(app, "Willow");
        shell.Navigate(Destination.Imports);

        page.Preview.Should().BeNull();
        page.BankFile.Should().BeNull();
        await page.ImportCommand.ExecuteAsync(null);
        app.Services.Context.Current.Data.Accounts().Should().BeEmpty();
    }

    private static async Task<ShellViewModel> Unlock(Harness app, string name)
    {
        if (app.Window is null)
        {
            app.Open();
        }

        var unlock = app.Screen<UnlockViewModel>();
        unlock.SelectedProfile = unlock.Profiles.Single(p => p.Name == name);
        unlock.Passphrase = Harness.Passphrase;
        await unlock.UnlockCommand.ExecuteAsync(null);
        Harness.Pump();
        return app.Screen<ShellViewModel>();
    }
}
