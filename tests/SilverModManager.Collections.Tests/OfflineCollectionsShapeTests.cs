using SilverModManager.Abstractions.Loadouts;
using NexusMods.Paths;
using System.IO.Compression;
using FluentAssertions;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using Xunit;

namespace SilverModManager.Collections.Tests;

/// <summary>The mod archive shapes the offline collections can build.</summary>
public class OfflineCollectionsShapeTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<OfflineCollectionsShapeTests>(helper)
{
    private OfflineCollections Offline => new(ServiceProvider, Connection, LibraryService, TemporaryFileManager);

    private static string Entry(AbsolutePath zipPath, string name)
    {
        using var zip = ZipFile.OpenRead(zipPath.ToString());
        var entry = zip.GetEntry(name);
        entry.Should().NotBeNull($"the archive should contain {name}");
        using var reader = new StreamReader(entry!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task Unknown_IsDeclinedByEveryInstallerOfTheGame()
    {
        var loadout = await CreateLoadout();
        var folder = TemporaryFileManager.CreateFolder();
        var (path, md5) = await Offline.CreateMod(folder.Path, 0, ModShape.Unknown);
        await Offline.AddDirectDownload(path, md5);
        var item = LibraryItem.All(Connection.Db).Single(i => i.Name == "mod0.zip");

        var parent = LoadoutItemGroupId.From(DefaultCollection.Of(Connection.Db, loadout.Id)?.Value ?? throw new InvalidOperationException("no collection"));

        foreach (var installer in Game.LibraryItemInstallers)
        {
            if (!installer.IsSupportedLibraryItem(item)) continue;

            using var tx = Connection.BeginTransaction();
            var group = new LoadoutItemGroup.New(tx, out var id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = "probe", LoadoutId = loadout.Id, ParentId = parent },
            };
            var result = await installer.ExecuteAsync(item, group, tx, loadout, CancellationToken.None);
            result.IsNotSupported(out _).Should().BeTrue($"{installer.GetType().Name} should decline an archive with only a readme and a guide");
        }
    }

    [Fact]
    public async Task FomodWithoutSteps_HasNoInstallSteps()
    {
        var folder = TemporaryFileManager.CreateFolder();
        var (path, _) = await Offline.CreateMod(folder.Path, 0, ModShape.FomodWithoutSteps);

        var config = Entry(path, "fomod/ModuleConfig.xml");

        config.Should().Contain("requiredInstallFiles").And.NotContain("installSteps");
    }
}
