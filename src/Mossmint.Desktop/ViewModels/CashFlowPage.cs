using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mossmint.Application.Forecasting;
using Mossmint.Desktop.Controls;
using Mossmint.Domain;

namespace Mossmint.Desktop.ViewModels;

// One forecast day as a table row: the chart's table twin.
public sealed record CashFlowRow(string Date, string Events, string In, string Out, string Low, string Closing, string Status, StatusTone Tone)
{
    public bool HasStatus => Status.Length > 0;
}

public sealed partial class CashFlowPageViewModel(AppServices services)
    : PageViewModel(Destination.CashFlow, "Cash Flow", "Every day's money in and out of the spending account, and the lowest it gets.")
{
    public IReadOnlyList<int> Ranges { get; } = [30, Forecasts.DefaultDays, 90];

    [ObservableProperty]
    private int _days = Forecasts.DefaultDays;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasForecast), nameof(Summary), nameof(ChartDescription))]
    private ProfileForecast? _forecast;

    [ObservableProperty]
    private string? _unavailable;

    public ObservableCollection<CashFlowRow> Rows { get; } = [];

    public bool HasForecast => Forecast is not null;

    public string Summary => Forecast is { } f
        ? $"{f.AccountName}, from {Describe.Money(f.Opening)} as of {f.OpeningProvenance.AsOf:d MMM}. Lowest {Describe.Money(f.Lowest)} on {f.LowestDate:ddd d MMM}; cushion {Describe.Money(f.Cushion)}."
        : "";

    public string ChartDescription => Forecast is { } f
        ? $"{f.AccountName} balance over {f.Days.Count} days. {Summary} The table below has every day."
        : "";

    partial void OnDaysChanged(int value) => OnNavigatedTo();

    public override void OnNavigatedTo()
    {
        Rows.Clear();
        if (!services.Context.IsUnlocked)
        {
            return;
        }

        var today = DateOnly.FromDateTime(services.Clock.GetLocalNow().DateTime);
        (Forecast, Unavailable) = Forecasts.Build(services.Context.Current.Data, today, Days) switch
        {
            ProfileForecast f => (f, (string?)null),
            ForecastUnavailable u => (null, string.Join(" ", u.Missing)),
            _ => (null, null),
        };
        foreach (var day in Forecast?.Days ?? [])
        {
            Rows.Add(Row(day));
        }
    }

    private static CashFlowRow Row(ForecastDay day)
    {
        var zero = Money.Zero(day.Closing.Currency);
        var inflow = day.Events.Where(e => e.IsInflow).Aggregate(zero, (sum, e) => sum + e.Amount);
        var outflow = day.Events.Where(e => !e.IsInflow).Aggregate(zero, (sum, e) => sum + e.Amount);
        var (status, tone) = day.BelowCushion ? ("Below cushion", StatusTone.Critical)
            : day.IsPayday ? ("Payday", StatusTone.Info)
            : ("", StatusTone.Neutral);
        return new CashFlowRow(
            day.Date.ToString("ddd d MMM"),
            day.Events.Count == 0 ? "—" : string.Join(", ", day.Events.Select(e => e.Label)),
            inflow.IsZero ? "" : Describe.Money(inflow),
            outflow.IsZero ? "" : "−" + Describe.Money(outflow),
            Describe.Money(day.Low),
            Describe.Money(day.Closing),
            status,
            tone);
    }
}
