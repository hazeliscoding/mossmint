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
