using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace Mossmint.Desktop.Services;

// The opt-in update check (ADR-0003). Nothing here runs unless the user clicks.
public interface IAppUpdates
{
    string Version { get; }

    // False when Mossmint runs from source instead of from an install.
    bool IsInstalled { get; }

    // The newer version on GitHub Releases, or null when this one is current.
    Task<string?> CheckAsync();

    Task DownloadAsync(Action<int> progress);

    // Exits at once, installs the downloaded update and starts Mossmint again.
    void ApplyAndRestart();
}

public sealed class VelopackUpdates : IAppUpdates
{
    private static readonly string AppVersion =
        typeof(VelopackUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    private readonly Lazy<UpdateManager?> _manager = new(Create);
    private UpdateInfo? _update;

    public string Version => AppVersion;

    public bool IsInstalled => _manager.Value is { IsInstalled: true };

    public async Task<string?> CheckAsync()
    {
        _update = await Manager.CheckForUpdatesAsync();
        return _update?.TargetFullRelease.Version.ToString();
    }

    public Task DownloadAsync(Action<int> progress) =>
        Manager.DownloadUpdatesAsync(_update ?? throw new InvalidOperationException("Check for an update first."), progress);

    public void ApplyAndRestart() => Manager.ApplyUpdatesAndRestart(_update?.TargetFullRelease);

    private UpdateManager Manager => _manager.Value ?? throw new InvalidOperationException("Mossmint isn't installed.");

    // GitHub Releases only, never Velopack's hosted service. A beta build also looks at pre-releases.
    private static UpdateManager? Create()
    {
        try
        {
            return new UpdateManager(new GithubSource("https://github.com/hazeliscoding/mossmint", accessToken: null, prerelease: AppVersion.Contains('-')));
        }
        catch (InvalidOperationException)
        {
            // Tests and tools never run VelopackApp, so Velopack has no locator: that's "not installed" too.
            return null;
        }
    }
}
