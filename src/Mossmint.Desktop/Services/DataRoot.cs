using Mossmint.Persistence.Profiles;

namespace Mossmint.Desktop.Services;

public static class DataRoot
{
    // Tests, the screenshot tool and portable setups point Mossmint somewhere
    // other than %LOCALAPPDATA%\Mossmint, so they never touch a real profile.
    public const string Variable = "MOSSMINT_DATA_ROOT";

    public static ProfileLocations Locations() =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } root
            ? new ProfileLocations(root)
            : ProfileLocations.Default();
}
