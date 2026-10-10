using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mossmint.Application.Data;
using Mossmint.Application.Forecasting;
using Mossmint.Application.Setup;
using Mossmint.Desktop.Controls;
using Mossmint.Desktop.Services;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;

namespace Mossmint.Desktop.ViewModels;

// One part of the savings plan: the cushion, the per-paycheck transfer, the savings accounts.
public sealed record PlanTile(string Label, string Value, string Note, string Status, StatusTone Tone);

public sealed record GoalCard(GoalView View, string Name, string When, double Share, string Progress, string Left, string Pace, string Status, StatusTone Tone)
{
    public string AccessibleName => $"{Name}, {When}. {Progress}. {Pace}. {Status}.";
}

public sealed partial class SavingsPageViewModel(AppServices services)
    : PageViewModel(Destination.Savings, "Savings", "Your cushion, what each paycheck sets aside, and the goals it's for.")
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public ObservableCollection<PlanTile> Plan { get; } = [];

    public ObservableCollection<GoalCard> Goals { get; } = [];

    public bool HasGoals => Goals.Count > 0;

    [ObservableProperty]
    private string _pace = "";

    [ObservableProperty]
    private bool _isPaceShort;

    [ObservableProperty]
    private string? _notice;

    // Add goal
    [ObservableProperty]
    private bool _isAdding;

    [ObservableProperty]
    private string _goalName = "";

    [ObservableProperty]
    private string _goalTarget = "";

    [ObservableProperty]
    private string _goalSaved = "";

    [ObservableProperty]
    private DateTime? _goalBy;

    // Update saved
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdating), nameof(UpdateTitle))]
    private GoalCard? _updating;

    [ObservableProperty]
    private string _savedText = "";

    [ObservableProperty]
    private string? _formError;

    public bool IsUpdating => Updating is not null;

    public string UpdateTitle => Updating is { } g ? $"How much is in {g.Name} now?" : "";

    public override void OnNavigatedTo()
    {
        Notice = null;
        CancelForms();
        Load();
    }

    [RelayCommand]
    private void StartAdd()
    {
        CancelForms();
        (GoalName, GoalTarget, GoalSaved, GoalBy) = ("", "", "", null);
        IsAdding = true;
    }

    [RelayCommand]
    private void StartUpdate(GoalCard goal)
    {
        CancelForms();
        SavedText = "";
        Updating = goal;
    }

    [RelayCommand]
    private void CancelForms()
    {
        IsAdding = false;
        Updating = null;
        FormError = null;
    }

    [RelayCommand]
    private void SaveGoal() => Save(() =>
    {
        var goal = Entry().AddGoal(GoalName, MoneyInput.Required(GoalTarget, _culture, "Goal amount"),
            MoneyInput.Optional(GoalSaved, _culture, "Saved so far") ?? 0m, GoalBy is { } by ? DateOnly.FromDateTime(by) : null);
        Notice = $"{goal.Name} added.";
    });

    [RelayCommand]
    private void SaveSaved() => Save(() =>
    {
        var goal = Entry().UpdateSaved(Updating!.View.Goal, MoneyInput.Required(SavedText, _culture, "Saved so far"));
        Notice = $"{goal.Name}: {Describe.Money(goal.Saved)} saved.";
    });

    private void Save(Action save)
    {
        try
        {
            save();
            CancelForms();
            Load();
        }
        catch (ArgumentException e)
        {
            FormError = e.Message;
        }
    }

    private ManualEntry Entry() => new(services.Context.Current.Data, services.Clock);

    private void Load()
    {
        Plan.Clear();
        Goals.Clear();
        (Pace, IsPaceShort) = ("", false);
        if (services.Context.IsUnlocked)
        {
            var data = services.Context.Current.Data;
            var today = DateOnly.FromDateTime(services.Clock.GetLocalNow().DateTime);
            var forecast = Forecasts.Build(data, today) as ProfileForecast;
            foreach (var tile in Tiles(data, forecast))
            {
                Plan.Add(tile);
            }

            var goals = Forecasts.Goals(data, today);
            foreach (var goal in goals)
            {
                Goals.Add(Card(goal));
            }

            var needed = goals.Select(g => g.PerPaycheck).OfType<Money>().ToList();
            if (needed.Count > 0)
            {
                var total = needed.Aggregate((a, b) => a + b);
                var set = data.Settings().SavingsTarget?.PerPaycheck ?? Money.Zero(total.Currency);
                IsPaceShort = total > set;
                Pace = $"Your dated goals need {Describe.Money(total)} per paycheck together; each paycheck sets aside {Describe.Money(set)}."
                    + (IsPaceShort ? $" That's {Describe.Money(total - set)} short, so a goal will finish late unless one of them changes." : " That covers them.");
            }
        }

        OnPropertyChanged(nameof(HasGoals));
    }

    private static IEnumerable<PlanTile> Tiles(IProfileData data, ProfileForecast? f)
    {
        var settings = data.Settings();
        if (settings.ReserveFloor?.Minimum is not { } cushion)
        {
            yield return new PlanTile("CUSHION", "—", "Not set. Start fresh asks for one.", "Not set", StatusTone.Neutral);
        }
        else
        {
            var below = f is not null && f.Lowest < cushion;
            yield return new PlanTile("CUSHION", Describe.Money(cushion), f is null ? "kept in checking" : $"kept in {f.AccountName} every day",
                f is null ? "No forecast" : below ? "Dips below" : "Kept", f is null ? StatusTone.Neutral : below ? StatusTone.Critical : StatusTone.Healthy);
        }

        if (settings.SavingsTarget?.PerPaycheck is not { } target)
        {
            yield return new PlanTile("EVERY PAYCHECK", "—", "Nothing set aside per paycheck.", "Not set", StatusTone.Neutral);
        }
        else
        {
            var paychecks = f?.Paychecks.Where(p => p.Payday is not null).ToList() ?? [];
            var shortOnes = paychecks.Count(p => p.SavingsFunded < p.SavingsTarget);
            yield return new PlanTile("EVERY PAYCHECK", Describe.Money(target),
                shortOnes > 0 ? $"to savings; {shortOnes} of {paychecks.Count} paychecks ahead can't cover it in full" : "to savings once the paycheck's bills are paid",
                shortOnes > 0 ? "Short" : "Scheduled", shortOnes > 0 ? StatusTone.Warning : StatusTone.Info);
        }

        var savings = data.Accounts().Where(a => a.Kind == AccountKind.Savings)
            .Select(a => (a.Name, Balance: data.Balances(a.Id).FirstOrDefault())).Where(s => s.Balance is not null).ToList();
        if (savings.Count == 0)
        {
            yield return new PlanTile("IN SAVINGS", "—", "No savings account yet.", "None", StatusTone.Neutral);
        }
        else
        {
            var weakest = savings.Select(s => s.Balance!.Provenance.Confidence).Min();
            var (status, tone) = Describe.Confidence(weakest);
            yield return new PlanTile("IN SAVINGS", Describe.Money(savings.Select(s => s.Balance!.Balance).Aggregate((a, b) => a + b)),
                string.Join(" · ", savings.Select(s => $"{s.Name} as of {s.Balance!.Provenance.AsOf:d MMM}")), status, tone);
        }
    }

    private static GoalCard Card(GoalView view)
    {
        var goal = view.Goal;
        var share = goal.Target.IsPositive ? (double)Math.Clamp(goal.Saved.Amount / goal.Target.Amount, 0m, 1m) : 0d;
        var when = goal.TargetDate is { } by ? $"by {by:d MMM yyyy}" : "no date";
        var (pace, status, tone) = view switch
        {
            { IsReached: true } => ("Reached. Nothing more needed.", "Reached", StatusTone.Healthy),
            { PerPaycheck: { } per } => ($"{Describe.Money(per)} per paycheck over {view.PaydaysLeft} paydays", "Scheduled", StatusTone.Info),
            _ when goal.TargetDate is not null => ("No payday left before the date.", "Needs review", StatusTone.Warning),
            _ => ("Add a date to see what each paycheck needs.", "No date", StatusTone.Neutral),
        };
        return new GoalCard(view, goal.Name, when, share, $"{Describe.Money(goal.Saved)} of {Describe.Money(goal.Target)} · {share:P0}",
            $"{Describe.Money(view.Left)} left · saved as of {goal.SavedProvenance.AsOf:d MMM}", pace, status, tone);
    }
}
