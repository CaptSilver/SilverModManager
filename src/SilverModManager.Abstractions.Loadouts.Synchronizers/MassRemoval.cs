using JetBrains.Annotations;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// When an apply that removes files from the game folder asks first.
/// </summary>
/// <param name="MinimumFiles">Never ask about fewer files than this, whatever their share. Switching one small mod off
/// and applying is the ordinary use, and a question every time teaches the player to press yes.</param>
/// <param name="Files">Ask when at least this many files go. A texture pack or a whole mod list is a long way past
/// what one mod has, and the player is unlikely to have meant to lose that many unnoticed.</param>
/// <param name="Share">Ask when at least this share of the mod files the loadout deployed goes. Half is the point
/// where "removing some mods" has become "removing the mod setup".</param>
[PublicAPI]
public sealed record MassRemovalThreshold(int MinimumFiles, int Files, double Share)
{
    /// <summary>10 files at least, then half of what is deployed or 200 files.</summary>
    public static MassRemovalThreshold Default => new(MinimumFiles: 10, Files: 200, Share: 0.5);

    /// <summary>Whether removing <paramref name="removed"/> of <paramref name="deployed"/> deployed mod files is worth a question.</summary>
    public bool Applies(int removed, int deployed)
        => removed >= MinimumFiles && (removed >= Files || removed >= Share * deployed);
}

/// <summary>Files that go because the item named is switched off.</summary>
[PublicAPI]
public sealed record MassRemovalReason(string Name, int FileCount);

/// <summary>
/// An apply that would remove a large part of what the loadout put in the game folder, and why.
/// </summary>
/// <param name="FileCount">Files the apply removes.</param>
/// <param name="DeployedCount">Mod files the loadout had deployed, the removed ones included.</param>
/// <param name="Reasons">The switched-off mods or collections the files belong to, largest first.</param>
/// <param name="Unexplained">Files no switched-off item accounts for: their mod was removed from the loadout or no longer provides them.</param>
[PublicAPI]
public sealed record MassRemoval(int FileCount, int DeployedCount, IReadOnlyList<MassRemovalReason> Reasons, int Unexplained)
{
    private const int ReasonsListed = 5;

    /// <summary>The share of the deployed files that go, as a whole percentage.</summary>
    public int Percent => DeployedCount == 0 ? 0 : (int)Math.Round(100.0 * FileCount / DeployedCount);

    /// <summary>"1 file" or "n files".</summary>
    public static string Files(int count) => count == 1 ? "1 file" : $"{count} files";

    /// <summary>What happens and why, as sentences a player reads: the count first, then one line per cause.</summary>
    public string Opening(string gameName)
    {
        var lines = new List<string>
        {
            $"Applying this loadout removes {Files(FileCount)} from {gameName}'s folder, {Percent}% of the {Files(DeployedCount)} it put there.",
        };

        foreach (var reason in Reasons.Take(ReasonsListed))
            lines.Add($"{reason.Name} is switched off, so applying removes its {Files(reason.FileCount)} from the game folder.");

        var more = Reasons.Skip(ReasonsListed).ToArray();
        if (more.Length > 0)
            lines.Add($"{more.Length} more switched-off {(more.Length == 1 ? "item accounts" : "items account")} for {Files(more.Sum(static r => r.FileCount))}.");

        if (Unexplained > 0)
            lines.Add($"{Files(Unexplained)} no longer belong to anything in the loadout, because their mod was removed or no longer provides them.");

        return string.Join("\n\n", lines);
    }
}

/// <summary>
/// Asks the player to confirm an apply that removes a large part of what the loadout deployed.
/// </summary>
[PublicAPI]
public interface IMassRemovalPrompt
{
    /// <summary>
    /// True to go ahead. Ends with <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/>
    /// is cancelled while the question is open; anything but an explicit yes is a no.
    /// </summary>
    ValueTask<bool> Confirm(GameInstallation installation, MassRemoval removal, CancellationToken cancellationToken);
}

/// <summary>
/// Carries the answer to the removal question given before the operation started, so the syncs it runs do not ask.
/// The command line gives one either way: a verb forwarded to a running window would otherwise open a dialog on the
/// desktop of someone who typed a command. Ambient like <see cref="BackupOverflowAnswer"/>, for the same reason:
/// the syncs are separate calls.
/// </summary>
public static class MassRemovalConsent
{
    private static readonly AsyncLocal<bool?> Given = new();

    /// <summary>The answer a scope gave, or null when the player is to be asked.</summary>
    internal static bool? Current => Given.Value;

    /// <summary>Confirms every removal the current operation makes. Dispose it when the operation ends.</summary>
    public static IDisposable Confirmed() => Set(true);

    /// <summary>Refuses every removal the current operation would ask about, without asking. Dispose it when the operation ends.</summary>
    public static IDisposable Refused() => Set(false);

    private static Restore Set(bool answer)
    {
        var before = Given.Value;
        Given.Value = answer;
        return new Restore(before);
    }

    private sealed class Restore(bool? before) : IDisposable
    {
        public void Dispose() => Given.Value = before;
    }
}

/// <summary>
/// Thrown, before anything is written, when an apply would remove a large part of what the loadout deployed and the
/// answer is no: the player cancelled, or nobody could be asked.
/// </summary>
[PublicAPI]
public sealed class MassRemovalException : UserFacingException
{
    /// <summary>The refusal for <paramref name="removal"/>.</summary>
    public MassRemovalException(GameInstallation installation, MassRemoval removal)
        : base($"{removal.Opening(installation.Game.DisplayName)}\n\nThe apply was stopped and nothing was changed.")
    {
        Removal = removal;
    }

    /// <summary>What the apply would have removed.</summary>
    public MassRemoval Removal { get; }

    /// <summary>True when the player answered the question with cancel, so the GUI skips its error dialog for it.</summary>
    public bool Declined { get; init; }

    /// <inheritdoc />
    public override string Title => "Files were not removed";

    /// <inheritdoc />
    public override string? CommandLineHint
        => "Run `silvermod loadout synchronize --loadout <loadout> --confirm-removal` to apply anyway, or switch the items back on first.";
}
