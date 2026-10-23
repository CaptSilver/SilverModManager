using NexusMods.Games.FromSoft.Build;

namespace NexusMods.Games.FromSoft.Tests.Build;

/// <summary>
/// A complete <see cref="BuildIdentity"/> for tests that need an identity to hand around. Only the fields a test cares
/// about are spelled; the rest are an ordinary install of the build the regulation fixtures are stamped with.
/// </summary>
public static class BuildIds
{
    public const string RegulationVersion = "11701000";

    public static BuildIdentity Make(
        string exe = "2.7.0.0",
        string steamBuildId = "23850278",
        string? regulationVersion = RegulationVersion,
        string? regulationHash = null)
        => new(exe, steamBuildId, regulationVersion, regulationHash ?? new string('a', 64));
}
