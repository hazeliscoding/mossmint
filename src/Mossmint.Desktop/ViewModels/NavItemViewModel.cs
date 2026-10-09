namespace Mossmint.Desktop.ViewModels;

public sealed class NavItemViewModel(PageViewModel page, string iconKey, string? shortcut, bool startsGroup) : ViewModelBase
{
    public PageViewModel Page { get; } = page;
    public Destination Destination => Page.Destination;
    public string Label => Page.Title;
    public string IconKey { get; } = iconKey;
    public string? Shortcut { get; } = shortcut;
    public bool StartsGroup { get; } = startsGroup;
    public string ToolTip => Shortcut is null ? Label : $"{Label} ({Shortcut})";
}
