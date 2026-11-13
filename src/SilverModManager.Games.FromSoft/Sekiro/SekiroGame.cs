using DynamicData.Kernel;
using JetBrains.Annotations;
using SilverModManager.Games.FromSoft.Cleanup;
using SilverModManager.Games.FromSoft.Doctor;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Me3;
using SilverModManager.Games.FromSoft.Saves;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft.Sekiro;

/// <summary>
/// Sekiro: Shadows Die Twice, launched through me3. Steam starts <c>sekiro.exe</c> from the install root: there is no
/// <c>Game</c> folder and no anti-cheat launcher. The game has no regulation file, so
/// <see cref="Build.BuildIdentity"/> never reports it as tampered, and no param merge is offered. The overlay's lower
/// folder is the whole install root including <c>Artwork_MiniSoundtrack/</c>; the snapshot cost is accepted until it
/// has been measured.
/// </summary>
[UsedImplicitly]
public class SekiroGame : AMe3FromSoftGame<SekiroGame>, IGameData<SekiroGame>
{
    public static GameId GameId { get; } = GameId.From("FromSoft.Sekiro");
    public static string DisplayName => "Sekiro: Shadows Die Twice";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(2763);

    public const uint SteamAppId = 814380;

    /// <remarks><c>new</c> on purpose, as for Elden Ring: the base's instance property is this same object.</remarks>
    public new static readonly FromSoftGameDefinition Definition = new()
    {
        Layout = new FromSoftGameLayout(
            SteamAppId, "Sekiro", "", "sekiro.exe", null, "Sekiro",
            SaveRootKind.RoamingAppData, SaveAccountFolderKind.SteamId64Decimal, SoundFolderKind.Sound),
        SaveNames = new FromSoftSaveNames("S0000.sl2", "S0000_silver.sl2", null),
        HasOnlinePlay = false,
        // Mod Engine 1 was Sekiro's loader, so its ini marks a modded folder as well. The cleanup rules already list it.
        CheckRules = GameFolderCheckRules.ModEngine2 with { ModLoaderFiles = [..GameFolderCheckRules.ModEngine2.ModLoaderFiles, "modengine.ini"] },
        Cleanup = CleanupRules.ModEngine2,
        CleanupVerb = FromSoftCleanupVerb.Name,
        Formats = new FromSoftFormatTraits(
            BinderFormat.Bnd4,
            [DcxCompression.Kraken, DcxCompression.Dflt],
            new DcxWriteFormat(DcxCompression.Dflt, Unk04: 0x11000, Level: 9, WindowLog: 0),
            RegulationFileName: null,
            RegulationWrite: null,
            RegulationKey: [],
            "sekiro",
            // The sound archive and the bonus archive are left out: indexing skips them.
            [
                ArchiveIndex.FromHex("Data1",
                    "f7697e016c75695ee6cedfbaaf4d1b9bfaa77386fa347dd556bfefe14c4c71fc" +
                    "eeb212fc8ec9fd6523f5b2cf79a2dfeaba4f1d389b29d3efdbbf11fe0bd0c47b" +
                    "3ccd2d2aaa0067768ead8f628b23ffa1616002a38cc94a8cdc5c54fb0a42e834" +
                    "65bcbe298b04362ae61177260357f396dac849fe9de8b281d541471cdf4d4642" +
                    "c29b789d1383eef7ce7690f1db88bd7812a7c30838309bb90b79076dde451b72" +
                    "6761bbfe9dbcfe361c6c4198060b3450f78807bba8b30b305ce628a1d00f67d1" +
                    "40aff8df5677c4f2ba269db8e40751d00c14615be75eafbff7bc0c5f40ba50ab" +
                    "c067e6ffd72b4336ef2f66dfbe961274d528d37360388553f3ee0d4410ee7f4d",
                    0x2a48f927),
                ArchiveIndex.FromHex("Data2",
                    "aa18e84e1597f15c2c4ca4c8d648e9d09065a025e157c8bdf4fd4a3d30930c19" +
                    "e685540f76efbb510e60e3ffde874a2a29a3948deb7ed123fde10b1c8c822b85" +
                    "5f753b37c8a2e21680924afd138e3ff037b1be409733dae983dc37c383c51f9e" +
                    "b32ad685ceab1c2b78a86bd7557ae087ace964ce61e5509349aba9e2aa885a6e" +
                    "38105dfe15e612edf685835e2b1f4404a2c89e208645666a48e920896a796b55" +
                    "df99875dece177197e4d09742d80123c4243694a8b752758030e70973ef2e1e8" +
                    "32e34da353cca5843a67c3738390a28bb127c1b0839ae270e7cb450666ce5476" +
                    "69918f7754878599a257484ef3b53a74a936b4e58654b2253783d1e996479353",
                    0xfff12991),
                ArchiveIndex.FromHex("Data3",
                    "c798e5808be81d02f0485b00c0a1596cda377e067cf42eed8f8870899b10560f" +
                    "109cd6688579794b9129f694b66e2385b1292e64c5ca99f604ae1d82b1d15baf" +
                    "b411ee4bcf3e26a75cdae4088e3195f43026f32690b9bdebf2664485334d6a74" +
                    "90a254259247fd422b5cd9fb7e693c566c756d0222f1bba619297783178c86eb" +
                    "6ffe50bd921b184e7d000532406c86f0359e463d7c73419b903d75c3639d869b" +
                    "e171ed8cb52e22bf07a8ac203254374c5d83175a5f118d7f9a4fa1c3098e7834" +
                    "39bddc8e11af764cac965fe36059d1c742dadc21a6b4b71732d1e1784431cc6b" +
                    "9e1836bf96492502fbc45ed72929fc8541671485e419f0b9e0021fd44c91ed43",
                    0xea55a14f),
                ArchiveIndex.FromHex("Data4",
                    "abc47202b93e7aa3007312c01d40d1615ef249c34a669292c469a025943bcba6" +
                    "3c7f9c1dacd0ad6f25df9ac7501024ad206964c168ed7e6f2dfdb459bb1c4f72" +
                    "2018685b16a3465ed8107ab98e99b5e3692c99fcdaa6cb4d70137fe69fde44ab" +
                    "bc809fbc9c11cdb7c0fcf4494641e3da027a4dcee0e872bb4bfe13882662aeaf" +
                    "9cff32d8de06fc03cb8814d2388f4ab14f37e80254bb2bab296637e7859ea5ee" +
                    "960587e825cf5e7001ba690bd3b2feee6f51a728cdfe2f9da71b5070f8eddb3d" +
                    "1193d90c79372df25b8a81f0b54cc97f206a666229237df28b02263e96fdeb3c" +
                    "2f414f3a4da5cab09ad3992b3fe3c05e0ec4305ee6aa3a0fba9e8113071de981",
                    0x18cd8e87),
                ArchiveIndex.FromHex("Data5",
                    "bbbe7f51b5f01ddbeefe9e3d4f09d8ecebba0c0b9960502d2d4930fd1e27be6d" +
                    "07595951b198812c6dcc386eac3e62b665cdc92c153640ae2e29acd9f55ae6a2" +
                    "d3383a962bd37ab6b62167bf735f0af07551e20920a7b791584735921bbebc04" +
                    "3fd6b95029eb4870febbe163e4c401af8cab122b70acd56f202e09d04cf53e99" +
                    "f7f83f1f7fc5e04113fda8254b2375ffc1d40379af81e68f0f59a6042fd30a9e" +
                    "72f54eae21fb83d160771047cfc65c14eb737c7dab770f4110d61db4588487d6" +
                    "48ff6eaed6a08dcb2672e1bea77e1fa7e2b258ec248a5dc526ac07d4a8124db9" +
                    "3d4dbd2804fcea4c224a784ff082eea163efa5062e53dc0ac765972c6d7cd5eb",
                    0xf9514cc7),
            ],
            PathHashBits: 32,
            PathHashPrime: 37,
            Bhd5Layout.Hash32Salted40ByteEntries),
    };

    public static readonly Me3LoaderOptions Me3Options = new("sekiro", []);

    public SekiroGame(IServiceProvider provider) : base(provider, Definition, Me3Options) { }
}
