using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk.Library;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Unity.Tests.Fixtures;

/// <summary>Puts test archives, named like Thunderstore downloads, into the library for a Unity game's installers.</summary>
public abstract class AUnityLoaderInstallerTests<TTest, TGame>(ITestOutputHelper outputHelper) : ALibraryArchiveInstallerTests<TTest, TGame>(outputHelper)
    where TGame : class, IGame, IGameData<TGame>
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGame<TGame>()
            .AddUniversalGameLocator<TGame>(new Version("1.0"));
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
}
