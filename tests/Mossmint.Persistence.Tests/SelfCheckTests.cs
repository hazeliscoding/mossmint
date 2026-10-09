using Mossmint.Persistence.Diagnostics;

namespace Mossmint.Persistence.Tests;

public class SelfCheckTests
{
    [Fact]
    public void The_encryption_self_check_passes_in_the_test_build()
    {
        var report = EncryptionSelfCheck.Run();

        report.Checks.Should().OnlyContain(c => c.Passed, because: string.Join("; ", report.Checks.Select(c => $"{c.Check}: {c.Detail}")));
        report.Passed.Should().BeTrue();
        report.CipherVersion.Should().StartWith("SQLite3 Multiple Ciphers");
    }

    [Fact]
    public void A_second_sqlite_binary_fails_the_self_check()
    {
        // Positive control: a stray e_sqlite3.dll beside the app must be reported.
        var appDirectory = Path.Combine(Path.GetTempPath(), "mossmint-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDirectory);
        File.WriteAllText(Path.Combine(appDirectory, "sqlite3mc.dll"), "");
        File.WriteAllText(Path.Combine(appDirectory, "e_sqlite3.dll"), "");
        try
        {
            var report = EncryptionSelfCheck.Run(appDirectory);

            report.Passed.Should().BeFalse();
            report.Checks.Single(c => c.Check == "one SQLite binary").Detail.Should().Contain("e_sqlite3.dll");
        }
        finally
        {
            Directory.Delete(appDirectory, recursive: true);
        }
    }
}
