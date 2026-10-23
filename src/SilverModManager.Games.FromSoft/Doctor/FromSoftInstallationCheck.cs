using SilverModManager.Games.FromSoft.Overlay;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Doctor;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Games.FromSoft.Doctor;

/// <summary>Puts <see cref="FromSoftChecks"/> and the mod loader's own check into the doctor report of a FromSoft installation.</summary>
public class FromSoftInstallationCheck(
    ISettingsManager settings,
    IFileSystem fileSystem,
    IExecutableLocator executables,
    ISandboxProbe probe) : IInstallationCheck
{
    public IReadOnlyList<DoctorCheck> Check(GameInstallation installation)
    {
        if (installation.Game is not IFromSoftGame game) return [];

        var results = new List<CheckResult>(FromSoftChecks.GameFolder(FromSoftPaths.GameFolder(game.Layout, installation), game.CheckRules))
        {
            FromSoftChecks.OverlaySupport(executables, probe),
        };
        if (game.Loader.HostCheck(installation) is { } host) results.Add(host);

        var pending = OverlayLayout.All(settings.Get<FromSoftSettings>(), fileSystem, installation).SelectMany(UpperdirAudit.PendingFindings).ToArray();
        if (FromSoftChecks.Upperdir(pending) is { } upperdir) results.Add(upperdir);

        return results.Select(r => new DoctorCheck(r.Id, Level(r.Level), r.Message)).ToArray();
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
