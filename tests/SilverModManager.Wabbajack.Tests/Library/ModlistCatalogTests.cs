using FluentAssertions;
using SilverModManager.Abstractions.Library;
using NexusMods.Paths;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Tests.Fixtures;

namespace SilverModManager.Wabbajack.Tests.Library;

public class ModlistCatalogTests(ILibraryService libraryService, IModlistCatalog catalog, TemporaryFileManager temporaryFileManager)
{
    private async Task<LibraryFileId> Add(string name, string fileName, string version = "1.0.0.0")
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var list = WabbajackFixture.EmptyList(name) with { Version = version };
        var path = WabbajackFixture.Write(folder.Path, list, fileName: fileName);
        var added = await libraryService.AddLocalFile(path, UnassignedAcquisition.Instance);
        return LibraryFileId.From(added.Id);
    }

    [Fact]
    public async Task ListIncludesEachModlistWithItsNameVersionAndFileName()
    {
        var id = await Add("Catalog list one", "catalog-one.wabbajack", "3.1");

        catalog.List().Should().ContainSingle(m => m.Id == id)
            .Which.Should().Match<ModlistInfo>(m => m.Name == "Catalog list one" && m.Version == "3.1" && m.FileName == "catalog-one.wabbajack");
    }

    [Fact]
    public async Task FindMatchesTheNameWithoutCaringAboutCase()
    {
        var id = await Add("Catalog Named List", "named.wabbajack");

        catalog.Find("catalog named list").Id.Should().Be(id);
    }

    [Fact]
    public async Task FindMatchesTheHexIdAsTheListingPrintsIt()
    {
        var id = await Add("Catalog hex list", "hex.wabbajack");

        catalog.Find(id.Value.Value.ToString("X")).Id.Should().Be(id);
        catalog.Find("EId:" + id.Value.Value.ToString("X")).Id.Should().Be(id);
    }

    [Fact]
    public async Task FindMatchesAPathByItsFileName()
    {
        var id = await Add("Catalog path list", "from-disk-catalog.wabbajack");

        catalog.Find("/some/folder/From-Disk-Catalog.wabbajack").Id.Should().Be(id);
        catalog.Find("from-disk-catalog.wabbajack").Id.Should().Be(id);
    }

    [Fact]
    public void FindRefusesAModlistTheAppDoesNotKnow()
    {
        var act = () => catalog.Find("No such list anywhere");

        act.Should().Throw<ModlistNotKnownException>().Which.Message.Should().Contain("No such list anywhere").And.Contain("wabbajack list");
    }

    [Fact]
    public async Task FindRefusesAnAmbiguousNameAndNamesTheCandidates()
    {
        var first = await Add("Catalog twin", "twin-a.wabbajack", "1.0");
        var second = await Add("Catalog twin", "twin-b.wabbajack", "2.0");

        var act = () => catalog.Find("Catalog twin");

        var message = act.Should().Throw<ModlistNotKnownException>().Which.Message;
        message.Should().Contain("1.0").And.Contain("2.0").And.Contain(first.Value.Value.ToString("X")).And.Contain(second.Value.Value.ToString("X"));
        catalog.Find(second.Value.Value.ToString("X")).Version.Should().Be("2.0");
    }
}
