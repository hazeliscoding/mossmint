using System.Security.Cryptography;
using System.Text;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Planning;
using Mossmint.Domain.Scheduling;

namespace Mossmint.Application.Import;

// A source file's bytes, read once. Everything downstream works on this copy,
// so nothing in the pipeline can write to the original.
public sealed class SourceFile
{
    public SourceFile(string name, byte[] content)
    {
        Name = name;
        Content = content;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
    }

    // The file name only, never a path.
    public string Name { get; }

    public byte[] Content { get; }

    public string Sha256 { get; }
}

public sealed record SourceFileInfo(string Name, string Sha256, long Length, string Shape);

// Where a value came from: a file and a location inside it (Sheet!A1 for a
// workbook, an observation ID for a connector).
public sealed record SourceRef(string File, string Location)
{
    public override string ToString() => $"{File} › {Location}";
}

public enum ImportOrigin
{
    Workbook,
    Connector,

    // A CSV or statement file downloaded from a bank or exported by another app.
    BankFile,
}

public enum FindingSeverity
{
    // Shown in the dry-run report only.
    Info,

    // Goes to the review queue: a figure the user should confirm or a mismatch to settle.
    NeedsReview,

    // The import can't be committed until the source is fixed.
    Blocking,
}

public sealed record ImportFinding(
    string Code,
    FindingSeverity Severity,
    string Title,
    string Detail,
    IReadOnlyList<SourceRef> Sources,
    string? SubjectKey = null)
{
    // The same finding from a re-import isn't queued twice, and one the user
    // already settled isn't reopened unless its details changed.
    public string Fingerprint =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Code}\n{SubjectKey}\n{Detail}")))[..32];
}

// Proposed records carry a stable key that identifies them across re-imports
// (for a workbook: what it is and where it lives, e.g. "bill:15th:rent"), so a
// second import updates what the first created instead of duplicating it.
public sealed record ProposedAccount(string Key, string Name, AccountKind Kind, IReadOnlyList<SourceRef> Sources);

public sealed record ProposedBalance(
    string AccountKey,
    Money Balance,
    DateOnly AsOf,
    string Label,
    SourceRef Source,
    Money? AccruedInterest = null);

public sealed record ProposedDebt(
    string AccountKey,
    DebtTerms Terms,
    IReadOnlyList<SourceRef> Sources,
    Money? OriginalPrincipal = null,
    DateOnly? OpenedOn = null);

public sealed record ProposedBill(
    string Key,
    string Name,
    AmountPolicy Amount,
    Schedule DueDates,
    BillPurpose Purpose,
    IReadOnlyList<SourceRef> Sources,
    string? TargetAccountKey = null,
    string? FundingAccountKey = null);

public sealed record ProposedIncome(
    string Key,
    string Name,
    Money NetPay,
    Schedule PayDates,
    IReadOnlyList<SourceRef> Sources,
    string? DepositAccountKey = null,
    IReadOnlyList<PayAmountOverride>? AmountOverrides = null);

public sealed record ProposedSettings(
    IReadOnlyList<SourceRef> Sources,
    SavingsContributionTarget? SavingsTarget = null,
    CashReserveFloor? ReserveFloor = null);

public sealed record ProposedTransaction(
    string AccountKey,
    string ExternalId,
    DateOnly Date,
    Money Amount,
    TransactionStatus Status,
    string Description,
    SourceRef Source);

// The source says a record it sent before no longer exists.
public sealed record Tombstone(string AccountKey, string ExternalId, SourceRef Source);

// A source row as read, before mapping, kept so every imported figure can be
// traced back and a newer mapper can re-map old imports.
public sealed record RawRecord(string Kind, SourceRef Source, IReadOnlyDictionary<string, string> Fields);

// Everything one import proposes, in normalized form. Workbook mappers and
// connectors both produce this, so they share validation, reconciliation,
// the dry run and the commit.
public sealed record ImportBatch(
    string Mapper,
    ImportOrigin Origin,
    DateOnly ObservedOn,
    Currency Currency,
    IReadOnlyList<SourceFileInfo> Files)
{
    public IReadOnlyList<ProposedAccount> Accounts { get; init; } = [];

    public IReadOnlyList<ProposedBalance> Balances { get; init; } = [];

    public IReadOnlyList<ProposedDebt> Debts { get; init; } = [];

    public IReadOnlyList<ProposedBill> Bills { get; init; } = [];

    public IReadOnlyList<ProposedIncome> Income { get; init; } = [];

    public ProposedSettings? Settings { get; init; }

    public IReadOnlyList<ProposedTransaction> Transactions { get; init; } = [];

    public IReadOnlyList<Tombstone> Tombstones { get; init; } = [];

    public IReadOnlyList<ImportFinding> Findings { get; init; } = [];

    public IReadOnlyList<RawRecord> RawRecords { get; init; } = [];

    // An opaque provenance reference: the source file's hash prefix and the
    // location, never a path.
    public string Reference(SourceRef source)
    {
        var file = Files.FirstOrDefault(f => f.Name == source.File);
        return $"{(file is null ? source.File : file.Sha256[..12])}:{source.Location}";
    }

    public Provenance ProvenanceFor(SourceRef source, DateOnly asOf) => Origin == ImportOrigin.Connector
        ? Provenance.Connector(asOf, Reference(source))
        : Provenance.Imported(asOf, Reference(source));
}

// Turns source files into a batch. The workbook importer implements it.
public interface ISourceMapper
{
    ImportBatch Map(IReadOnlyList<SourceFile> files, DateOnly today, Currency currency);
}
