using System.Text.Json;
using Mossmint.Application.Profiles;

namespace Mossmint.Persistence.Profiles;

// Where profiles live (ADR-0001): %LOCALAPPDATA%\Mossmint\Profiles\<id>\,
// never inside the program folder, which Velopack replaces on every update.
public sealed class ProfileLocations
{
    public const int LayoutVersion = 1;

    public ProfileLocations(string root, string? installDirectory = null)
    {
        var full = Path.GetFullPath(root);
        var install = Path.GetFullPath(installDirectory ?? AppContext.BaseDirectory);
        if (IsSameOrInside(full, install))
        {
            throw new InvalidOperationException("Profiles can't be stored inside the program folder; updates replace it.");
        }

        Root = full;
    }

    public string Root { get; }

    public string ProfilesDirectory => Path.Combine(Root, "Profiles");

    public static ProfileLocations Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "Mossmint"));

    public string ProfileDirectory(ProfileId id) => Path.Combine(ProfilesDirectory, id.ToString());

    // Work-in-progress folders start with a dot and are never listed as profiles.
    public string StagingDirectory(string purpose) => Path.Combine(ProfilesDirectory, $".{purpose}-{Guid.NewGuid():N}");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(ProfilesDirectory);
        var layout = Path.Combine(Root, "layout.json");
        if (!File.Exists(layout))
        {
            File.WriteAllText(layout, JsonSerializer.Serialize(new { version = LayoutVersion }));
        }
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        var p = Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
        var f = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }
}
