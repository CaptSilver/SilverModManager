using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Generic.Installers;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Witcher.Witcher2;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Witcher.Tests.Witcher2;

public class Witcher2InstallerTests(ITestOutputHelper outputHelper) : ALibraryArchiveInstallerTests<Witcher2InstallerTests, Witcher2Game>(outputHelper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddWitcherGames()
            .AddUniversalGameLocator<Witcher2Game>(new Version("3.5"), locationsFileSystem: IsolatedKnownFolders.Create);
    }

    public static readonly List<(string TestName, Type InstallerType, string[] Paths)> TestCases =
    [
        ("UserContent mod", typeof(PredicateBasedInstaller), ["cook.hash", "pack0.dzip", "strings.w2strings"]),
        ("UserContent mod in a folder", typeof(PredicateBasedInstaller), ["MyMod/cook.hash", "MyMod/pack0.dzip"]),
        ("CookedPC mod", typeof(GenericPatternMatchInstaller), ["pack0.dzip", "readme.txt"]),
        ("CookedPC rooted archive", typeof(GenericPatternMatchInstaller), ["CookedPC/pack0.dzip", "CookedPC/en0.w2speech"]),
    ];

    public static IEnumerable<object[]> TestCaseData() => TestCases.Select(row => (object[])[row.TestName, row.InstallerType, row.Paths]);

    [Theory]
    [MemberData(nameof(TestCaseData))]
    public async Task CanInstallWitcher2Mods(string testCaseName, Type installerType, string[] archivePaths)
    {
        var loadout = await CreateLoadout();
        var archive = await AddFromPaths(archivePaths);
        var group = await Install(installerType, loadout, archive);

        await VerifyChildren(ChildrenFilesAndHashes(group)).UseParameters(testCaseName);
    }

    [Fact]
    public async Task CookHash_GoesToUserContentEvenWhenTheGenericInstallerWouldMatch()
    {
        var loadout = await CreateLoadout();
        var archive = await AddFromPaths("cook.hash", "pack0.dzip");

        // The install job asks the game's installers in order and the first one that supports the archive wins.
        var result = await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId);

        var targets = result.LoadoutItemGroup!.Value.Children.OfTypeLoadoutItemWithTargetPath()
            .Select(f => ((GamePath)f.TargetPath).ToString())
            .Order(StringComparer.Ordinal)
            .ToArray();
        targets.Should().Equal("{Game}/UserContent/cook.hash", "{Game}/UserContent/pack0.dzip");
    }

    [Fact]
    public async Task UnknownLayout_IsNotSupportedByEveryInstaller()
    {
        var loadout = await CreateLoadout();
        var archive = await AddFromPaths("random.bin", "notes/other.xyz");
        var libraryItem = archive.AsLibraryFile().AsLibraryItem();

        Game.LibraryItemInstallers.Should().NotBeEmpty();
        foreach (var installer in Game.LibraryItemInstallers)
        {
            using var tx = Connection.BeginTransaction();
            var group = new LoadoutItemGroup.New(tx, out var groupId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = libraryItem.Name, LoadoutId = loadout },
            };

            var result = await installer.ExecuteAsync(libraryItem, group, tx, loadout, CancellationToken.None);

            result.IsNotSupported(out _).Should().BeTrue(because: installer.GetType().Name);
        }
    }

    private static SettingsTask VerifyChildren(
        IEnumerable<(RelativePath FromPath, Hash Hash, GamePath GamePath)> childrenFilesAndHashes,
        [CallerFilePath] string sourceFile = "")
    {
        return Verify(childrenFilesAndHashes.Select(row => new
            {
                FromPath = row.FromPath.ToString(),
                Hash = row.Hash.ToString(),
                ToGamePath = row.GamePath.ToString(),
            }),
            // ReSharper disable once ExplicitCallerInfoArgument
            sourceFile: sourceFile);
    }
}
