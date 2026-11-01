using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Unity.BepInEx;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Unity.Tests.Fixtures;

/// <summary>Runs the loader pack installer on a test archive and reports where its files would land.</summary>
public abstract class AUnityInstallerTests<TTest>(ITestOutputHelper outputHelper) : ALibraryArchiveInstallerTests<TTest, TestUnityGame>(outputHelper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGame<TestUnityGame>()
            .AddUniversalGameLocator<TestUnityGame>(new Version("1.0"));
    }

    /// <summary>
    /// Like <c>AddFromPaths</c> but the zip keeps the given file name, so the library item's name is known. A path
    /// ending in a slash becomes an empty folder entry.
    /// </summary>
    protected Task<LibraryArchive.ReadOnly> AddFromPathsNamed(string archiveFileName, params string[] paths)
        => AddNamed(archiveFileName, null, paths);

    /// <summary>Like <see cref="AddFromPathsNamed"/> but the archive's root manifest.json holds <paramref name="manifestJson"/>.</summary>
    protected Task<LibraryArchive.ReadOnly> AddWithManifest(string archiveFileName, string manifestJson, params string[] paths)
        => AddNamed(archiveFileName, manifestJson, [.. paths, "manifest.json"]);

    private async Task<LibraryArchive.ReadOnly> AddNamed(string archiveFileName, string? manifestJson, string[] paths)
    {
        var archivePath = TemporaryFileManager.CreateFolder().Path / archiveFileName;
        await using (var archiveStream = archivePath.Create())
        {
            using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Create);
            foreach (var path in paths)
            {
                var entry = zip.CreateEntry(path);
                if (path.EndsWith('/')) continue;

                await using var stream = entry.Open();
                var content = path == "manifest.json" && manifestJson is not null ? manifestJson : path;
                await stream.WriteAsync(Encoding.UTF8.GetBytes(content));
            }
        }

        return await RegisterLocalArchive(archivePath);
    }

    protected BepInExPluginInstaller PluginInstaller => Game.LibraryItemInstallers.OfType<BepInExPluginInstaller>().Single();

    protected BepInExPackInstaller PackInstaller => Game.LibraryItemInstallers.OfType<BepInExPackInstaller>().Single();

    /// <summary>Runs the pack installer without committing when it refuses; the targets are "Location:path".</summary>
    protected Task<(InstallerResult Result, string[] Targets)> TryInstallPack(LibraryArchive.ReadOnly archive)
        => TryInstall(PackInstaller, archive);
}
