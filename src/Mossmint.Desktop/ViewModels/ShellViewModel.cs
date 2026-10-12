using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mossmint.Application.Forecasting;
using Mossmint.Desktop.Services;

namespace Mossmint.Desktop.ViewModels;

public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly IAppNavigator _navigator;
    private readonly AppServices _services;

    public ShellViewModel(AppServices services, IAppNavigator navigator)
    {
        (Context, _navigator, _services) = (services.Context, navigator, services);
        NavItems =
        [
            Nav(new OverviewPageViewModel(services, navigator), "IconLayoutDashboard", "Ctrl+1"),
            Nav(new CashFlowPageViewModel(services), "IconWaves", "Ctrl+2"),
            Nav(new PaychecksPageViewModel(services, navigator), "IconBanknote", "Ctrl+3"),
            Nav(new DebtsPageViewModel(services, navigator), "IconCreditCard", "Ctrl+4"),
            Nav(new BillsPageViewModel(services, navigator), "IconReceiptText", "Ctrl+5"),
            Nav(new SavingsPageViewModel(services), "IconPiggyBank", "Ctrl+6"),
            Nav(new TransactionsPageViewModel(services, navigator), "IconList", "Ctrl+7"),
            Nav(new ScenariosPageViewModel(services, navigator), "IconGitCompareArrows", "Ctrl+8"),
            Nav(new ImportsPageViewModel(services), "IconFileSpreadsheet", null, startsGroup: true),
            Nav(new ConnectionsPageViewModel(services), "IconPlug", null),
            Nav(Settings = new SettingsPageViewModel(services, navigator), "IconSettings2", "Ctrl+,"),
            Nav(new BackupsPageViewModel(services, navigator), "IconArchive", null),
        ];
        _selectedItem = NavItems[0];
    }

    public ProfileContext Context { get; }

    // Also drives the update strip at the top of the app.
    public SettingsPageViewModel Settings { get; }

    public ObservableCollection<NavItemViewModel> NavItems { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    private NavItemViewModel? _selectedItem;

    [ObservableProperty]
    private bool _isSidebarCollapsed;

    public PageViewModel? CurrentPage => SelectedItem?.Page;

    [RelayCommand]
    public void Navigate(Destination destination) =>
        SelectedItem = NavItems.First(item => item.Destination == destination);

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    [RelayCommand]
    private void Lock() => _navigator.Lock();

    // The top bar's "Forecast as of": the date of the balance the forecast starts from.
    [ObservableProperty]
    private string _forecastAsOf = "—";

    [ObservableProperty]
    private string _forecastAsOfTip = "";

    // Offline until the user connects something; even then, syncing is by hand.
    [ObservableProperty]
    private string _dataMode = "Offline · Manual";

    partial void OnSelectedItemChanged(NavItemViewModel? value) => Refresh();

    public void Refresh()
    {
        SelectedItem?.Page.OnNavigatedTo();
        DataMode = Context.IsUnlocked && Context.Current.Imports.Connections().Count > 0 ? "SimpleFIN · Manual sync" : "Offline · Manual";
        var today = DateOnly.FromDateTime(_services.Clock.GetLocalNow().DateTime);
        var outcome = Context.IsUnlocked ? Forecasts.Build(Context.Current.Data, today) : new ForecastUnavailable(["Unlock a profile."]);
        if (Context.IsUnlocked)
        {
            // The forecast the top bar shows is the one the journal keeps for later backtests.
            ForecastJournal.Record(Context.Current.Data, outcome, today);
        }
        (ForecastAsOf, ForecastAsOfTip) = outcome switch
        {
            ProfileForecast f => (f.OpeningProvenance.AsOf.ToString("d MMM yyyy", CultureInfo.CurrentCulture),
                $"Starts from the {f.AccountName} balance of {Describe.Money(f.Opening)} as of {f.OpeningProvenance.AsOf:d MMM yyyy} ({Describe.Confidence(f.OpeningProvenance.Confidence).Status})."),
            ForecastUnavailable u => ("—", "No forecast yet: " + string.Join(" ", u.Missing)),
            _ => ("—", ""),
        };
    }

    private static NavItemViewModel Nav(PageViewModel page, string icon, string? shortcut, bool startsGroup = false) =>
        new(page, icon, shortcut, startsGroup);
}
