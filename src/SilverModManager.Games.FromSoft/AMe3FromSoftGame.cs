using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FromSoft.Me3;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft;

/// <summary>
/// A FromSoft game that me3 launches and has nothing of its own beyond its definition: the loader, the synchronizer and
/// the installers and the per-loadout save plan are the family's, so the game class holds only the three statics and
/// the definition.
/// </summary>
public abstract class AMe3FromSoftGame<TSelf> : AFromSoftGame<TSelf>, IPerLoadoutSaves, IGitHubShelfGame
    where TSelf : AMe3FromSoftGame<TSelf>, IGameData<TSelf>
{
    protected AMe3FromSoftGame(IServiceProvider provider, FromSoftGameDefinition definition, Me3LoaderOptions options)
        : base(provider, definition, _ => new Me3Loader(provider, options, definition)) { }

    /// <inheritdoc />
    /// <remarks>me3 is the loader every game of this family launches through, so each lists its release.</remarks>
    public IReadOnlyList<GitHubShelfEntryDefinition> GitHubShelf { get; } = [Me3HostFiles.ShelfEntry];

    protected override ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider) => new FromSoftSynchronizer(provider, this);

    /// <remarks>The host installer goes first because a release of the loader can hold a lone DLL that the native installer would take for a mod.</remarks>
    protected sealed override ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider) =>
    [
        Loader.HostInstaller(),
        ..GameInstallers(provider),
        ..Loader.ModInstallers(),
    ];

    /// <summary>Installers for the game's own mods, tried after the loader's host installer and before its generic ones.</summary>
    protected virtual IEnumerable<ILibraryItemInstaller> GameInstallers(IServiceProvider provider) => [];

    /// <inheritdoc />
    /// <remarks>me3 reads the save name from the profile's <c>savefile</c> key.</remarks>
    public SaveRedirectPlan? PlanSaveRedirect(Loadout.ReadOnly loadout)
        => SaveRedirectRefusal(loadout) is { } reason
            ? new SaveRedirectPlan.Unsupported(reason)
            : new SaveRedirectPlan.LoaderKey("savefile", LoadoutSave(loadout));

    /// <summary>Why a loadout cannot keep its saves apart, or null. A mod that writes its own save file is the usual reason.</summary>
    protected virtual string? SaveRedirectRefusal(Loadout.ReadOnly loadout) => null;

    private string LoadoutSave(Loadout.ReadOnly loadout)
        => Definition.SaveNames.ModdedFor(loadout) ?? throw new InvalidOperationException($"{TSelf.DisplayName}'s save names have no modded save to name for a loadout.");

    /// <inheritdoc />
    public SaveAdoptionPlan? PlanAdoptCurrentSave(Loadout.ReadOnly loadout, AbsolutePath saveRoot)
    {
        if (Definition.SaveNames.Modded is null) return null;

        return Definition.SaveNames.PlanAdoption(LoadoutSave(loadout), saveRoot);
    }
}
