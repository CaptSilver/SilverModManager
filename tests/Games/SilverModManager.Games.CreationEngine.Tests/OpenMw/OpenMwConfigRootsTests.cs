using FluentAssertions;
using SilverModManager.Games.CreationEngine.OpenMw;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.OpenMw;

public class OpenMwConfigRootsTests
{
    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);

    private IReadOnlyList<OpenMwConfigRoot> Roots => new OpenMwConfigRoots(_fs).Roots;

    [Fact]
    public void TheNativeRootComesFirstAndNamesItsStore()
    {
        Roots[0].Store.Should().Be(GameStore.OpenMW);
        Roots[1].Store.Should().Be(GameStore.OpenMWFlatpak);
        Roots[0].ConfigFolder.Should().Be(_fs.GetKnownPath(KnownPath.XDG_CONFIG_HOME) / "openmw");
        Roots[0].UserDataFolder.Should().Be(_fs.GetKnownPath(KnownPath.XDG_DATA_HOME) / "openmw");
        Roots[1].ConfigFolder.Should().Be(_fs.GetKnownPath(KnownPath.HomeDirectory) / ".var/app/org.openmw.OpenMW/config/openmw");
        Roots[1].UserDataFolder.Should().Be(_fs.GetKnownPath(KnownPath.HomeDirectory) / ".var/app/org.openmw.OpenMW/data/openmw");
    }

    [Fact]
    public void CandidatesAreTheConfigFolders() =>
        ((IOpenMwConfigRoots)new OpenMwConfigRoots(_fs)).Candidates.Should().Equal(Roots.Select(root => root.ConfigFolder));

    [Fact]
    public void TheNativeProgramIsNullUntilOpenmwIsInstalled()
    {
        Roots[0].ResolveProgram().Should().BeNull();

        var program = _fs.FromUnsanitizedFullPath("/usr/bin/openmw");
        program.Parent.CreateDirectory();
        program.WriteAllTextAsync("").GetAwaiter().GetResult();

        Roots[0].ResolveProgram().Should().Be(program);
    }

    [Fact]
    public void ADebianLayoutIsFoundUnderUsrGames()
    {
        var program = _fs.FromUnsanitizedFullPath("/usr/games/openmw");
        program.Parent.CreateDirectory();
        program.WriteAllTextAsync("").GetAwaiter().GetResult();

        Roots[0].ResolveProgram().Should().Be(program);
    }

    [Theory]
    [InlineData("/var/lib/flatpak/app/org.openmw.OpenMW")]
    [InlineData("~/.local/share/flatpak/app/org.openmw.OpenMW")]
    public void TheFlatpakProgramIsFlatpakOnceTheAppIsInstalledSystemWideOrForTheUser(string appFolder)
    {
        Roots[1].ResolveProgram().Should().BeNull();

        var folder = appFolder.StartsWith('~')
            ? _fs.GetKnownPath(KnownPath.HomeDirectory) / appFolder[2..]
            : _fs.FromUnsanitizedFullPath(appFolder);
        folder.CreateDirectory();

        Roots[1].ResolveProgram().Should().Be(_fs.FromUnsanitizedFullPath("/usr/bin/flatpak"));
    }

    private AbsolutePath Touch(string path)
    {
        var file = _fs.FromUnsanitizedFullPath(path);
        file.Parent.CreateDirectory();
        file.WriteAllTextAsync("").GetAwaiter().GetResult();
        return file;
    }

    [Fact]
    public void TheNativeGlobalConfigIsNullWhenThereIsNone() =>
        Roots[0].GlobalConfig.Should().BeNull();

    [Fact]
    public void TheNativeGlobalConfigIsTheSystemOne()
    {
        var system = Touch("/etc/openmw/openmw.cfg");

        Roots[0].GlobalConfig.Should().Be(system);
    }

    [Fact]
    public void AConfigNextToTheNativeBinaryWinsOverTheSystemOne()
    {
        Touch("/usr/games/openmw");
        Touch("/etc/openmw/openmw.cfg");
        var local = Touch("/usr/games/openmw.cfg");

        Roots[0].GlobalConfig.Should().Be(local);
    }

    [Fact]
    public void TheFlatpakHasNoGlobalConfig()
    {
        Touch("/var/lib/flatpak/app/org.openmw.OpenMW/current/active/files/etc/openmw/openmw.cfg");

        Roots[1].GlobalConfig.Should().BeNull();
    }
}
