using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mossmint.Application.Import;
using Mossmint.Desktop.Controls;
using Mossmint.Import.Excel;
using Mossmint.Import.Transactions;

namespace Mossmint.Desktop.ViewModels;

public sealed record SourceFileRow(string Name, string Shape, string Hash);

public sealed record FindingRow(string Title, string Detail, string Sources, string Badge, StatusTone Tone);

public sealed record ChangeSummaryRow(string Label, string Count);

public sealed record HistoryRow(string When, string Summary);

public sealed partial class ReviewItemRow(ReviewItem item) : ObservableObject
{
    public ReviewItem Item { get; } = item;
    public string Title => Item.Title;
    public string Detail => Item.Detail;
    public string Sources => string.Join("\n", Item.Sources.Where(s => !s.EndsWith("› ", StringComparison.Ordinal)));
    public bool HasSources => Sources.Length > 0;
    public bool IsOpen => Item.Status == ReviewStatus.Open;
    public string Badge => Item.Status switch
    {
        ReviewStatus.Resolved => "Resolved",
        ReviewStatus.Dismissed => "Dismissed",
        _ => "To review",
    };
    public StatusTone Tone => IsOpen ? StatusTone.Warning : StatusTone.Neutral;
}

// The dry run for a set of picked workbooks: what would change, what was
// found, and whether it can be imported.
public sealed class ImportPreviewViewModel(ImportPlan plan, IReadOnlyList<string> paths)
{
    public ImportPlan Plan { get; } = plan;
    public IReadOnlyList<string> Paths { get; } = paths;

    public IReadOnlyList<SourceFileRow> Files { get; } =
        [.. plan.Batch.Files.Select(f => new SourceFileRow(f.Name, f.Shape, $"SHA-256 {f.Sha256[..16]}…"))];

    public IReadOnlyList<ChangeSummaryRow> Changes { get; } =
    [
        .. plan.Changes.GroupBy(c => (c.Kind, c.Entity))
            .OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.Entity, StringComparer.Ordinal)
            .Select(g => new ChangeSummaryRow($"{Verb(g.Key.Kind)} · {Noun(g.Key.Entity, g.Count())}", g.Count().ToString(CultureInfo.CurrentCulture))),
    ];

    public IReadOnlyList<FindingRow> Findings { get; } =
    [
        .. plan.Findings.OrderByDescending(f => f.Severity).Select(f => new FindingRow(
            f.Title, f.Detail, string.Join("  ·  ", f.Sources.Where(s => s.Location.Length > 0).Select(s => s.ToString())),
            f.Severity switch { FindingSeverity.Blocking => "Blocks import", FindingSeverity.NeedsReview => "For review", _ => "Note" },
            f.Severity switch { FindingSeverity.Blocking => StatusTone.Critical, FindingSeverity.NeedsReview => StatusTone.Warning, _ => StatusTone.Info })),
    ];

    public bool CanCommit => Plan.CanCommit;

    public string Verdict => Plan.CanCommit
        ? $"Ready to import. {Plan.Findings.Count(f => f.Severity == FindingSeverity.NeedsReview)} item(s) will wait for your review; nothing is saved until you import."
        : "Something here blocks the import. Fix the workbook or leave the file out, then preview again.";

    private static string Verb(ChangeKind kind) => kind switch
    {
        ChangeKind.Add => "New",
        ChangeKind.Update => "Updated",
        ChangeKind.Unchanged => "Unchanged",
        ChangeKind.KeepEdited => "Kept your edits",
        ChangeKind.NotInSource => "No longer in the source",
        _ => "Removed at the source",
    };

    private static string Noun(string entity, int count) => (entity, count) switch
    {
        ("Income", 1) => "paycheck",
        ("Income", _) => "paychecks",
        ("Settings", _) => "savings and cushion",
        (_, 1) => entity.ToLowerInvariant(),
        _ => entity.ToLowerInvariant() + "s",
    };
}

public sealed partial class ImportsPageViewModel(AppServices services)
    : PageViewModel(Destination.Imports, "Imports", "Preview and import Excel workbooks and files from your bank. Originals are only read, never changed.")
{
    // The bank file mapping to remember once its import is committed.
    private (string Fingerprint, SavedCsvMapping Mapping)? _pendingMapping;

    public ObservableCollection<ReviewItemRow> Review { get; } = [];

    public ObservableCollection<HistoryRow> History { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview), nameof(IsChoosing), nameof(IsMatching))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand), nameof(SaveReportCommand))]
    private ImportPreviewViewModel? _preview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosing), nameof(IsMatching))]
    private BankFileViewModel? _bankFile;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private StatusTone _statusTone = StatusTone.Neutral;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseWorkbooksCommand), nameof(ChooseBankFileCommand), nameof(PreviewBankFileCommand), nameof(ImportCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _showClosed;

    [ObservableProperty]
    private string _reviewSummary = "";

    public bool HasPreview => Preview is not null;

    public bool IsChoosing => Preview is null && BankFile is null;

    public bool IsMatching => Preview is null && BankFile is not null;

    public bool HasReview => Review.Count > 0;

    public bool HasHistory => History.Count > 0;

    public override void OnNavigatedTo() => Refresh();

    partial void OnShowClosedChanged(bool value) => Refresh();

    partial void OnIsBusyChanged(bool value) => services.Context.ActiveWork += value ? 1 : -1;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ChooseWorkbooks()
    {
        var paths = await services.Dialogs.PickWorkbooksAsync();
        if (paths.Count == 0)
        {
            return;
        }

        IsBusy = true;
        Status = null;
        try
        {
            var session = services.Context.Current;
            var plan = await Task.Run(() => services.Imports.Preview(session, [.. paths.Select(SourceFiles.Read)]));
            Preview = new ImportPreviewViewModel(plan, paths);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Show($"Couldn't read the workbooks: {e.Message}", StatusTone.Critical);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ChooseBankFile()
    {
        var path = await services.Dialogs.PickBankFileAsync();
        if (path is null)
        {
            return;
        }

        IsBusy = true;
        Status = null;
        try
        {
            var session = services.Context.Current;
            BankFile = await Task.Run(() =>
            {
                var file = SourceFiles.Read(path);
                var table = CsvReader.Read(file.Content);
                var saved = session.Imports.FileMapping(CsvMappings.Fingerprint(table)) is { } json ? SavedCsvMapping.FromJson(json) : null;
                return new BankFileViewModel(file, path, table, CsvMappings.Guess(table), saved, session.Data.Accounts(), session.Data.Settings().Currency);
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CsvFormatException)
        {
            Show($"Couldn't read that file: {e.Message}", StatusTone.Critical);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task PreviewBankFile()
    {
        if (BankFile is not { } file || file.Targets(() => Guid.CreateVersion7(services.Clock.GetUtcNow())) is not { } targets)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var session = services.Context.Current;
            var mapping = file.Mapping();
            var today = DateOnly.FromDateTime(services.Clock.GetLocalNow().DateTime);
            var plan = await Task.Run(() => services.Imports.Plan(session,
                CsvImport.Map(file.File, CsvReader.Read(file.File.Content), mapping, targets, today, session.Data.Settings().Currency)));
            _pendingMapping = (file.Fingerprint, BankFileViewModel.Remember(mapping, targets));
            Preview = new ImportPreviewViewModel(plan, [file.Path]);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelBankFile() => BankFile = null;

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task Import()
    {
        var preview = Preview!;
        IsBusy = true;
        try
        {
            var session = services.Context.Current;
            var remember = _pendingMapping;
            await Task.Run(() =>
            {
                ImportService.Commit(session, preview.Plan);
                if (remember is { } r)
                {
                    session.Imports.SaveFileMapping(r.Fingerprint, r.Mapping.ToJson());
                }
            });
            var untouched = preview.Paths.All(path =>
                preview.Plan.Batch.Files.FirstOrDefault(f => f.Name == Path.GetFileName(path)) is not { } file || SafeUnchanged(path, file.Sha256));
            var queued = preview.Plan.Commit.ReviewItems.Count;
            Show($"Imported. {queued} item(s) added to the review queue. " +
                (untouched ? "The original files are unchanged (SHA-256 checked after the import)." : "A file changed on disk during the import; preview it again to be sure."),
                untouched ? StatusTone.Healthy : StatusTone.Warning);
            Preview = null;
            BankFile = null;
            _pendingMapping = null;
            Refresh();
        }
        catch (StaleImportPlanException e)
        {
            Show(e.Message, StatusTone.Warning);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            Show($"Nothing was imported: {e.Message}", StatusTone.Critical);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // A bank file goes back to its columns, so a wrong match can be fixed.
    [RelayCommand]
    private void CancelPreview()
    {
        Preview = null;
        _pendingMapping = null;
    }

    [RelayCommand(CanExecute = nameof(HasPreview))]
    private async Task SaveReport()
    {
        if (Preview is { } preview)
        {
            await services.Dialogs.SaveTextAsync($"Mossmint import {services.Clock.GetLocalNow():yyyy-MM-dd}.md", preview.Plan.Report(), "Save the dry-run report");
        }
    }

    [RelayCommand]
    private void Resolve(ReviewItemRow row) => Close(row, ReviewStatus.Resolved);

    [RelayCommand]
    private void Dismiss(ReviewItemRow row) => Close(row, ReviewStatus.Dismissed);

    [RelayCommand]
    private void Reopen(ReviewItemRow row)
    {
        services.Context.Current.Imports.ReopenReviewItem(row.Item.Id);
        Refresh();
    }

    private void Close(ReviewItemRow row, ReviewStatus status)
    {
        services.Context.Current.Imports.CloseReviewItem(row.Item.Id, status, null);
        Refresh();
    }

    private void Refresh()
    {
        if (!services.Context.IsUnlocked)
        {
            return;
        }

        var imports = services.Context.Current.Imports;
        var items = imports.ReviewItems();
        var open = items.Count(i => i.Status == ReviewStatus.Open);
        ReviewSummary = items.Count == 0
            ? "Nothing to review."
            : $"{open} open, {items.Count - open} settled. Mismatches are never corrected for you: confirm the figure, fix it where it's wrong, or dismiss the item.";
        Review.Clear();
        foreach (var item in items.Where(i => ShowClosed || i.Status == ReviewStatus.Open).OrderBy(i => i.Status).ThenBy(i => i.Title, StringComparer.CurrentCulture))
        {
            Review.Add(new ReviewItemRow(item));
        }

        History.Clear();
        foreach (var run in imports.Runs().Reverse())
        {
            History.Add(new HistoryRow(run.At.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture),
                $"{string.Join(", ", run.Files.Select(f => f.Name))} ({run.Mapper})"));
        }

        OnPropertyChanged(nameof(HasReview));
        OnPropertyChanged(nameof(HasHistory));
    }

    private void Show(string message, StatusTone tone) => (Status, StatusTone) = (message, tone);

    private bool IsIdle() => !IsBusy;

    private bool CanImport() => !IsBusy && Preview is { CanCommit: true };

    private static bool SafeUnchanged(string path, string sha)
    {
        try
        {
            return SourceFiles.IsUnchanged(path, sha);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
