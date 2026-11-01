using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Games.FromSoft.Heal;

/// <summary>The regulation was not put back, and the message says why. The game folder is unchanged.</summary>
public sealed class RegulationRestoreRefusedException(string message, Exception? cause = null) : UserFacingException(message, cause)
{
    /// <inheritdoc />
    public override string Title => "The regulation was not restored";
}

/// <summary>What <see cref="RegulationRestore.Restore"/> did.</summary>
/// <param name="Changed">False when the game folder already held the vouched file, so nothing was written.</param>
/// <param name="SetAside">Where the file that was replaced is kept; null when none was replaced.</param>
public sealed record RegulationRestored(bool Changed, AbsolutePath? SetAside);

/// <summary>
/// Puts the game's own regulation back over a modified one in the real game folder, which is what a Steam launch loads
/// online. The file written is a kept baseline that Steam's manifest for the installed build lists, checked against its
/// recorded hashes on every use. The file it replaces is first kept under the set-aside folder, so a mod author's
/// edits are not lost to the restore.
/// <para>
/// When the install's manifest cannot be read, the baseline of the stamped build serves instead, but only while the
/// executable and Steam's build id are the stamped ones: then the installed build is the stamped one.
/// </para>
/// </summary>
/// <param name="setAsideRoot">The folder replaced files are kept in. Resolved on every call, so a changed data root is followed.</param>
public sealed class RegulationRestore(RegulationBaselines baselines, IManifestVouching vouching, IGameLaunchSession session, Func<AbsolutePath> setAsideRoot, TimeProvider? time = null, ILogger? logger = null)
{
    /// <summary>The id of the action that restores the regulation. It takes no arguments.</summary>
    public const string RestoreActionId = "fromsoft-restore-regulation";

    private const string TempSuffix = ".restoring";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public static DiagnosticAction RestoreAction()
        => new(RestoreActionId, "Restore the game's own regulation", [],
            "The file that is there now is kept in the app's data folder first.");

    /// <summary>
    /// The baseline <see cref="Restore"/> would write, or null when no kept file qualifies. A kept file that no
    /// longer hashes the way it was recorded does not qualify.
    /// </summary>
    /// <param name="stamped">The build recorded for the installation, null when there is none.</param>
    public AbsolutePath? Candidate(GameInstallation installation, BuildIdentity? stamped)
    {
        try
        {
            return Find(installation, stamped)?.File;
        }
        catch (HealException exception)
        {
            _logger.LogDebug(exception, "The kept regulations could not be read, so none is offered for a restore");
            return null;
        }
    }

    /// <summary>
    /// Replaces the regulation in the real game folder with the vouched baseline, keeping the old file first.
    /// The new file is written beside the old one and renamed over it, so the game never sees half a file.
    /// </summary>
    /// <exception cref="RegulationRestoreRefusedException">The game is running, or no kept baseline is vouched for the installed build. Nothing was changed.</exception>
    public RegulationRestored Restore(GameInstallation installation, BuildIdentity? stamped)
    {
        if (session.IsRunning(installation))
            throw new RegulationRestoreRefusedException($"{installation.Game.DisplayName} is running. Close it, then restore the regulation.");

        var game = GameOf(installation);

        Vouched vouched;
        try
        {
            vouched = Find(installation, stamped) ?? throw new RegulationRestoreRefusedException(
                $"The app has no copy of {game.DisplayName}'s own regulation that Steam's manifest vouches for, so there is nothing to restore it from. Verify the game's files in Steam instead.");
        }
        catch (HealException exception)
        {
            throw new RegulationRestoreRefusedException(exception.Message, exception);
        }

        var target = RegulationFile(installation);
        try
        {
            if (!target.FileExists) return Write(target, vouched.Bytes, setAside: null);

            var current = File.ReadAllBytes(target.GetFullPath());
            if (SHA256.HashData(current).AsSpan().SequenceEqual(SHA256.HashData(vouched.Bytes))) return new RegulationRestored(false, null);

            return Write(target, vouched.Bytes, SetAside(target, current));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RegulationRestoreRefusedException($"{target} could not be replaced: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// The buttons a diagnostic about the real game folder offers. The restore is offered for a differing regulation
    /// when <see cref="Candidate"/> finds a file to restore it from; Steam's verification covers every other file, and
    /// stands in for the restore when there is none. Nothing is offered when nothing differs or the game is not from Steam.
    /// </summary>
    /// <param name="regulationDiffers">The regulation is not the one Steam shipped.</param>
    /// <param name="otherFilesDiffer">Other files differ from Steam's, or mods put files in the folder.</param>
    public IReadOnlyList<DiagnosticAction> Actions(GameInstallation installation, BuildIdentity? stamped, bool regulationDiffers, bool otherFilesDiffer)
    {
        var actions = new List<DiagnosticAction>();
        var restorable = regulationDiffers && Candidate(installation, stamped) is not null;
        if (restorable) actions.Add(RestoreAction());

        var locator = installation.LocatorResult;
        var needsVerify = otherFilesDiffer || (regulationDiffers && !restorable);
        if (needsVerify && locator.Store == GameStore.Steam && ulong.TryParse(locator.StoreIdentifier, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            actions.Add(DiagnosticActions.VerifyGameFilesInSteam(locator.StoreIdentifier));
        return actions;
    }

    private sealed record Vouched(AbsolutePath File, byte[] Bytes);

    private Vouched? Find(GameInstallation installation, BuildIdentity? stamped)
    {
        var game = GameOf(installation);
        var kept = baselines.List().Where(k => k.Provenance.Tier == BaselineTier.Vouched).Select(Read).OfType<Vouched>().ToList();

        if (vouching.InstalledFiles(installation) is { } listed)
        {
            var hashes = ListedRegulationHashes(game, listed);
            return OnlyOne(kept.Where(k => hashes.Contains(Sha1Value.From(SHA1.HashData(k.Bytes)))));
        }

        if (stamped?.RegulationVersion is not { } version || !InstallUnchanged(installation, stamped)) return null;
        var ofBuild = baselines.List().Where(k => k.Version == version).Select(k => k.File).ToHashSet();
        var sameBuild = kept.Where(k => ofBuild.Contains(k.File)).ToList();
        return OnlyOne(sameBuild.Count > 1 && stamped.RegulationHash is { } hash
            ? sameBuild.Where(k => Regulation.Sha256Hex(k.Bytes).Equals(hash, StringComparison.OrdinalIgnoreCase))
            : sameBuild);
    }

    // The bytes are read once and every later check and the write use that read, so a file swapped in between is not written.
    private Vouched? Read(KeptBaseline baseline)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(baseline.File.GetFullPath());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The kept regulation {File} could not be read", baseline.File);
            return null;
        }

        var provenance = baseline.Provenance;
        var recorded = Convert.ToHexStringLower(SHA1.HashData(bytes)).Equals(provenance.Sha1, StringComparison.OrdinalIgnoreCase)
                       && Regulation.Sha256Hex(bytes).Equals(provenance.Sha256, StringComparison.OrdinalIgnoreCase);
        if (recorded) return new Vouched(baseline.File, bytes);

        _logger.LogWarning("The kept regulation {File} no longer matches the hashes recorded for it, so it is not restored from", baseline.File);
        return null;
    }

    // More than one different file would be a guess about which one the game shipped.
    private static Vouched? OnlyOne(IEnumerable<Vouched> candidates)
    {
        var found = candidates.ToList();
        return found.Select(f => Convert.ToHexString(SHA256.HashData(f.Bytes))).Distinct().Count() == 1 ? found[0] : null;
    }

    // Names Steam hides leave only the hash to go by, so every nameless entry counts.
    private static HashSet<Sha1Value> ListedRegulationHashes(IFromSoftGame game, IReadOnlyList<ManifestFile> listed)
    {
        var path = game.Layout.GameSubfolder + "/" + game.Formats.RegulationFileName;
        return listed
            .Where(f => f.Path is null || f.Path.Replace('\\', '/').Equals(path, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Hash)
            .ToHashSet();
    }

    private static bool InstallUnchanged(GameInstallation installation, BuildIdentity stamped)
    {
        try
        {
            var live = BuildIdentity.Identify(installation);
            return live.Exe == stamped.Exe && live.SteamBuildId == stamped.SteamBuildId;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // The old file goes aside before the new one is written, and under a name that cannot overwrite an earlier one.
    private AbsolutePath SetAside(AbsolutePath target, byte[] current)
    {
        var folder = setAsideRoot();
        Directory.CreateDirectory(folder.GetFullPath());
        var stem = Path.GetFileNameWithoutExtension(target.FileName);
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(current))[..12];
        var name = $"{stem}-modified-{_time.GetUtcNow():yyyyMMdd'T'HHmmss'Z'}-{sha1}{Path.GetExtension(target.FileName)}";
        var kept = folder / name;
        if (!kept.FileExists) WriteAtomically(kept, current);
        return kept;
    }

    private static RegulationRestored Write(AbsolutePath target, byte[] bytes, AbsolutePath? setAside)
    {
        WriteAtomically(target, bytes);
        return new RegulationRestored(true, setAside);
    }

    // Written beside the destination so the rename stays on one file system.
    private static void WriteAtomically(AbsolutePath destination, byte[] bytes)
    {
        var temp = destination.GetFullPath() + TempSuffix;
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, destination.GetFullPath(), overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static IFromSoftGame GameOf(GameInstallation installation)
        => installation.Game as IFromSoftGame
           ?? throw new InvalidOperationException($"{installation.Game.DisplayName} is not a FromSoft game, so it has no regulation to restore.");

    private static AbsolutePath RegulationFile(GameInstallation installation)
    {
        var game = GameOf(installation);
        var fileName = game.Formats.RegulationFileName
            ?? throw new InvalidOperationException($"{game.DisplayName} has no regulation file to restore.");
        return FromSoftPaths.GameFolder(game.Layout, installation) / fileName;
    }
}
