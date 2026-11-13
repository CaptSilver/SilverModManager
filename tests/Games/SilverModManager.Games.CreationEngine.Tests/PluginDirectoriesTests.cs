using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests;

public class PluginDirectoriesTests(IServiceProvider serviceProvider) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("plugin-directories").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private CreationEngine.SkyrimSE.SkyrimSE Skyrim => serviceProvider.GetServices<IGame>().OfType<CreationEngine.SkyrimSE.SkyrimSE>().Single();

    private GameInstallation InstallationAt(string folder) => new(null!, GameLocations.Create(
        ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, FileSystem.Shared.FromUnsanitizedFullPath(folder))));

    [Fact]
    public void SkyrimHasExactlyItsDataFolder()
    {
        var installation = InstallationAt(_root);

        Skyrim.PluginDirectories(installation).Should().Equal(installation.Locations.ToAbsolutePath(Skyrim.DataPath));
    }

    [Fact]
    public void AnEarlyPluginIsInstalledWhenItIsInAnyOfTheDirectories()
    {
        var elsewhere = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        File.WriteAllBytes(Path.Combine(elsewhere, "Skyrim.esm"), []);
        var game = new TwoDirectorySkyrim(serviceProvider, FileSystem.Shared.FromUnsanitizedFullPath(elsewhere));

        game.InstalledEarlyLoadingPlugins(InstallationAt(_root)).Should().Contain("Skyrim.esm");
    }

    [Fact]
    public void AnEarlyPluginInNoDirectoryIsNotInstalled()
    {
        var game = new TwoDirectorySkyrim(serviceProvider, FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_root, "elsewhere")));

        game.InstalledEarlyLoadingPlugins(InstallationAt(_root)).Should().NotContain("Skyrim.esm");
    }

    /// <summary>A Skyrim whose plugins live in one more folder than its Data folder.</summary>
    private sealed class TwoDirectorySkyrim(IServiceProvider provider, AbsolutePath extra) : CreationEngine.SkyrimSE.SkyrimSE(provider)
    {
        public override IReadOnlyList<AbsolutePath> PluginDirectories(GameInstallation installation) =>
            [.. base.PluginDirectories(installation), extra];
    }
}
