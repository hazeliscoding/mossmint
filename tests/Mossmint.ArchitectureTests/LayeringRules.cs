using System.Reflection;
using System.Xml.Linq;

namespace Mossmint.ArchitectureTests;

// The layering rule from ROADMAP.md: Desktop -> Application -> Domain + Engine.
// Every src project must be listed here, so a new project can't skip the rules.
public static class LayeringRules
{
    public static readonly IReadOnlyDictionary<string, string[]> AllowedProjectReferences =
        new Dictionary<string, string[]>
        {
            ["Mossmint.Domain"] = [],
            ["Mossmint.Engine"] = ["Mossmint.Domain"],
            ["Mossmint.Connectors.Abstractions"] = ["Mossmint.Domain"],
            ["Mossmint.Application"] = ["Mossmint.Domain", "Mossmint.Engine", "Mossmint.Connectors.Abstractions"],
            ["Mossmint.Persistence"] = ["Mossmint.Application", "Mossmint.Domain"],
            ["Mossmint.Import.Excel"] = ["Mossmint.Application", "Mossmint.Domain"],
            ["Mossmint.Import.Transactions"] = ["Mossmint.Application", "Mossmint.Domain"],
            // Desktop is the composition root, so it may wire infrastructure in.
            ["Mossmint.Desktop"] =
            [
                "Mossmint.Application", "Mossmint.Persistence", "Mossmint.Import.Excel", "Mossmint.Import.Transactions",
            ],
        };

    // Pure layers: no package dependencies at all.
    public static readonly string[] PackageFreeProjects =
        ["Mossmint.Domain", "Mossmint.Engine", "Mossmint.Connectors.Abstractions", "Mossmint.Application"];

    // Assemblies a pure layer must never touch: UI, storage, spreadsheets, updater.
    public static readonly string[] ForbiddenInPureLayers =
    [
        "Avalonia", "Microsoft.EntityFrameworkCore", "Microsoft.Data.Sqlite", "SQLitePCL", "ClosedXML",
        "DocumentFormat.OpenXml", "Velopack", "CommunityToolkit.Mvvm",
    ];

    // Normal use makes no network calls. Only a future connector or update
    // project may be granted an exception, with its own ADR.
    public static readonly string[] NetworkAssemblies =
    [
        "System.Net.Http", "System.Net.Sockets", "System.Net.WebSockets", "System.Net.Requests",
        "System.Net.WebClient", "System.Net.Mail", "System.Net.Quic",
    ];

    public static IEnumerable<string> ProjectReferenceViolations(string project, IEnumerable<string> references)
    {
        if (!AllowedProjectReferences.TryGetValue(project, out var allowed))
        {
            yield return $"{project} is not classified in LayeringRules";
            yield break;
        }

        foreach (var reference in references.Where(r => !allowed.Contains(r)))
        {
            yield return $"{project} must not reference {reference}";
        }
    }

    public static IEnumerable<string> AssemblyViolations(Assembly assembly, IEnumerable<string> forbiddenPrefixes) =>
        from reference in assembly.GetReferencedAssemblies()
        from prefix in forbiddenPrefixes
        where reference.Name is not null
              && (reference.Name == prefix || reference.Name.StartsWith(prefix + ".", StringComparison.Ordinal))
        select $"{assembly.GetName().Name} references {reference.Name}";

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mossmint.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Mossmint.slnx not found above the test output.");
    }

    public static IEnumerable<(string Name, XDocument Project)> SourceProjects() =>
        Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(path => (Path.GetFileNameWithoutExtension(path), XDocument.Load(path)));

    public static IEnumerable<string> ProjectReferences(XDocument project) =>
        project.Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')));

    public static IEnumerable<string> PackageReferences(XDocument project) =>
        project.Descendants("PackageReference").Select(e => e.Attribute("Include")!.Value);
}
