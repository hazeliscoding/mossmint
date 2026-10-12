using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mossmint.Import.Transactions;

// A mapping the user confirmed, and which Mossmint account each source account
// went to ("" when the file has no Account column). It is kept inside the
// encrypted profile, keyed by the file's header fingerprint.
public sealed record SavedCsvMapping(CsvMapping Mapping, IReadOnlyDictionary<string, Guid> Destinations)
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public string ToJson() => JsonSerializer.Serialize(new Stored(CurrentVersion, Mapping, Destinations), Json);

    // A mapping from a newer version, or one that no longer reads, is ignored
    // and the file's columns are guessed again.
    public static SavedCsvMapping? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Stored>(json, Json) is { Version: CurrentVersion, Mapping: { } mapping } stored
                ? new SavedCsvMapping(mapping, stored.Destinations ?? new Dictionary<string, Guid>())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Stored(int Version, CsvMapping? Mapping, IReadOnlyDictionary<string, Guid>? Destinations);
}
