using System.Reflection;

namespace Mossmint.ArchitectureTests;

public class LayeringTests
{
    private static readonly string[] AllMossmintAssemblies =
    [
        "Mossmint.Domain", "Mossmint.Engine", "Mossmint.Connectors.Abstractions", "Mossmint.Application",
        "Mossmint.Persistence", "Mossmint.Import.Excel", "Mossmint.Import.Transactions", "Mossmint.Desktop",
    ];

    [Fact]
    public void Every_source_project_is_classified_and_references_only_allowed_projects()
    {
        var projects = LayeringRules.SourceProjects().ToList();
        projects.Should().NotBeEmpty();

        var violations = projects
            .SelectMany(p => LayeringRules.ProjectReferenceViolations(p.Name, LayeringRules.ProjectReferences(p.Project)));

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Pure_layers_have_no_package_dependencies()
    {
        var violations =
            from p in LayeringRules.SourceProjects()
            where LayeringRules.PackageFreeProjects.Contains(p.Name)
            from package in LayeringRules.PackageReferences(p.Project)
            select $"{p.Name} references package {package}";

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Pure_layer_assemblies_reference_no_ui_storage_or_updater_code()
    {
        var violations = LayeringRules.PackageFreeProjects
            .SelectMany(name => LayeringRules.AssemblyViolations(Assembly.Load(name), LayeringRules.ForbiddenInPureLayers));

        violations.Should().BeEmpty();
    }

    [Fact]
    public void No_assembly_references_networking_code()
    {
        var violations = AllMossmintAssemblies
            .SelectMany(name => LayeringRules.AssemblyViolations(Assembly.Load(name), LayeringRules.NetworkAssemblies));

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Desktop_reaches_the_engine_only_through_application()
    {
        LayeringRules.AssemblyViolations(Assembly.Load("Mossmint.Desktop"), ["Mossmint.Engine"])
            .Should().BeEmpty();
    }

    // Positive controls: each checker must actually catch what it is looking for.

    [Fact]
    public void Project_reference_check_catches_an_engine_that_references_desktop()
    {
        LayeringRules.ProjectReferenceViolations("Mossmint.Engine", ["Mossmint.Domain", "Mossmint.Desktop"])
            .Should().ContainSingle().Which.Should().Contain("Mossmint.Desktop");
    }

    [Fact]
    public void Project_reference_check_catches_an_unclassified_project()
    {
        LayeringRules.ProjectReferenceViolations("Mossmint.Surprise", [])
            .Should().ContainSingle().Which.Should().Contain("not classified");
    }

    [Fact]
    public void Assembly_check_catches_ui_code_where_it_is_forbidden()
    {
        LayeringRules.AssemblyViolations(Assembly.Load("Mossmint.Desktop"), LayeringRules.ForbiddenInPureLayers)
            .Should().Contain(v => v.Contains("Avalonia"));
    }

    [Fact]
    public void Assembly_check_catches_networking_code()
    {
        // Positive control: the BCL's own HTTP assembly references System.Net.Sockets.
        LayeringRules.AssemblyViolations(typeof(HttpClient).Assembly, LayeringRules.NetworkAssemblies)
            .Should().NotBeEmpty();
    }
}
