using DynamicData.Kernel;
using JetBrains.Annotations;
using SilverModManager.Games.FromSoft.Cleanup;
using SilverModManager.Games.FromSoft.Doctor;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Me3;
using SilverModManager.Games.FromSoft.Saves;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft.EldenRingNightreign;

/// <summary>
/// Elden Ring Nightreign, launched through me3 like Elden Ring. The Steam launcher with anti-cheat starts
/// <c>nightreign.exe</c> from the <c>Game</c> folder.
/// </summary>
[UsedImplicitly]
public class EldenRingNightreignGame : AMe3FromSoftGame<EldenRingNightreignGame>, IGameData<EldenRingNightreignGame>
{
    public static GameId GameId { get; } = GameId.From("FromSoft.EldenRingNightreign");
    public static string DisplayName => "Elden Ring Nightreign";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(7800);

    public const uint SteamAppId = 2622380;

    /// <summary>The anti-cheat launcher Steam starts; it starts <c>nightreign.exe</c>.</summary>
    public const string AntiCheatLauncher = "start_protected_game.exe";

    /// <remarks><c>new</c> on purpose, as for Elden Ring: the base's instance property is this same object.</remarks>
    public new static readonly FromSoftGameDefinition Definition = new()
    {
        Layout = new FromSoftGameLayout(
            SteamAppId, "EldenRingNightreign", "Game", "nightreign.exe", AntiCheatLauncher, "Nightreign",
            SaveRootKind.RoamingAppData, SaveAccountFolderKind.SteamId64Decimal),
        SaveNames = new FromSoftSaveNames("NR0000.sl2", "NR0000_silver.sl2", null),
        CheckRules = GameFolderCheckRules.ModEngine2 with { Launcher = AntiCheatLauncher },
        Cleanup = CleanupRules.ModEngine2 with { Launcher = AntiCheatLauncher },
        Formats = new FromSoftFormatTraits(
            BinderFormat.Bnd4,
            [DcxCompression.Kraken, DcxCompression.Dflt, DcxCompression.Zstd],
            new DcxWriteFormat(DcxCompression.Dflt, Unk04: 0x11000, Level: 9, WindowLog: 0),
            "regulation.bin",
            new DcxWriteFormat(DcxCompression.Zstd, Unk04: 0x11000, Level: 22, WindowLog: 27),
            [..Convert.FromHexString("9A8EE90C4C01A43168A17D9D75E4A7D02107EBCF43D5ACB0554F941601B57918")],
            "nightreign",
            // The sound archive (sd/sd) is left out, as Elden Ring's is: indexing skips the sd folder.
            [
                ArchiveIndex.FromHex("Data0",
                    "cfc17d53557d8602ace3481dce5d5939fdc8062adfeb1504cd7b4377aa121011" +
                    "3a5e262872f001ca488af9631d01a24bec7279f06b76c52471037b7db6808628" +
                    "c23fde03407dc474055f3782c428d0ed1d54fd1fab2c57940da918de41e0813c" +
                    "68b58f5f670d345485280763627cec6127afcde7ffa609e95756603dfda3d924" +
                    "303464ae7d878ddcec238bc9742b60b47ca231ea048ed1c2bd92f4c89bc01da6" +
                    "fcd96c1913ca54f58c5de6f03b12035385cc5253745c996d3462f3933a6e6bfa" +
                    "ac7b6f65ce4e5b6d5dc9eed0e9f98b9713b231eb4b4eee1da4e5e31c001ffcb1" +
                    "536df4295c5afd6973b83ba23b9d60c99fb151ccd94b91bf11c12c99d7f296c9",
                    0xb103b007),
                ArchiveIndex.FromHex("Data1",
                    "d04e9db670e64fa776f9568d90fce899436ff93ebcf7a1fffd101a4d1da0b8f0" +
                    "02303359a40b05bd5dcc7cd711d814ba09bc65e79326316b26b19b35b36f7732" +
                    "e39d4be73df6f9996cd61c5d68f1d2e2463be56b4577b8dd3473990161d6e0fe" +
                    "5dcabafb061f4eb1b22ce740b66c1d0d736f5bc8d8a050ccee7b2ea0f28e5cab" +
                    "4a774bb3eff30a6582e4d49ee9028050ab0f550f9aa2d8887ceb39f45569b994" +
                    "b313234aa7bad3b3a59543bd958183f8ebe0d6e7aff3b13cb7893b5b45b91b87" +
                    "50fb81016b5833f276dcc3eaa8a910ac6e73dabecf7b1508ef8e143dd9a8dd29" +
                    "fe32aca7bb1c6f57d48486c91aea94adce7f11f62f78c0e28f4d0df61c416e51",
                    0xffffffff),
                ArchiveIndex.FromHex("Data2",
                    "aa991ff721e7c7c93ce3e5972132c5516fd24f2a578d930fbb0f38da9ccd1dae" +
                    "4beeff605383398411f04d0b3460871f9dffa66e3aa82589e660f05b9712e315" +
                    "78304f820225da673cc63be41c03d0dbf6f3f0afbc8b00d104163a151e3979e6" +
                    "e414f6e9d1988b70871cfbc3d7817bead26ddd33bf082ddfca17719def42adae" +
                    "46f3b6864c91afd089ac53428f0a2cd85ff4474368dd25d941e411f390c80b84" +
                    "86847610b79e122a3090cd40d18a328eeb455d6d00419fd33219db73c612c3ec" +
                    "6d5837d679e7468ab7dfc59f0fd02b6591ba6ccd75be57d57189ab26ceefe2f0" +
                    "632979d8570557d2b40621f48d0e76997048f8b6c974e00aa7ba94ddbd2ac75f",
                    0xd3a0e915),
                ArchiveIndex.FromHex("Data3",
                    "c26d91730e1ea0ff058168a3c70d57f1bf6b11516c56acbbad759c1f6c959bad" +
                    "726019733e54ea2aa9dcd957aa65287f4cb5de158dfe3a6d97efc450c59352dc" +
                    "bdcea24bc3ee5ff4c2a3c8a57492ba679e2fc500666e64ee7140da6d8725f73e" +
                    "174ee4370c0212a86616ce584083275b222baa711ac7fa929dc144acb8c860c0" +
                    "4c02b56754b13c5aa82c0fb37c956f77edd954713d9368f8751df8dabca39253" +
                    "2fce1ccf736a77b20cbe0d4660dd53c1a63574d29b990f62849108322f1558f4" +
                    "83989a5ebeb479187477ee344dcb6805476d6a82423cec91966904eea53a43d1" +
                    "72caf30d6e1b5f0fde5dfba2097a335dee4bf257f212941dfb91bd9487d4dabf",
                    0x3aa28bb7),
            ],
            PathHashBits: 64,
            PathHashPrime: 133,
            Bhd5Layout.Salted40ByteEntries),
    };

    public static readonly Me3LoaderOptions Me3Options = new("nightreign", []);

    public EldenRingNightreignGame(IServiceProvider provider) : base(provider, Definition, Me3Options) { }
}
