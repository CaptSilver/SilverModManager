using SilverModManager.Games.TestFramework.Helpers;

namespace SilverModManager.Games.FromSoft.Tests.Fixtures;

/// <summary>Test Souls as Steam installs it: a <c>Bin</c> folder with the executable and archives, and no anti-cheat launcher.</summary>
internal static class TestSoulsInstall
{
    public const uint AppId = 424242;
    public const uint DepotId = 424243;
    public const ulong ManifestId = 8000000000000000042;
    public const ulong SteamBuildId = 16000000;

    public static readonly Version ExecutableVersion = new(1, 2, 3, 4);

    public static byte[] Executable { get; } = FakePe.WithVersion(ExecutableVersion);

    public static SteamFile[] GameFiles { get; } =
    [
        new("Bin/testsouls.exe", Executable),
        new("Bin/regulation.bin", "regulation"),
        new("Bin/Data0.bdt", "data archive"),
        new("Bin/sd/sd.bdt", "sound archive"),
    ];

    public static SteamInstall Install(SteamLibraryFixture library)
        => library.AddGame(AppId, "TEST SOULS", "TEST SOULS", SteamBuildId, new SteamDepot(DepotId, ManifestId, GameFiles));
}
