using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using JetBrains.Annotations;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FromSoft.Cleanup;
using NexusMods.Games.FromSoft.Doctor;
using NexusMods.Games.FromSoft.Formats;
using NexusMods.Games.FromSoft.Installers;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Games.FromSoft.Saves;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.FromSoft.EldenRing;

[UsedImplicitly]
public class EldenRingGame : AFromSoftGame<EldenRingGame>, IGameData<EldenRingGame>
{
    public static GameId GameId { get; } = GameId.From("FromSoft.EldenRing");
    public static string DisplayName => "Elden Ring";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(4333);

    /// <remarks>
    /// <c>new</c> on purpose: the base's instance <see cref="AFromSoftGame{TSelf}.Definition"/> is this same object, but the
    /// static is also read by the doctor checks, where there is no game instance.
    /// Built from <see cref="EldenRingPaths"/> and constants only: the base constructor reads it, and no other static of this class is set by then.
    /// </remarks>
    public new static readonly FromSoftGameDefinition Definition = new()
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
        CheckRules = GameFolderCheckRules.ModEngine2 with
        {
            Launcher = EldenRingPaths.AntiCheatLauncher,
            ModFolders = [SeamlessCoop.Folder, "mods"],
            ExtraArtifacts = ["ersc_launcher.exe"],
            Spawners = ["ermerchant.dll", "erdtools.dll", "glorious_merchant.dll"],
        },
        Cleanup = CleanupRules.ModEngine2 with
        {
            Launcher = EldenRingPaths.AntiCheatLauncher,
            ModsFolder = "mods",
            Leftovers = [..CleanupRules.ModEngine2.Leftovers, SeamlessCoop.Folder, "ersc_launcher.exe"],
        },
        CleanupVerb = EldenRingCleanupVerb.Name,
        Overlay = OverlayAllowlist.Default with { Folders = ["mods/", $"{SeamlessCoop.Folder}/"] },
        Formats = new FromSoftFormatTraits(
            BinderFormat.Bnd4,
            [DcxCompression.Kraken, DcxCompression.Dflt, DcxCompression.Zstd],
            new DcxWriteFormat(DcxCompression.Dflt, Unk04: 0x11000, Level: 9, WindowLog: 0),
            "regulation.bin",
            new DcxWriteFormat(DcxCompression.Zstd, Unk04: 0x11000, Level: 15, WindowLog: 16),
            // Static across every retail build; the regulation is encrypted only in the sense that the key ships with the game.
            [..Convert.FromHexString("99BFFC366A6BC8C6F5827D093602D676C42892A01C207FB024D3AF4E493FEF99")],
            "eldenring"),
    };

    public static readonly Me3LoaderOptions Me3Options = new("eldenring", [SeamlessCoop.Native]);

    public EldenRingGame(IServiceProvider provider) : base(provider, Definition, _ => new Me3Loader(provider, Me3Options, Definition)) { }

    /// <summary>Seamless Co-op's settings file gets the co-op password when it is written for a session.</summary>
    public override IReadOnlyList<IConfigurationLayerTransform> LayerTransforms
        => [new SeamlessCoopSettingsTransform(Provider.GetRequiredService<ISettingsManager>())];

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
