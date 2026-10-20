using NexusMods.Platform.Proton.Launch;
using NexusMods.Platform.Proton.Steam;
using NexusMods.Sdk.Games;

namespace NexusMods.Platform.Proton.Doctor;

/// <summary>
/// An installation with a loadout, and the names of the loadouts.
/// </summary>
/// <param name="Deployment">The deployment plan to show, when the source of the installation has one.</param>
public sealed record ManagedInstallation(
    GameInstallation Installation,
    IReadOnlyList<string> Loadouts,
    IReadOnlyList<DoctorDeploymentRoot>? Deployment = null);

/// <summary>
/// Builds the doctor report by asking the same resolvers, runner and lock a launch would use. Read-only: the
/// lock is probed and released again, and localconfig.vdf is only read.
/// </summary>
public sealed class DoctorReporter(
    IPrefixResolver prefixResolver,
    IProtonResolver protonResolver,
    IToolRunner toolRunner,
    IInstallationLockFactory lockFactory)
{
    /// <summary>The program the runner is asked to plan, so the report shows a real command line.</summary>
    public const string PlaceholderExe = "/doctor/placeholder.exe";

    public DoctorReport Build(IEnumerable<ManagedInstallation> installations)
    {
        var entries = installations
            .Select(Describe)
            .OrderBy(i => i.Game, StringComparer.Ordinal)
            .ThenBy(i => i.Store, StringComparer.Ordinal)
            .ThenBy(i => i.StoreId, StringComparer.Ordinal)
            .ToArray();
        return new DoctorReport(entries);
    }

    private DoctorInstallation Describe(ManagedInstallation managed)
    {
        var installation = managed.Installation;
        var result = installation.LocatorResult;
        var store = result.Store.Value.ToLowerInvariant();

        var steamAppId = SteamApp(installation);
        return new DoctorInstallation(
            installation.Game.DisplayName,
            store,
            result.StoreIdentifier,
            managed.Loadouts,
            DescribePrefix(prefixResolver.Resolve(installation)),
            steamAppId is { } id ? DescribeProton(result.LinuxCompatabilityDataProvider!, id) : null,
            DescribeRunner(installation),
            steamAppId is { } app ? DescribeLaunchOptions(result.LinuxCompatabilityDataProvider!, app) : null,
            DescribeLock(store, result.StoreIdentifier),
            managed.Deployment ?? []);
    }

    /// <summary>
    /// The app id for a Steam game whose Steam installation is known; the Proton and launch option sections
    /// need both.
    /// </summary>
    private static uint? SteamApp(GameInstallation installation)
    {
        var result = installation.LocatorResult;
        if (result.Store != GameStore.Steam || result.LinuxCompatabilityDataProvider is null) return null;
        return uint.TryParse(result.StoreIdentifier, out var id) ? id : null;
    }

    private static DoctorPrefix DescribePrefix(PrefixResolution resolution)
    {
        if (resolution.IsResolved)
            return new DoctorPrefix(DoctorPrefixStatus.Resolved, resolution.Prefix.Path.ToString(), resolution.Prefix.Kind, null);
        return new DoctorPrefix(resolution.IsNative ? DoctorPrefixStatus.Native : DoctorPrefixStatus.Failed, null, null, resolution.Diagnostic);
    }

    private DoctorProton DescribeProton(ILinuxCompatabilityDataProvider provider, uint appId)
    {
        var steam = provider.SteamInstallPath;
        var builds = protonResolver.ListBuilds(steam).Select(DescribeBuild).ToArray();
        var pick = protonResolver.ResolveForApp(steam, appId);
        return pick.IsResolved
            ? new DoctorProton(builds, DescribeBuild(pick.Build), null)
            : new DoctorProton(builds, null, pick.Diagnostic);
    }

    private static DoctorProtonBuild DescribeBuild(ProtonBuild build) => new(build.Name, build.Directory.ToString());

    private DoctorRunner DescribeRunner(GameInstallation installation)
    {
        if (!toolRunner.CanRun(installation, out var diagnostic)) return new DoctorRunner(false, null, [], diagnostic);

        var placeholder = installation.LocatorResult.Path.FileSystem.FromUnsanitizedFullPath(PlaceholderExe);
        var plan = toolRunner.Plan(installation, placeholder, [], null);
        return plan.IsRunnable
            ? new DoctorRunner(true, plan.Strategy, plan.Argv, null)
            : new DoctorRunner(false, null, [], plan.Diagnostic);
    }

    private static DoctorLaunchOptions DescribeLaunchOptions(ILinuxCompatabilityDataProvider provider, uint appId)
    {
        var path = provider.SteamLocalConfigPath;
        var read = LocalConfigLaunchOptions.Read(path, appId);
        if (!read.FileReadable) return new DoctorLaunchOptions(path.ToString(), DoctorLaunchOptionsStatus.NoConfig, null, null);
        if (read.Line is null) return new DoctorLaunchOptions(path.ToString(), DoctorLaunchOptionsStatus.NoOptions, null, null);

        var parsed = SteamLaunchOptionLine.Parse(read.Line);
        return parsed.HasSpan
            ? new DoctorLaunchOptions(path.ToString(), DoctorLaunchOptionsStatus.Span, read.Line, parsed.LoadoutName)
            : new DoctorLaunchOptions(path.ToString(), DoctorLaunchOptionsStatus.NoSpan, read.Line, null);
    }

    private DoctorLock DescribeLock(string store, string storeId)
    {
        try
        {
            using var installationLock = lockFactory.Create(store, storeId);
            var held = installationLock.Probe();
            var state = installationLock.ReadState();

            if (state is null)
            {
                return held
                    ? new DoctorLock(DoctorLockStatus.Session, null, null, null, "The lock is held but its holder has not written a state file yet.")
                    : new DoctorLock(DoctorLockStatus.Free, null, null, null, null);
            }

            return new DoctorLock(
                state.IsLeftover ? DoctorLockStatus.Leftover : DoctorLockStatus.Session,
                state.Pid,
                state.Loadout,
                state.Phase.ToString().ToLowerInvariant(),
                null);
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new DoctorLock(DoctorLockStatus.Unavailable, null, null, null, e.Message);
        }
    }
}
