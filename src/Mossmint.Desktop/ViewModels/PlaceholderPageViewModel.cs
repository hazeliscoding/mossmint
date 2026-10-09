namespace Mossmint.Desktop.ViewModels;

public abstract class PageViewModel(Destination destination, string title, string purpose) : ViewModelBase
{
    public Destination Destination { get; } = destination;
    public string Title { get; } = title;
    public string Purpose { get; } = purpose;

    // Pages read fresh data each time they are shown.
    public virtual void OnNavigatedTo()
    {
    }
}

// A destination that isn't built yet. It says what the page is for and which
// milestone delivers it, instead of showing controls that do nothing.
public sealed class PlaceholderPageViewModel(
    Destination destination,
    string title,
    string purpose,
    string milestone,
    string emptyTitle,
    string emptyBody) : PageViewModel(destination, title, purpose)
{
    public string Milestone { get; } = milestone;
    public string MilestoneLabel => $"Arrives in {Milestone}";
    public string EmptyTitle { get; } = emptyTitle;
    public string EmptyBody { get; } = emptyBody;
}
