namespace SilverModManager.Games.Larian.Engine;

/// <summary>Which Larian game each LSPK package version belongs to, to say whose pak a mod ships when it is not the game's.</summary>
public static class LarianPakVersions
{
    private static readonly (int Version, string Game)[] Games =
    [
        (7, "Divinity: Original Sin Enhanced Edition"), (9, "Divinity: Original Sin Enhanced Edition"),
        (10, "Divinity: Original Sin 2"), (13, "Divinity: Original Sin 2"),
        (15, "Baldur's Gate 3"), (16, "Baldur's Gate 3"), (18, "Baldur's Gate 3"),
    ];

    /// <summary>
    /// A sentence saying why a pak of <paramref name="version"/> is no use to <paramref name="profile"/>'s game, or
    /// null when the game loads that version.
    /// </summary>
    public static string? WhyForeign(LarianGameProfile profile, int version)
    {
        if (profile.PakVersions.Contains(version)) return null;

        var game = Games.FirstOrDefault(g => g.Version == version).Game;
        return game is null
            ? $"It is a version {version} package, which {profile.GameName} does not load."
            : $"It is a {game} pak (v{version}), which {profile.GameName} does not load.";
    }
}
