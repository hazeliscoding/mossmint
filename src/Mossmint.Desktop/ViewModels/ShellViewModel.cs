using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Mossmint.Desktop.ViewModels;

public sealed partial class ShellViewModel : ViewModelBase
{
    public ShellViewModel()
    {
        NavItems =
        [
            Nav(Destination.Overview, "Overview", "IconLayoutDashboard", "Ctrl+1", "M4",
                "What you have, what's coming, and the lowest point before payday.",
                "Nothing to forecast yet",
                "Once a profile holds a confirmed balance, income and bills, this page shows money available now, safe to spend until payday, the next paycheck and the debt-free forecast. Each figure carries its source and as-of date."),
            Nav(Destination.CashFlow, "Cash Flow", "IconWaves", "Ctrl+2", "M4",
                "A day-by-day checking forecast with the cushion line and every scheduled event.",
                "No forecast yet",
                "The forecast engine simulates every day, not just paydays, so a bill due the day before payday can't hide behind a healthy month-end total. Every chart here will have a table twin."),
            Nav(Destination.Paychecks, "Paychecks", "IconBanknote", "Ctrl+3", "M4",
                "Which bills each paycheck covers, and what is left after them.",
                "No paychecks yet",
                "Weekly, every two weeks, twice a month or monthly, including the 15th and the last day. Each bill is funded by exactly one paycheck, and its due date never moves."),
            Nav(Destination.Debts, "Debts", "IconCreditCard", "Ctrl+4", "M4",
                "Balances, interest and payoff dates, and what an extra payment really changes.",
                "No debts yet",
                "Loans use daily simple interest with a documented day count and payment order. Card forecasts are labelled approximate until a statement confirms them."),
            Nav(Destination.Bills, "Bills", "IconReceiptText", "Ctrl+5", "M4",
                "Recurring bills, due dates, and what is already paid.",
                "No bills yet",
                "Fixed, estimated and variable bills, each with its due-date rule and the account that pays it. Card payments are transfers, so purchases are never counted twice."),
            Nav(Destination.Savings, "Savings", "IconPiggyBank", "Ctrl+6", "M4",
                "Savings goals and the cash reserve floor, tracked separately.",
                "No savings goals yet",
                "A contribution target (how much to set aside each paycheck) and a reserve floor (cash that must stay put) are separate settings, so meeting one never hides a shortfall in the other."),
            Nav(Destination.Transactions, "Transactions", "IconList", "Ctrl+7", "M4",
                "The ledger: manual entries, imports, transfers and reconciliation.",
                "No transactions yet",
                "Manual entries and imported rows keep their source. Transfers link both sides, and duplicates are flagged for review rather than merged silently."),
            Nav(Destination.Scenarios, "Scenarios", "IconGitCompareArrows", "Ctrl+8", "M5",
                "What-if plans that never touch your real plan until you apply them.",
                "No scenarios yet",
                "Compare avalanche, snowball and custom payoff plans under base, cautious and stress spending. An extra payment is never called safe if daily cash would drop below the cushion."),
            Nav(Destination.Imports, "Imports", "IconFileSpreadsheet", null, "M3",
                "Preview and import Excel workbooks. Originals are never modified.",
                "Workbook import isn't built yet",
                "Imports will be read-only and previewed first. Every value keeps its sheet and cell, mismatches become review items, and nothing is saved until one final step.",
                startsGroup: true),
            Nav(Destination.Connections, "Connections", "IconPlug", null, "M7",
                "Optional bank connections. Mossmint works fully without them.",
                "No connections, by design",
                "Mossmint never asks for bank logins and makes no network calls in normal use. File imports (CSV, OFX) and optional read-only feeds may come after the MVP, each with its own privacy notes and a way to disconnect and erase."),
            Nav(Destination.Settings, "Settings", "IconSettings2", "Ctrl+,", "M4",
                "Appearance, profiles and privacy.",
                "Settings aren't built yet",
                "The theme follows Windows for now. Profile, privacy and update-check settings arrive with encrypted profiles (M2) and the dashboard (M4). Update checks will be opt-in."),
            Nav(Destination.Backups, "Backups", "IconArchive", null, "M2",
                "Encrypted backups you can restore on any PC.",
                "No backups yet",
                "Backups will be encrypted with your passphrase, verified by a test restore before Mossmint reports success, and never depend on this PC to open."),
        ];
        _selectedItem = NavItems[0];
    }

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

    private static NavItemViewModel Nav(
        Destination destination, string title, string icon, string? shortcut, string milestone,
        string purpose, string emptyTitle, string emptyBody, bool startsGroup = false) =>
        new(new PageViewModel(destination, title, purpose, milestone, emptyTitle, emptyBody), icon, shortcut, startsGroup);
}
