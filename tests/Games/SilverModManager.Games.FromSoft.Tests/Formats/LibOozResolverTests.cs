using FluentAssertions;
using SilverModManager.Games.FromSoft.Formats.Native;

namespace SilverModManager.Games.FromSoft.Tests.Formats;

public class LibOozResolverTests
{
    [Fact]
    public void ASingleFileApp_FindsTheLibraryUnderTheAppsBaseDirectory()
    {
        // A single-file publish reports an empty Assembly.Location; the library is unpacked beside the executable.
        var candidates = LibOozResolver.CandidatePaths(assemblyLocation: "", baseDirectory: "/tmp/.mount_app/usr/bin/");

        candidates.Should().Equal(Path.Combine("/tmp/.mount_app/usr/bin/", LibOozResolver.RelativePath));
    }

    [Fact]
    public void AnAssemblyOnDisk_IsTriedBeforeTheBaseDirectory()
    {
        var candidates = LibOozResolver.CandidatePaths(assemblyLocation: "/opt/smm/plugins/SilverModManager.Games.FromSoft.dll", baseDirectory: "/opt/smm/");

        candidates.Should().Equal(
            Path.Combine("/opt/smm/plugins", LibOozResolver.RelativePath),
            Path.Combine("/opt/smm/", LibOozResolver.RelativePath));
    }

    [Fact]
    public void TheSameFolderIsOnlyTriedOnce()
    {
        var candidates = LibOozResolver.CandidatePaths(assemblyLocation: "/opt/smm/SilverModManager.Games.FromSoft.dll", baseDirectory: "/opt/smm/");

        candidates.Should().Equal(Path.Combine("/opt/smm", LibOozResolver.RelativePath));
    }
}
