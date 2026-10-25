using System.Collections.Immutable;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;

/// <summary>A file of a Nexus Mods page.</summary>
/// <param name="Name">The file's name on the page.</param>
/// <param name="Uploaded">Unix seconds.</param>
public sealed record NexusFile(uint FileId, string Name, string Version, long Uploaded);

public sealed record NexusModHit(uint ModId, string Name);

/// <summary>
/// What the save step asks Nexus Mods. The app implements it over its GraphQL client; this project does not reference
/// the network code, and tests answer from memory.
/// </summary>
public interface INexusCatalog
{
    /// <summary>The files of a mod page, or null when the page is unknown.</summary>
    Task<IReadOnlyList<NexusFile>?> ListFilesAsync(NexusModsGameId game, uint modId, CancellationToken cancellationToken);

    /// <summary>The mod page a file belongs to, or null when the file is unknown.</summary>
    Task<uint?> ModOfFileAsync(NexusModsGameId game, uint fileId, CancellationToken cancellationToken);

    /// <summary>The mods of the game whose name matches <paramref name="name"/>, closest first.</summary>
    Task<IReadOnlyList<NexusModHit>> FindModsByNameAsync(NexusModsGameId game, string name, CancellationToken cancellationToken);

    /// <summary>The file id of a mod's one main file, or null when it has none or several.</summary>
    Task<uint?> FindMainFileAsync(NexusModsGameId game, uint modId, CancellationToken cancellationToken);
}

/// <summary>Whether a Nexus Mods file is in the library already.</summary>
public interface ILibraryProbe
{
    bool Contains(NexusModsGameId game, uint fileId);
}

public enum ModDownloadKind
{
    /// <summary>The file is in the library.</summary>
    Added,

    /// <summary>The account cannot download through the API, so the file's page was opened; the file arrives when the user downloads it there.</summary>
    OpenedInBrowser,

    Failed,
}

public sealed record ModDownloadResult(ModDownloadKind Kind, string? Message = null)
{
    public static ModDownloadResult Added { get; } = new(ModDownloadKind.Added);

    public static ModDownloadResult OpenedInBrowser { get; } = new(ModDownloadKind.OpenedInBrowser);

    public static ModDownloadResult Failed(string message) => new(ModDownloadKind.Failed, message);
}

/// <summary>Puts a Nexus Mods file in the library the way an <c>nxm</c> link does.</summary>
public interface IModDownloader
{
    Task<ModDownloadResult> DownloadAsync(NexusModsGameId game, ModToSave mod, CancellationToken cancellationToken);
}

/// <summary>A Nexus Mods file the cleanup is about to lose the only copy of.</summary>
/// <param name="Id">What the old manager called it.</param>
/// <param name="Name">The file's name on Nexus Mods, or <paramref name="Id"/> when it is not known.</param>
public sealed record ModToSave(string Id, string Name, string Version, uint ModId, uint FileId);

public enum ListedReason
{
    AlreadyInLibrary,
    GitHub,
    Unidentified,
}

/// <summary>A mod the list shows but the save step does not download.</summary>
/// <param name="Detail">Why, in a sentence for the user.</param>
public sealed record ListedMod(string Id, ListedReason Reason, string Detail);

/// <param name="Game">The game's Nexus Mods id; null when it has none, in which case nothing is downloadable.</param>
/// <param name="Save">What would be downloaded.</param>
/// <param name="Listed">What the user should know about but is not downloaded.</param>
/// <param name="Problems">What could not be read, one sentence each.</param>
public sealed record SavePlan(NexusModsGameId? Game, ImmutableArray<ModToSave> Save, ImmutableArray<ListedMod> Listed, ImmutableArray<string> Problems = default)
{
    public ImmutableArray<string> Problems { get; init; } = Problems.IsDefault ? [] : Problems;

    public bool IsEmpty => Save.IsEmpty && Listed.IsEmpty && Problems.IsEmpty;
}

public sealed record FailedDownload(ModToSave Mod, string Message);

public sealed record SaveReport(ImmutableArray<ModToSave> Added, ImmutableArray<ModToSave> OpenedInBrowser, ImmutableArray<FailedDownload> Failed)
{
    public static SaveReport None { get; } = new([], [], []);

    /// <summary>This report followed by <paramref name="later"/>, for a fix that offered the mods more than once.</summary>
    public SaveReport Plus(SaveReport later) => new([..Added, ..later.Added], [..OpenedInBrowser, ..later.OpenedInBrowser], [..Failed, ..later.Failed]);
}

/// <summary>The questions the save step puts to the user.</summary>
public interface ISavePrompts
{
    /// <summary>Shows the list with each mod to save checked. Returns the ones still checked, or null when the user cancels the cleanup.</summary>
    Task<ImmutableArray<ModToSave>?> ChooseAsync(SavePlan plan, CancellationToken cancellationToken);

    /// <summary>Shows what failed. Returns true to go on with the cleanup anyway.</summary>
    Task<bool> ContinueAfterFailuresAsync(SaveReport report, CancellationToken cancellationToken);
}
