using SilverModManager.Games.FromSoft.ArmoredCore6;
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

    public static IEnumerable<object[]> All => [[ArmoredCore6]];

    public override string ToString() => DisplayName;
}
