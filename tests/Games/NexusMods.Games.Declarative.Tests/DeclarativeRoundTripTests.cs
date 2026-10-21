using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.Declarative.Schema;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using System.IO.Compression;

namespace NexusMods.Games.Declarative.Tests;

/// <summary>
/// Every bundled definition takes a synthetic mod through install, apply, disable and apply again, on the
/// default deployment plan and on copy, and the game's own files survive all of it.
/// </summary>
public class DeclarativeRoundTripTests(IServiceProvider serviceProvider)
{
    public static IEnumerable<object[]> Cases() => RoundTripDefinitions.Cases();

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ASyntheticModAppliesAndIsRemoved(string definitionId, string plan)
    {
        var definition = RoundTripDefinitions.All().Single(d => d.Id == definitionId);
        if (definition.Nexus is null)
            Assert.Skip($"'{definitionId}' has no Nexus block, and loadouts of a game without a Nexus id need game ids to key the registry");

        await new RoundTrip(serviceProvider, definition, plan).Run();
    }

    private sealed class RoundTrip(IServiceProvider provider, GameDefinition definition, string plan)
        : ADeclarativeGameTest(provider, definition.Id, installation => installation.LocatorResult.Path.ToString().Contains(RoundTripDefinitions.FolderName(definition.Id, plan)))
    {
        private static readonly GamePath GameRoot = new(LocationId.Game, RelativePath.Empty);

        public async Task Run()
        {
            var copy = plan == "copy";
            var vanilla = RoundTripDefinitions.VanillaFiles(definition);
            var expected = ExpectedFiles();
            expected.Should().NotBeEmpty($"'{definition.Id}' needs data markers or a creation engine for the round trip to install anything");
            var loadout = await CreateLoadout();

            if (copy)
                await ServiceProvider.GetRequiredService<ISynchronizerService>().ChangeDeploymentBackend(GameInstallation, GameRoot, DeploymentBackendId.Copy, chosen: true);

            var group = await Install(loadout);
            loadout = await Synchronizer.Synchronize(Refresh(loadout));

            var metadataId = GameRegistry.ForceGetMetadata(GameInstallation).Id;
            // BackendFor falls back to copy for an uncovered path, so read the game root's own entry: only it
            // shows whether copy was chosen or merely defaulted to.
            var plans = ServiceProvider.GetRequiredService<IDeploymentPlanStore>().Load(Connection.Db, metadataId);
            var gameRoot = plans.Roots.Should().ContainSingle(root => root.Root == GameRoot).Subject;
            gameRoot.Chosen.Should().Be(copy, "only the copy case picks a backend by hand");
            if (copy) plans.BackendFor(GameRoot).Should().Be(DeploymentBackendId.Copy);

            foreach (var (path, content) in expected) (await Read(path)).Should().Be(content, $"{path} is applied");
            await AssertVanillaIntact(vanilla);
            await AssertPluginsFile();

            using (var tx = Connection.BeginTransaction())
            {
                tx.Add(group.Id, LoadoutItem.Disabled, Null.Instance);
                await tx.Commit();
            }

            await Synchronizer.Synchronize(Refresh(loadout));

            foreach (var (path, _) in expected) File(path).FileExists.Should().BeFalse($"{path} is gone once its group is disabled");
            await AssertVanillaIntact(vanilla);
        }

        private async Task<LoadoutItemGroup.ReadOnly> Install(Loadout.ReadOnly loadout)
        {
            var library = ServiceProvider.GetRequiredService<ILibraryService>();
            await using var archive = await BuildArchive();
            var libraryFile = await library.AddLocalFile(archive.Path);
            var installed = await LoadoutManager.InstallItem(libraryFile.AsLibraryFile().AsLibraryItem(), loadout);
            return installed.LoadoutItemGroup ?? throw new InvalidOperationException($"Installing into '{definition.Id}' made no group");
        }

        /// <summary>
        /// The archive's files and where each lands, each file's content being its archive path. A game with a
        /// creation engine gets a Data folder holding a plugin and a texture; any other game gets one file under
        /// each data marker.
        /// </summary>
        private (string ArchivePath, GamePath Target)[] ArchiveFiles()
        {
            if (CreationEngine is { } engine)
            {
                var dataFolder = engine.GetValueOrDefault("dataFolder")?.ToString() ?? "Data";
                string[] paths = [$"{dataFolder}/RoundTrip.esp", $"{dataFolder}/textures/roundtrip.dds"];
                return paths.Select(path => (path, new GamePath(LocationId.Game, path))).ToArray();
            }

            var target = ParseTarget(definition.Installer?.Target ?? "Game");
            return (definition.Installer?.DataMarkers ?? [])
                .Select(marker => ($"{marker}/roundtrip.dat", new GamePath(target.LocationId, target.Path.Join($"{marker}/roundtrip.dat"))))
                .ToArray();
        }

        private Dictionary<GamePath, string> ExpectedFiles() => ArchiveFiles().ToDictionary(file => file.Target, file => file.ArchivePath);

        private async Task<TemporaryPath> BuildArchive()
        {
            var file = TemporaryFileManager.CreateFile();
            await using (var stream = file.Path.Create())
            {
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
                foreach (var (archivePath, _) in ArchiveFiles())
                {
                    await using var entry = zip.CreateEntry(archivePath).Open();
                    await entry.WriteAsync(Encoding.UTF8.GetBytes(archivePath));
                }
            }

            return file;
        }

        private static GamePath ParseTarget(string target)
        {
            var parts = target.Split('/', 2);
            return new GamePath(LocationId.From(parts[0]), parts.Length > 1 ? parts[1] : "");
        }

        private AbsolutePath File(GamePath path) => GameInstallation.Locations.ToAbsolutePath(path);

        private async Task<string> Read(GamePath path) => File(path).FileExists ? await File(path).ReadAllTextAsync() : "<missing>";

        private async Task AssertVanillaIntact(Dictionary<RelativePath, byte[]> vanilla)
        {
            foreach (var (path, content) in vanilla)
            {
                var file = File(new GamePath(LocationId.Game, path));
                file.FileExists.Should().BeTrue($"the game's own {path} is never removed");
                (await file.ReadAllBytesAsync()).Should().Equal(content, $"the game's own {path} is never changed");
            }
        }

        private Dictionary<object, object?>? CreationEngine
            => definition.Capabilities.GetValueOrDefault("creationEngine") as Dictionary<object, object?>;

        private string? PluginParser => CreationEngine?.GetValueOrDefault("parser")?.ToString();

        private AbsolutePath PluginsFile()
        {
            var location = CreationEngine?.GetValueOrDefault("pluginsFile") as Dictionary<object, object?>;
            return File(new GamePath(
                LocationId.From(location?.GetValueOrDefault("location")?.ToString() ?? "AppData"),
                location?.GetValueOrDefault("path")?.ToString() ?? "Plugins.txt"));
        }

        /// <summary>
        /// A game whose parser is none has no plugin list to write; one that reads plugins like Skyrim Special
        /// Edition lists the mod's plugin with the '*' enabled marker.
        /// </summary>
        private async Task AssertPluginsFile()
        {
            switch (PluginParser)
            {
                case "none":
                    PluginsFile().FileExists.Should().BeFalse("a game whose parser is none has no plugin list");
                    break;
                case "skyrimSE":
                    PluginsFile().FileExists.Should().BeTrue("a game that reads plugins writes its plugin list");
                    (await PluginsFile().ReadAllTextAsync()).Should().Contain("*RoundTrip.esp");
                    break;
            }
        }
    }
}
