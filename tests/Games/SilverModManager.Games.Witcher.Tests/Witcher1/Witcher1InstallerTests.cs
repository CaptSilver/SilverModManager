using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Games.Generic.Installers;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Witcher.Witcher1;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Witcher.Tests.Witcher1;

public class Witcher1InstallerTests(ITestOutputHelper outputHelper) : ALibraryArchiveInstallerTests<Witcher1InstallerTests, Witcher1Game>(outputHelper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddWitcherGames()
            .AddUniversalGameLocator<Witcher1Game>(new Version("1.5"), locationsFileSystem: IsolatedKnownFolders.Create);
    }

    public static readonly List<(string TestName, string[] Paths)> TestCases =
    [
        ("Loose override files", ["0000_my_tweak.2da", "readme.txt"]),
        ("Nested folder", ["MyMod/Override/dialog.tlk", "MyMod/screenshot.png"]),
        ("Archive already rooted at Data/Override", ["Data/Override/area.are", "Data/Override/sub/area2.are"]),
    ];

    public static IEnumerable<object[]> TestCaseData() => TestCases.Select(row => (object[])[row.TestName, row.Paths]);

    [Theory]
    [MemberData(nameof(TestCaseData))]
    public async Task CanInstallWitcherMods(string testCaseName, string[] archivePaths)
    {
        var loadout = await CreateLoadout();
        var archive = await AddFromPaths(archivePaths);
        var group = await Install(typeof(GenericPatternMatchInstaller), loadout, archive);

        await VerifyChildren(ChildrenFilesAndHashes(group)).UseParameters(testCaseName);
    }

    private static SettingsTask VerifyChildren(
        IEnumerable<(RelativePath FromPath, Sdk.Hashes.FileIdentity Identity, GamePath GamePath)> childrenFilesAndHashes,
        [CallerFilePath] string sourceFile = "")
    {
        return Verify(childrenFilesAndHashes.Select(row => new
            {
                FromPath = row.FromPath.ToString(),
                Hash = row.Identity.XxHash3().ToString(),
                ToGamePath = row.GamePath.ToString(),
            }),
            // ReSharper disable once ExplicitCallerInfoArgument
            sourceFile: sourceFile);
    }
}
