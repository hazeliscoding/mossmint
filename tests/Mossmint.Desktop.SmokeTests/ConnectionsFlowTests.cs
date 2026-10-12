using Avalonia.Headless.XUnit;
using Mossmint.Connectors.Abstractions;
using Mossmint.Desktop.Controls;
using Mossmint.Desktop.ViewModels;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;

namespace Mossmint.Desktop.SmokeTests;

public class ConnectionsFlowTests
{
    [AvaloniaFact]
    public async Task SimpleFin_connects_links_previews_the_first_sync_then_syncs_straight_in()
    {
        using var app = new Harness();
        var page = await OpenConnections(app);
        var checking = new Account(new AccountId(Guid.CreateVersion7()), "Everyday Checking", AccountKind.Checking, Currency.Usd);
        app.Services.Context.Current.Data.AddAccount(checking);

        page.StartConnectCommand.Execute(null);
        page.SetupToken = "not a token";
        page.TokenHost.Should().BeNull();
        page.TokenError.Should().Contain("isn't a SimpleFIN setup token");
        page.ConnectCommand.CanExecute(null).Should().BeFalse();
        page.SetupToken = FakeConnectorProvider.Token;
        page.TokenHost.Should().Be("bridge.example");
        page.TokenError.Should().BeNull();

        await page.ConnectCommand.ExecuteAsync(null);
        Harness.Pump();

        app.VisibleTexts().Should().Contain("Choose the accounts to bring in");
        page.Linking.Should().HaveCount(2);
        page.Linking[0].Selected!.Account!.Id.Should().Be(checking.Id, "an account with the same name is suggested");
        page.Linking[1].Selected = page.Linking[1].Choices.Single(c => c.Account is null && !c.IsNew);
        await page.ContinueCommand.ExecuteAsync(null);
        Harness.Pump();

        page.Preview!.CanCommit.Should().BeTrue("the first sync is previewed");
        app.Services.Context.Current.Data.Transactions(checking.Id).Should().BeEmpty();
        await page.ImportCommand.ExecuteAsync(null);
        Harness.Pump();

        app.Services.Context.Current.Data.Transactions(checking.Id).Should().HaveCount(3);
        app.Services.Context.Current.Data.Accounts().Should().ContainSingle("the savings account was left out");
        var connection = app.Services.Context.Current.Imports.Connections().Single();
        connection.Secret.Should().Be(FakeConnectorProvider.Credential);
        connection.LastSyncAt.Should().NotBeNull();
        page.Connections.Single().Title.Should().Contain("bridge.example").And.NotContain("s3cret");
        app.Screen<ShellViewModel>().Refresh();
        app.Screen<ShellViewModel>().DataMode.Should().Be("SimpleFIN · Manual sync");

        await page.SyncCommand.ExecuteAsync(page.Connections.Single());
        Harness.Pump();

        page.Preview.Should().BeNull("later syncs come straight in");
        page.StatusTone.Should().Be(StatusTone.Warning, "the bank dropped a transaction, which waits for review");
        page.Status.Should().StartWith("Synced. 1 new transaction, 1 updated").And.Contain("1 item to review");
        app.Services.Context.Current.Data.Transactions(checking.Id).Select(t => t.Description).Should().BeEquivalentTo(
            ["Corner Grocer", "Bean There Cafe", "Fuel Stop"], "one arrived, and the dropped charge is kept out of the ledger for review");
        app.Services.Context.Current.Data.Balances(checking.Id)[0].Balance.Should().Be(Money.Usd(1_409.85m));

        page.StartDisconnectCommand.Execute(page.Connections.Single());
        page.ConfirmDisconnectCommand.Execute(null);

        app.Services.Context.Current.Imports.Connections().Should().BeEmpty();
        page.Status.Should().Contain("SimpleFIN too");
        app.Services.Context.Current.Data.Transactions(checking.Id).Should().HaveCount(3, "what a connection brought in stays");
    }

    [AvaloniaFact]
    public async Task Disconnecting_can_also_erase_what_the_connection_brought_in()
    {
        using var app = new Harness();
        var page = await OpenConnections(app);
        page.StartConnectCommand.Execute(null);
        page.SetupToken = FakeConnectorProvider.Token;
        await page.ConnectCommand.ExecuteAsync(null);
        foreach (var row in page.Linking)
        {
            row.Selected = row.Choices.Single(c => c.IsNew);
        }

        await page.ContinueCommand.ExecuteAsync(null);
        await page.ImportCommand.ExecuteAsync(null);
        var data = app.Services.Context.Current.Data;
        data.Accounts().SelectMany(a => data.Transactions(a.Id)).Should().NotBeEmpty();

        page.StartDisconnectCommand.Execute(page.Connections.Single());
        page.EraseOnDisconnect = true;
        page.ConfirmDisconnectCommand.Execute(null);

        data.Accounts().SelectMany(a => data.Transactions(a.Id)).Should().BeEmpty();
        data.Accounts().SelectMany(a => data.Balances(a.Id)).Should().BeEmpty();
        page.Status.Should().Contain("removed 3 transactions and 2 balances");
    }

    [AvaloniaFact]
    public async Task A_refused_sync_says_why_and_changes_nothing()
    {
        using var app = new Harness();
        var page = await OpenConnections(app);
        page.StartConnectCommand.Execute(null);
        page.SetupToken = FakeConnectorProvider.Token;
        await page.ConnectCommand.ExecuteAsync(null);
        foreach (var row in page.Linking)
        {
            row.Selected = row.Choices.Single(c => c.IsNew);
        }

        await page.ContinueCommand.ExecuteAsync(null);
        await page.ImportCommand.ExecuteAsync(null);
        var before = app.Services.Context.Current.Data.Counts();
        app.SimpleFin.NextFailure = new ConnectorException(ConnectorFailure.Refused, "SimpleFIN no longer accepts this connection.");

        await page.SyncCommand.ExecuteAsync(page.Connections.Single());

        page.StatusTone.Should().Be(StatusTone.Critical);
        page.Status.Should().Be("SimpleFIN no longer accepts this connection.");
        app.Services.Context.Current.Data.Counts().Should().Be(before);
    }

    private static async Task<ConnectionsPageViewModel> OpenConnections(Harness app)
    {
        app.CreateProfile();
        app.Open();
        var unlock = app.Screen<UnlockViewModel>();
        unlock.Passphrase = Harness.Passphrase;
        await unlock.UnlockCommand.ExecuteAsync(null);
        Harness.Pump();
        var shell = app.Screen<ShellViewModel>();
        shell.Navigate(Destination.Connections);
        return (ConnectionsPageViewModel)shell.CurrentPage!;
    }
}
