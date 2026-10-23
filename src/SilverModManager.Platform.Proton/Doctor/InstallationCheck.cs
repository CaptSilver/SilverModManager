using NexusMods.Sdk.Games;

namespace NexusMods.Platform.Proton.Doctor;

/// <summary>How serious a <see cref="DoctorCheck"/> result is.</summary>
public enum DoctorCheckLevel
{
    /// <summary>The thing checked is as it should be.</summary>
    Ok,

    /// <summary>A fact worth knowing that needs no action.</summary>
    Info,

    /// <summary>Something that may cause trouble.</summary>
    Warn,

    /// <summary>Something that is wrong and will cause trouble.</summary>
    Fail,
}

/// <summary>One game-specific finding in the doctor report.</summary>
/// <param name="Id">A stable name for what was checked, such as <c>artifacts</c>.</param>
/// <param name="Level">How serious the result is.</param>
/// <param name="Message">The result in a sentence.</param>
public sealed record DoctorCheck(string Id, DoctorCheckLevel Level, string Message);

/// <summary>
/// Adds a game's own checks to the doctor report. Register one per game, and return an empty list for an
/// installation of any other game.
/// </summary>
public interface IInstallationCheck
{
    IReadOnlyList<DoctorCheck> Check(GameInstallation installation);
}
