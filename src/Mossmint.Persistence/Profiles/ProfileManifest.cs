using System.Text.Json;
using Mossmint.Persistence.Security;

namespace Mossmint.Persistence.Profiles;

// profile.json: the profile's name and wrapped keys, never a secret in clear.
// Losing it loses the keys, so it is always replaced atomically.
internal sealed record ProfileManifest(
    string Format,
    int FormatVersion,
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    IReadOnlyList<KeyEnvelope> Envelopes)
{
    public const string FileName = "profile.json";
    public const string DatabaseFileName = "profile.db";
    public const string FormatName = "mossmint-profile";
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public KeyEnvelope? Envelope(EnvelopeKind kind) => Envelopes.FirstOrDefault(e => e.Kind == kind);

    public ProfileManifest WithEnvelope(KeyEnvelope envelope) =>
        this with { Envelopes = [.. Envelopes.Where(e => e.Kind != envelope.Kind), envelope] };

    public ProfileManifest WithoutEnvelope(EnvelopeKind kind) =>
        this with { Envelopes = [.. Envelopes.Where(e => e.Kind != kind)] };

    public static ProfileManifest Read(string directory)
    {
        var manifest = JsonSerializer.Deserialize<ProfileManifest>(File.ReadAllText(Path.Combine(directory, FileName)), Options)
            ?? throw new InvalidDataException("profile.json is empty.");
        if (manifest.Format != FormatName)
        {
            throw new InvalidDataException("profile.json isn't a Mossmint profile.");
        }

        if (manifest.FormatVersion > CurrentFormatVersion)
        {
            throw new InvalidDataException($"This profile was made by a newer Mossmint (format {manifest.FormatVersion}).");
        }

        return manifest;
    }

    public void Write(string directory)
    {
        var path = Path.Combine(directory, FileName);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, this, Options);
        }

        File.Move(temp, path, overwrite: true);
    }
}
