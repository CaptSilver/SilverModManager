using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using JetBrains.Annotations;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Secrets;
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
            "eldenring",
            // Data0 holds the base game's files, DLC the Shadow of the Erdtree ones; a lookup stops at the first hit.
            // The index is encrypted with the private key, so these public pairs can read it and nothing more.
            [
                ArchiveIndex.FromHex("Data0",
                    "f518eedb086bb970d5419a5fca55443de3719bb5e0307703c9a691508a57404d" +
                    "2a128fba637e8b3f4c6916f2f41f490ac47c29b884ac760ae730727fca3e12eb" +
                    "5316ce13bccb80506f0ff325e8610d244279c149985073dc8d890b91428e82be" +
                    "f36c5df713395d775fb1d0734624a086e2072f8aa4d9667fd1ffe72b957281e5" +
                    "979ffa0b79804b8d7d52f30492012cdaeb82578edd1b3ff8ba9a957648b66a29" +
                    "1c68ef1d9b210ad1d721cd7a4485da306164881c6ed301c630b2c27fbba7de78" +
                    "c2371f8f1079556699e2119b2a18b56e160f71c86de179c685c51358edcd9895" +
                    "ac97071c8d0e24b2eb4bab7b918cb30cd087939103ba3eff3f47b50d16d7425b",
                    0x4731fc21),
                ArchiveIndex.FromHex("Data1",
                    "c5a0421d026bb4b26224d746f67ab775e03db18e18099e1d6d33873bebfe6205" +
                    "9131c13a88aea864826afa704c50d05b18f99b46b1239241fd33b2c0ca900538" +
                    "f282eb0307cea20c06c50cb987dfba506e3aab44043d75080c1fc77eeb8f027e" +
                    "ff5b6712ab498d892b2bdcb2ac837a63e8f2e1fb72990b6393cdfe2fcf69bcc3" +
                    "2690d79968f38de0ef6ae4cf4f9c5a0abcaf1e638e5957f27293e3defb173af9" +
                    "ee21e1cc7c30be54da430cdd50c026eb21e33ff4e614b7fdb0b807a2bdfbb74f" +
                    "c20a9980ac7527b0c1b4b74748083044fa628cf78e8ca7006b604368da7d09fa" +
                    "27ef4a02f99081f87910ec58c9705eca687b07e681efb88ec46409765d3b0b11",
                    0x35aff625),
                ArchiveIndex.FromHex("Data2",
                    "d220d5550db7d1182b9081c934383113b23fd80687e8b8b512ef66b697ebadf8" +
                    "5fa0ad9eef2e0e594fa58fb0062381888245a93f0f091da57def40abebeb131b" +
                    "84ebe5e3103f1ac2207ec12403c2968ca9c5cb3604d7d3197ce6eaf7947eb1b3" +
                    "2c80e9ef0cb6e25200f2bf59c14be92a5376f24c410c67826c60a461e49536e1" +
                    "7e47dacde0e00c458874af543de74c5cdaa48d96836c8e90e848aa183744fbbb" +
                    "9ae7327ff89ad7259d63d545653de34cebd6bca6bf825cdee98d7fa713d57aab" +
                    "4b581268ad495997ec49e0fa0f0937bafaaebdf63a625191ca0225544300bcaa" +
                    "c344c1f296c2edaaa85891935731dcb4357a297f5ac41cacbda6868712626577",
                    0xffffffff),
                ArchiveIndex.FromHex("Data3",
                    "bd144d06756add692708d1eb2517a5704036bff3b32a5424c59c35c8a965d18d" +
                    "8a9fa1bdb6cf7849f81961b7450a7217cb9344bb21d12abc57cf9beeae386b72" +
                    "ea18008d995215cf5d925d807cb069cfe2dd2fc863457f7836ea7a9c89587a4f" +
                    "e88e7ae724aa968975a7f0527158db12c572bd91ad5885cb3c36f1e04605280e" +
                    "41e76b8ae86773e2a732545b5512136832f83e1285a7c8b0fb114cee0762a197" +
                    "8e82b8d89cb725ab2e5207829289af5403f1ad257743c33c70d99f4deae900ef" +
                    "65ddb7516d9df49fbf1de06de768f4f3eaa80573f0867759b263cc5a78100da8" +
                    "a4e8533b1154507ad28f8ba879ba9ad598c012e7047f8d02154cca538ffcc977",
                    0x5eccde43),
                ArchiveIndex.FromHex("DLC",
                    "99827fe46254e1ba094af67cd4114e1d84c674158f1e7598972df25a8d350588" +
                    "c6467cfc3539330c7531b1b8c882d1b95eab107d9b2d6482d0f7b860488e4068" +
                    "0a021ab4fcb27d0f7978368b498ac0bdb58118d151dbcf0e9d58fbb860ac6c2e" +
                    "4e48f272aaac43fa882e6e0dbc91ef19177bc4b389f6bb36cf062171e455aeaf" +
                    "575360176dd638a5d090dfe1eea1a162274756d25cb9a9d1178db4afd901187b" +
                    "7ea7582b6c77a84463d2e36c15fc0f57cadc3d9ce82d2035d1c9187da210e354" +
                    "37a0cc2bdb74bf62419f008e650ed6f86b7b4a934dae4c7cc4a10cf201fd9dad" +
                    "7483d9ded753223b51672e6f22e333b155d3c74012880a429c192d68dc2d9d7f",
                    0x5f0fdfdf),
            ],
            PathHashBits: 64,
            PathHashPrime: 133,
            Bhd5Layout.Salted40ByteEntries),
    };

    public static readonly Me3LoaderOptions Me3Options = new("eldenring", [SeamlessCoop.Native]);

    public EldenRingGame(IServiceProvider provider) : base(provider, Definition, _ => new Me3Loader(provider, Me3Options, Definition)) { }

    /// <summary>Seamless Co-op's settings file gets the co-op password when it is written for a session.</summary>
    public override IReadOnlyList<IConfigurationLayerTransform> LayerTransforms
        => [new SeamlessCoopSettingsTransform(Provider.GetRequiredService<ISecretStore>())];

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
