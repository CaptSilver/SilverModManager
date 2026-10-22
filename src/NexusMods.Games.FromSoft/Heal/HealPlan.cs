using NexusMods.Games.FromSoft.Build;
using NexusMods.Games.FromSoft.Formats;

namespace NexusMods.Games.FromSoft.Heal;

/// <summary>What one step of a heal does.</summary>
public enum HealStepKind
{
    /// <summary>The install cannot be healed; nothing else is planned.</summary>
    Refuse,

    /// <summary>Keep the installed game's regulation as the baseline of its build.</summary>
    AdoptBaseline,

    /// <summary>Look for newer versions of the mods. Pins live in the updater, so this is a prompt, not an action the app takes here.</summary>
    CheckForModUpdates,

    /// <summary>Check the mods' param row layouts against the new baseline.</summary>
    Gate,

    /// <summary>Merge again against the new baseline; the next apply does it.</summary>
    Rebuild,

    /// <summary>Check the rebase kept every row a mod authored.</summary>
    Verify,

    /// <summary>Take the installed build as the new reference.</summary>
    Stamp,
}

/// <summary>One step of a heal and the sentence that says what it does.</summary>
public sealed record HealStep(HealStepKind Kind, string Detail);

/// <summary>
/// The ordered steps that bring a loadout's merges back onto the installed build. Pure: it looks at the stamped and the
/// live build and nothing else, so a dry run is this function and the tests of its order are the tests of the heal.
/// </summary>
public static class HealPlan
{
    private const string NoRegulation = "the regulation";

    /// <summary>
    /// The steps for the move from <paramref name="stamped"/> to <paramref name="live"/>, classified by
    /// <see cref="BuildIdentity.Classify(BuildIdentity?, BuildIdentity)"/>. An unstamped install has no build to heal
    /// from, so it plans nothing.
    /// </summary>
    public static IReadOnlyList<HealStep> Plan(BuildIdentity? stamped, BuildIdentity live, FromSoftFormatTraits traits)
    {
        var kind = BuildIdentity.Classify(stamped, live);
        if (kind == BuildChangeKind.Unchanged) return [];
        if (kind == BuildChangeKind.Tampered) return [new HealStep(HealStepKind.Refuse, TamperedRefusal(live, traits))];

        var steps = new List<HealStep>();
        var patched = kind == BuildChangeKind.Patched;
        if (patched)
            steps.Add(new HealStep(HealStepKind.AdoptBaseline, $"keep the {live.RegulationVersion ?? "installed"} regulation as the merge baseline"));

        // Before the gate: an updated mod may be exactly what makes its rows fit the new baseline.
        steps.Add(new HealStep(HealStepKind.CheckForModUpdates, "check for mod updates"));
        if (patched)
        {
            steps.Add(new HealStep(HealStepKind.Gate, "check param row layouts against the baseline"));
            steps.Add(new HealStep(HealStepKind.Rebuild, "rebuild every merge against the new baseline"));
            steps.Add(new HealStep(HealStepKind.Verify, "check the rebase kept every authored row"));
        }
        steps.Add(new HealStep(HealStepKind.Stamp, $"record build {live.Exe} (Steam build {live.SteamBuildId})"));
        return steps;
    }

    /// <summary>
    /// Why a regulation that changed without a patch is not taken as the vanilla one. Everything that refuses on it says
    /// this sentence, so Health Check, the heal command and the apply cannot word it differently.
    /// </summary>
    public static string TamperedRefusal(BuildIdentity live, FromSoftFormatTraits traits)
        => $"{traits.RegulationFileName ?? NoRegulation} changed but the game was not patched " +
           $"(exe {live.Exe}, Steam build {live.SteamBuildId} unchanged) — something overwrote the install's regulation. " +
           "Refusing to adopt it as vanilla. Verify the game's files in its launcher, or heal against the last known-good baseline.";
}
