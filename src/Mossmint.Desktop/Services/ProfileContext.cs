using CommunityToolkit.Mvvm.ComponentModel;
using Mossmint.Application.Profiles;

namespace Mossmint.Desktop.Services;

// The one unlocked profile, if any. Locking disposes the session, which closes
// the database and zeroes the key.
public sealed partial class ProfileContext : ObservableObject, IDisposable
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnlocked), nameof(ProfileName), nameof(ProfileInitial))]
    private IProfileSession? _session;

    public bool IsUnlocked => Session is not null;

    public string ProfileName => Session?.Profile.Name ?? "No profile";

    public string ProfileInitial => ProfileName[..1].ToUpperInvariant();

    // Backups and imports count themselves in while they run, so an update restart can't cut one short.
    public int ActiveWork { get; set; }

    public IProfileSession Current => Session ?? throw new InvalidOperationException("No profile is unlocked.");

    // The shell outlives a lock: work started for one session checks this after
    // every wait, so it can never show or save into the next profile unlocked.
    public bool IsCurrent(IProfileSession? session) => session is not null && ReferenceEquals(Session, session);

    public void Open(IProfileSession session)
    {
        Session?.Dispose();
        Session = session;
    }

    public void Lock()
    {
        var session = Session;
        Session = null;
        session?.Dispose();
    }

    public void Dispose() => Lock();
}
