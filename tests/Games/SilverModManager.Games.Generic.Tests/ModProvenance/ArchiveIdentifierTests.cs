using System.Security.Cryptography;
using FluentAssertions;
using SilverModManager.Games.Generic.ModProvenance;

namespace SilverModManager.Games.Generic.Tests.ModProvenance;

public class ArchiveIdentifierTests : IDisposable
{
    private readonly Tree _tree = new();

    public void Dispose() => _tree.Dispose();

    private static string Md5(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));

    [Fact]
    public async Task OnlyArchivesAreHashedAndLooseGameFilesNever()
    {
        var zip = new byte[] { 1, 2, 3 };
        _tree.WriteBytes("dl/SkipTheIntro-421-4-0-1732388874.zip", zip);
        _tree.WriteBytes("dl/readme.txt", [9, 9]);
        _tree.WriteBytes("Game/mods/Loose/Loose.dll", [7, 7, 7]);
        var catalog = new FakeArchiveCatalog();
        catalog.Hits[Md5(zip)] = new ArchiveHit(Md5(zip), "SkipTheIntro-421-4-0-1732388874.zip", 3333, 421, 32781);

        var found = await ArchiveIdentifier.FindAsync(catalog, 3333, [_tree.Path_("dl")], default);

        catalog.Looked.Should().ContainSingle().Which.Should().Equal(Md5(zip));
        found.Matches.Should().ContainSingle().Which.Hit.FileId.Should().Be(32781);
        found.Problems.Should().BeEmpty();
    }

    [Fact]
    public async Task AHitForAnotherGameIsDropped()
    {
        var zip = new byte[] { 1, 2, 3 };
        _tree.WriteBytes("dl/a.zip", zip);
        var catalog = new FakeArchiveCatalog();
        catalog.Hits[Md5(zip)] = new ArchiveHit(Md5(zip), "a.zip", 1111, 1, 2);

        var found = await ArchiveIdentifier.FindAsync(catalog, 3333, [_tree.Path_("dl")], default);

        found.Matches.Should().BeEmpty();
    }

    [Fact]
    public async Task ArchivesInSubfoldersAreFoundAndAFolderThatIsMissingIsSkipped()
    {
        var zip = new byte[] { 4, 5 };
        _tree.WriteBytes("dl/eldenring/b.7z", zip);
        var catalog = new FakeArchiveCatalog();
        catalog.Hits[Md5(zip)] = new ArchiveHit(Md5(zip), "b.7z", 3333, 1, 2);

        var found = await ArchiveIdentifier.FindAsync(catalog, 3333, [_tree.Path_("dl"), _tree.Path_("nope")], default);

        found.Matches.Should().ContainSingle();
    }

    [Fact]
    public async Task NoArchivesMeansNoLookup()
    {
        Directory.CreateDirectory(_tree.Path_("dl"));
        var catalog = new FakeArchiveCatalog();

        var found = await ArchiveIdentifier.FindAsync(catalog, 3333, [_tree.Path_("dl")], default);

        found.Matches.Should().BeEmpty();
        catalog.Looked.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedLookupIsReportedNotThrown()
    {
        _tree.WriteBytes("dl/a.zip", [1]);
        var catalog = new FakeArchiveCatalog { Fail = true };

        var found = await ArchiveIdentifier.FindAsync(catalog, 3333, [_tree.Path_("dl")], default);

        found.Matches.Should().BeEmpty();
        found.Problems.Should().ContainSingle().Which.Should().Contain("boom");
    }

    [Fact]
    public async Task CancellationIsNotSwallowed()
    {
        _tree.WriteBytes("dl/a.zip", [1]);
        var catalog = new FakeArchiveCatalog { Cancel = true };

        var act = () => ArchiveIdentifier.FindAsync(catalog, 3333, [_tree.Path_("dl")], default);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ArchivesNamedLikeAWantedModAreHashedFirstAndTheByteBudgetStopsTheRest()
    {
        var small = new byte[] { 1, 2, 3 };
        _tree.WriteBytes("dl/Aaa-Big-1-1-0-1700000000.zip", new byte[100]);
        _tree.WriteBytes("dl/SkipTheIntro-421-4-0-1732388874.zip", small);
        var catalog = new FakeArchiveCatalog();

        var found = await ArchiveIdentifier.FindAsync(catalog, 3333, [_tree.Path_("dl")], default, wantedMods: ["Skip The Intro"], byteBudget: 50);

        catalog.Looked.Should().ContainSingle().Which.Should().Equal(Md5(small));
        found.Problems.Should().ContainSingle().Which.Should().Contain("1 archive").And.Contain("not checked");
    }

    [Theory]
    [InlineData("SkipTheIntro-421-4-0-1732388874.zip", "SkipTheIntro")]
    [InlineData("Adjust The FOV 325 2.0 2022-05-29T12-49Z abc123.zip", "Adjust The FOV")]
    [InlineData("NoWeight.zip", "NoWeight")]
    [InlineData("Mod-Name-With-Dashes-77-1-0-1700000000.7z", "Mod-Name-With-Dashes")]
    public void TheNamePartOfAnArchiveIsWhatPrecedesItsModId(string file, string name)
    {
        ArchiveIdentifier.NamePart(file).Should().Be(name);
    }
}
