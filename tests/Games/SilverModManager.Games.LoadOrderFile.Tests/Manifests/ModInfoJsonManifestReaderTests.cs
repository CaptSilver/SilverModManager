using System.Text;
using FluentAssertions;
using SilverModManager.Games.LoadOrderFile.Manifests;

namespace SilverModManager.Games.LoadOrderFile.Tests.Manifests;

public class ModInfoJsonManifestReaderTests
{
    private static readonly ModInfoJsonManifestReader Reader = new();

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void TheManifestIsModInfoJson()
    {
        Reader.ManifestPath.ToString().Should().Be("mod_info.json");
    }

    [Fact]
    public void TheIdIsReadVerbatim()
    {
        Reader.Read(Bytes("""{"id":"LazyLib","name":"LazyLib"}"""))!.Should().Match<ModManifest>(m => m.Id == "LazyLib" && m.Name == "LazyLib");
    }

    [Fact]
    public void JavaStyleCommentsAndTrailingCommasAreAccepted()
    {
        Reader.Read(Bytes("// comment\n{\"id\":\"a\", \"version\":{\"major\":1},}"))!.Id.Should().Be("a");
    }

    [Fact]
    public void AByteOrderMarkIsAccepted()
    {
        Reader.Read(Encoding.UTF8.GetPreamble().Concat(Bytes("""{"id":"a"}""")).ToArray())!.Id.Should().Be("a");
    }

    [Fact]
    public void AVersionStringIsNotRead()
    {
        Reader.Read(Bytes("""{"id":"a","version":"1.2.3"}"""))!.Id.Should().Be("a");
    }

    [Fact]
    public void DependenciesAreReadById()
    {
        Reader.Read(Bytes("""{"id":"a","dependencies":[{"id":"lw_lazylib"},{"name":"no id"}]}"""))!.Dependencies.Should().Equal("lw_lazylib");
    }

    [Fact]
    public void NoOrderListsExist()
    {
        var manifest = Reader.Read(Bytes("""{"id":"a"}"""))!;

        manifest.LoadAfter.Should().BeEmpty();
        manifest.LoadBefore.Should().BeEmpty();
        manifest.ForceLoadAfter.Should().BeEmpty();
        manifest.ForceLoadBefore.Should().BeEmpty();
        manifest.IncompatibleWith.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("""{"id":"  "}""")]
    public void UnreadableOrIdlessNamesNothing(string text)
    {
        Reader.Read(Bytes(text)).Should().BeNull();
    }
}
