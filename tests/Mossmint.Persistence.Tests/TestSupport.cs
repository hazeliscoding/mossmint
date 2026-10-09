using Mossmint.Persistence.Profiles;

namespace Mossmint.Persistence.Tests;

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

// A throwaway Mossmint root under the temp folder, deleted afterwards.
internal sealed class TempRoot : IDisposable
{
    public TempRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mossmint-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        Locations = new ProfileLocations(Path);
    }

    public string Path { get; }

    public ProfileLocations Locations { get; }

    public ProfileStore Store(FixedClock? clock = null) =>
        new(Locations, clock ?? new FixedClock(new DateTimeOffset(2026, 10, 9, 14, 0, 0, TimeSpan.Zero)), KeyEnvelopeTests.Fast);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A file still held open by a failed test; the OS temp cleanup gets it.
        }
    }
}
