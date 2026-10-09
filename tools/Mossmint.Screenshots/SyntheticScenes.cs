using Mossmint.Application.Profiles;
using Mossmint.Application.Setup;
using Mossmint.Desktop.Services;
using Mossmint.Desktop.ViewModels;
using Mossmint.Desktop.Views;
using Mossmint.Persistence.Backups;
using Mossmint.Persistence.Profiles;
using Mossmint.Persistence.Security;
using Mossmint.Samples;
using Mossmint.Samples.Workbooks;

namespace Mossmint.Screenshots;

// Builds app windows over a throwaway data root holding only the synthetic
// Juniper profile. Nothing here can see a real profile.
internal sealed class SyntheticScenes : IDisposable
{
    public const string Passphrase = "synthetic juniper profile";
    private static readonly Argon2Settings Fast = new(1024, 1, 1);
    private readonly List<string> _roots = [];

    public MainWindow Empty() => Window(Services(NewRoot()));

    // A seeded profile, locked: the unlock screen.
    public MainWindow Locked()
    {
        var services = Services(NewRoot());
        Seed(services).Dispose();
        return Window(services);
    }

    public MainWindow Unlocked(Action<MainWindowViewModel> show)
    {
        var services = Services(NewRoot());
        services.Context.Open(Seed(services));
        var window = Window(services);
        show((MainWindowViewModel)window.DataContext!);
        return window;
    }

    public MainWindow CreateProfile()
    {
        var window = Empty();
        var vm = (MainWindowViewModel)window.DataContext!;
        vm.Welcome.StartFreshCommand.Execute(null);
        var create = (CreateProfileViewModel)vm.CurrentScreen;
        (create.Name, create.Passphrase, create.Confirm) = (SyntheticProfile.Name, Passphrase, Passphrase);
        return window;
    }

    public MainWindow RecoveryKey() => Unlocked(vm =>
        vm.ShowRecoveryKey(Persistence.Security.RecoveryKey.Generate().ToString(), () => { }));

    public MainWindow Setup() => Unlocked(vm =>
    {
        vm.StartSetup();
        var setup = (SetupViewModel)vm.CurrentScreen;
        foreach (var label in new[] { "Account · Checking", "Paycheck · Paycheck", "Bill · Rent", "Bill · Electric" })
        {
            setup.Added.Add(label);
        }

        setup.SkipCommand.Execute(null);
        setup.SkipCommand.Execute(null);
        (setup.BillName, setup.BillAmount, setup.BillDueDay) = ("Phone", "45.00", 20);
    });

    // A fresh profile on the Imports page with the synthetic workbooks picked:
    // the dry run, or the review queue once it's imported.
    public MainWindow Imports(bool commit)
    {
        var root = NewRoot();
        var folder = Path.Combine(root, "workbooks");
        Directory.CreateDirectory(folder);
        var paths = SyntheticWorkbooks.All().Select(w =>
        {
            var path = Path.Combine(folder, w.FileName);
            File.WriteAllBytes(path, w.Content);
            return path;
        }).ToList();
        var services = Services(root, paths);
        services.Context.Open(services.Profiles.Create(SyntheticProfile.Name, Passphrase, false).Session);
        var window = Window(services);
        var vm = (MainWindowViewModel)window.DataContext!;
        vm.OpenApp(Destination.Imports);
        var page = (ImportsPageViewModel)vm.Shell.CurrentPage!;
        Wait(page.ChooseWorkbooksCommand.ExecuteAsync(null));
        if (commit)
        {
            Wait(page.ImportCommand.ExecuteAsync(null));
            page.Status = null;
        }

        return window;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Left for the OS temp cleanup.
            }
        }
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mossmint-screenshots", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    // Commands run work on the thread pool and finish on the UI thread, so the
    // dispatcher has to keep pumping while we wait.
    private static void Wait(Task task)
    {
        while (!task.IsCompleted)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        task.GetAwaiter().GetResult();
    }

    private static AppServices Services(string root, IReadOnlyList<string>? workbooks = null)
    {
        var locations = new ProfileLocations(root);
        return new AppServices(
            new ProfileStore(locations, TimeProvider.System, Fast),
            new BackupService(locations, TimeProvider.System),
            new NoDialogs(workbooks ?? []),
            new ProfileContext(),
            TimeProvider.System);
    }

    // The same synthetic numbers as the engine preview (samples/Mossmint.Samples).
    private static IProfileSession Seed(AppServices services)
    {
        var session = services.Profiles.Create(SyntheticProfile.Name, Passphrase, false).Session;
        var entry = new ManualEntry(session.Data, services.Clock);
        var asOf = SyntheticProfile.Today;
        var checking = entry.AddCashAccount("Checking", 1_240m, asOf.AddDays(-1));
        entry.AddPaycheck(new PaycheckEntry("Paycheck", 2_050m, PayPattern.TwiceMonthly), checking.Id);
        entry.AddBill(new BillEntry("Rent", 1_150m, 1), checking.Id);
        entry.AddBill(new BillEntry("Electric", 70m, 12, IsEstimate: true, Low: 60m, High: 95m), checking.Id);
        entry.AddBill(new BillEntry("Phone", 45m, 20), checking.Id);
        entry.AddBill(new BillEntry("Internet", 60m, 17), checking.Id);
        entry.AddBill(new BillEntry("Insurance", 132m, 25), checking.Id);
        entry.AddBill(new BillEntry("Streaming", 18m, 22), checking.Id);
        entry.AddLoan(new LoanEntry("Car loan", 9_800m, asOf.AddDays(-9), 7.4m, 285m, 28), checking.Id);
        entry.AddCard(new CardEntry("Card", 2_300m, asOf.AddDays(-18), 24.99m, 150m, 21, 3), checking.Id);
        entry.SetSavings(150m, 300m);
        return session;
    }

    private static MainWindow Window(AppServices services) => new() { DataContext = new MainWindowViewModel(services) };

    private sealed class NoDialogs(IReadOnlyList<string> workbooks) : IDesktopDialogs
    {
        public Task<string?> PickBackupToOpenAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickBackupSaveLocationAsync(string suggestedName) => Task.FromResult<string?>(null);

        public Task<bool> SaveTextAsync(string suggestedName, string text, string title) => Task.FromResult(false);

        public Task<IReadOnlyList<string>> PickWorkbooksAsync() => Task.FromResult(workbooks);

        public Task CopyAsync(string text) => Task.CompletedTask;
    }
}
