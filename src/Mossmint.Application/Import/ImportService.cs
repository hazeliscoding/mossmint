using Mossmint.Application.Profiles;

namespace Mossmint.Application.Import;

// Preview, then commit. Preview reads the files and the profile and writes
// nothing; commit writes exactly what the preview showed, or nothing.
public sealed class ImportService(ISourceMapper mapper, TimeProvider clock)
{
    public ImportPlan Preview(IProfileSession session, IReadOnlyList<SourceFile> files)
    {
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var batch = mapper.Map(files, today, session.Data.Settings().Currency);
        return Plan(session, batch);
    }

    // Connectors and other sources hand over a batch directly.
    public ImportPlan Plan(IProfileSession session, ImportBatch batch) =>
        new ImportPlanner(session.Data, session.Imports, clock).Plan(batch);

    public static void Commit(IProfileSession session, ImportPlan plan)
    {
        if (!plan.CanCommit)
        {
            throw new InvalidOperationException("This import has blocking problems; fix the source and preview it again.");
        }

        session.Imports.Commit(plan.Commit);
    }
}
