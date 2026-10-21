using NexusMods.Games.FromSoft.Saves;

namespace NexusMods.Games.FromSoft.Tests.Fixtures;

/// <summary>
/// A made-up FromSoft game whose every name differs from Elden Ring's, so a test that passes with it cannot be
/// passing because a name was hard-coded.
/// </summary>
public static class TestSoulsDefinition
{
    public static readonly FromSoftGameLayout Layout = new(
        SteamAppId: 424242,
        DataFolderName: "TestSouls",
        GameSubfolder: "Bin",
        GameExecutable: "testsouls.exe",
        Launcher: null,
        SaveFolderName: "TestSouls",
        SaveRoot: SaveRootKind.RoamingAppData,
        AccountFolder: SaveAccountFolderKind.SteamId64Decimal);

    public static readonly FromSoftGameDefinition Definition = new()
    {
        Layout = Layout,
        SaveNames = new FromSoftSaveNames("TS0000.sl2", "TS0000_silver.sl2", null),
    };
}
