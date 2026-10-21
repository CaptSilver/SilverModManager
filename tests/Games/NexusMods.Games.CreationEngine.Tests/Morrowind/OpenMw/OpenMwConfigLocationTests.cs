using FluentAssertions;
using NexusMods.Games.CreationEngine.Morrowind.OpenMw;
using NexusMods.Paths;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.Morrowind.OpenMw;

public class OpenMwConfigLocationTests
{
    private sealed class FakeRoots(params AbsolutePath[] candidates) : IOpenMwConfigRoots
    {
        public IReadOnlyList<AbsolutePath> Candidates { get; } = candidates;
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
    public async Task BothRootsHoldingAConfigIsReported()
    {
        await WithConfig(First);
        await WithConfig(Second);
        Locate().Should().Be(new OpenMwConfigLocation.Both(First, Second));
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
