using DynamicData.Kernel;
using JetBrains.Annotations;
using SilverModManager.Games.FromSoft.Cleanup;
using SilverModManager.Games.FromSoft.Doctor;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Me3;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Saves;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft.DarkSouls3;

/// <summary>
/// Dark Souls III, launched through me3. The game has no anti-cheat launcher and plays online, so a vanilla launch
/// loads whatever is in <c>Game/</c>; the modded session is offline (me3's default, the profile never writes
/// <c>start_online</c>).
/// <para>
/// The regulation is <c>Game/Data0.bdt</c> (AES-256-CBC, key ASCII <c>ds3#jn/8_7(rsY9pg55GFN7VFL#+3n/)</c>,
/// SoulsFormats <c>SFUtil.cs</c>). It is left unread until a real file confirms its inner wrapper, so
/// <see cref="FromSoftFormatTraits.RegulationFileName"/> is null: <c>Data0.bdt</c> is skipped as an archive by name, a
/// mod's copy deploys whole, no regulation gate or param merge applies, and
/// <see cref="Build.BuildIdentity"/> cannot report the build as tampered.
/// </para>
/// </summary>
[UsedImplicitly]
public class DarkSouls3Game : AMe3FromSoftGame<DarkSouls3Game>, IGameData<DarkSouls3Game>
{
    public static GameId GameId { get; } = GameId.From("FromSoft.DarkSouls3");
    public static string DisplayName => "Dark Souls III";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(1392);

    public const uint SteamAppId = 374320;

    private static readonly string[] ModEngineFiles = ["modengine2_launcher.exe", "config_darksouls3.toml", "launchmod_darksouls3.bat"];

    /// <remarks><c>new</c> on purpose, as for Elden Ring: the base's instance property is this same object.</remarks>
    public new static readonly FromSoftGameDefinition Definition = new()
    {
        Layout = new FromSoftGameLayout(
            SteamAppId, "DarkSouls3", "Game", "DarkSoulsIII.exe", null, "DarkSoulsIII",
            SaveRootKind.RoamingAppData, SaveAccountFolderKind.SteamId64Decimal, SoundFolderKind.Sound),
        // Mod Engine 1 wrote its modded session to DS30000.sl3; the saves page lists it and adoption can copy it.
        SaveNames = new FromSoftSaveNames("DS30000.sl2", "DS30000_silver.sl2", null, LegacyModded: "DS30000.sl3"),
        HasOnlinePlay = true,
        // As soon as a mod ships one of these, me3 switches Data0.bdt off and the game reads the loose params instead.
        LooseParams = new LooseParamsRule(
            ["param/gameparam/gameparam.parambnd.dcx", "param/gameparam/gameparam_dlc1.parambnd.dcx", "param/gameparam/gameparam_dlc2.parambnd.dcx"],
            "Data0.bdt"),
        CheckRules = GameFolderCheckRules.ModEngine2 with
        {
            ModLoaderFiles = [..GameFolderCheckRules.ModEngine2.ModLoaderFiles, "modengine.ini", "modengine2_launcher.exe"],
            ExtraArtifacts = ["config_darksouls3.toml", "launchmod_darksouls3.bat"],
        },
        // modengine.ini is already one of the Mod Engine 2 rules' leftovers.
        Cleanup = CleanupRules.ModEngine2 with { Leftovers = [..CleanupRules.ModEngine2.Leftovers, ..ModEngineFiles] },
        CleanupVerb = FromSoftCleanupVerb.Name,
        Formats = new FromSoftFormatTraits(
            BinderFormat.Bnd4,
            [DcxCompression.Kraken, DcxCompression.Dflt],
            new DcxWriteFormat(DcxCompression.Dflt, Unk04: 0x10000, Level: 9, WindowLog: 0),
            RegulationFileName: null,
            RegulationWrite: null,
            RegulationKey: [],
            "darksouls3",
            // The five base archives and the two DLC ones; a lookup stops at the first hit.
            [
                ArchiveIndex.FromHex("Data1",
                    "d3986ac9ba16fea65a27718120004556dd57d5a6b4fec28836496fa644d10bee" +
                    "58b5bc6fa75f0b33580dea08d38122623d481d95a31ba6d55aeba333be294493" +
                    "ad1a1f2555121b455b2f87cd4677c51426eabff6aac2b7f305e7760114e49213" +
                    "da3ac15d97197d80c2914923b0656462a8c134a7e9f9ce639673706e27948a15" +
                    "934843be0c0f2769a082cd90f77bbaca8434322c292976c1a6baa06f974d8ebb" +
                    "b41e7a16cf3e7a3bf26398b365e43fa346a9b145190c1ab30cb2c97950db7375" +
                    "e59685191451d31623ad7e0b49ee8e27c331fd991e734a3b2f6f0283009399c0" +
                    "f6c0ef131004c40146db57ed52bbdbaadbec10c609ec029fb1ce19fda20f619b",
                    0x0b7e5b49),
                ArchiveIndex.FromHex("Data2",
                    "bc26402bd51f3dd93925a4e51bb9f5af42d257321a9f78740522da3174073b03" +
                    "bbb531a9bed7579b664b63dcbc49598ec564d0a91ab5e1a5462d3283c87f3b84" +
                    "fb8a778ed8d04e4ae5ead3a816d75ba003e049757a2af6549cffa701edef7155" +
                    "ce23a50276a8d40e4cb00ca567d079409cb31c9406f8bc7333399649cc9e5e54" +
                    "0b38ab9d7e2206d2a170ff185130b34023e8b2a64ccee4d29b9582324945790f" +
                    "1efae3d5ddb19e5423025487138db0ab864be31b79314199331e86d363e44c0f" +
                    "c46753e0204f01675ec8f869a0724709b82bb6737d6a4c109a9c1d041c53fec4" +
                    "c3d7a01d9fd07e4f1b860d04002de7784b82af33e7da771dbba50cfca4ff552f",
                    0x2faee209),
                ArchiveIndex.FromHex("Data3",
                    "a8bcad583db44d25cf7800354460f03d6d7c9c9c1edab057fb41cfb5dcc59907" +
                    "3f2a64255ab3fef7893a28294ae5fee6d31507c13f3ec8518bc4f751bd43f23a" +
                    "10449d103ab6439b5434b18a3e72d098788788a500eef723c54c726b1c4bbd54" +
                    "2a4724e38e38f9e478bd577e48f97322ef1582a2de7ef127fec5ffa407af0e9e" +
                    "f0fa00f4360bce7bd53a8961172b04ce3c1f7b5fe636b81d378124a8827995f2" +
                    "1d9db54149e11ca7e63ae95a6b2e51b76f1a9d02581287085aee3579dfb06d21" +
                    "95ff4ba9b5661336c597f3218c199b5638ed7a5a2b66ca0b3caf8e88f4c75b91" +
                    "1cc309cf875bc714518ceea44c734caf1ea2679e21160ec4b3f01be0f86c6981",
                    0x0bc5cd57),
                ArchiveIndex.FromHex("Data4",
                    "adf51a6568d8014699d2afb9ce7a57e7919ec9ea5ac02679367b0c8c85bd080d" +
                    "eface814191921e9a014f5fae569f43cd4b40c4600339ce9d0184ecadc285fc9" +
                    "20e9aba6f8ed22a6e1b58fa9ded4961d834f12208ba40faa6e145092f67642da" +
                    "0a7ba8c9afe918c38ebe14f52c3ff408aa3f88ac1f05c61ffce027c129f13110" +
                    "b748d46e832bc5ca509eb7d0c474be4bf2e0446b8b15e14dbcf19c594992e0da" +
                    "d075d31cb948702c9b554d3f812ac398896ea1c40ca6569f62c93f9b79c29109" +
                    "bd5d800a20385397f28d02c3cd1c2d08acde2bbc8590a89923de4926153fdc39" +
                    "d5becb2323192e8186644bd0f793d9beb6435ed1556b778981126a95c60f95f7",
                    0x2fa63ab7),
                ArchiveIndex.FromHex("Data5",
                    "bca4e55379e46b89d07ac467620d4d5a8bc20932e11010278e65f023af651587" +
                    "dce25bd9b13ad0134636e4fb683f465d740de7cdf949373d6b070024a9f05119" +
                    "379e4c2032a0da7f89e97710092277fa4a3addc91241f1ae88d1b96612434110" +
                    "6b07016f309f74b1238b399111ea512872144835a0bb41d39b04851c778215ba" +
                    "4103587df4bd978b31f95c54cebb45087149812ec2ba0d8b088f9406ad66b1c3" +
                    "05dc4c937f5fefecc89fd36d02bd5bf4c219a1a28ab18cd6edb0bd95d267bb95" +
                    "337087c2036100c8aa2efa8e47281c0de36ac60abe7570d3c646b8de3ee23c95" +
                    "9d1ecf1bdb97f2687de46d4c4a94319a10434d372650710ae0cfc43bea3204d1",
                    0x154b33f7),
                ArchiveIndex.FromHex("DLC1",
                    "b0218cf5d170cda20e5088a7dc35f2ef1ae6236a2d2862d9250c8a8b95f7ce72" +
                    "a1493cb0a5c15c2a85ba8608de8787c95b6e23873c01a2c1ba780cc320d9be2b" +
                    "4da91c21bca99fc3fe121bbbeba2d043432aaad5c8586742d5f9fd0264550b63" +
                    "f0ce1ad0521f76d137c24c5a2ad3fa217588cb80deb1274658e35558b7e2d9ea" +
                    "bad80e6db39305a8ca125775e34d8b82817a9943a78e13e34805046aa59c4b34" +
                    "54b36c9c165e9020da404c929cb9fb2c0e0728e65b35308e72059754f5ed1f03" +
                    "a5353ff77a23c7efead69f9bd68e62b3929ca0fed0c233aace31c91fc6d3cadc" +
                    "d16e0991703316dda871808d3bd1de394cc5cad98c11cc8ebf25b3353f6f5a9d",
                    0x15ecf263),
                ArchiveIndex.FromHex("DLC2",
                    "b425d4f5afc604c568aada50a31f69d3fe6c58f688bc3a7c6af2c59c806137bb" +
                    "e4813c2e9591e2bbae2f64089401d55e157e05f90b64fb98af2b8f3c32ed82cc" +
                    "0309733e51bf231026d3905ee0c3ab322fdb54ebe73baeb2cc2812b1f1c4ff13" +
                    "64dc641e6ed16a6eceaad9fb2d09cc4d8c6c060659df8c89911b409999c6a02b" +
                    "b961a511e5eba4f2517be62762f7bb2c73350d576d76b4bcc831ad58395eecc4" +
                    "e6fbfa073455322ab37a5af8e5cb3c8fdb31fc1473f28d8eeaf968b0c39ed407" +
                    "1b1e73b8db5cefc80c1b09663ea0e1813bac28f3b85722afa0bd2421362fba4a" +
                    "172404da6b51c859552384b9befee8fcafc3cd82895eac3792fbc14ce41a5ef5",
                    0x1f00992f),
            ],
            PathHashBits: 32,
            PathHashPrime: 37,
            Bhd5Layout.Hash32Salted40ByteEntries),
    };

    public static readonly Me3LoaderOptions Me3Options = new("darksouls3", []);

    public DarkSouls3Game(IServiceProvider provider) : base(provider, Definition, Me3Options) { }
}
