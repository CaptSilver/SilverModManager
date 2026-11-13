using FluentAssertions;
using SilverModManager.Games.Unity.Tests.Fixtures;

namespace SilverModManager.Games.Unity.Tests;

public class MelonLoaderModInstallerTests(ITestOutputHelper outputHelper) : AMelonLoaderInstallerTests<MelonLoaderModInstallerTests>(outputHelper)
{
    [Fact]
    public async Task ALooseDllGoesToMods()
    {
        var (result, targets) = await TryInstallMod(await AddFromPathsNamed("Someone-Foo-1.0.0.zip", "Foo.dll"));

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(OnGame("Mods/Foo.dll"));
    }

    [Fact]
    public async Task ALooseCfgGoesToUserData()
    {
        var (_, targets) = await TryInstallMod(await AddFromPathsNamed("Someone-Foo-1.0.0.zip", "Foo.dll", "Foo.cfg", "foo.json", "foo.ini"));

        targets.Should().BeEquivalentTo(OnGame("Mods/Foo.dll"), OnGame("UserData/Foo.cfg"), OnGame("UserData/foo.json"), OnGame("UserData/foo.ini"));
    }

    [Fact]
    public async Task AModsFolderKeepsItsSubtreeWhereverItSits()
    {
        var (_, targets) = await TryInstallMod(await AddFromPathsNamed("Someone-Foo-1.0.0.zip", "x/Mods/Foo.dll", "x/Mods/Sub/Bar.dll"));

        targets.Should().BeEquivalentTo(OnGame("Mods/Foo.dll"), OnGame("Mods/Sub/Bar.dll"));
    }

    [Fact]
    public async Task APluginsFolderKeepsItsSubtree()
    {
        var (_, targets) = await TryInstallMod(await AddFromPathsNamed("Someone-Foo-1.0.0.zip", "Plugins/Foo.dll"));

        targets.Should().BeEquivalentTo(OnGame("Plugins/Foo.dll"));
    }

    [Fact]
    public async Task UserLibsAndUserDataAreKept()
    {
        var (_, targets) = await TryInstallMod(await AddFromPathsNamed("Someone-Foo-1.0.0.zip", "Mods/Foo.dll", "UserLibs/Lib.dll", "UserData/Foo/a.cfg"));

        targets.Should().BeEquivalentTo(OnGame("Mods/Foo.dll"), OnGame("UserLibs/Lib.dll"), OnGame("UserData/Foo/a.cfg"));
    }

    [Fact]
    public async Task RootMetadataIsDropped()
    {
        var (_, targets) = await TryInstallMod(await AddFromPathsNamed("Someone-Foo-1.0.0.zip", "Foo.dll", "manifest.json", "README.md", "icon.png", "CHANGELOG.md"));

        targets.Should().BeEquivalentTo(OnGame("Mods/Foo.dll"));
    }

    [Fact]
    public async Task AnArchiveWithNothingToInstallIsNotSupported()
    {
        var (result, targets) = await TryInstallMod(await AddFromPathsNamed("Someone-Docs-1.0.0.zip", "manifest.json", "README.md", "icon.png"));

        result.IsNotSupported(out _).Should().BeTrue();
        targets.Should().BeEmpty();
    }

    [Fact]
    public async Task ThePackIsRefused()
    {
        var archive = await AddFromPathsNamed("Test-MelonLoader-0.7.1.zip", "version.dll", "MelonLoader/net6/MelonLoader.dll");

        var (result, targets) = await TryInstallMod(archive);

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Be("The archive is the Test-MelonLoader loader pack, which the loader pack installer installs, not a mod.");
        targets.Should().BeEmpty();
    }

    [Fact]
    public async Task TwoFilesToOneTargetAreRefused()
    {
        var archive = await AddFromPathsNamed("Someone-Foo-1.0.0.zip", "a/Foo.dll", "b/Foo.dll");

        var (result, _) = await TryInstallMod(archive);

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Contain("a/Foo.dll").And.Contain("b/Foo.dll").And.Contain("Mods/Foo.dll");
    }

    [Fact]
    public async Task AnArchiveWithBothBranchesInstallsOnlyTheInstallsBranch()
    {
        SetRuntime(UnityRuntime.Il2Cpp);

        var (_, targets) = await TryInstallMod(await AddFromPathsNamed("Someone-Foo-1.0.0.zip", "Foo.Il2cpp.dll", "Foo.Mono.dll"));

        targets.Should().BeEquivalentTo(OnGame("Mods/Foo.Il2cpp.dll"));
    }

    [Fact]
    public async Task AMonoInstallKeepsTheMonoBranch()
    {
        SetRuntime(UnityRuntime.Mono);

        var (_, targets) = await TryInstallMod(await AddFromPathsNamed("Someone-Foo-1.0.0.zip", "Foo.Il2cpp.dll", "Foo.Mono.dll"));

        targets.Should().BeEquivalentTo(OnGame("Mods/Foo.Mono.dll"));
    }

    [Fact]
    public async Task AnUnknownRuntimeKeepsBothBranches()
    {
        var (_, targets) = await TryInstallMod(await AddFromPathsNamed("Someone-Foo-1.0.0.zip", "Foo.Il2cpp.dll", "Foo.Mono.dll"));

        targets.Should().BeEquivalentTo(OnGame("Mods/Foo.Il2cpp.dll"), OnGame("Mods/Foo.Mono.dll"));
    }

    [Fact]
    public async Task ADllWithoutASiblingBranchIsKeptWhateverItsToken()
    {
        SetRuntime(UnityRuntime.Il2Cpp);

        var (_, targets) = await TryInstallMod(await AddFromPathsNamed("Someone-Foo-1.0.0.zip", "Foo.Mono.dll"));

        targets.Should().BeEquivalentTo(OnGame("Mods/Foo.Mono.dll"));
    }
}
