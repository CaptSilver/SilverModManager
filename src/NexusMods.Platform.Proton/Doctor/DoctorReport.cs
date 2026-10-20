using System.Text.Json;
using System.Text.Json.Serialization;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Platform.Proton.Doctor;

/// <summary>
/// Everything needed to diagnose a launch problem for the managed games, in one place.
/// </summary>
/// <param name="Installations">One entry per managed installation, ordered by game, store and store id.</param>
public sealed record DoctorReport(IReadOnlyList<DoctorInstallation> Installations)
{
    /// <summary>
    /// The options the <c>--json</c> form is written and read with.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// The report as the JSON the <c>--json</c> flag prints.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>
/// One managed game installation and what each part of launching it looks like.
/// </summary>
/// <param name="Game">Display name of the game.</param>
/// <param name="Store">The store the installation came from, lower case.</param>
/// <param name="StoreId">The store's id for the game.</param>
/// <param name="Loadouts">Names of the loadouts that manage the installation.</param>
/// <param name="Prefix">Which Wine prefix the game runs in.</param>
/// <param name="Proton">The Proton builds and the pick for the game; null unless the game is a Steam game.</param>
/// <param name="Runner">What running a Windows tool for this game would do.</param>
/// <param name="LaunchOptions">The game's Steam launch options; null unless the game is a Steam game with a known localconfig.vdf.</param>
/// <param name="Lock">Whether a launch of this installation is in progress.</param>
/// <param name="Deployment">Which backend deploys each root; empty when the source of the installations had no plan to show.</param>
public sealed record DoctorInstallation(
    string Game,
    string Store,
    string StoreId,
    IReadOnlyList<string> Loadouts,
    DoctorPrefix Prefix,
    DoctorProton? Proton,
    DoctorRunner Runner,
    DoctorLaunchOptions? LaunchOptions,
    DoctorLock Lock,
    IReadOnlyList<DoctorDeploymentRoot> Deployment);

/// <summary>How the prefix lookup ended.</summary>
public enum DoctorPrefixStatus
{
    /// <summary>A prefix was chosen.</summary>
    Resolved,

    /// <summary>The game is a native Linux build and needs none.</summary>
    Native,

    /// <summary>The game needs a prefix and none could be chosen.</summary>
    Failed,
}

/// <param name="Status">How the lookup ended.</param>
/// <param name="Path">The prefix directory, when resolved.</param>
/// <param name="Kind">Which launcher or runner owns the prefix, when resolved.</param>
/// <param name="Message">The resolver's explanation when the lookup did not end in a prefix.</param>
public sealed record DoctorPrefix(DoctorPrefixStatus Status, string? Path, CompatibilityLayerKind? Kind, string? Message);

/// <param name="Name">The name Steam uses for the build.</param>
/// <param name="Directory">The directory that holds the build's <c>proton</c> script.</param>
public sealed record DoctorProtonBuild(string Name, string Directory);

/// <param name="Builds">Every build the Steam installation can see.</param>
/// <param name="Selected">The build Steam runs the game with, when one could be chosen.</param>
/// <param name="Diagnostic">Why no build could be chosen.</param>
public sealed record DoctorProton(IReadOnlyList<DoctorProtonBuild> Builds, DoctorProtonBuild? Selected, string? Diagnostic);

/// <param name="CanRun">Whether a Windows tool can be run for the game.</param>
/// <param name="Strategy">The strategy that would run it.</param>
/// <param name="Argv">The command line it would use for a placeholder program.</param>
/// <param name="Diagnostic">Why nothing can run it.</param>
public sealed record DoctorRunner(bool CanRun, string? Strategy, IReadOnlyList<string> Argv, string? Diagnostic);

/// <summary>What the game's launch options in localconfig.vdf hold.</summary>
public enum DoctorLaunchOptionsStatus
{
    /// <summary>There is no localconfig.vdf, or it cannot be parsed.</summary>
    NoConfig,

    /// <summary>The file has no launch options for the game.</summary>
    NoOptions,

    /// <summary>The game has launch options without the launcher span.</summary>
    NoSpan,

    /// <summary>The launch options contain the launcher span.</summary>
    Span,
}

/// <param name="ConfigPath">The localconfig.vdf that was read.</param>
/// <param name="Status">What it holds for the game.</param>
/// <param name="Line">The launch options as Steam stores them.</param>
/// <param name="Loadout">The loadout the span launches, when there is a span.</param>
public sealed record DoctorLaunchOptions(string ConfigPath, DoctorLaunchOptionsStatus Status, string? Line, string? Loadout);

/// <summary>Whether a launch is in progress.</summary>
public enum DoctorLockStatus
{
    /// <summary>Nobody holds the lock and no state is left behind.</summary>
    Free,

    /// <summary>A process holds the lock: a launch is in progress.</summary>
    Session,

    /// <summary>Nobody holds the lock, but a state file remains from a launch that died.</summary>
    Leftover,

    /// <summary>The lock could not be inspected.</summary>
    Unavailable,
}

/// <param name="Status">Whether a launch is in progress.</param>
/// <param name="Pid">Process id of the launch, from the state file.</param>
/// <param name="Loadout">Loadout the launch applied.</param>
/// <param name="Phase">How far the launch had got.</param>
/// <param name="Diagnostic">Why the lock could not be inspected, or what is odd about its state file.</param>
public sealed record DoctorLock(DoctorLockStatus Status, int? Pid, string? Loadout, string? Phase, string? Diagnostic);

/// <summary>One root of an installation and the backend that deploys it.</summary>
/// <param name="Root">The root as <c>Location</c> or <c>Location/path</c>.</param>
/// <param name="Backend">Id of the backend the root is routed to.</param>
/// <param name="Recorded">False when no row is stored and the root is routed to copy by default.</param>
/// <param name="Registered">Whether a backend with that id is registered in this build.</param>
/// <param name="Chosen">True when a person picked the backend, so the selection chain leaves it alone.</param>
/// <param name="Reason">Why the selection chain recorded the backend.</param>
/// <param name="Available">
/// The backend the chain would pick now, when it differs from the one a person chose. Unchosen rows already hold the
/// chain's own pick, so they never carry one.
/// </param>
public sealed record DoctorDeploymentRoot(string Root, string Backend, bool Recorded, bool Registered, bool Chosen = false, string? Reason = null, string? Available = null)
{
    /// <summary>
    /// The recorded roots, followed by a copy entry for each location the recorded plan does not cover, which
    /// is what the synchronizer would route there.
    /// </summary>
    /// <param name="available">
    /// The backend the selection chain picks for a root now, or null to skip the question. Only asked for chosen
    /// rows, since answering it writes probe files into the root.
    /// </param>
    public static IReadOnlyList<DoctorDeploymentRoot> From(DeploymentPlan recorded, GameLocations locations, IReadOnlySet<DeploymentBackendId> registered, Func<GamePath, DeploymentBackendId?>? available = null)
    {
        string? Differing(DeploymentPlanRoot row)
            => !row.Chosen || available?.Invoke(row.Root) is not { } pick || pick == row.Backend ? null : pick.Value;

        var roots = recorded.Roots
            .Select(r => new DoctorDeploymentRoot(Describe(r.Root), r.Backend.Value, Recorded: true, registered.Contains(r.Backend), r.Chosen, r.Reason, Differing(r)));
        var implied = recorded.MissingRoots(locations)
            .Select(r => new DoctorDeploymentRoot(Describe(r), DeploymentBackendId.Copy.Value, Recorded: false, registered.Contains(DeploymentBackendId.Copy)))
            // GameLocations enumerates in hash order; sort so the report reads the same for every game.
            .OrderBy(r => r.Root, StringComparer.Ordinal);
        return roots.Concat(implied).ToArray();
    }

    /// <summary>
    /// A root as <c>Location</c> or <c>Location/path</c>.
    /// </summary>
    internal static string Describe(GamePath root) => root.Path == RelativePath.Empty
        ? root.LocationId.ToString()
        : $"{root.LocationId}/{root.Path}";
}
