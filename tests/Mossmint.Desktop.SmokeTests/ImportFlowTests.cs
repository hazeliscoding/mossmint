using Avalonia.Headless.XUnit;
using Mossmint.Desktop.Controls;
using Mossmint.Desktop.ViewModels;
using Mossmint.Samples.Workbooks;

namespace Mossmint.Desktop.SmokeTests;

public class ImportFlowTests
{
    [AvaloniaFact]
    public async Task A_new_user_imports_workbooks_and_works_through_the_review_queue()
    {
        using var app = new Harness();
        app.Open();
        app.ViewModel.Welcome.ImportMyFinancesCommand.Execute(null);
        var create = app.Screen<CreateProfileViewModel>();
        (create.Name, create.Passphrase, create.Confirm) = ("Juniper", Harness.Passphrase, Harness.Passphrase);
        await create.CreateCommand.ExecuteAsync(null);
        var recovery = app.Screen<RecoveryKeyViewModel>();
        recovery.Stored = true;
        recovery.ContinueCommand.Execute(null);
        Harness.Pump();

        var imports = app.Screen<ShellViewModel>().CurrentPage.Should().BeOfType<ImportsPageViewModel>().Subject;
        app.Dialogs.NextWorkbooks.AddRange(WriteWorkbooks(app.Root));
        await imports.ChooseWorkbooksCommand.ExecuteAsync(null);
        Harness.Pump();

        imports.Preview.Should().NotBeNull();
        imports.Preview!.Files.Should().HaveCount(5);
        imports.Preview.CanCommit.Should().BeTrue();
        app.Services.Context.Current.Data.Accounts().Should().BeEmpty("a preview saves nothing");

        await imports.ImportCommand.ExecuteAsync(null);
        Harness.Pump();

        imports.StatusTone.Should().Be(StatusTone.Healthy);
        imports.Status.Should().Contain("unchanged");
        imports.Preview.Should().BeNull();
        var open = imports.Review.Count;
        open.Should().BeGreaterThan(0);
        app.Services.Context.Current.Data.Accounts().Should().HaveCount(5);

        imports.ResolveCommand.Execute(imports.Review[0]);
        imports.Review.Should().HaveCount(open - 1);
        imports.ShowClosed = true;
        imports.Review.Should().HaveCount(open);
    }

    [AvaloniaFact]
    public async Task A_file_that_isnt_a_workbook_blocks_the_import()
    {
        using var app = new Harness();
        app.CreateProfile();
        app.Open();
        var unlock = app.Screen<UnlockViewModel>();
        unlock.Passphrase = Harness.Passphrase;
        await unlock.UnlockCommand.ExecuteAsync(null);
        Harness.Pump();
        var shell = app.Screen<ShellViewModel>();
        shell.Navigate(Destination.Imports);
        var imports = (ImportsPageViewModel)shell.CurrentPage!;
        var path = Path.Combine(app.Root, "notes.xlsx");
        await File.WriteAllTextAsync(path, "not a workbook");
        app.Dialogs.NextWorkbooks.Add(path);

        await imports.ChooseWorkbooksCommand.ExecuteAsync(null);

        imports.Preview!.CanCommit.Should().BeFalse();
        imports.ImportCommand.CanExecute(null).Should().BeFalse();
        imports.Preview.Findings.Should().Contain(f => f.Badge == "Blocks import");
    }

    private static IEnumerable<string> WriteWorkbooks(string root)
    {
        var folder = Path.Combine(root, "workbooks");
        Directory.CreateDirectory(folder);
        foreach (var workbook in SyntheticWorkbooks.All())
        {
            var path = Path.Combine(folder, workbook.FileName);
            File.WriteAllBytes(path, workbook.Content);
            yield return path;
        }
    }
}
