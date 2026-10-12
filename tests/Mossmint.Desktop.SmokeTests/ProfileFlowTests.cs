using Avalonia.Headless.XUnit;
using Mossmint.Application.Setup;
using Mossmint.Desktop.ViewModels;

namespace Mossmint.Desktop.SmokeTests;

public class ProfileFlowTests
{
    [AvaloniaFact]
    public async Task A_new_user_creates_a_profile_saves_the_key_and_sets_up()
    {
        using var app = new Harness();
        app.Open();
        app.ViewModel.Welcome.StartFreshCommand.Execute(null);
        var create = app.Screen<CreateProfileViewModel>();
        (create.Name, create.Passphrase, create.Confirm) = ("Juniper", Harness.Passphrase, Harness.Passphrase);

        await create.CreateCommand.ExecuteAsync(null);
        Harness.Pump();

        var recovery = app.Screen<RecoveryKeyViewModel>();
        recovery.RecoveryKey.Should().MatchRegex("^([0-9A-Z]{4}-){7}[0-9A-Z]{4}$");
        recovery.ContinueCommand.CanExecute(null).Should().BeFalse("the key must be saved first");
        await recovery.SaveToFileCommand.ExecuteAsync(null);
        app.Dialogs.SavedTexts.Single().Should().Contain(recovery.RecoveryKey);
        recovery.Stored = true;
        recovery.ContinueCommand.Execute(null);
        Harness.Pump();

        app.ViewModel.CurrentScreen.Should().BeOfType<SetupViewModel>();
        app.Services.Context.ProfileName.Should().Be("Juniper");
    }

    [AvaloniaFact]
    public async Task Mismatched_passphrases_create_nothing()
    {
        using var app = new Harness();
        app.Open();
        app.ViewModel.Welcome.StartFreshCommand.Execute(null);
        var create = app.Screen<CreateProfileViewModel>();
        (create.Name, create.Passphrase, create.Confirm) = ("Juniper", Harness.Passphrase, "moss under the old elm");

        await create.CreateCommand.ExecuteAsync(null);

        create.Error.Should().Contain("don't match");
        app.Services.Profiles.List().Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Start_fresh_setup_stores_every_kind_of_record()
    {
        using var app = new Harness();
        app.Open();
        app.ViewModel.Welcome.StartFreshCommand.Execute(null);
        var create = app.Screen<CreateProfileViewModel>();
        (create.Name, create.Passphrase, create.Confirm) = ("Juniper", Harness.Passphrase, Harness.Passphrase);
        await create.CreateCommand.ExecuteAsync(null);
        var recovery = app.Screen<RecoveryKeyViewModel>();
        recovery.Stored = true;
        recovery.ContinueCommand.Execute(null);

        var setup = app.Screen<SetupViewModel>();
        (setup.CashName, setup.CashBalance) = ("Everyday", "1,240.55");
        setup.SaveAndContinueCommand.Execute(null);
        (setup.NetPay, setup.Pattern) = ("2,050.00", setup.Patterns.Single(p => p.Pattern == PayPattern.TwiceMonthly));
        setup.SaveAndContinueCommand.Execute(null);
        (setup.BillName, setup.BillAmount, setup.BillDueDay) = ("Rent", "1,150", 1);
        setup.AddBillCommand.Execute(null);
        (setup.BillName, setup.BillAmount, setup.BillDueDay, setup.BillIsEstimate, setup.BillLow, setup.BillHigh) = ("Electric", "70", 12, true, "60", "95");
        setup.SaveAndContinueCommand.Execute(null);
        (setup.DebtName, setup.DebtBalance, setup.DebtApr, setup.DebtPayment, setup.DebtDueDay) = ("Car loan", "9,800", "7.4", "285", 28);
        setup.AddDebtCommand.Execute(null);
        (setup.DebtIsCard, setup.DebtName, setup.DebtBalance, setup.DebtApr, setup.DebtPayment, setup.DebtDueDay, setup.DebtClosingDay) = (true, "Card", "2,300", "24.99%", "150", 3, 21);
        setup.SaveAndContinueCommand.Execute(null);
        (setup.SavingsPerPaycheck, setup.Cushion) = ("150", "300");
        setup.SaveAndContinueCommand.Execute(null);

        setup.Error.Should().BeNull();
        setup.IsDone.Should().BeTrue();
        setup.FinishCommand.Execute(null);
        Harness.Pump();

        var counts = app.Services.Context.Current.Data.Counts();
        (counts.Accounts, counts.Income, counts.Bills, counts.Debts).Should().Be((3, 1, 4, 2));
        app.PageTitle().Should().Be("Overview");
        var overview = (OverviewPageViewModel)app.ViewModel.Shell.CurrentPage!;
        overview.Metrics.Select(m => m.Label).Should().HaveCount(4).And.Contain(l => l.StartsWith("SAFE TO SPEND", StringComparison.Ordinal));
        overview.Forecast!.AccountName.Should().Be("Everyday");
        app.ViewModel.Shell.ForecastAsOf.Should().NotBe("—");

        app.ViewModel.Shell.Navigate(Destination.Bills);
        Harness.Pump();
        app.VisibleTexts().Should().Contain("Electric").And.Contain("Car loan").And.Contain(t => t != null && t.Contains("not spending"));
    }

    [AvaloniaFact]
    public void Setup_reports_typos_instead_of_saving_them()
    {
        using var app = new Harness();
        app.Services.Context.Open(app.Services.Profiles.Create("Juniper", Harness.Passphrase, false).Session);
        app.Open();
        app.ViewModel.StartSetup();
        var setup = app.Screen<SetupViewModel>();
        setup.CashBalance = "twelve";

        setup.SaveAndContinueCommand.Execute(null);

        setup.Error.Should().Contain("isn't an amount");
        setup.IsCash.Should().BeTrue();
        app.Services.Context.Current.Data.Accounts().Should().BeEmpty();
    }

    [AvaloniaFact]
    public void Going_back_to_a_saved_step_shows_it_instead_of_adding_it_again()
    {
        using var app = new Harness();
        app.Services.Context.Open(app.Services.Profiles.Create("Juniper", Harness.Passphrase, false).Session);
        app.Open();
        app.ViewModel.StartSetup();
        var setup = app.Screen<SetupViewModel>();
        setup.CashBalance = "1240";
        setup.SaveAndContinueCommand.Execute(null);

        setup.BackCommand.Execute(null);
        var shown = (setup.IsSaved, setup.ShowCashForm, setup.PrimaryText, setup.SavedNote);
        setup.SaveAndContinueCommand.Execute(null);

        shown.Should().Be((true, false, "Continue", "Checking, $1,240.00 as of " + $"{DateTime.Today:d MMM}" + ". To correct it later, reconcile the balance on Transactions."));
        (setup.IsPaychecks, app.Services.Context.Current.Data.Accounts().Count).Should().Be((true, 1));
        setup.Steps[0].AccessibleName.Should().Be("Cash, step 1 of 5, added");
    }

    [AvaloniaFact]
    public void Setup_ends_with_the_first_forecast_or_what_it_still_needs()
    {
        using var app = new Harness();
        app.Services.Context.Open(app.Services.Profiles.Create("Juniper", Harness.Passphrase, false).Session);
        app.Open();
        app.ViewModel.StartSetup();
        var setup = app.Screen<SetupViewModel>();
        setup.Step = SetupStep.Done;
        var missing = setup.Missing;

        setup.Step = SetupStep.Cash;
        setup.CashBalance = "1240";
        setup.SaveAndContinueCommand.Execute(null);
        setup.Step = SetupStep.Done;

        missing.Should().Contain("checking account");
        (setup.Missing, setup.Results.Count).Should().Be((null, 2));
        setup.Results[0].Label.Should().StartWith("SAFE TO SPEND");
    }

    [AvaloniaFact]
    public async Task An_empty_page_opens_its_own_setup_step_and_comes_back()
    {
        using var app = new Harness();
        app.CreateProfile();
        app.Open();
        var unlock = app.Screen<UnlockViewModel>();
        unlock.Passphrase = Harness.Passphrase;
        await unlock.UnlockCommand.ExecuteAsync(null);
        app.ViewModel.Shell.Navigate(Destination.Bills);

        ((BillsPageViewModel)app.ViewModel.Shell.CurrentPage!).AddBillsCommand.Execute(null);
        var setup = app.Screen<SetupViewModel>();
        var opened = (setup.Step, setup.CanGoBack);
        (setup.BillName, setup.BillAmount) = ("Rent", "1150");
        setup.SaveAndContinueCommand.Execute(null);
        var after = setup.Step;
        setup.FinishCommand.Execute(null);

        (opened, after).Should().Be(((SetupStep.Bills, false), SetupStep.Done));
        app.ViewModel.Shell.CurrentPage.Should().BeOfType<BillsPageViewModel>().Which.Rows.Should().ContainSingle(r => r.Name == "Rent");
    }

    [AvaloniaFact]
    public async Task A_backup_can_be_made_and_restored_from_the_app()
    {
        using var app = new Harness();
        app.CreateProfile();
        app.Open();
        var unlock = app.Screen<UnlockViewModel>();
        unlock.Passphrase = Harness.Passphrase;
        await unlock.UnlockCommand.ExecuteAsync(null);

        app.ViewModel.Shell.Navigate(Destination.Backups);
        var backups = (BackupsPageViewModel)app.ViewModel.Shell.CurrentPage!;
        app.Dialogs.NextSave = Path.Combine(app.Root, "juniper.mmbak");
        await backups.BackUpNowCommand.ExecuteAsync(null);

        backups.Status.Should().StartWith("Backed up and verified");
        File.Exists(app.Dialogs.NextSave).Should().BeTrue();

        backups.RestoreCommand.Execute(null);
        var restore = app.Screen<RestoreViewModel>();
        app.Dialogs.NextOpen = app.Dialogs.NextSave;
        await restore.PickFileCommand.ExecuteAsync(null);
        restore.Passphrase = Harness.Passphrase;
        await restore.RestoreCommand.ExecuteAsync(null);

        var unlockAgain = app.Screen<UnlockViewModel>();
        unlockAgain.Notice.Should().Contain("Restored");
        unlockAgain.Profiles.Should().HaveCount(2);
    }

    // A sample to explore before typing a real number: its own profile, named as a sample, opened on this PC.
    [AvaloniaFact]
    public async Task Try_a_sample_household_opens_a_separate_sample_profile()
    {
        using var app = new Harness();
        app.Open();

        await app.ViewModel.Welcome.TrySampleCommand.ExecuteAsync(null);

        app.ViewModel.CurrentScreen.Should().BeSameAs(app.ViewModel.Shell);
        app.ViewModel.Shell.CurrentPage!.Destination.Should().Be(Destination.Overview);
        var profile = app.Services.Profiles.List().Should().ContainSingle().Subject;
        (profile.Name, profile.UnlocksOnThisDevice).Should().Be(("Sample household", true));
        app.Services.Context.Current.Data.Bills().Should().NotBeEmpty();
    }

    // Export writes readable files, so it only runs after its warning, and never over an earlier export.
    [AvaloniaFact]
    public async Task Export_runs_only_after_its_warning_and_writes_a_new_folder_each_time()
    {
        using var app = new Harness();
        app.CreateProfile();
        app.Open();
        var unlock = app.Screen<UnlockViewModel>();
        unlock.Passphrase = Harness.Passphrase;
        await unlock.UnlockCommand.ExecuteAsync(null);
        app.ViewModel.Shell.Navigate(Destination.Backups);
        var backups = (BackupsPageViewModel)app.ViewModel.Shell.CurrentPage!;
        app.Dialogs.NextFolder = Directory.CreateDirectory(Path.Combine(app.Root, "exports")).FullName;

        backups.ExportCommand.CanExecute(null).Should().BeFalse("the warning comes first");
        for (var i = 0; i < 2; i++)
        {
            backups.StartExportCommand.Execute(null);
            backups.IsConfirmingExport.Should().BeTrue();
            await backups.ExportCommand.ExecuteAsync(null);
            backups.IsConfirmingExport.Should().BeFalse();
        }

        backups.ExportStatus.Should().StartWith("Exported to");
        var folders = Directory.GetDirectories(app.Dialogs.NextFolder).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToList();
        folders.Should().HaveCount(2).And.AllSatisfy(f => f.Should().StartWith("Mossmint export Juniper "));
        folders[1].Should().EndWith(" (2)");
        Directory.GetFiles(Path.Combine(app.Dialogs.NextFolder, folders[0])).Select(Path.GetFileName)
            .Should().BeEquivalentTo("profile.json", "transactions.csv", "bills.csv", "debts.csv");
    }

    [AvaloniaFact]
    public async Task Settings_change_the_passphrase_and_delete_only_after_typing_the_name()
    {
        using var app = new Harness();
        var created = app.CreateProfile();
        app.Open();
        var unlock = app.Screen<UnlockViewModel>();
        unlock.Passphrase = Harness.Passphrase;
        await unlock.UnlockCommand.ExecuteAsync(null);
        app.ViewModel.Shell.Navigate(Destination.Settings);
        var settings = (SettingsPageViewModel)app.ViewModel.Shell.CurrentPage!;

        (settings.NewPassphrase, settings.ConfirmPassphrase) = ("a brand new passphrase", "a brand new passphrase");
        await settings.ChangePassphraseCommand.ExecuteAsync(null);
        settings.Status.Should().StartWith("Passphrase changed");

        settings.DeleteProfileCommand.CanExecute(null).Should().BeFalse();
        settings.DeleteConfirmation = "Juniper";
        settings.DeleteProfileCommand.CanExecute(null).Should().BeTrue();
        settings.DeleteProfileCommand.Execute(null);
        Harness.Pump();

        app.Services.Profiles.List().Should().BeEmpty();
        app.ViewModel.CurrentScreen.Should().BeOfType<WelcomeViewModel>();
        _ = created;
    }
}
