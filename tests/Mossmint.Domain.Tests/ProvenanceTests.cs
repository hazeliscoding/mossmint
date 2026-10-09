namespace Mossmint.Domain.Tests;

public class ProvenanceTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);

    [Fact]
    public void Each_source_has_a_fixed_confidence()
    {
        Provenance.UserConfirmed(Today).Confidence.Should().Be(Confidence.Confirmed);
        Provenance.LenderStatement(Today).Confidence.Should().Be(Confidence.Confirmed);
        Provenance.Scheduled(Today).Confidence.Should().Be(Confidence.Scheduled);
        Provenance.Estimated(Today).Confidence.Should().Be(Confidence.Estimated);
        Provenance.Imported(Today).Confidence.Should().Be(Confidence.NeedsReview);
    }

    [Fact]
    public void An_import_cannot_claim_to_be_confirmed()
    {
        var act = () => new Provenance(SourceKind.Imported, Today, Confidence.Confirmed);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_derived_figure_takes_its_weakest_input()
    {
        var derived = Provenance.Derived(Today, [Provenance.UserConfirmed(Today), Provenance.Estimated(Today), Provenance.Scheduled(Today)]);

        derived.Source.Should().Be(SourceKind.Derived);
        derived.Confidence.Should().Be(Confidence.Estimated);
    }

    [Fact]
    public void A_derived_figure_with_no_inputs_needs_review()
    {
        Provenance.Derived(Today, []).Confidence.Should().Be(Confidence.NeedsReview);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(-1, false)]
    public void Freshness_counts_whole_days_and_rejects_future_dates(int ageInDays, bool fresh)
    {
        var provenance = Provenance.UserConfirmed(Today.AddDays(-ageInDays));

        provenance.IsFreshOn(Today, maxAgeDays: 3).Should().Be(fresh);
    }
}
