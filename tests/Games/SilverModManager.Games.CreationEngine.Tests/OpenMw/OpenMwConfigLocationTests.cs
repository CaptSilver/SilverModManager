using FluentAssertions;
using SilverModManager.Games.CreationEngine.OpenMw;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.OpenMw;

public class OpenMwConfigLocationTests
{
    private sealed class FakeRoots(params AbsolutePath[] candidates) : IOpenMwConfigRoots
    {
        public IReadOnlyList<OpenMwConfigRoot> Roots { get; } = candidates
            .Select(folder => new OpenMwConfigRoot(folder, folder, GameStore.OpenMW, () => null))
            .ToList();
    }

    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);
    private AbsolutePath First => _fs.FromUnsanitizedFullPath("/cfg/openmw");
    private AbsolutePath Second => _fs.FromUnsanitizedFullPath("/flatpak/openmw");

    private async Task WithConfig(AbsolutePath root)
    {
        root.CreateDirectory();
        await (root / "openmw.cfg").WriteAllTextAsync("content=Morrowind.esm\n");
    }

    private OpenMwConfigLocation Locate() => OpenMwConfigLocation.Locate(new FakeRoots(First, Second), _fs);

    [Fact]
    public async Task OnlyTheFirstRootHoldingAConfigIsFound()
    {
        await WithConfig(First);
        Locate().Should().Be(new OpenMwConfigLocation.One(First));
    }

    [Fact]
    public async Task OnlyTheSecondRootHoldingAConfigIsFound()
    {
        await WithConfig(Second);
        Locate().Should().Be(new OpenMwConfigLocation.One(Second));
    }

    [Fact]
    public async Task WithAConfigInBothRootsTheNativeOneIsTheLocation()
    {
        await WithConfig(First);
        await WithConfig(Second);
        Locate().Should().Be(new OpenMwConfigLocation.One(First));
    }

    [Fact]
    public void NoConfigAnywhereFindsNothing() => Locate().Should().Be(new OpenMwConfigLocation.None());

    [Fact]
    public async Task ARootFolderWithoutAConfigDoesNotCount()
    {
        First.CreateDirectory();
        await (First / "settings.cfg").WriteAllTextAsync("x\n");
        await WithConfig(Second);

        Locate().Should().Be(new OpenMwConfigLocation.One(Second));
    }
}
