using Avalonia.Headless.XUnit;
using Mossmint.Desktop.Controls;
using Mossmint.Desktop.ViewModels;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;

namespace Mossmint.Desktop.SmokeTests;

public class BankFileFlowTests
{
    [AvaloniaFact]
    public async Task A_bank_file_is_matched_previewed_imported_and_remembered()
    {
        using var app = new Harness();
        var imports = await OpenImports(app);
        var checking = new Account(new AccountId(Guid.CreateVersion7()), "Everyday Checking", AccountKind.Checking, Currency.Usd);
        app.Services.Context.Current.Data.AddAccount(checking);
        app.Dialogs.NextBankFile = Write(app, "october.csv", "Date,Description,Amount\n10/01/2026,Paycheck,1200.00\n10/15/2026,Corner Cafe,-4.50\n");

        await imports.ChooseBankFileCommand.ExecuteAsync(null);
        Harness.Pump();

        var file = imports.BankFile!;
        file.DateColumn.Label.Should().StartWith("Date");
        file.DescriptionColumn.Label.Should().StartWith("Description");
        file.IsDateAmbiguous.Should().BeFalse();
        app.VisibleTexts().Should().Contain("Match the columns");
        var destination = file.Destinations.Should().ContainSingle().Subject;
        destination.Selected.Should().BeNull("nothing is imported into an account the user didn't choose");
        await imports.PreviewBankFileCommand.ExecuteAsync(null);
        file.Error.Should().NotBeNull();
        imports.Preview.Should().BeNull();

        destination.Selected = destination.Choices.Single(c => c.Account?.Id == checking.Id);
        await imports.PreviewBankFileCommand.ExecuteAsync(null);
        Harness.Pump();

        imports.Preview!.CanCommit.Should().BeTrue();
        imports.Preview.Changes.Should().Contain(c => c.Label == "New · transactions" && c.Count == "2");
        app.Services.Context.Current.Data.Transactions(checking.Id).Should().BeEmpty("a preview saves nothing");

        await imports.ImportCommand.ExecuteAsync(null);
        Harness.Pump();

        imports.StatusTone.Should().Be(StatusTone.Healthy);
        imports.BankFile.Should().BeNull();
        app.Services.Context.Current.Data.Transactions(checking.Id).Select(t => t.Amount).Should().BeEquivalentTo([Money.Usd(1200m), Money.Usd(-4.50m)]);

        app.Dialogs.NextBankFile = Write(app, "november.csv", "Date,Description,Amount\n11/01/2026,Paycheck,1200.00\n");
        await imports.ChooseBankFileCommand.ExecuteAsync(null);

        imports.BankFile!.Summary.Should().Contain("last time");
        imports.BankFile.Destinations.Single().Selected!.Account!.Id.Should().Be(checking.Id);
    }

    [AvaloniaFact]
    public async Task An_imported_entry_can_be_voided_and_stays_void_on_the_next_import()
    {
        using var app = new Harness();
        var imports = await OpenImports(app);
        var checking = new Account(new AccountId(Guid.CreateVersion7()), "Everyday Checking", AccountKind.Checking, Currency.Usd);
        app.Services.Context.Current.Data.AddAccount(checking);
        app.Dialogs.NextBankFile = Write(app, "october.csv", "Date,Description,Amount\n10/01/2026,Paycheck,1200.00\n10/15/2026,Corner Cafe,-4.50\n");
        await imports.ChooseBankFileCommand.ExecuteAsync(null);
        var row = imports.BankFile!.Destinations.Single();
        row.Selected = row.Choices.Single(c => c.Account?.Id == checking.Id);
        await imports.PreviewBankFileCommand.ExecuteAsync(null);
        await imports.ImportCommand.ExecuteAsync(null);

        app.Screen<ShellViewModel>().Navigate(Destination.Transactions);
        var ledger = (TransactionsPageViewModel)app.Screen<ShellViewModel>().CurrentPage!;
        var cafe = ledger.Rows.Single(r => r.Description == "Corner Cafe");
        cafe.CanVoid.Should().BeTrue("an imported entry can be removed too");
        ledger.VoidCommand.Execute(cafe);

        app.Screen<ShellViewModel>().Navigate(Destination.Imports);
        await imports.ChooseBankFileCommand.ExecuteAsync(null);
        await imports.PreviewBankFileCommand.ExecuteAsync(null);
        await imports.ImportCommand.ExecuteAsync(null);

        app.Services.Context.Current.Data.Transactions(checking.Id).Single(t => t.Description == "Corner Cafe").Status.Should().Be(TransactionStatus.Void);
    }

    [AvaloniaFact]
    public async Task An_ambiguous_date_is_flagged_and_a_new_account_needs_a_name()
    {
        using var app = new Harness();
        var imports = await OpenImports(app);
        app.Dialogs.NextBankFile = Write(app, "export.csv", "Date,Description,Amount\n01/02/2026,Cafe,-4.50\n03/04/2026,Shop,-1.00\n");

        await imports.ChooseBankFileCommand.ExecuteAsync(null);

        var file = imports.BankFile!;
        file.IsDateAmbiguous.Should().BeTrue();
        file.DateFormats.Select(f => f.Label).Should().Equal("M/d/yyyy · 01/02/2026 is 2 Jan 2026", "d/M/yyyy · 01/02/2026 is 1 Feb 2026");
        var destination = file.Destinations.Single();
        destination.Selected = destination.Choices.Single(c => c.IsNew);
        destination.NewName = " ";
        await imports.PreviewBankFileCommand.ExecuteAsync(null);
        file.Error.Should().Contain("name");

        destination.NewName = "Joint Checking";
        file.DateFormat = file.DateFormats[1];
        await imports.PreviewBankFileCommand.ExecuteAsync(null);

        imports.Preview!.Changes.Should().Contain(c => c.Label == "New · account");
        imports.Preview.Plan.Batch.Transactions.Select(t => t.Date).Should().Equal(new DateOnly(2026, 2, 1), new DateOnly(2026, 4, 3));
    }

    [AvaloniaFact]
    public async Task A_statement_file_only_asks_where_each_account_goes()
    {
        using var app = new Harness();
        var imports = await OpenImports(app);
        const string Statement = "OFXHEADER:100\nDATA:OFXSGML\nVERSION:102\nCHARSET:1252\n\n<OFX><CREDITCARDMSGSRSV1><CCSTMTTRNRS><CCSTMTRS><CURDEF>USD" +
            "<CCACCTFROM><ACCTID>4111000000004321</CCACCTFROM><BANKTRANLIST><DTSTART>20261001<DTEND>20261012" +
            "<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20261004<TRNAMT>-25.10<FITID>CC-1<NAME>GREEN GROCER</STMTTRN></BANKTRANLIST>" +
            "<LEDGERBAL><BALAMT>-25.10<DTASOF>20261012</LEDGERBAL></CCSTMTRS></CCSTMTTRNRS></CREDITCARDMSGSRSV1></OFX>";
        app.Dialogs.NextBankFile = Write(app, "card.qfx", Statement);

        await imports.ChooseBankFileCommand.ExecuteAsync(null);
        Harness.Pump();

        imports.BankFile.Should().BeNull();
        var statement = imports.StatementFile!;
        app.VisibleTexts().Should().Contain("Choose the accounts");
        var row = statement.Destinations.Should().ContainSingle().Subject;
        row.Label.Should().Contain("Card ending 4321").And.NotContain("4111000000004321");
        row.Selected = row.Choices.Single(c => c.IsNew);
        row.NewKind.Kind.Should().Be(AccountKind.CreditCard);

        await imports.PreviewStatementFileCommand.ExecuteAsync(null);
        await imports.ImportCommand.ExecuteAsync(null);
        Harness.Pump();

        var card = app.Services.Context.Current.Data.Accounts().Single(a => a.Kind == AccountKind.CreditCard);
        app.Services.Context.Current.Data.Balances(card.Id).Single().Balance.Should().Be(Money.Usd(25.10m));
        app.Services.Context.Current.Data.Transactions(card.Id).Single().Amount.Should().Be(Money.Usd(25.10m));

        await imports.ChooseBankFileCommand.ExecuteAsync(null);
        imports.StatementFile!.Destinations.Single().Selected!.Account!.Id.Should().Be(card.Id, "the statement's account is remembered");
    }

    private static async Task<ImportsPageViewModel> OpenImports(Harness app)
    {
        app.CreateProfile();
        app.Open();
        var unlock = app.Screen<UnlockViewModel>();
        unlock.Passphrase = Harness.Passphrase;
        await unlock.UnlockCommand.ExecuteAsync(null);
        Harness.Pump();
        var shell = app.Screen<ShellViewModel>();
        shell.Navigate(Destination.Imports);
        return (ImportsPageViewModel)shell.CurrentPage!;
    }

    private static string Write(Harness app, string name, string text)
    {
        var path = Path.Combine(app.Root, name);
        File.WriteAllText(path, text);
        return path;
    }
}
