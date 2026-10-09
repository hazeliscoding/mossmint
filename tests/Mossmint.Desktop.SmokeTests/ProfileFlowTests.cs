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
