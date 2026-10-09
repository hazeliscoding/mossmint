using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Mossmint.Desktop.Controls;
using Mossmint.Domain;
using Mossmint.Engine.Cards;
using Mossmint.Engine.CashFlow;
using Mossmint.Engine.Loans;
using Mossmint.Samples;

namespace Mossmint.Screenshots.EnginePreview;

internal static class Format
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static string Money(Money m) =>
        (m.IsNegative ? "−" : "") + m.Abs().Amount.ToString("C2", Us);

    public static string Date(DateOnly d) => d.ToString("ddd d MMM", CultureInfo.InvariantCulture);
}

// Engine output for the synthetic profile, laid out like the M4 Overview so a
// reviewer can see what the numbers will feed. Not part of the app.
internal static class EnginePreviewWindow
{
    public static Window Create()
    {
        var plan = SyntheticProfile.CashFlow();
        var forecast = CashFlowSimulator.Run(plan);
        var funding = PaycheckFunding.Plan(plan);
        var safePlan = plan with { Through = SafeToSpend.DayBeforeNextPayday(plan) };
        var safe = SafeToSpend.Calculate(safePlan);
        var car = SimpleInterestLoan.Project(SyntheticProfile.CarLoan());
        var card = CreditCard.Project(SyntheticProfile.Card());
        var paydays = CashFlowPlanner.Expand(plan).Paydays;
        var next = funding.First(p => p.Payday is not null);

        var root = new StackPanel { Spacing = 16, Margin = new Thickness(24, 20) };
        root.Children.Add(Header());
        root.Children.Add(Metrics(safe, forecast, next, car, card));
        root.Children.Add(Card("Checking balance forecast · next 45 days",
            "Line: closing balance with each day's intraday low · dashed: $300 cushion · PAY: paydays",
            new ForecastChart(forecast.Days, plan.ReserveFloor!.Minimum.Amount, paydays, forecast.Lowest) { Height = 250 }));

        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*") };
        bottom.Children.Add(Card("Paycheck funding", "Each bill is funded once, by the latest paycheck on or before its due date", FundingTable(funding)));
        var debts = Card("Debt projections", "Projections, not lender payoff quotes", DebtTable(car, card));
        Grid.SetColumn(debts, 2);
        bottom.Children.Add(debts);
        root.Children.Add(bottom);

        return new Window
        {
            Title = "Mossmint engine preview",
            Width = 1440,
            Height = 900,
            Content = root,
        };
    }

    private static Control Header()
    {
        var titles = new StackPanel { Spacing = 4 };
        titles.Children.Add(Text("Engine preview", "page-title"));
        titles.Children.Add(Text($"{SyntheticProfile.Name} · synthetic profile · today {Format.Date(SyntheticProfile.Today)} · rendered from Mossmint.Engine, not the app UI yet", "secondary"));

        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        badges.Children.Add(new StatusBadge { Tone = StatusTone.Neutral, Text = "Synthetic data" });
        badges.Children.Add(new StatusBadge { Tone = StatusTone.Info, Text = "Base scenario · assumption" });
        badges.Children.Add(new StatusBadge { Tone = StatusTone.Healthy, Text = "Balance confirmed yesterday" });

        var header = new DockPanel();
        DockPanel.SetDock(badges, Dock.Right);
        header.Children.Add(badges);
        header.Children.Add(titles);
        return header;
    }

    private static Control Metrics(SafeToSpendResult safe, CashFlowForecast forecast, FundingPeriod next, LoanProjection car, CardProjection card)
    {
        var grid = new UniformGrid { Columns = 4 };
        var lowOnPayday = forecast.Days.Single(d => d.Date == forecast.Lowest.Date).Inflows.IsPositive;
        grid.Children.Add(safe switch
        {
            SafeAmount s => Metric($"SAFE TO SPEND UNTIL {s.Through:d MMM}".ToUpperInvariant(), Format.Money(s.Amount),
                "After bills, cushion and groceries", StatusTone.Info, "Estimated",
                $"lowest {Format.Money(s.Lowest.Amount)} on {s.Lowest.Date:d MMM}", $"{Format.Money(s.ReserveFloor)} cushion kept", "electric at top of range"),
            Shortfall s => Metric("SAFE TO SPEND", $"−{Format.Money(s.Amount)}", "Below the cushion", StatusTone.Critical, "Shortfall", s.Basis[0]),
            NeedsConfirmation n => Metric("SAFE TO SPEND", "—", "Needs confirmation", StatusTone.Warning, "Needs review", n.Missing[0]),
            _ => throw new InvalidOperationException(),
        });
        grid.Children.Add(Metric("LOWEST POINT · NEXT 45 DAYS", Format.Money(forecast.Lowest.Amount),
            forecast.FirstDayBelowReserve is null ? "Stays above the $300 cushion" : "Dips under the cushion", StatusTone.Healthy, "Above cushion",
            Format.Date(forecast.Lowest.Date), lowOnPayday ? "morning of payday, before the deposit" : "between paydays", $"{forecast.Days.Count} days simulated"));
        grid.Children.Add(Metric($"NEXT PAYCHECK · {next.Payday:d MMM}".ToUpperInvariant(), Format.Money(next.NetPay),
            $"Covers {next.Obligations.Count} bills, leaves {Format.Money(next.Leftover)}", StatusTone.Info, "Scheduled",
            $"bills {Format.Money(next.ObligationsTotal)}", $"savings {Format.Money(next.ContributionFunded)} of {Format.Money(next.ContributionTarget)}", "15th and last business day"));

        var carDate = car.Outcome is PaysOff c ? c.PayoffDate : (DateOnly?)null;
        var cardDate = card.Outcome is PaysOff k ? k.PayoffDate : (DateOnly?)null;
        var last = new[] { carDate, cardDate }.Max();
        grid.Children.Add(Metric("DEBT-FREE FORECAST", last is { } d ? d.ToString("MMM yyyy", CultureInfo.InvariantCulture) : "—",
            "Card is an approximation", StatusTone.Info, "Estimated",
            $"car loan {carDate:MMM yyyy}", $"card {cardDate:MMM yyyy}", "regular payments only"));

        foreach (var child in grid.Children)
        {
            child.Margin = new Thickness(0, 0, 12, 0);
        }

        return grid;
    }

    private static Control Metric(string label, string value, string human, StatusTone tone, string status, params string[] facts)
    {
        var stack = new StackPanel { Spacing = 6 };
        var top = new DockPanel();
        var badge = new StatusBadge { Tone = tone, Text = status };
        DockPanel.SetDock(badge, Dock.Right);
        top.Children.Add(badge);
        top.Children.Add(Text(label, "label"));
        stack.Children.Add(top);
        stack.Children.Add(Text(human, "card-title"));
        var number = Text(value, "mono");
        number.FontSize = 28;
        number.Foreground = Resource("AccentText");
        stack.Children.Add(number);
        foreach (var fact in facts)
        {
            stack.Children.Add(Text(fact, "mono", "faint"));
        }

        return new Border { Classes = { "card" }, Child = stack };
    }

    private static Control Card(string title, string subtitle, Control body)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(Text(title, "card-title"));
        stack.Children.Add(Text(subtitle, "compact", "secondary"));
        stack.Children.Add(body);
        return new Border { Classes = { "card" }, Child = stack };
    }

    private static Control FundingTable(IReadOnlyList<FundingPeriod> periods) =>
        Table(["Paycheck", "Net pay", "Bills", "Savings", "Leftover"],
            periods.Select(p => new[]
            {
                p.Payday is { } d ? Format.Date(d) : "Cash on hand",
                Format.Money(p.NetPay),
                $"{Format.Money(p.ObligationsTotal)} ({p.Obligations.Count})",
                Format.Money(p.ContributionFunded),
                Format.Money(p.Leftover),
            }));

    private static Control DebtTable(LoanProjection car, CardProjection card)
    {
        static string When(PayoffOutcome o) => o switch
        {
            PaysOff p => p.PayoffDate.ToString("d MMM yyyy", CultureInfo.InvariantCulture),
            NeverPaysOff => "Never pays off",
            _ => "Needs data",
        };

        static string Interest(PayoffOutcome o) => o is PaysOff p ? Format.Money(p.TotalInterest) : "—";

        return Table(["Debt", "Model", "Payoff", "Interest left", "Confidence"],
        [
            ["Car loan", "Simple interest", When(car.Outcome), Interest(car.Outcome), car.Confidence.ToString()],
            ["Card", "Cycles (approx.)", When(card.Outcome), Interest(card.Outcome), card.Confidence.ToString()],
        ]);
    }

    private static Control Table(string[] headers, IEnumerable<string[]> rows)
    {
        var all = rows.ToList();
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", headers.Select((_, i) => i == 0 ? "1.4*" : "*"))),
            RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("28", all.Count + 1))),
        };

        for (var c = 0; c < headers.Length; c++)
        {
            var header = Text(headers[c].ToUpperInvariant(), "label");
            header.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(header, c);
            grid.Children.Add(header);
        }

        for (var r = 0; r < all.Count; r++)
        {
            for (var c = 0; c < headers.Length; c++)
            {
                var cell = Text(all[r][c], c == 0 ? "compact" : "mono");
                cell.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        return grid;
    }

    private static TextBlock Text(string text, params string[] classes)
    {
        var block = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis };
        block.Classes.AddRange(classes);
        return block;
    }

    private static IBrush? Resource(string key)
    {
        var app = Application.Current!;
        return app.TryFindResource(key, app.ActualThemeVariant, out var value) ? value as IBrush : null;
    }
}
