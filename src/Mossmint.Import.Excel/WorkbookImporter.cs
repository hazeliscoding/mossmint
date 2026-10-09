using Mossmint.Application.Import;
using Mossmint.Domain;
using Mossmint.Domain.Planning;
using Mossmint.Import.Excel.Mapping;
using Mossmint.Import.Excel.Xlsx;

namespace Mossmint.Import.Excel;

public enum WorkbookShape
{
    Budget,
    CreditCard,
    SimpleInterestLoan,
    AutoLoan,
}

// Recognizes a workbook by its sheets and labels, never by its file name.
public static class WorkbookShapes
{
    public static WorkbookShape? Recognize(XlsxWorkbook workbook)
    {
        if (workbook.Sheet("Main Money") is not null)
        {
            return WorkbookShape.Budget;
        }

        if (workbook.Sheet("Settings") is not { } settingsSheet || workbook.Sheet("Payment Schedule") is null)
        {
            return null;
        }

        var settings = new LabelledValues(settingsSheet);
        if (settings.Has("Statement Closing Day"))
        {
            return WorkbookShape.CreditCard;
        }

        if (settings.Has("Unpaid Accrued Interest") || (settings.Has("Due Day") && settings.Has("Regular Payment")))
        {
            return WorkbookShape.SimpleInterestLoan;
        }

        return settings.Has("Baseline Date") || settings.Has("Payments Completed Before Baseline") ? WorkbookShape.AutoLoan : null;
    }

    public static string Describe(WorkbookShape shape) => shape switch
    {
        WorkbookShape.Budget => "budget workbook",
        WorkbookShape.CreditCard => "credit card workbook",
        WorkbookShape.SimpleInterestLoan => "simple-interest loan workbook",
        WorkbookShape.AutoLoan => "auto loan workbook",
        _ => "workbook",
    };
}

// Maps the known workbook shapes to an import batch. The mapper version is
// stored with every import, so records from an older mapping can be told apart.
public sealed class WorkbookImporter : ISourceMapper
{
    public const string MapperVersion = "workbook/1";

    public ImportBatch Map(IReadOnlyList<SourceFile> files, DateOnly today, Currency currency)
    {
        var output = new ImportCollector();
        var infos = new List<SourceFileInfo>();
        var books = new List<(SourceFile File, XlsxWorkbook Book, WorkbookShape Shape)>();
        var hashes = new Dictionary<string, string>();
        foreach (var file in files)
        {
            if (hashes.TryGetValue(file.Sha256, out var original))
            {
                output.Note("duplicate-file", $"{file.Name} is a copy of {original}", "Identical files are imported once.", [new SourceRef(file.Name, "")]);
                continue;
            }

            hashes[file.Sha256] = file.Name;
            XlsxWorkbook book;
            try
            {
                book = XlsxReader.Read(file.Content);
            }
            catch (XlsxFormatException e)
            {
                infos.Add(new SourceFileInfo(file.Name, file.Sha256, file.Content.LongLength, "unreadable"));
                output.Block("unreadable", $"{file.Name} can't be read", e.Message, [new SourceRef(file.Name, "")]);
                continue;
            }

            var shape = WorkbookShapes.Recognize(book);
            infos.Add(new SourceFileInfo(file.Name, file.Sha256, file.Content.LongLength, shape is { } s ? WorkbookShapes.Describe(s) : "not recognized"));
            if (shape is null)
            {
                output.Block("unknown-workbook", $"{file.Name} isn't a workbook Mossmint knows",
                    "Mossmint reads the budget workbook (a Main Money sheet) and the debt workbooks (Settings and Payment Schedule sheets). Remove this file from the import.",
                    [new SourceRef(file.Name, "")]);
                continue;
            }

            books.Add((file, book, shape.Value));
        }

        foreach (var group in books.Where(b => b.Shape != WorkbookShape.Budget).GroupBy(b => DebtWorkbooks.AccountKey(Labels.AccountNameFromFile(b.File.Name))).Where(g => g.Count() > 1))
        {
            output.Block("duplicate-workbook", "Two workbooks describe the same debt",
                $"{string.Join(" and ", group.Select(g => g.File.Name))} would both become the same account. Import one of them.",
                [.. group.Select(g => new SourceRef(g.File.Name, ""))]);
        }

        if (books.Count(b => b.Shape == WorkbookShape.Budget) > 1)
        {
            output.Block("duplicate-workbook", "More than one budget workbook",
                "Import one budget workbook at a time; each would describe the same paychecks.",
                [.. books.Where(b => b.Shape == WorkbookShape.Budget).Select(b => new SourceRef(b.File.Name, ""))]);
        }

        if (output.Findings.Any(f => f.Severity == FindingSeverity.Blocking))
        {
            return Batch(output, infos, today, currency);
        }

        var debts = new List<DebtFacts>();
        foreach (var (file, book, shape) in books.Where(b => b.Shape != WorkbookShape.Budget))
        {
            var ctx = new MappingContext(file.Name, book, currency, today, output);
            _ = shape switch
            {
                WorkbookShape.CreditCard => DebtWorkbooks.MapCard(ctx, debts),
                WorkbookShape.SimpleInterestLoan => DebtWorkbooks.MapSimpleInterestLoan(ctx, debts),
                _ => DebtWorkbooks.MapAutoLoan(ctx, debts),
            };
        }

        foreach (var (file, book, _) in books.Where(b => b.Shape == WorkbookShape.Budget))
        {
            BudgetWorkbook.Map(new MappingContext(file.Name, book, currency, today, output), debts);
        }

        AddMissingDebtPayments(output, debts, books.Any(b => b.Shape == WorkbookShape.Budget));
        return Batch(output, infos, today, currency);
    }

    // A debt with no payment line in the budget would vanish from the
    // forecast, so its own regular payment becomes the bill.
    private static void AddMissingDebtPayments(ImportCollector output, List<DebtFacts> debts, bool hasBudget)
    {
        foreach (var debt in debts.Where(d => d.RegularPayment is { IsPositive: true } && d.PaymentDates is not null))
        {
            if (output.Bills.Any(b => b.TargetAccountKey == debt.AccountKey))
            {
                continue;
            }

            output.Bills.Add(new ProposedBill($"bill:debt:{Labels.Slug(debt.Name)}", debt.Name, AmountPolicy.Fixed(debt.RegularPayment!.Value),
                debt.PaymentDates!, BillPurpose.DebtPayment, [debt.Source], debt.AccountKey, hasBudget ? BudgetWorkbook.AccountKey : null));
            if (hasBudget)
            {
                output.Review("debt-not-in-budget", $"{debt.Name} has no payment line in the budget",
                    $"Mossmint added its regular payment of {ImportText.Amount(debt.RegularPayment.Value)} as a bill so the forecast includes it. Remove it if the debt is paid another way.",
                    [debt.Source], debt.AccountKey);
            }
        }
    }

    private static ImportBatch Batch(ImportCollector output, List<SourceFileInfo> infos, DateOnly today, Currency currency) =>
        new(MapperVersion, ImportOrigin.Workbook, today, currency, infos)
        {
            Accounts = output.Accounts,
            Balances = output.Balances,
            Debts = output.Debts,
            Bills = output.Bills,
            Income = output.Income,
            Settings = output.Settings,
            Findings = output.Findings,
            RawRecords = output.RawRecords,
        };
}
