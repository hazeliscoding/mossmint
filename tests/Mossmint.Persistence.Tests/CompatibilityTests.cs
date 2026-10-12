using System.Security.Cryptography;
using System.Text.Json;
using Mossmint.Persistence.Diagnostics;

namespace Mossmint.Persistence.Tests;

// The compatibility promise: today's code opens every release's profile and restores its
// backup with every record. Each fixture in tests/Compatibility was made by that release's
// own import tool (build/capture-fixture.ps1) and is pinned by its SHA256SUMS, so a failure
// here is fixed in the code, never by capturing the fixture again.
public sealed class CompatibilityTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Compatibility");
    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 10, 12, 14, 0, 0, TimeSpan.Zero));

    public static TheoryData<string> Releases => [.. Directory.GetDirectories(Fixtures).Select(d => Path.GetFileName(d))];

    [Theory]
    [MemberData(nameof(Releases))]
    public void A_fixture_is_exactly_as_its_release_made_it(string version) =>
        Changed(Path.Combine(Fixtures, version)).Should().BeEmpty();

    [Fact]
    public void A_changed_fixture_file_is_caught()
    {
        using var root = new TempRoot();
        var copy = Copy(Path.Combine(Fixtures, "0.3.0"), root.Path);
        File.AppendAllText(Path.Combine(copy, "secrets.txt"), " ");
        File.Delete(Path.Combine(copy, "backup.mmbak"));

        Changed(copy).Should().Equal("backup.mmbak", "secrets.txt");
    }

    [Theory]
    [MemberData(nameof(Releases))]
    public void Todays_code_opens_the_profile_and_restores_the_backup_with_every_record(string version)
    {
        // Opening a profile upgrades it, so only a copy is opened.
        using var root = new TempRoot();
        var fixture = Copy(Path.Combine(Fixtures, version), root.Path);
        var captured = JsonSerializer.Deserialize<UpgradeReport>(File.ReadAllText(Path.Combine(fixture, "upgrade-check.json")))!;
        var passphrase = File.ReadLines(Path.Combine(fixture, "secrets.txt")).First(l => l.StartsWith("Passphrase: ", StringComparison.Ordinal))["Passphrase: ".Length..];

        var report = UpgradeCheck.Run(Path.Combine(fixture, "data"), passphrase, Path.Combine(fixture, "backup.mmbak"), Clock);

        report.Failure.Should().BeNull();
        report.Upgraded.Should().Be(captured.Upgraded);
        report.Restored.Should().Be(captured.Restored);
        report.Upgraded!.Accounts.Should().BePositive("a fixture that holds nothing would prove nothing");
    }

    // Files added, missing or different since SHA256SUMS was written.
    private static List<string> Changed(string fixture)
    {
        var pinned = File.ReadLines(Path.Combine(fixture, "SHA256SUMS")).Select(l => l.Split("  ", 2)).ToDictionary(p => p[1], p => p[0]);
        var actual = Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(fixture, f).Replace('\\', '/'))
            .Where(f => f != "SHA256SUMS")
            .ToDictionary(f => f, f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture, f)))));
        return [.. pinned.Keys.Union(actual.Keys).Where(f => pinned.GetValueOrDefault(f) != actual.GetValueOrDefault(f)).Order(StringComparer.Ordinal)];
    }

    private static string Copy(string source, string root)
    {
        var target = Path.Combine(root, Path.GetFileName(source));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }

        return target;
    }
}
