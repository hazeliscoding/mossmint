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
            Nav(new PaychecksPageViewModel(services), "IconBanknote", "Ctrl+3"),
            Nav(new DebtsPageViewModel(services), "IconCreditCard", "Ctrl+4"),
            Nav(new BillsPageViewModel(services), "IconReceiptText", "Ctrl+5"),
            Nav(new SavingsPageViewModel(services), "IconPiggyBank", "Ctrl+6"),
            Nav(new TransactionsPageViewModel(services), "IconList", "Ctrl+7"),
            Nav(Placeholder(Destination.Scenarios, "Scenarios", "M5",
                "What-if plans that never touch your real plan until you apply them.",
                "No scenarios yet",
                "Compare avalanche, snowball and custom payoff plans under base, cautious and stress spending. An extra payment is never called safe if daily cash would drop below the cushion."), "IconGitCompareArrows", "Ctrl+8"),
            Nav(new ImportsPageViewModel(services), "IconFileSpreadsheet", null, startsGroup: true),
            Nav(Placeholder(Destination.Connections, "Connections", "M7",
                "Optional bank connections. Mossmint works fully without them.",
                "No connections, by design",
                "Mossmint never asks for bank logins and makes no network calls in normal use. File imports (CSV, OFX) and optional read-only feeds may come after the MVP, each with its own privacy notes and a way to disconnect and erase."), "IconPlug", null),
            Nav(new SettingsPageViewModel(services, navigator), "IconSettings2", "Ctrl+,"),
            Nav(new BackupsPageViewModel(services, navigator), "IconArchive", null),
        ];
        _selectedItem = NavItems[0];
    }

    public ProfileContext Context { get; }

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

    partial void OnSelectedItemChanged(NavItemViewModel? value) => Refresh();

    public void Refresh()
    {
        SelectedItem?.Page.OnNavigatedTo();
        var outcome = Context.IsUnlocked
            ? Forecasts.Build(Context.Current.Data, DateOnly.FromDateTime(_services.Clock.GetLocalNow().DateTime))
            : new ForecastUnavailable(["Unlock a profile."]);
        (ForecastAsOf, ForecastAsOfTip) = outcome switch
        {
            ProfileForecast f => (f.OpeningProvenance.AsOf.ToString("d MMM yyyy", CultureInfo.CurrentCulture),
                $"Starts from the {f.AccountName} balance of {Describe.Money(f.Opening)} as of {f.OpeningProvenance.AsOf:d MMM yyyy} ({Describe.Confidence(f.OpeningProvenance.Confidence).Status})."),
            ForecastUnavailable u => ("—", "No forecast yet: " + string.Join(" ", u.Missing)),
            _ => ("—", ""),
        };
    }

    private static PlaceholderPageViewModel Placeholder(Destination destination, string title, string milestone, string purpose, string emptyTitle, string emptyBody) =>
        new(destination, title, purpose, milestone, emptyTitle, emptyBody);

    private static NavItemViewModel Nav(PageViewModel page, string icon, string? shortcut, bool startsGroup = false) =>
        new(page, icon, shortcut, startsGroup);
}
