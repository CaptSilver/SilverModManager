using NexusMods.Games.FromSoft.Formats;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Native;

namespace NexusMods.Games.FromSoft.Build;

/// <summary>What a change to the installed build amounts to.</summary>
public enum BuildChangeKind
{
    /// <summary>Nothing tracked moved, or there is no stamp to compare with.</summary>
    Unchanged,

    /// <summary>The install and the game's data moved together: a real update.</summary>
    Patched,

    /// <summary>The install moved and the game's data did not: the same data under another executable or depot build.</summary>
    Repackaged,

    /// <summary>The game's data moved and the install did not: something wrote to the data.</summary>
    Tampered,
}

/// <summary>One field of a <see cref="BuildIdentity"/> that moved, with the value it had when stamped and the value it has now.</summary>
public sealed record BuildChange(string Field, string? Stamped, string? Live);

/// <summary>A named reading of a <see cref="BuildIdentity"/>, so the classification can be told which fields count as the install and which as the data.</summary>
internal sealed record BuildField(string Name, Func<BuildIdentity, string?> Get);

/// <summary>
/// Which build of a FromSoft game is installed: the executable's file version and Steam's build id, which are
/// required (an identity with one of them guessed would look like agreement when the build is unknown), and the
/// regulation's version and hash when the game has one.
/// </summary>
/// <param name="Exe">The file version of the game's executable, such as <c>2.7.1.0</c>.</param>
/// <param name="SteamBuildId">The build id in Steam's app manifest.</param>
/// <param name="RegulationVersion">The build stamp inside the decrypted regulation; null for a game with no regulation, or whose regulation file is missing.</param>
/// <param name="RegulationHash">The SHA-256 of the whole regulation file, lowercase hex; null under the same conditions.</param>
public sealed record BuildIdentity(string Exe, string SteamBuildId, string? RegulationVersion = null, string? RegulationHash = null)
{
    internal static readonly BuildField ExeField = new("exe", i => i.Exe);
    internal static readonly BuildField SteamBuildIdField = new("steam_buildid", i => i.SteamBuildId);

    /// <summary>Fields that move when the install is replaced: the executable and the depot build.</summary>
    private static readonly BuildField[] InstallFields = [ExeField, SteamBuildIdField];

    internal static readonly BuildField RegulationVersionField = new("regulation_version", i => i.RegulationVersion);
    internal static readonly BuildField RegulationHashField = new("regulation_hash", i => i.RegulationHash);

    /// <summary>Fields that move when the game's data does: a real update changes the regulation, a hand edit changes it alone.</summary>
    private static readonly BuildField[] DataFields = [RegulationVersionField, RegulationHashField];

    /// <summary>
    /// Reads the installed build. Throws when it cannot: a build that is not known must stop whatever needed it, and
    /// reporting "no change" for it would hide a stale setup.
    /// </summary>
    public static BuildIdentity Identify(GameInstallation installation)
    {
        if (installation.Game is not IFromSoftGame game)
            throw new InvalidOperationException($"{installation.Game.DisplayName} is not a FromSoft game, so its build is not stamped.");

        var exe = FromSoftPaths.GameFolder(game.Layout, installation) / game.Layout.GameExecutable;
        var version = ReadFileVersion(exe, game.Layout.GameExecutable);

        var manifest = $"appmanifest_{FromSoftPaths.InstallationFolderName(game.Layout, installation.LocatorResult)}.acf";
        var buildId = installation.LocatorResult.StoreBuildId;
        if (string.IsNullOrEmpty(buildId) || buildId == "0")
            throw new InvalidOperationException($"Steam's {manifest} gives no build id for {game.DisplayName} ({(buildId is null ? "none" : $"'{buildId}'")}), so the installed build cannot be identified.");

        var (regulationVersion, regulationHash) = ReadRegulation(game, installation);
        return new BuildIdentity(version, buildId, regulationVersion, regulationHash);
    }

    /// <summary>
    /// Whether the stamp lacks a data field that <paramref name="live"/> has, so the stamp can be completed from it.
    /// </summary>
    internal static bool CanBackfill(BuildIdentity stamped, BuildIdentity live)
        => DataFields.Any(f => f.Get(stamped) is null && f.Get(live) is not null);

    /// <summary>
    /// What kind of change took the build from <paramref name="stamped"/> to <paramref name="live"/>. A real update
    /// moves the install and the data together; data moving alone means nothing was updated and something else
    /// wrote to it. No stamp is no change: unknown is not changed.
    /// </summary>
    public static BuildChangeKind Classify(BuildIdentity? stamped, BuildIdentity live) => Classify(stamped, live, InstallFields, DataFields);

    internal static BuildChangeKind Classify(BuildIdentity? stamped, BuildIdentity live, IReadOnlyList<BuildField> installFields, IReadOnlyList<BuildField> dataFields)
    {
        if (stamped is null) return BuildChangeKind.Unchanged;

        var installMoved = installFields.Any(f => Moved(f, stamped, live));
        var dataMoved = dataFields.Any(f => Moved(f, stamped, live));
        return (installMoved, dataMoved) switch
        {
            (true, true) => BuildChangeKind.Patched,
            (true, false) => BuildChangeKind.Repackaged,
            (false, true) => BuildChangeKind.Tampered,
            _ => BuildChangeKind.Unchanged,
        };
    }

    /// <summary>
    /// Whether the field differs. A field the stamp never recorded is not compared: stamps written before the
    /// regulation was tracked have no value for it, and reading that as a change would flag every existing install as
    /// tampered. A field the stamp has and the live build lacks did move: the game lost its regulation.
    /// </summary>
    private static bool Moved(BuildField field, BuildIdentity stamped, BuildIdentity live)
        => field.Get(stamped) is { } was && was != field.Get(live);

    /// <summary>The fields that moved between the stamp and the live build. Empty when there is no stamp.</summary>
    public static IReadOnlyList<BuildChange> Drift(BuildIdentity? stamped, BuildIdentity live)
    {
        if (stamped is null) return [];
        return InstallFields.Concat(DataFields)
            .Where(f => Moved(f, stamped, live))
            .Select(f => new BuildChange(f.Name, f.Get(stamped), f.Get(live)))
            .ToArray();
    }

    /// <summary>
    /// The regulation's version and hash. A game without a regulation, or without the file in its folder, has none;
    /// a file that is there but cannot be decrypted throws, since reporting it as absent would hide a broken install.
    /// </summary>
    private static (string? Version, string? Hash) ReadRegulation(IFromSoftGame game, GameInstallation installation)
    {
        var traits = game.Formats;
        if (traits.RegulationFileName is not { } fileName) return (null, null);

        var file = FromSoftPaths.GameFolder(game.Layout, installation) / fileName;
        if (!file.FileExists) return (null, null);

        try
        {
            var blob = File.ReadAllBytes(file.GetFullPath());
            return (Regulation.Version(Regulation.Unpack(blob, traits)), Regulation.Sha256Hex(blob));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or RegulationException or AesException or DcxException or KrakenException)
        {
            throw new InvalidOperationException($"Could not read {fileName} to identify the installed build: {exception.Message}", exception);
        }
    }

    private static string ReadFileVersion(AbsolutePath exe, string executableName)
    {
        try
        {
            using var stream = exe.Read();
            return PeFileVersion.Read(stream)
                   ?? throw new InvalidOperationException($"{executableName} carries no version resource, so the installed build cannot be identified.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Could not read {executableName} to identify the installed build: {exception.Message}", exception);
        }
    }
}
