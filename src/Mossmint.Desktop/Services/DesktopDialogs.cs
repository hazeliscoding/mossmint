using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace Mossmint.Desktop.Services;

// File pickers and the clipboard, behind an interface so view models stay
// testable and headless tests never open real dialogs.
public interface IDesktopDialogs
{
    Task<string?> PickBackupToOpenAsync();

    Task<string?> PickBackupSaveLocationAsync(string suggestedName);

    Task<string?> PickFolderAsync(string title);

    Task<bool> SaveTextAsync(string suggestedName, string text, string title);

    // Paths of the workbooks the user picked; empty when they cancel.
    Task<IReadOnlyList<string>> PickWorkbooksAsync();

    Task CopyAsync(string text);
}

internal sealed class AvaloniaDesktopDialogs : IDesktopDialogs
{
    private static readonly FilePickerFileType Backup = new("Mossmint backup") { Patterns = ["*.mmbak"] };
    private static readonly FilePickerFileType Workbook = new("Excel workbook") { Patterns = ["*.xlsx"] };

    public TopLevel? Owner { get; set; }

    public async Task<string?> PickBackupToOpenAsync()
    {
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a Mossmint backup",
            FileTypeFilter = [Backup],
            AllowMultiple = false,
        });
        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickBackupSaveLocationAsync(string suggestedName)
    {
        var file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save an encrypted backup",
            SuggestedFileName = suggestedName,
            DefaultExtension = "mmbak",
            FileTypeChoices = [Backup],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count == 1 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<IReadOnlyList<string>> PickWorkbooksAsync()
    {
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose your workbooks",
            FileTypeFilter = [Workbook],
            AllowMultiple = true,
        });
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    public async Task<bool> SaveTextAsync(string suggestedName, string text, string title)
    {
        var file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "txt",
        });
        if (file is null)
        {
            return false;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(text);
        return true;
    }

    public Task CopyAsync(string text) =>
        Owner?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;

    private IStorageProvider Storage =>
        Owner?.StorageProvider ?? throw new InvalidOperationException("The window isn't ready for file dialogs yet.");
}
