using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.TestFramework;

/// <summary>
/// A prefix resolver that hands out a prefix in a temporary folder, or whatever the test configures, so no
/// test looks at Steam's or Heroic's real directories.
/// </summary>
public class FakePrefixResolver(TemporaryFileManager temporaryFileManager) : IPrefixResolver
{
    private ResolvedPrefix? _defaultPrefix;

    /// <summary>
    /// What <see cref="Resolve"/> returns. Defaults to a Proton-style prefix in a fresh temporary folder.
    /// </summary>
    public Func<GameInstallation, PrefixResolution>? Resolution { get; set; }

    /// <summary>
    /// The prefix returned when <see cref="Resolution"/> is not set.
    /// </summary>
    public ResolvedPrefix DefaultPrefix => _defaultPrefix ??= new ResolvedPrefix(
        temporaryFileManager.CreateFolder().Path,
        CompatibilityLayerKind.SteamProton
    );

    /// <inheritdoc />
    public PrefixResolution Resolve(GameInstallation installation)
        => Resolution is not null ? Resolution(installation) : PrefixResolution.Resolved(DefaultPrefix);
}
