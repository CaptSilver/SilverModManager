using NexusMods.Paths;

namespace SilverModManager.Games.Larian.Engine;

/// <summary>Where a Larian game keeps the folder that holds <c>modsettings.lsx</c>, relative to its Larian folder.</summary>
public abstract record LarianProfileLayout
{
    /// <summary>One folder the game always uses (Baldur's Gate 3: <c>PlayerProfiles/Public</c>).</summary>
    public sealed record Fixed(RelativePath ProfileFolder) : LarianProfileLayout;

    /// <summary>One folder per player profile inside <paramref name="ProfilesFolder"/>, chosen at sync time.</summary>
    public sealed record PerProfileFolder(RelativePath ProfilesFolder) : LarianProfileLayout;
}
