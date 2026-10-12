using System.Text.Json;

namespace Mossmint.Desktop.Services;

// The daily update check's settings (ADR-0003). They belong to this install, not to
// a profile, so they sit in the data root beside the profiles and hold no financial data.
// NotNow is the version the user put off; a newer one shows the strip again.
public sealed record UpdatePreferences(bool CheckDaily = false, DateTimeOffset? LastChecked = null, string? NotNow = null)
{
    public const string FileName = "updates.json";

    // A missing or damaged file means the defaults: the check stays off.
    public static UpdatePreferences Load(string folder)
    {
        try
        {
            return JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(Path.Combine(folder, FileName))) ?? new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    // Best effort: if it can't be saved, the choice lasts until Mossmint closes.
    public void Save(string folder)
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
