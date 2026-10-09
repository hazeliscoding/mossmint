namespace Mossmint.Desktop.ViewModels;

// A destination that isn't built yet. It says what the page is for and which
// milestone delivers it, instead of showing controls that do nothing.
public sealed class PageViewModel(
    Destination destination,
    string title,
    string purpose,
    string milestone,
    string emptyTitle,
    string emptyBody) : ViewModelBase
{
    public Destination Destination { get; } = destination;
    public string Title { get; } = title;
    public string Purpose { get; } = purpose;
    public string Milestone { get; } = milestone;
    public string MilestoneLabel => $"Arrives in {Milestone}";
    public string EmptyTitle { get; } = emptyTitle;
    public string EmptyBody { get; } = emptyBody;
}
