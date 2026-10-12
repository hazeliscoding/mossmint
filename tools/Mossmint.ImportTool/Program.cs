using System.Globalization;
using System.Security.Cryptography;
using Mossmint.Application.Import;
using Mossmint.Import.Excel;
using Mossmint.Persistence.Backups;
using Mossmint.Persistence.Profiles;
using Mossmint.Samples.Workbooks;

// Imports workbooks into a brand-new encrypted profile from the command line,
// then writes a verified backup of it: a profile prepared on one PC for
// someone to restore on another.
//
// The console shows file names, shapes, hashes and counts, never amounts. The
// passphrase, recovery key and dry-run report go only to the paths given,
// which should be somewhere private.
//
//   dotnet run --project tools/Mossmint.ImportTool -- --data-root <folder> --profile <name>
//       --secrets <file> --report <file> [--backup <file>] [--dry-run] <workbook.xlsx>...

// `--write-samples <folder>` writes the synthetic workbooks instead, as files to import:
// the release workflow's upgrade test imports them with the previous release's tool.
if (args is ["--write-samples", var samples])
{
    Directory.CreateDirectory(samples);
    foreach (var workbook in SyntheticWorkbooks.All())
    {
        File.WriteAllBytes(Path.Combine(samples, workbook.FileName), workbook.Content);
    }

    return 0;
}

var options = Options.Parse(args);
if (options is null)
{
    Console.Error.WriteLine("Usage: --data-root <folder> --profile <name> --secrets <file> --report <file> [--backup <file>] [--dry-run] <workbook.xlsx>...");
    return 64;
}

foreach (var existing in new[] { options.Secrets, options.Report, options.Backup }.OfType<string>().Where(File.Exists))
{
    Console.Error.WriteLine($"Refusing to overwrite {existing}.");
    return 73;
}

var defaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mossmint");
if (Path.GetFullPath(options.DataRoot).TrimEnd('\\', '/').Equals(defaultRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Use a separate data root; this tool never writes to the app's own profiles folder.");
    return 64;
}

var clock = TimeProvider.System;
var files = options.Workbooks.Select(SourceFiles.Read).ToList();
var locations = new ProfileLocations(Path.GetFullPath(options.DataRoot));
var store = new ProfileStore(locations, clock);
var passphrase = NewPassphrase();
var created = store.Create(options.Profile, passphrase, unlockOnThisDevice: false);
using var session = created.Session;

var service = new ImportService(new WorkbookImporter(), clock);
var plan = service.Preview(session, files);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Report))!);
File.WriteAllText(options.Report, plan.Report());

foreach (var file in plan.Batch.Files)
{
    Console.WriteLine($"{file.Name}: {file.Shape}, SHA-256 {file.Sha256}");
}

Console.WriteLine($"Changes: {string.Join(", ", Enum.GetValues<ChangeKind>().Where(k => plan.Count(k) > 0).Select(k => $"{plan.Count(k)} {k}"))}");
Console.WriteLine($"Findings: {plan.Findings.Count(f => f.Severity == FindingSeverity.Blocking)} blocking, " +
    $"{plan.Findings.Count(f => f.Severity == FindingSeverity.NeedsReview)} for review, {plan.Findings.Count(f => f.Severity == FindingSeverity.Info)} notes");
Console.WriteLine($"Dry-run report: {options.Report}");

if (!plan.CanCommit || options.DryRun)
{
    foreach (var blocking in plan.Findings.Where(f => f.Severity == FindingSeverity.Blocking))
    {
        Console.Error.WriteLine($"Blocking: {blocking.Title}");
    }

    session.Dispose();
    store.Delete(session.Profile.Id);
    Console.WriteLine(options.DryRun ? "Dry run only: nothing was kept." : "Nothing was imported.");
    return plan.CanCommit ? 0 : 2;
}

ImportService.Commit(session, plan);
var untouched = options.Workbooks.All(path =>
    plan.Batch.Files.FirstOrDefault(f => f.Name == Path.GetFileName(path)) is not { } file || SourceFiles.IsUnchanged(path, file.Sha256));
Console.WriteLine(untouched ? "Originals unchanged: every SHA-256 matches after the import." : "WARNING: a workbook changed on disk during the import.");

var counts = session.Data.Counts();
Console.WriteLine($"Profile '{session.Profile.Name}': {counts.Accounts} accounts, {counts.Debts} debts, {counts.Income} paychecks, {counts.Bills} bills, " +
    $"{counts.BalanceSnapshots} balances, {counts.ReviewItems} review items.");

string? backupLine = null;
if (options.Backup is { } backupPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(backupPath))!);
    var report = new BackupService(locations, clock).Create(session, backupPath);
    backupLine = $"Backup: {Path.GetFileName(report.Path)} ({report.SizeBytes:N0} bytes), test-restored and verified.";
    Console.WriteLine(backupLine);
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Secrets))!);
File.WriteAllText(options.Secrets, string.Join(Environment.NewLine,
    $"Mossmint profile: {session.Profile.Name}",
    $"Created: {clock.GetLocalNow().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}",
    "",
    $"Passphrase: {passphrase}",
    $"Recovery key: {created.RecoveryKey}",
    "",
    "Send the backup file and these two secrets separately. After restoring, choose a new passphrase",
    "and make a new recovery key in Settings, so these stop working.",
    backupLine ?? "") + Environment.NewLine);
Console.WriteLine($"Secrets written to {options.Secrets}.");
return untouched ? 0 : 1;

// 24 characters from an alphabet without look-alikes: 120 random bits, typed once.
static string NewPassphrase()
{
    const string alphabet = "23456789ABCDEFGHJKMNPQRSTVWXYZ";
    var chars = RandomNumberGenerator.GetItems<char>(alphabet, 24);
    return string.Join('-', chars.Chunk(4).Select(c => new string(c)));
}

internal sealed record Options(string DataRoot, string Profile, string Secrets, string Report, string? Backup, bool DryRun, IReadOnlyList<string> Workbooks)
{
    public static Options? Parse(string[] args)
    {
        var values = new Dictionary<string, string>();
        var workbooks = new List<string>();
        var dryRun = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dry-run":
                    dryRun = true;
                    break;
                case var flag when flag.StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length:
                    values[flag[2..]] = args[++i];
                    break;
                default:
                    workbooks.Add(args[i]);
                    break;
            }
        }

        return values.TryGetValue("data-root", out var root) && values.TryGetValue("profile", out var profile)
            && values.TryGetValue("secrets", out var secrets) && values.TryGetValue("report", out var report) && workbooks.Count > 0
                ? new Options(root, profile, secrets, report, values.GetValueOrDefault("backup"), dryRun, workbooks)
                : null;
    }
}
