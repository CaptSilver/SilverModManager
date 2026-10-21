using DynamicData.Kernel;
using JetBrains.Annotations;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FromSoft.Installers;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Games.FromSoft.Saves;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.FromSoft.EldenRing;

[UsedImplicitly]
public class EldenRingGame : AFromSoftGame<EldenRingGame>, IGameData<EldenRingGame>
{
    public static GameId GameId { get; } = GameId.From("FromSoft.EldenRing");
    public static string DisplayName => "Elden Ring";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(4333);

    /// <remarks>Built from <see cref="EldenRingPaths"/> only: the base constructor reads it, and no other static of this class is set by then.</remarks>
    public static readonly FromSoftGameDefinition Definition = new()
    {
        Layout = new FromSoftGameLayout(
            EldenRingPaths.SteamAppId,
            "EldenRing",
            EldenRingPaths.GameSubfolder,
            EldenRingPaths.GameExecutable,
            EldenRingPaths.AntiCheatLauncher,
            "EldenRing",
            SaveRootKind.RoamingAppData,
            SaveAccountFolderKind.SteamId64Decimal),
        SaveNames = new FromSoftSaveNames(EldenRingPaths.VanillaSave, EldenRingPaths.ModdedSave, EldenRingPaths.SeamlessCoopSave),
    };

    public static readonly Me3LoaderOptions Me3Options = new("eldenring", [SeamlessCoop.Native]);

    public EldenRingGame(IServiceProvider provider) : base(provider, Definition, _ => new Me3Loader(provider, Me3Options, Definition)) { }

    protected override ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider) => new EldenRingSynchronizer(provider, this);

    // No FOMOD installer: Elden Ring FOMODs assume the game folder, which nothing is ever installed into.
    protected override ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider) =>
    [
        // First, because the release also holds a lone DLL that the native installer would take for a mod.
        Loader.HostInstaller(),
        new SeamlessCoopInstaller(provider),
        ..Loader.ModInstallers(),
    ];
}
