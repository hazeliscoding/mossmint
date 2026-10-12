using Mossmint.Application.Data;
using Mossmint.Domain.Accounts;

namespace Mossmint.Application.Setup;

// A made-up household for "Try a sample household" and the screenshots. Every name
// and figure is invented, and dates are relative to today so the sample never looks stale.
public static class SampleHousehold
{
    public const string ProfileName = "Sample household";

    // Public on purpose: the sample holds only made-up numbers, and a passphrase anyone can
    // look up means it can always be unlocked, deleted or restored, with or without this PC.
    public const string Passphrase = "sample household";

    public static void Fill(IProfileData data, TimeProvider clock)
    {
        var entry = new ManualEntry(data, clock);
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var checking = entry.AddCashAccount("Checking", 1_240m, today.AddDays(-1));
        entry.AddPaycheck(new PaycheckEntry("Paycheck", 2_050m, PayPattern.TwiceMonthly), checking.Id);
        entry.AddBill(new BillEntry("Rent", 1_150m, 1), checking.Id);
        entry.AddBill(new BillEntry("Electric", 70m, 12, IsEstimate: true, Low: 60m, High: 95m), checking.Id);
        entry.AddBill(new BillEntry("Phone", 45m, 20), checking.Id);
        entry.AddBill(new BillEntry("Internet", 60m, 17), checking.Id);
        entry.AddBill(new BillEntry("Insurance", 132m, 25), checking.Id);
        entry.AddBill(new BillEntry("Streaming", 18m, 22), checking.Id);
        entry.AddLoan(new LoanEntry("Car loan", 9_800m, today.AddDays(-9), 7.4m, 285m, 28), checking.Id);
        entry.AddCard(new CardEntry("Card", 2_300m, today.AddDays(-18), 24.99m, 150m, 21, 3), checking.Id);
        entry.SetSavings(150m, 300m);
        entry.AddCashAccount("Savings", 1_850m, today.AddDays(-8), AccountKind.Savings);
        entry.AddGoal("Car fund", 3_000m, 1_250m, new DateOnly(today.Year, today.Month, 1).AddMonths(7));
        entry.AddGoal("Holiday trip", 900m, 120m, today.AddDays(70));
        entry.AddGoal("New laptop", 1_400m, 1_400m);
        entry.RecordEntry(checking.Id, "Groceries", -64.20m, today, "groceries");
    }
}
