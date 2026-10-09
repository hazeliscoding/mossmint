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

    public IProfileSession Current => Session ?? throw new InvalidOperationException("No profile is unlocked.");

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
