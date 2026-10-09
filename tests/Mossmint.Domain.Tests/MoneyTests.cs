using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;

namespace Mossmint.Domain.Tests;

public class MoneyTests
{
    private static readonly Currency Eur = Currency.Of("EUR");

    // Whole-cent amounts up to ±10 million, the range a household ledger needs.
    private static readonly Arbitrary<Money> Cents =
        Gen.Choose(-1_000_000_000, 1_000_000_000).Select(c => Money.FromMinorUnits(c, Currency.Usd)).ToArbitrary();

    [Fact]
    public void Adding_different_currencies_throws()
    {
        var act = () => Money.Usd(1m) + new Money(1m, Eur);

        act.Should().Throw<CurrencyMismatchException>().WithMessage("*USD*EUR*");
    }

    [Fact]
    public void Comparing_different_currencies_throws()
    {
        var act = () => Money.Usd(1m) < new Money(2m, Eur);

        act.Should().Throw<CurrencyMismatchException>();
    }

    [Fact]
    public void Money_without_a_currency_is_rejected()
    {
        var act = () => new Money(1m, default);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(2.345, MidpointRounding.AwayFromZero, 2.35)]
    [InlineData(2.345, MidpointRounding.ToEven, 2.34)]
    [InlineData(-2.345, MidpointRounding.AwayFromZero, -2.35)]
    [InlineData(10.191780821917808, MidpointRounding.AwayFromZero, 10.19)]
    public void Rounding_uses_the_given_midpoint_rule(double amount, MidpointRounding mode, double expected)
    {
        Money.Usd((decimal)amount).Round(mode).Should().Be(Money.Usd((decimal)expected));
    }

    [Fact]
    public void Currencies_round_to_their_own_minor_units()
    {
        new Money(1234.5m, Currency.Of("JPY")).Round(MidpointRounding.AwayFromZero).Amount.Should().Be(1235m);
        new Money(1.2345m, Currency.Of("KWD")).Round(MidpointRounding.AwayFromZero).Amount.Should().Be(1.235m);
    }

    [Fact]
    public void Minor_units_refuse_unrounded_amounts()
    {
        var act = () => Money.Usd(0.005m).ToMinorUnits();

        act.Should().Throw<InvalidOperationException>().WithMessage("*round it first*");
    }

    [Fact]
    public void Unknown_currency_codes_are_rejected()
    {
        var act = () => Currency.Of("XYZ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Equal_amounts_with_different_scale_are_equal()
    {
        Money.Usd(1.0m).Should().Be(Money.Usd(1.00m));
        Money.Usd(1.0m).GetHashCode().Should().Be(Money.Usd(1.00m).GetHashCode());
    }

    [Fact]
    public void Money_round_trips_through_json()
    {
        var original = new Money(-12.34m, Eur);

        var json = JsonSerializer.Serialize(original);
        var copy = JsonSerializer.Deserialize<Money>(json);

        json.Should().Be("""{"Amount":-12.34,"Currency":"EUR"}""");
        copy.Should().Be(original);
    }

    [Fact]
    public void Addition_is_commutative_and_associative() =>
        Prop.ForAll(Cents, Cents, Cents, (a, b, c) =>
            a + b == b + a && (a + b) + c == a + (b + c)).QuickCheckThrowOnFailure();

    [Fact]
    public void Subtracting_a_value_from_itself_is_zero() =>
        Prop.ForAll(Cents, a => (a - a).IsZero && (a + -a).IsZero).QuickCheckThrowOnFailure();

    [Fact]
    public void Minor_units_round_trip() =>
        Prop.ForAll(Cents, a => Money.FromMinorUnits(a.ToMinorUnits(), a.Currency) == a).QuickCheckThrowOnFailure();

    [Fact]
    public void Rounding_is_idempotent() =>
        Prop.ForAll(Cents, Gen.Choose(0, 99).ToArbitrary(), (a, extra) =>
        {
            var unrounded = a + Money.Usd(extra / 10_000m);
            var once = unrounded.Round(MidpointRounding.AwayFromZero);
            return once.Round(MidpointRounding.AwayFromZero) == once && once.IsWholeMinorUnits;
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void Annual_rate_rejects_impossible_values()
    {
        var negative = () => new AnnualRate(-0.01m);
        var typo = () => AnnualRate.FromPercent(2499m);

        negative.Should().Throw<ArgumentOutOfRangeException>();
        typo.Should().Throw<ArgumentOutOfRangeException>();
        AnnualRate.FromPercent(24.99m).Value.Should().Be(0.2499m);
    }
}
