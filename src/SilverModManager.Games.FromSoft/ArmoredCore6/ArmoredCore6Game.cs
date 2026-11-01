using DynamicData.Kernel;
using JetBrains.Annotations;
using SilverModManager.Games.FromSoft.Cleanup;
using SilverModManager.Games.FromSoft.Doctor;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Me3;
using SilverModManager.Games.FromSoft.Saves;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft.ArmoredCore6;

/// <summary>
/// Armored Core VI: Fires of Rubicon, launched through me3 like Elden Ring. The same Steam launcher with anti-cheat
/// starts <c>armoredcore6.exe</c> from the <c>Game</c> folder.
/// </summary>
[UsedImplicitly]
public class ArmoredCore6Game : AMe3FromSoftGame<ArmoredCore6Game>, IGameData<ArmoredCore6Game>
{
    public static GameId GameId { get; } = GameId.From("FromSoft.ArmoredCore6");
    public static string DisplayName => "Armored Core VI: Fires of Rubicon";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(5679);

    public const uint SteamAppId = 1888160;

    /// <summary>The anti-cheat launcher Steam starts; it starts <c>armoredcore6.exe</c>.</summary>
    public const string AntiCheatLauncher = "start_protected_game.exe";

    /// <remarks><c>new</c> on purpose, as for Elden Ring: the base's instance property is this same object.</remarks>
    public new static readonly FromSoftGameDefinition Definition = new()
    {
        Layout = new FromSoftGameLayout(
            SteamAppId, "ArmoredCore6", "Game", "armoredcore6.exe", AntiCheatLauncher, "ArmoredCore6",
            SaveRootKind.RoamingAppData, SaveAccountFolderKind.SteamId64Decimal),
        SaveNames = new FromSoftSaveNames("AC60000.sl2", "AC60000_silver.sl2", null),
        CheckRules = GameFolderCheckRules.ModEngine2 with { Launcher = AntiCheatLauncher },
        Cleanup = CleanupRules.ModEngine2 with { Launcher = AntiCheatLauncher },
        Formats = new FromSoftFormatTraits(
            BinderFormat.Bnd4,
            [DcxCompression.Kraken, DcxCompression.Dflt],
            new DcxWriteFormat(DcxCompression.Dflt, Unk04: 0x11000, Level: 9, WindowLog: 0),
            "regulation.bin",
            new DcxWriteFormat(DcxCompression.Dflt, Unk04: 0x11000, Level: 9, WindowLog: 0),
            [..Convert.FromHexString("10CEED477B7CD9D7E6938E114713E787D53913B10D318EC135E4BE50504E0E10")],
            "armoredcore6",
            // The sound archive (sd/sd) is left out, as Elden Ring's is: indexing skips the sd folder.
            [
                ArchiveIndex.FromHex("Data0",
                    "ec5e374acf64ae86b0052516e86061494a3a1adc58b5457ccc23e470785224b1" +
                    "8f0121e1c2c697ccc46b77e525f6a077a187216f8c58b5d3162857b4fd90e2f6" +
                    "f8185ef177ae559a64a02ed64fc5e91c1501dfff2372a326c62585acac9089ca" +
                    "a81dc1bc1fca2f619af1006c67f1bd70b95462ca888f76acf68a9b7c45d59865" +
                    "5eba183423e35034bfb6b1386aa7978e42cb5f5d082a76e3add22c5dfebf5ee5" +
                    "58cb8728f6cf8842105d9356c0f0f7018405e9b54e470e0bf13ca1b97d7d31f0" +
                    "10aee5da2f1ae0769f63b7e5536db4afccb853043ef40abde315184f6c5d7138" +
                    "ddc8121a66dc9262647ce81ebcd0ec8bf68884a27105861e1ca2bffce0f4a635",
                    0xe1d7dbb9),
                ArchiveIndex.FromHex("Data1",
                    "b0ce504e2e85a35962db77e4bcfee35ea1656a747ad554b5ce71259ac647d04c" +
                    "f82edb8ced65024276728bdbb5e53f3d5a620a086a5f80764180911516a5bd14" +
                    "c841c6b039fe05dcaa71844a373e34d17bd6b7ee83faa051eec1f6d5e5ab5f3f" +
                    "6dd0fba8f123618d361f97e1e19fe0d3ea02952372651e3a1bf317652c3c28c5" +
                    "784078a4080c49378d0dc5df8ca507297372794977ec5f18bd80d4b11ced7e2b" +
                    "31b046b121d7ff5dedb3f27788c1b9347e48477e6431d1de34db6d1354ca907f" +
                    "c7bf34297b899c6ac2f147c1eadb717c198bb3b9e686d78ee91d6baf9cd6b872" +
                    "41af2ece7fdb782444110f06f6553ddfa50da81e039ce730d830e74e6dd5ffb5",
                    0x65876719),
                ArchiveIndex.FromHex("Data2",
                    "a6f355f1c09ac5306d5ba901f7819de7ff8db909d52f1468e9bd1051249787c2" +
                    "8658244d3e3a6a2c4caed24b870931b9c5fa26cc1e6f96af4836ed2875901666" +
                    "ef8d5afcfe6327672103be50f0c5f49ff013dd923330639fb778b848257af3ec" +
                    "c102e2255413842b6b97507c742cfbbc5be54a49f1db78dd510ff485fc559cbb" +
                    "cfd86a4d5bfbf98832be276449171a5df2d77d1691cc17e38cc53de9bd0c8e06" +
                    "579680472689a0353ba031640dd819668beea9ca831e0c3e2e44c750a39f0eba" +
                    "f80db991cdf852f0c0e29b5e2470be0304ef8cb3779c5da3af797ad6311c5d42" +
                    "c25a9038add593766efb74bd8ac315c3898c4d01e704155b86fcf455ec9f211d",
                    0xfdf2b929),
                ArchiveIndex.FromHex("Data3",
                    "bd2e175e17ae7e842fbdf4e4b0984eec9ac7d7290c6b6a201eda878403995e8a" +
                    "cb2d7bec59f54e43e7a7158ee40632b255b50e96a8f733a1e8d92f108094cefd" +
                    "7cf70e10f76251ef6b9f519c85c8da0992cafad307467f1a8a6627355f73da3b" +
                    "efadc2332ff6eed4b8fdd04cf6a2acf9cb2f8c4e9f1b8ef4674db9c840a0b53b" +
                    "57ce5b60e2986d1a48d5e7eb4d8eb16de7ec3123274d079e97a30f500b1b6964" +
                    "9e13b2d759bdb82ad4ad3a9a92eb453443c692cf81d0c0f4a2b0c9ec18962536" +
                    "b65efc6d4e637202f7b3bf0a6e2276552ae538bd80c48e93a480fe905703cb4e" +
                    "7926c324291d959d13eb795d3ca10721cd0e668937b3594024ee621c668147c3",
                    0xffffffff),
            ],
            PathHashBits: 64,
            PathHashPrime: 133,
            Bhd5Layout.Salted40ByteEntries),
    };

    public static readonly Me3LoaderOptions Me3Options = new("armoredcore6", []);

    public ArmoredCore6Game(IServiceProvider provider) : base(provider, Definition, Me3Options) { }
}
