using NexusMods.Paths;

namespace SilverModManager.Games.FromSoft.Heal;

/// <summary>What <see cref="RegulationBaselines.Import"/> did with a file.</summary>
public enum ImportOutcome
{
    /// <summary>The file is now the baseline of its version.</summary>
    Kept,

    /// <summary>A baseline with exactly these bytes was already there; nothing changed.</summary>
    AlreadyKept,

    /// <summary>The version had a validated baseline, which the vouched file now takes the place of.</summary>
    ReplacedValidated,

    /// <summary>The version already had a different vouched baseline. Both are the game's own, so this one is kept beside it.</summary>
    KeptAlongside,
}

/// <summary>A file kept by <see cref="RegulationBaselines.Import"/>.</summary>
/// <param name="File">Where the baseline lives under the data root.</param>
/// <param name="Version">The build stamp inside the regulation.</param>
/// <param name="Outcome">What was done with it.</param>
/// <param name="Provenance">What is recorded about the kept file.</param>
public sealed record RegulationImport(AbsolutePath File, string Version, ImportOutcome Outcome, RegulationProvenance Provenance);
