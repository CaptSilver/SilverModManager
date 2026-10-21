using NexusMods.Paths;

namespace NexusMods.Games.CreationEngine.Morrowind.OpenMw;

/// <summary>
/// The folders an openmw.cfg can live in. Kept behind a service so that the one place that knows the real
/// locations can be swapped for temp folders in tests: the test host's file system is the real one, and looking
/// these up directly would find and rewrite the developer's own config.
/// </summary>
public interface IOpenMwConfigRoots
{
    IReadOnlyList<AbsolutePath> Candidates { get; }
}
