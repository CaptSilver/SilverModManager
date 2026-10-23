using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FromSoft.Installers;
using NexusMods.Paths;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Games.FromSoft.Tests.Fixtures;

/// <summary>Elden Ring with helpers to put archives into the library and baselines under the data root.</summary>
public abstract class AVanillaDumpTest<TTest>(ITestOutputHelper helper) : AModdedEldenRingTest<TTest>(helper)
{
    protected const string GameVersion = EldenRingInstall.RegulationVersion;
    protected const string OlderVersion = "11601000";
    protected const string NewerVersion = "99999999";

    protected static FromSoftFormatTraits Formats => EldenRingGame.Definition.Formats;

    protected AbsolutePath DataRoot => ServiceProvider.GetRequiredService<ISettingsManager>().Get<FromSoftSettings>().DataRoot.ToPath(ServiceProvider.GetRequiredService<IFileSystem>());

    protected static byte[] Regulation(string version, byte fill)
        => RegulationFixture.BuildFromEntries([(1, "a.param", Enumerable.Repeat(fill, 16).ToArray())], version, Formats);

    protected void KeepBaseline(string version, byte[] bytes)
    {
        var folder = DataRoot / "baselines";
        folder.CreateDirectory();
        File.WriteAllBytes((folder / $"regulation-{version}.bin").GetFullPath(), bytes);
    }

    /// <summary>Puts a zip with these members into the library without installing it.</summary>
    protected async Task<LibraryArchive.ReadOnly> AddArchive(string name, params (string Path, byte[] Content)[] files)
    {
        var path = TemporaryFileManager.CreateFolder().Path / (name + ".zip");
        await TestArchives.WriteEntries(path, files);
        return await RegisterLocalArchive(path);
    }

    protected async Task InstallPackage(LoadoutId loadout, string name, params (string Path, byte[] Content)[] files)
    {
        var archive = await AddArchive(name, files);
        var installer = Game.LibraryItemInstallers.OfType<Me3PackageInstaller>().Single();
        await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout, installer: installer);
    }
}
