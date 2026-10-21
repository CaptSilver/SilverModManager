using Microsoft.Extensions.DependencyInjection;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Doctor;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.FromSoft.Doctor;

/// <summary>Puts <see cref="EldenRingChecks"/> into the doctor report of an Elden Ring installation.</summary>
public class EldenRingInstallationCheck(
    ISettingsManager settings,
    IFileSystem fileSystem,
    IExecutableLocator executables,
    ISandboxProbe probe,
    Me3VersionProbe me3Version) : IInstallationCheck
{
    public IReadOnlyList<DoctorCheck> Check(GameInstallation installation)
    {
        if (installation.Game.GameId != EldenRingGame.GameId) return [];

        var results = new List<CheckResult>(EldenRingChecks.GameFolder(EldenRingPaths.GameFolder(installation)))
        {
            EldenRingChecks.OverlaySupport(executables, probe),
            Me3(installation),
        };

        var pending = OverlayLayout.All(settings.Get<FromSoftSettings>(), fileSystem, installation).SelectMany(UpperdirAudit.PendingFindings).ToArray();
        if (EldenRingChecks.Upperdir(pending) is { } upperdir) results.Add(upperdir);

        return results.Select(r => new DoctorCheck(r.Id, Level(r.Level), r.Message)).ToArray();
    }

    private CheckResult Me3(GameInstallation installation)
    {
        var binary = installation.Locations[LocationId.Me3Host].Path / Me3HostFiles.Binary;
        var executable = Me3HostFiles.IsExecutable(binary);
        return EldenRingChecks.Me3(binary, executable, executable ? me3Version.Read(binary) : null);
    }

    private static DoctorCheckLevel Level(CheckLevel level) => level switch
    {
        CheckLevel.Ok => DoctorCheckLevel.Ok,
        CheckLevel.Info => DoctorCheckLevel.Info,
        CheckLevel.Warn => DoctorCheckLevel.Warn,
        CheckLevel.Fail => DoctorCheckLevel.Fail,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };
}
