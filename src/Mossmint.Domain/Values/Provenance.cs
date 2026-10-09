namespace Mossmint.Domain;

// Where a figure came from (ADR-0002). The source fixes how far it can be trusted.
public enum SourceKind
{
    UserConfirmed,
    LenderStatement,
    Imported,
    Connector,
    Scheduled,
    Estimated,
    Derived,
}

// Ordered weakest to strongest, so the weakest of several inputs is their minimum.
public enum Confidence
{
    NeedsReview = 0,
    Estimated = 1,
    Scheduled = 2,
    Confirmed = 3,
}

public sealed record Provenance
{
    public Provenance(SourceKind source, DateOnly asOf, Confidence confidence, string? reference = null)
    {
        if (source != SourceKind.Derived && confidence != CanonicalConfidence(source))
        {
            throw new ArgumentException(
                $"{source} figures are always {CanonicalConfidence(source)}; only derived figures choose their confidence.",
                nameof(confidence));
        }

        Source = source;
        AsOf = asOf;
        Confidence = confidence;
        Reference = reference;
    }

    public SourceKind Source { get; }

    public DateOnly AsOf { get; }

    public Confidence Confidence { get; }

    // Opaque pointer to the evidence: a statement ID, an import cell, a connector
    // observation. Never a file path.
    public string? Reference { get; }

    public static Provenance UserConfirmed(DateOnly asOf, string? reference = null) =>
        new(SourceKind.UserConfirmed, asOf, Confidence.Confirmed, reference);

    public static Provenance LenderStatement(DateOnly asOf, string? reference = null) =>
        new(SourceKind.LenderStatement, asOf, Confidence.Confirmed, reference);

    public static Provenance Imported(DateOnly asOf, string? reference = null) =>
        new(SourceKind.Imported, asOf, Confidence.NeedsReview, reference);

    public static Provenance Connector(DateOnly asOf, string? reference = null) =>
        new(SourceKind.Connector, asOf, Confidence.Confirmed, reference);

    public static Provenance Scheduled(DateOnly asOf, string? reference = null) =>
        new(SourceKind.Scheduled, asOf, Confidence.Scheduled, reference);

    public static Provenance Estimated(DateOnly asOf, string? reference = null) =>
        new(SourceKind.Estimated, asOf, Confidence.Estimated, reference);

    // A derived figure is never more certain than its weakest input.
    public static Provenance Derived(DateOnly asOf, IEnumerable<Provenance> inputs)
    {
        var weakest = inputs.Select(i => i.Confidence).DefaultIfEmpty(Confidence.NeedsReview).Min();
        return new(SourceKind.Derived, asOf, weakest);
    }

    public int AgeInDays(DateOnly today) => today.DayNumber - AsOf.DayNumber;

    // Fresh means observed no more than maxAgeDays ago and not dated in the future.
    public bool IsFreshOn(DateOnly today, int maxAgeDays) => AgeInDays(today) is var age && age >= 0 && age <= maxAgeDays;

    public static Confidence CanonicalConfidence(SourceKind source) => source switch
    {
        SourceKind.UserConfirmed or SourceKind.LenderStatement or SourceKind.Connector => Confidence.Confirmed,
        SourceKind.Scheduled => Confidence.Scheduled,
        SourceKind.Estimated => Confidence.Estimated,
        SourceKind.Imported => Confidence.NeedsReview,
        SourceKind.Derived => Confidence.NeedsReview,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };
}
