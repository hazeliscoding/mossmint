using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mossmint.Domain;

// An ISO 4217 currency and its minor-unit precision. Only currencies with a
// known precision are accepted, so cent rounding is never guessed.
[JsonConverter(typeof(CurrencyJsonConverter))]
public readonly record struct Currency
{
    private static readonly Dictionary<string, int> MinorUnitsByCode = new(StringComparer.Ordinal)
    {
        ["USD"] = 2,
        ["CAD"] = 2,
        ["MXN"] = 2,
        ["EUR"] = 2,
        ["GBP"] = 2,
        ["CHF"] = 2,
        ["SEK"] = 2,
        ["NOK"] = 2,
        ["DKK"] = 2,
        ["PLN"] = 2,
        ["CZK"] = 2,
        ["AUD"] = 2,
        ["NZD"] = 2,
        ["INR"] = 2,
        ["BRL"] = 2,
        ["ZAR"] = 2,
        ["SGD"] = 2,
        ["HKD"] = 2,
        ["JPY"] = 0,
        ["KRW"] = 0,
        ["CLP"] = 0,
        ["ISK"] = 0,
        ["VND"] = 0,
        ["KWD"] = 3,
        ["BHD"] = 3,
        ["JOD"] = 3,
        ["OMR"] = 3,
        ["TND"] = 3,
    };

    private Currency(string code, int minorUnits)
    {
        Code = code;
        MinorUnits = minorUnits;
    }

    public static Currency Usd { get; } = new("USD", 2);

    public string Code { get; }

    public int MinorUnits { get; }

    // default(Currency) has no code; Money refuses it.
    public bool IsDefined => Code is not null;

    public static Currency Of(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return MinorUnitsByCode.TryGetValue(code, out var minorUnits)
            ? new Currency(code, minorUnits)
            : throw new ArgumentException($"Unsupported currency code '{code}'.", nameof(code));
    }

    public override string ToString() => Code ?? "(none)";
}

internal sealed class CurrencyJsonConverter : JsonConverter<Currency>
{
    public override Currency Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Currency.Of(reader.GetString() ?? throw new JsonException("Currency code is missing."));

    public override void Write(Utf8JsonWriter writer, Currency value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Code);
}
