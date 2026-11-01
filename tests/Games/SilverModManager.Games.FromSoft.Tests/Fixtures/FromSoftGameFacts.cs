using SilverModManager.Games.FromSoft.ArmoredCore6;
using SilverModManager.Games.FromSoft.EldenRingNightreign;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Saves;

namespace SilverModManager.Games.FromSoft.Tests.Fixtures;

/// <summary>
/// One archive of a game, pinned by the parts of its public key that a transcription slip would change: the length,
/// the first and last bytes of the modulus, and the exponent.
/// </summary>
public sealed record ArchiveKeyFacts(string Name, string ModulusStart, string ModulusEnd, ulong Exponent);

/// <summary>
/// What a FromSoft game is, spelled as literals that were read from the sources named in each row. A game class that
/// moves a value, or a typo in one, fails a test instead of passing quietly because both sides were copied together.
/// </summary>
public sealed record FromSoftGameFacts(
    string GameId,
    string DisplayName,
    uint SteamAppId,
    string SteamInstallFolder,
    uint NexusModsGameId,
    string Me3GameName,
    FromSoftGameLayout Layout,
    FromSoftSaveNames SaveNames,
    BinderFormat Binder,
    DcxCompression[] ReadableCompressions,
    DcxWriteFormat PackedWrite,
    string? RegulationFileName,
    DcxWriteFormat? RegulationWrite,
    string RegulationKeyHex,
    string ParamdefSet,
    ArchiveKeyFacts[] Archives,
    int PathHashBits,
    int PathHashPrime,
    Bhd5Layout ArchiveLayout,
    Func<FromSoftGameDefinition> Definition)
{
    /// <summary>
    /// Armored Core VI. Steam 1888160 launches <c>Game/start_protected_game.exe</c> (api.steamcmd.net launch entries);
    /// the game's own executable is <c>Game/armoredcore6.exe</c> (me3 <c>Game::executable</c>); the saves are
    /// <c>%APPDATA%/ArmoredCore6/&lt;account&gt;/AC60000.sl2</c> (me3 <c>savefile_dir</c>, Ludusavi manifest); the Nexus
    /// id and domain <c>armoredcore6firesofrubicon</c> come from Nexus's games.json. The archive keys are UXM's
    /// <c>ArmoredCore6Keys</c> and the regulation key is SoulsFormats' <c>RegulationKey.ArmoredCore6</c>; the
    /// regulation's wrapper (zlib, header word 0x11000, level 9) was read off the regulation.bin that Smithbox ships
    /// for 1.09.1 after decrypting it with that key.
    /// </summary>
    public static readonly FromSoftGameFacts ArmoredCore6 = new(
        GameId: "FromSoft.ArmoredCore6",
        DisplayName: "Armored Core VI: Fires of Rubicon",
        SteamAppId: 1888160,
        SteamInstallFolder: "ARMORED CORE VI FIRES OF RUBICON",
        NexusModsGameId: 5679,
        Me3GameName: "armoredcore6",
        Layout: new FromSoftGameLayout(1888160, "ArmoredCore6", "Game", "armoredcore6.exe", "start_protected_game.exe", "ArmoredCore6", SaveRootKind.RoamingAppData, SaveAccountFolderKind.SteamId64Decimal),
        SaveNames: new FromSoftSaveNames("AC60000.sl2", "AC60000_silver.sl2", null),
        Binder: BinderFormat.Bnd4,
        ReadableCompressions: [DcxCompression.Kraken, DcxCompression.Dflt],
        PackedWrite: new DcxWriteFormat(DcxCompression.Dflt, 0x11000, 9, 0),
        RegulationFileName: "regulation.bin",
        RegulationWrite: new DcxWriteFormat(DcxCompression.Dflt, 0x11000, 9, 0),
        RegulationKeyHex: "10CEED477B7CD9D7E6938E114713E787D53913B10D318EC135E4BE50504E0E10",
        ParamdefSet: "armoredcore6",
        Archives:
        [
            new("Data0", "ec5e374a", "e0f4a635", 0xe1d7dbb9),
            new("Data1", "b0ce504e", "6dd5ffb5", 0x65876719),
            new("Data2", "a6f355f1", "ec9f211d", 0xfdf2b929),
            new("Data3", "bd2e175e", "668147c3", 0xffffffff),
        ],
        PathHashBits: 64,
        PathHashPrime: 133,
        ArchiveLayout: Bhd5Layout.Salted40ByteEntries,
        Definition: () => ArmoredCore6Game.Definition);

    /// <summary>
    /// Elden Ring Nightreign. Steam 2622380 launches <c>Game/start_protected_game.exe</c> (api.steamcmd.net launch
    /// entries); the game's own executable is <c>Game/nightreign.exe</c> (me3 <c>Game::executable</c>); the saves are
    /// <c>%APPDATA%/Nightreign/&lt;account&gt;/NR0000.sl2</c> (me3 <c>savefile_dir</c>, Ludusavi manifest); the Nexus id
    /// and domain <c>eldenringnightreign</c> come from Nexus's games.json. The archive keys are UXM's
    /// <c>EldenRingNightreignKeys</c> and the regulation key is SoulsFormats' <c>RegulationKey.EldenRingNightreign</c>
    /// (its encrypt path uses Elden Ring's key, its decrypt path this one). The regulation's wrapper (Zstandard,
    /// header word 0x11000, level byte 22, a 2^27 window in the frame header) was read off the regulation.bin that
    /// Smithbox ships for 1.03.5 after decrypting it with that key.
    /// </summary>
    public static readonly FromSoftGameFacts EldenRingNightreign = new(
        GameId: "FromSoft.EldenRingNightreign",
        DisplayName: "Elden Ring Nightreign",
        SteamAppId: 2622380,
        SteamInstallFolder: "ELDEN RING NIGHTREIGN",
        NexusModsGameId: 7800,
        Me3GameName: "nightreign",
        Layout: new FromSoftGameLayout(2622380, "EldenRingNightreign", "Game", "nightreign.exe", "start_protected_game.exe", "Nightreign", SaveRootKind.RoamingAppData, SaveAccountFolderKind.SteamId64Decimal),
        SaveNames: new FromSoftSaveNames("NR0000.sl2", "NR0000_silver.sl2", null),
        Binder: BinderFormat.Bnd4,
        ReadableCompressions: [DcxCompression.Kraken, DcxCompression.Dflt, DcxCompression.Zstd],
        PackedWrite: new DcxWriteFormat(DcxCompression.Dflt, 0x11000, 9, 0),
        RegulationFileName: "regulation.bin",
        RegulationWrite: new DcxWriteFormat(DcxCompression.Zstd, 0x11000, 22, 27),
        RegulationKeyHex: "9A8EE90C4C01A43168A17D9D75E4A7D02107EBCF43D5ACB0554F941601B57918",
        ParamdefSet: "nightreign",
        Archives:
        [
            new("Data0", "cfc17d53", "d7f296c9", 0xb103b007),
            new("Data1", "d04e9db6", "1c416e51", 0xffffffff),
            new("Data2", "aa991ff7", "bd2ac75f", 0xd3a0e915),
            new("Data3", "c26d9173", "87d4dabf", 0x3aa28bb7),
        ],
        PathHashBits: 64,
        PathHashPrime: 133,
        ArchiveLayout: Bhd5Layout.Salted40ByteEntries,
        Definition: () => EldenRingNightreignGame.Definition);

    public static IEnumerable<object[]> All => [[ArmoredCore6], [EldenRingNightreign]];

    public override string ToString() => DisplayName;
}
