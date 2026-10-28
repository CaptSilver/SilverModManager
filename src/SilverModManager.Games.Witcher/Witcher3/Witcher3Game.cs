using DynamicData.Kernel;
using NexusMods.Paths;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Witcher.Witcher3.Diagnostics;
using SilverModManager.Games.Witcher.Witcher3.Installers;
using SilverModManager.Games.Witcher.Witcher3.Merge;
using SilverModManager.Games.Witcher.Witcher3.Order;
using SilverModManager.Games.Witcher.Witcher3.SortOrder;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.Witcher.Witcher3;

public sealed class Witcher3Game(IServiceProvider provider) : AWitcherGame<Witcher3Game>(provider), IGameData<Witcher3Game>, IHasMergeGame, IModOrderGame
{
    private AMergeGame? _mergeGame;
    private Witcher3VortexOrderFormat? _vortexOrder;

    private static readonly RelativePath Dx12Exe = "bin/x64_dx12/witcher3.exe";
    private static readonly RelativePath Dx11Exe = "bin/x64/witcher3.exe";

    public static GameId GameId { get; } = GameId.From("Witcher.Witcher3");
    public static string DisplayName => "The Witcher 3: Wild Hunt";
    public static Optional<NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(952);

    // 292030 is the base game, 499450 the Game of the Year edition.
    protected override StoreIdentifiers CreateStoreIdentifiers() => new(GameId)
    {
        SteamAppIds = [499450u, 292030u],
        GOGProductIds = [1207664663L, 1495134320L, 1207664643L, 1640424747L],
        EGSCatalogItemId = ["725a22e15ed74735bb0d6a19f3cc82d0"],
    };

    // The page is a view over the priority rows, which the default sink writes, so there is nothing of its own to apply.
    IModOrderSink? IModOrderGame.OrderSink => null;

    IVortexOrderFormat? IModOrderGame.VortexOrderFormat => _vortexOrder ??= new Witcher3VortexOrderFormat();

    public AMergeGame MergeGame => _mergeGame ??= new Witcher3MergeGame(this);

    protected override RelativePath PrimaryExe => Dx12Exe;
    protected override string? DocumentsFolderName => "The Witcher 3";

    // Script Merger first: its archive also contains files the mod rules could claim.
    protected override ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider) =>
    [
        new ScriptMergerArchiveInstaller(provider),
        new Witcher3ModInstaller(provider),
    ];

    protected override IEnumerable<IDiagnosticEmitter> CreateEmitters(IServiceProvider provider) =>
    [
        .. base.CreateEmitters(provider),
        provider.GetRequiredService<SkippedScriptMergerEmitter>(),
        provider.GetRequiredService<UnplacedArchiveFilesEmitter>(),
        provider.GetRequiredService<ModsSettingsReplacedEmitter>(),
        provider.GetRequiredService<EngineMergeDiagnosticsEmitter>(),
    ];

    protected override ISortOrderVariety[] CreateSortOrderVarieties(IServiceProvider provider) => [new WitcherModSortOrderVariety(provider, this)];

    protected override ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider) => new Witcher3Synchronizer(provider, this);

    // Older installs and the Steam default for some editions ship only the original executable.
    public override GamePath GetPrimaryFile(GameInstallation installation)
    {
        var dx12 = new GamePath(LocationId.Game, Dx12Exe);
        return installation.Locations.ToAbsolutePath(dx12).FileExists ? dx12 : new GamePath(LocationId.Game, Dx11Exe);
    }
}
