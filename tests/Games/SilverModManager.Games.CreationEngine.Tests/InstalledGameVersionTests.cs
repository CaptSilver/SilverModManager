using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>
/// The installed version of a Bethesda game comes from the version resource of its executable. On Linux the reader
/// the platform library offers has nothing to read in a native executable and answers 0.0.0.0, so these games need
/// the fixed file info read directly.
/// </summary>
public class InstalledGameVersionTests(IServiceProvider serviceProvider) : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("installed-game-version").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private T GameOf<T>() where T : IGame => serviceProvider.GetServices<IGame>().OfType<T>().Single();

    /// <summary>A game folder holding <paramref name="exe"/>, a native executable whose file version is the given high and low words.</summary>
    private GameInstallation InstallWith(string exe, uint fileVersionMs, uint fileVersionLs)
    {
        File.WriteAllBytes(Path.Combine(_folder, exe), NativeExecutable.WithFileVersion(fileVersionMs, fileVersionLs));

        var root = FileSystem.Shared.FromUnsanitizedFullPath(_folder);
        // Only Locations is read by the games under test.
        return new GameInstallation(null!, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, root)));
    }

    [Fact]
    public void SkyrimVRReadsItsVersionFromSkyrimVRExe()
    {
        var installation = InstallWith("SkyrimVR.exe", 0x0001_0004, 0x000F_0000);

        var version = ((IGameData)GameOf<CreationEngine.SkyrimVR.SkyrimVR>()).GetLocalVersion(installation);

        version.HasValue.Should().BeTrue();
        version.Value.Should().Be(new Version(1, 4, 15, 0));
    }

    [Fact]
    public void SkyrimSEReadsItsVersionFromSkyrimSEExe()
    {
        var installation = InstallWith("SkyrimSE.exe", 0x0001_0006, 0x0492_0000);

        var version = ((IGameData)GameOf<CreationEngine.SkyrimSE.SkyrimSE>()).GetLocalVersion(installation);

        version.HasValue.Should().BeTrue();
        version.Value.Should().Be(new Version(1, 6, 1170, 0));
    }

    [Fact]
    public void Fallout4VRReadsItsVersionFromFallout4VRExe()
    {
        var installation = InstallWith("Fallout4VR.exe", 0x0001_0002, 0x0001_0000);

        var version = ((IGameData)GameOf<CreationEngine.Fallout4VR.Fallout4VR>()).GetLocalVersion(installation);

        version.HasValue.Should().BeTrue();
        version.Value.Should().Be(new Version(1, 2, 1, 0));
    }

    [Fact]
    public void AGameFolderWithoutTheExecutableHasNoVersion()
    {
        var installation = InstallWith("SomethingElse.exe", 0x0001_0004, 0x000F_0000);

        ((IGameData)GameOf<CreationEngine.SkyrimVR.SkyrimVR>()).GetLocalVersion(installation).HasValue.Should().BeFalse();
    }
}
