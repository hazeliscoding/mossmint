using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mossmint.Application.Data;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Planning;

namespace Mossmint.Application.Import;

public enum ChangeKind
{
    Add,
    Update,
    Unchanged,

    // The user changed this record after the last import; their version stays.
    KeepEdited,

    // A record from an earlier import that this source no longer has. It is
    // kept and queued for review, never deleted.
    NotInSource,

    // A transaction the source says was removed. It is flagged, not deleted.
    RemovedAtSource,
}

public sealed record PlannedChange(ChangeKind Kind, string Entity, string Key, string Name, string? Detail = null);

// The dry run: what an import would change, what it found, and the exact
// commit that confirming it would write.
public sealed class ImportPlan(ImportBatch batch, IReadOnlyList<PlannedChange> changes, IReadOnlyList<ImportFinding> findings, ImportCommit commit)
{
    public ImportBatch Batch { get; } = batch;

    public IReadOnlyList<PlannedChange> Changes { get; } = changes;

    public IReadOnlyList<ImportFinding> Findings { get; } = findings;

    public ImportCommit Commit { get; } = commit;

    public bool CanCommit => !Findings.Any(f => f.Severity == FindingSeverity.Blocking);

    public int Count(ChangeKind kind) => Changes.Count(c => c.Kind == kind);

    public string Report() => ImportReport.Write(this);
}

// Builds the dry run for a batch against the current profile: validation,
// matching against earlier imports, reconciliation of balances and
// transactions, and the review items. It writes nothing.
public sealed class ImportPlanner(IProfileData data, IImportStore store, TimeProvider clock)
{
    // Older than this, a balance needs a fresh figure before a forecast trusts it.
    public const int StaleBalanceDays = 35;

    // Fingerprints compare values, not how they were written: 9000.00 read back
    // from whole cents is 9000, and must not look like an edit.
    private static readonly JsonSerializerOptions Json = new() { Converters = { new NormalizedDecimal() } };
    private static readonly Provenance Neutral = new(SourceKind.Imported, DateOnly.MinValue, Confidence.NeedsReview);

    public ImportPlan Plan(ImportBatch batch)
    {
        var context = new PlanContext(batch, data, store, clock);
        context.Validate();
        if (!context.HasBlocking)
        {
            context.PlanAccounts();
            context.PlanDebts();
            context.PlanBills();
            context.PlanIncome();
            context.PlanSettings();
            context.PlanBalances();
            context.PlanTransactions();
            context.PlanMissing();
        }

        return context.Build();
    }

    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

    internal static string Fingerprint(Account a) => Hash($"{a.Name}\n{a.Kind}\n{a.Currency.Code}");

    internal static string Fingerprint(DebtAccount d) => Hash(JsonSerializer.Serialize(d with { Account = default }, Json));

    internal static string Fingerprint(RecurringBill b) => Hash(JsonSerializer.Serialize(b with { Id = default, Provenance = Neutral }, Json));

    internal static string Fingerprint(IncomeSchedule i) => Hash(JsonSerializer.Serialize(i with { Id = default, Provenance = Neutral }, Json));

    internal static string Fingerprint(PlanSettings s) =>
        Hash(JsonSerializer.Serialize(new { s.SavingsTarget, s.ReserveFloor }, Json));

    internal static string Fingerprint(Transaction t) =>
        Hash(JsonSerializer.Serialize(t with { Id = default, Provenance = Neutral }, Json));

    private sealed class PlanContext(ImportBatch batch, IProfileData data, IImportStore store, TimeProvider clock)
    {
        private readonly List<PlannedChange> _changes = [];
        private readonly List<ImportFinding> _findings = [.. batch.Findings];
        private readonly Dictionary<string, ImportLink> _links = store.Links().ToDictionary(l => LinkKey(l.Entity, l.Key));
        private readonly Dictionary<string, AccountId> _accountIds = [];
        private readonly List<Account> _newAccounts = [];
        private readonly List<BalanceSnapshot> _balances = [];
        private readonly List<DebtAccount> _debts = [];
        private readonly List<RecurringBill> _bills = [];
        private readonly List<IncomeSchedule> _income = [];
        private readonly List<ImportedTransaction> _transactions = [];
        private readonly List<ImportLink> _newLinks = [];
        private readonly Guid _runId = Guid.CreateVersion7(clock.GetUtcNow());
        private readonly IReadOnlyList<Account> _existingAccounts = data.Accounts();
        private PlanSettings? _settings;

        public bool HasBlocking => _findings.Any(f => f.Severity == FindingSeverity.Blocking);

        private Currency Currency => batch.Currency;

        public void Validate()
        {
            var profileCurrency = data.Settings().Currency;
            if (profileCurrency != batch.Currency)
            {
                Block("currency", "Different currency", $"The source is in {batch.Currency.Code} but this profile uses {profileCurrency.Code}.");
            }

            foreach (var group in batch.Accounts.GroupBy(a => a.Key).Where(g => g.Count() > 1))
            {
                Block("duplicate-key", "The same account appears twice", $"Two sources describe the account '{group.First().Name}'.",
                    [.. group.SelectMany(a => a.Sources)]);
            }

            foreach (var group in batch.Bills.GroupBy(b => b.Key).Where(g => g.Count() > 1))
            {
                Block("duplicate-key", "The same bill appears twice", $"'{group.First().Name}' is proposed more than once.",
                    [.. group.SelectMany(b => b.Sources)]);
            }

            var known = batch.Accounts.Select(a => a.Key)
                .Concat(_links.Values.Where(l => l.Entity == LinkedEntity.Account).Select(l => l.Key))
                .ToHashSet();
            var references = batch.Balances.Select(b => (b.AccountKey, b.Source))
                .Concat(batch.Debts.Select(d => (d.AccountKey, d.Sources.FirstOrDefault() ?? Unknown)))
                .Concat(batch.Bills.SelectMany(b => new[] { b.TargetAccountKey, b.FundingAccountKey }.OfType<string>().Select(k => (k, b.Sources.FirstOrDefault() ?? Unknown))))
                .Concat(batch.Transactions.Select(t => (t.AccountKey, t.Source)));
            foreach (var (key, source) in references.Where(r => !known.Contains(r.Item1)).DistinctBy(r => r.Item1))
            {
                Block("unknown-account", "A record points at an account that doesn't exist", $"Nothing defines the account '{key}'.", [source]);
            }

            foreach (var balance in batch.Balances.Where(b => !b.Balance.IsWholeMinorUnits || b.AccruedInterest is { IsWholeMinorUnits: false }))
            {
                Block("fractional-amount", "An amount has fractions of a cent", $"{balance.Label}: {balance.Balance} can't be stored exactly.", [balance.Source]);
            }

            foreach (var money in AllAmounts().Where(m => m.Currency != Currency))
            {
                Block("currency", "Mixed currencies", $"{money} isn't in {Currency.Code}.");
            }
        }

        public void PlanAccounts()
        {
            foreach (var proposed in batch.Accounts)
            {
                var account = new Account(default, proposed.Name, proposed.Kind, Currency);
                if (Existing(LinkedEntity.Account, proposed.Key) is { } link && _existingAccounts.FirstOrDefault(a => a.Id.Value == link.EntityId) is { } current)
                {
                    // Accounts are never rewritten by an import: a renamed account keeps the user's name.
                    _accountIds[proposed.Key] = current.Id;
                    var renamedHere = Fingerprint(current) != link.Fingerprint;
                    _changes.Add(current.Name == proposed.Name
                        ? new(ChangeKind.Unchanged, "Account", proposed.Key, current.Name)
                        : new(ChangeKind.KeepEdited, "Account", proposed.Key, current.Name,
                            renamedHere ? $"Renamed in Mossmint; the source calls it '{proposed.Name}'." : $"The source now calls it '{proposed.Name}'."));
                    if (current.Kind != proposed.Kind)
                    {
                        Review("account-kind", "Account type differs", $"'{current.Name}' is a {current.Kind} in Mossmint but the source describes a {proposed.Kind}.",
                            proposed.Sources, proposed.Key);
                    }

                    Link(LinkedEntity.Account, proposed.Key, current.Id.Value, proposed.Sources, renamedHere ? link.Fingerprint : Fingerprint(account));
                    continue;
                }

                var id = new AccountId(NewId());
                _accountIds[proposed.Key] = id;
                account = account with { Id = id };
                _newAccounts.Add(account);
                _changes.Add(new(ChangeKind.Add, "Account", proposed.Key, proposed.Name, proposed.Kind.ToString()));
                Link(LinkedEntity.Account, proposed.Key, id.Value, proposed.Sources, Fingerprint(account));
            }

            foreach (var link in _links.Values.Where(l => l.Entity == LinkedEntity.Account && !_accountIds.ContainsKey(l.Key)))
            {
                if (_existingAccounts.Any(a => a.Id.Value == link.EntityId))
                {
                    _accountIds[link.Key] = new AccountId(link.EntityId);
                }
            }
        }

        public void PlanDebts()
        {
            var existing = data.Debts().ToDictionary(d => d.Account);
            foreach (var proposed in batch.Debts)
            {
                var accountId = _accountIds[proposed.AccountKey];
                var debt = new DebtAccount(accountId, proposed.Terms, proposed.OriginalPrincipal, proposed.OpenedOn);
                var name = AccountName(proposed.AccountKey);
                Upsert(LinkedEntity.Debt, proposed.AccountKey, name, accountId.Value, proposed.Sources, Fingerprint(debt),
                    existing.TryGetValue(accountId, out var current) ? Fingerprint(current) : null,
                    () => _debts.Add(debt));
            }
        }

        public void PlanBills()
        {
            var existing = data.Bills().ToDictionary(b => b.Id.Value);
            foreach (var proposed in batch.Bills)
            {
                var link = Existing(LinkedEntity.Bill, proposed.Key);
                var current = link is null ? null : existing.GetValueOrDefault(link.EntityId);
                var id = current?.Id ?? new BillId(NewId());
                var bill = new RecurringBill(id, proposed.Name, proposed.Amount, proposed.DueDates, proposed.Purpose,
                    batch.ProvenanceFor(proposed.Sources[0], batch.ObservedOn),
                    proposed.FundingAccountKey is { } funding ? _accountIds[funding] : null,
                    proposed.TargetAccountKey is { } target ? _accountIds[target] : null);
                Upsert(LinkedEntity.Bill, proposed.Key, proposed.Name, id.Value, proposed.Sources, Fingerprint(bill),
                    current is null ? null : Fingerprint(current), () => _bills.Add(bill));
            }
        }

        public void PlanIncome()
        {
            var existing = data.Income().ToDictionary(i => i.Id.Value);
            foreach (var proposed in batch.Income)
            {
                var link = Existing(LinkedEntity.Income, proposed.Key);
                var current = link is null ? null : existing.GetValueOrDefault(link.EntityId);
                var id = current?.Id ?? new IncomeId(NewId());
                var income = new IncomeSchedule(id, proposed.Name, proposed.NetPay, proposed.PayDates,
                    batch.ProvenanceFor(proposed.Sources[0], batch.ObservedOn),
                    proposed.AmountOverrides,
                    proposed.DepositAccountKey is { } deposit ? _accountIds[deposit] : null);
                Upsert(LinkedEntity.Income, proposed.Key, proposed.Name, id.Value, proposed.Sources, Fingerprint(income),
                    current is null ? null : Fingerprint(current), () => _income.Add(income));
            }
        }

        public void PlanSettings()
        {
            if (batch.Settings is not { } proposed)
            {
                return;
            }

            var current = data.Settings();
            var settings = current with
            {
                SavingsTarget = proposed.SavingsTarget ?? current.SavingsTarget,
                ReserveFloor = proposed.ReserveFloor ?? current.ReserveFloor,
            };
            var link = Existing(LinkedEntity.Settings, "settings");
            Upsert(LinkedEntity.Settings, "settings", "Savings and cushion", Guid.Empty, proposed.Sources, Fingerprint(settings),
                link is null ? null : Fingerprint(current), () => _settings = settings,
                isNew: link is null && current.SavingsTarget is null && current.ReserveFloor is null);
        }

        public void PlanBalances()
        {
            foreach (var group in batch.Balances.GroupBy(b => b.AccountKey))
            {
                var accountId = _accountIds[group.Key];
                var name = AccountName(group.Key);
                var existing = data.Balances(accountId);
                foreach (var proposed in group.OrderBy(b => b.AsOf))
                {
                    var reference = batch.Reference(proposed.Source);
                    var snapshot = new BalanceSnapshot(accountId, proposed.Balance, batch.ProvenanceFor(proposed.Source, proposed.AsOf),
                        AccruedInterest: proposed.AccruedInterest);
                    var same = existing.Where(e => e.Provenance.AsOf == proposed.AsOf && SameLocation(e.Provenance.Reference, reference)).ToList();
                    if (same.Any(e => e.Balance == proposed.Balance && e.AccruedInterest == proposed.AccruedInterest))
                    {
                        _changes.Add(new(ChangeKind.Unchanged, "Balance", group.Key, name, $"{proposed.Label} ({proposed.AsOf:yyyy-MM-dd})"));
                        continue;
                    }

                    _balances.Add(snapshot);
                    _changes.Add(new(ChangeKind.Add, "Balance", group.Key, name, $"{proposed.Label}: {proposed.Balance} as of {proposed.AsOf:yyyy-MM-dd}"));
                    if (same.Count > 0)
                    {
                        Review("balance-changed", $"{name}: balance changed at the source",
                            $"{proposed.Label} as of {proposed.AsOf:yyyy-MM-dd} was {same[0].Balance} when last imported and is now {proposed.Balance}. Both are kept.",
                            [proposed.Source], group.Key);
                    }

                    var newest = existing.FirstOrDefault();
                    if (newest is not null && newest.Provenance.AsOf > proposed.AsOf && newest.Provenance.Source != snapshot.Provenance.Source)
                    {
                        Note("older-balance", $"{name}: older than the balance on record",
                            $"{proposed.Label} is dated {proposed.AsOf:yyyy-MM-dd}; Mossmint already has a balance from {newest.Provenance.AsOf:yyyy-MM-dd}, which stays current.",
                            [proposed.Source], group.Key);
                    }
                }

                var latest = existing.Select(e => e.Provenance.AsOf).Concat(group.Select(b => b.AsOf)).Max();
                var age = batch.ObservedOn.DayNumber - latest.DayNumber;
                if (age > StaleBalanceDays)
                {
                    Review("stale-balance", $"{name}: balance is {age} days old",
                        $"The newest balance is from {latest:yyyy-MM-dd}. Enter a current one so forecasts don't start from an old figure.",
                        [.. group.Select(b => b.Source)], group.Key);
                }
            }
        }

        public void PlanTransactions()
        {
            var existing = store.Transactions().ToDictionary(t => t.Key);
            foreach (var proposed in batch.Transactions)
            {
                var key = $"{proposed.AccountKey}|{proposed.ExternalId}";
                var accountId = _accountIds[proposed.AccountKey];
                var name = AccountName(proposed.AccountKey);
                var current = existing.GetValueOrDefault(key);
                var transaction = new Transaction(current?.Transaction.Id ?? new TransactionId(NewId()), accountId, proposed.Date, proposed.Amount,
                    proposed.Status, proposed.Description, batch.ProvenanceFor(proposed.Source, proposed.Date));
                if (current is not null && !current.RemovedAtSource && Fingerprint(current.Transaction) == Fingerprint(transaction))
                {
                    _changes.Add(new(ChangeKind.Unchanged, "Transaction", key, name, proposed.Description));
                    continue;
                }

                _transactions.Add(new ImportedTransaction(transaction, key));
                _changes.Add(new(current is null ? ChangeKind.Add : ChangeKind.Update, "Transaction", key, name, proposed.Description));
                if (current is { RemovedAtSource: true })
                {
                    Note("transaction-returned", $"{name}: a removed transaction came back", $"'{proposed.Description}' was removed at the source earlier and is back.",
                        [proposed.Source], key);
                }
            }

            foreach (var tombstone in batch.Tombstones)
            {
                var key = $"{tombstone.AccountKey}|{tombstone.ExternalId}";
                if (!existing.TryGetValue(key, out var current) || current.RemovedAtSource)
                {
                    continue;
                }

                var name = AccountName(tombstone.AccountKey);
                _transactions.Add(current with { RemovedAtSource = true });
                _changes.Add(new(ChangeKind.RemovedAtSource, "Transaction", key, name, current.Transaction.Description));
                Review("removed-at-source", $"{name}: a transaction was removed at the source",
                    $"'{current.Transaction.Description}' on {current.Transaction.EffectiveDate:yyyy-MM-dd} ({current.Transaction.Amount}) is no longer reported. It is kept and flagged; delete it only if it really didn't happen.",
                    [tombstone.Source], key);
            }
        }

        public void PlanMissing()
        {
            var scopes = batch.Files.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var seen = _newLinks.Select(l => LinkKey(l.Entity, l.Key)).ToHashSet();
            foreach (var link in _links.Values.Where(l => scopes.Contains(l.Scope) && !seen.Contains(LinkKey(l.Entity, l.Key))))
            {
                _changes.Add(new(ChangeKind.NotInSource, link.Entity.ToString(), link.Key, link.Key));
                Review("not-in-source", $"No longer in {link.Scope}",
                    $"A {link.Entity.ToString().ToLowerInvariant()} imported earlier ({link.Key}) isn't in the source any more. It is kept until you remove it.",
                    [new SourceRef(link.Scope, "")], link.Key);
            }
        }

        public ImportPlan Build()
        {
            var lastRun = store.Runs().LastOrDefault()?.Id;
            var open = store.ReviewItems().Select(r => r.Fingerprint).ToHashSet();
            var now = clock.GetUtcNow();
            var reviewItems = _findings
                .Where(f => f.Severity == FindingSeverity.NeedsReview && !open.Contains(f.Fingerprint))
                .DistinctBy(f => f.Fingerprint)
                .Select(f => new ReviewItem(NewId(), _runId, f.Code, f.Severity, f.Title, f.Detail, [.. f.Sources.Select(s => s.ToString())],
                    f.SubjectKey, f.Fingerprint, ReviewStatus.Open, now))
                .ToList();
            var run = new ImportRun(_runId, batch.Mapper, batch.Origin, now, batch.Files, "");
            var commit = new ImportCommit(run, lastRun, _newAccounts, _balances, _debts, _bills, _income, _settings, _transactions,
                reviewItems, batch.RawRecords, _newLinks);
            var plan = new ImportPlan(batch, _changes, _findings, commit);
            return new ImportPlan(batch, _changes, _findings, commit with { Run = run with { Report = plan.Report() } });
        }

        private void Upsert(LinkedEntity entity, string key, string name, Guid entityId, IReadOnlyList<SourceRef> sources, string proposed,
            string? current, Action write, bool isNew = false)
        {
            var link = Existing(entity, key);
            var label = entity.ToString();
            if (link is null || current is null)
            {
                if (!isNew && link is null && current is not null)
                {
                    _changes.Add(new(ChangeKind.Update, label, key, name, "Replaces values entered in Mossmint."));
                }
                else
                {
                    _changes.Add(new(ChangeKind.Add, label, key, name));
                }

                write();
                Link(entity, key, entityId, sources, proposed);
                return;
            }

            var editedHere = current != link.Fingerprint;
            var changedThere = proposed != link.Fingerprint;
            switch (editedHere, changedThere)
            {
                case (false, false):
                    _changes.Add(new(ChangeKind.Unchanged, label, key, name));
                    Link(entity, key, entityId, sources, proposed);
                    break;
                case (false, true):
                    _changes.Add(new(ChangeKind.Update, label, key, name));
                    write();
                    Link(entity, key, entityId, sources, proposed);
                    break;
                case (true, false):
                    _changes.Add(new(ChangeKind.KeepEdited, label, key, name, "Edited in Mossmint; the source hasn't changed."));
                    Link(entity, key, entityId, sources, link.Fingerprint);
                    break;
                case (true, true):
                    _changes.Add(new(ChangeKind.KeepEdited, label, key, name, "Edited in Mossmint and changed in the source."));
                    Review("edited-both", $"{name}: changed in both places",
                        "You edited this in Mossmint, and the source has changed since the last import. Your version is kept; compare it with the source.",
                        sources, key);
                    Link(entity, key, entityId, sources, link.Fingerprint);
                    break;
            }
        }

        private void Link(LinkedEntity entity, string key, Guid id, IReadOnlyList<SourceRef> sources, string fingerprint) =>
            _newLinks.Add(new ImportLink(key, entity, id, sources.FirstOrDefault()?.File ?? "", fingerprint, _runId));

        private ImportLink? Existing(LinkedEntity entity, string key) => _links.GetValueOrDefault(LinkKey(entity, key));

        private string AccountName(string key) =>
            batch.Accounts.FirstOrDefault(a => a.Key == key)?.Name
            ?? (_accountIds.TryGetValue(key, out var id) ? _existingAccounts.FirstOrDefault(a => a.Id == id)?.Name : null)
            ?? key;

        private IEnumerable<Money> AllAmounts() =>
            batch.Balances.Select(b => b.Balance)
                .Concat(batch.Bills.Select(b => b.Amount.Expected).OfType<Money>())
                .Concat(batch.Income.Select(i => i.NetPay))
                .Concat(batch.Transactions.Select(t => t.Amount));

        private Guid NewId() => Guid.CreateVersion7(clock.GetUtcNow());

        private void Block(string code, string title, string detail, IReadOnlyList<SourceRef>? sources = null) =>
            _findings.Add(new ImportFinding(code, FindingSeverity.Blocking, title, detail, sources ?? []));

        private void Review(string code, string title, string detail, IReadOnlyList<SourceRef> sources, string? subject) =>
            _findings.Add(new ImportFinding(code, FindingSeverity.NeedsReview, title, detail, sources, subject));

        private void Note(string code, string title, string detail, IReadOnlyList<SourceRef> sources, string? subject) =>
            _findings.Add(new ImportFinding(code, FindingSeverity.Info, title, detail, sources, subject));

        private static string LinkKey(LinkedEntity entity, string key) => $"{entity}:{key}";

        // References are "<file hash prefix>:<location>"; the same cell in an
        // edited file is still the same figure.
        private static bool SameLocation(string? existing, string proposed) =>
            existing is not null && existing[(existing.IndexOf(':') + 1)..] == proposed[(proposed.IndexOf(':') + 1)..];

        private static readonly SourceRef Unknown = new("", "");
    }
}

internal sealed class NormalizedDecimal : System.Text.Json.Serialization.JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDecimal();

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value / 1.0000000000000000000000000000m);
}

internal static class ImportReport
{
    public static string Write(ImportPlan plan)
    {
        var b = plan.Batch;
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# Import dry run ({b.Mapper})");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Observed on {b.ObservedOn:yyyy-MM-dd}. {(plan.CanCommit ? "Ready to import." : "Can't be imported until the blocking problems are fixed.")}");
        text.AppendLine();
        text.AppendLine("## Sources");
        text.AppendLine();
        foreach (var file in b.Files)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {file.Name}: {file.Shape}, {file.Length:N0} bytes, SHA-256 {file.Sha256}");
        }

        text.AppendLine();
        text.AppendLine("## Changes");
        text.AppendLine();
        foreach (var kind in Enum.GetValues<ChangeKind>())
        {
            var changes = plan.Changes.Where(c => c.Kind == kind).ToList();
            if (changes.Count == 0)
            {
                continue;
            }

            text.AppendLine(CultureInfo.InvariantCulture, $"### {kind} ({changes.Count})");
            text.AppendLine();
            foreach (var change in changes)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- {change.Entity}: {change.Name}{(change.Detail is null ? "" : $" ({change.Detail})")}");
            }

            text.AppendLine();
        }

        foreach (var severity in new[] { FindingSeverity.Blocking, FindingSeverity.NeedsReview, FindingSeverity.Info })
        {
            var findings = plan.Findings.Where(f => f.Severity == severity).ToList();
            if (findings.Count == 0)
            {
                continue;
            }

            text.AppendLine(CultureInfo.InvariantCulture, $"## {severity switch { FindingSeverity.Blocking => "Blocking", FindingSeverity.NeedsReview => "For review", _ => "Notes" }} ({findings.Count})");
            text.AppendLine();
            foreach (var finding in findings)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- **{finding.Title}.** {finding.Detail}");
                foreach (var source in finding.Sources.Where(s => s.Location.Length > 0))
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"  - {source}");
                }
            }

            text.AppendLine();
        }

        return text.ToString().TrimEnd() + "\n";
    }
}
