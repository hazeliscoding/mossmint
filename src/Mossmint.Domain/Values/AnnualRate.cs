using System.Text.Json.Serialization;

namespace Mossmint.Domain;

// A nominal annual rate stored as a fraction (0.1999 is 19.99%).
public readonly record struct AnnualRate
{
    [JsonConstructor]
    public AnnualRate(decimal value)
    {
        // Anything at or above 1000% is almost certainly a data-entry error
        // (a percent typed into a fraction field twice, for example).
        if (value < 0m || value >= 10m)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "An annual rate must be between 0 and 1000%.");
        }

        Value = value;
    }

    public decimal Value { get; }

    [JsonIgnore]
    public decimal Percent => Value * 100m;

    public static AnnualRate Zero => new(0m);

    public static AnnualRate FromPercent(decimal percent) => new(percent / 100m);

    public override string ToString() => $"{Percent:0.####}%";
}
