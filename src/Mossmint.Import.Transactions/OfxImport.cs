using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mossmint.Application.Import;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;

namespace Mossmint.Import.Transactions;

// Turns statement files into an import batch for the accounts the user chose.
// Each transaction is keyed by the bank's own FITID, so a statement that
// overlaps an earlier one adds only what's new. The account number never
// leaves the file: keys hash it and labels show its last four digits.
public static class OfxImport
{
    public const string Mapper = "ofx";

    public static string AccountKey(OfxStatement statement) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{statement.BankId}\n{statement.AccountId}")))[..32];

    public static string Describe(OfxStatement statement)
    {
        var type = statement.Type switch
        {
            OfxAccountType.Checking => "Checking",
            OfxAccountType.Savings => "Savings",
            OfxAccountType.MoneyMarket => "Money market",
            OfxAccountType.CreditLine => "Credit line",
            OfxAccountType.CreditCard => "Card",
            _ => "Account",
        };
        return statement.AccountId.Length >= 4 ? $"{type} ending {statement.AccountId[^4..]}" : type;
    }

    public static ImportBatch Map(SourceFile file, OfxFile ofx, IReadOnlyDictionary<string, Account> targets, DateOnly today, Currency currency)
    {
        var findings = new List<ImportFinding>();
        var transactions = new List<ProposedTransaction>();
        var balances = new List<ProposedBalance>();
        var used = new List<Account>();
        foreach (var statement in ofx.Statements)
        {
            var label = Describe(statement);
            var whole = new SourceRef(file.Name, label);
            if (!targets.TryGetValue(AccountKey(statement), out var account))
            {
                findings.Add(new ImportFinding("ofx-account-skipped", FindingSeverity.Info, $"{label} left out",
                    "No Mossmint account was chosen for this statement, so it wasn't imported.", [whole]));
                continue;
            }

            if (statement.Currency.Length > 0 && statement.Currency != currency.Code)
            {
                findings.Add(new ImportFinding("ofx-currency", FindingSeverity.Blocking, $"{label} is in {statement.Currency}",
                    $"This profile uses {currency.Code}, and Mossmint doesn't convert currencies.", [whole]));
            }

            findings.AddRange(statement.Problems.Select(p => new ImportFinding("ofx-unreadable", FindingSeverity.Blocking, $"{label}: something can't be read", p, [whole])));
            used.Add(account);
            var key = ImportKeys.Account(account.Id);
            var seen = new Dictionary<string, int>();
            foreach (var t in statement.Transactions)
            {
                // A bank that repeats an ID within one file still sent two transactions.
                var occurrence = seen[t.FitId] = seen.GetValueOrDefault(t.FitId) + 1;
                var id = occurrence == 1 ? t.FitId : $"{t.FitId}#{occurrence}";
                if (occurrence == 2)
                {
                    findings.Add(new ImportFinding("ofx-repeated-id", FindingSeverity.Info, $"{label}: the bank used one ID twice",
                        $"Two transactions share the ID {t.FitId}; both are kept.", [new SourceRef(file.Name, $"{label} › {t.FitId}")]));
                }

                transactions.Add(new ProposedTransaction(key, $"ofx:{id}", t.Posted, Signed(t.Amount, account, currency), TransactionStatus.Posted, t.Description,
                    new SourceRef(file.Name, $"{label} › {id}")));
            }

            if (statement.Ledger is { } ledger)
            {
                var balance = Signed(ledger.Amount, account, currency);
                balances.Add(new ProposedBalance(key, balance, ledger.AsOf, $"Statement balance, {label}", new SourceRef(file.Name, $"{label} › balance")));
                if (account.IsLiability && balance.Amount < 0)
                {
                    findings.Add(new ImportFinding("ofx-card-credit", FindingSeverity.NeedsReview, $"{account.Name}: the statement shows a credit",
                        $"It says the bank owes you {(-balance).Amount.ToString("N2", CultureInfo.InvariantCulture)}. Some banks sign card balances the other way round; if the card actually owes that, record the right balance.",
                        [new SourceRef(file.Name, $"{label} › balance")], key));
                }
            }
        }

        return new ImportBatch(Mapper, ImportOrigin.BankFile, today, currency, [new SourceFileInfo(file.Name, file.Sha256, file.Content.Length, "Bank statement (OFX)")])
        {
            Accounts = [.. used.Distinct().Select(a => new ProposedAccount(ImportKeys.Account(a.Id), a.Name, a.Kind, [new SourceRef(file.Name, "")]))],
            Balances = balances,
            Transactions = transactions,
            Findings = findings,
        };
    }

    // OFX signs from the holder's side; a card or loan in Mossmint is what's owed.
    private static Money Signed(decimal amount, Account account, Currency currency) => new(account.IsLiability ? -amount : amount, currency);
}
