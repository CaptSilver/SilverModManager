using System.Text;
using FluentAssertions;
using SilverModManager.Games.LoadOrderFile.Manifests;

namespace SilverModManager.Games.LoadOrderFile.Tests.Manifests;

public class AboutXmlManifestReaderTests
{
    private static readonly AboutXmlManifestReader Reader = new();

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string About(string packageId, string extra = "")
        => $"<?xml version=\"1.0\" encoding=\"utf-8\"?><ModMetaData><name>Some Mod</name><packageId>{packageId}</packageId>{extra}</ModMetaData>";

    [Fact]
    public void TheManifestIsAboutAboutXml()
    {
        Reader.ManifestPath.ToString().Should().Be("About/About.xml");
    }

    [Fact]
    public void TheIdIsThePackageIdLowerCased()
    {
        Reader.Read(Bytes(About("Brrainz.Harmony")))!.Id.Should().Be("brrainz.harmony");
    }

    [Fact]
    public void DependenciesAndOrderListsAreReadLowerCased()
    {
        var manifest = Reader.Read(Bytes(About("a.b", """
            <modDependencies><li><packageId>Ludeon.RimWorld</packageId><displayName>Core</displayName></li></modDependencies>
            <loadBefore><li> Ludeon.RimWorld </li></loadBefore>
            <loadAfter><li>Some.Other</li></loadAfter>
            <forceLoadAfter><li>Force.After</li></forceLoadAfter>
            <forceLoadBefore><li>Force.Before</li></forceLoadBefore>
            <incompatibleWith><li>Bad.Mod</li></incompatibleWith>
            <loadAfterByVersion><v1.5><li>X.Y</li></v1.5></loadAfterByVersion>
            <modDependenciesByVersion><v1.5><li><packageId>Dep.One</packageId></li></v1.5></modDependenciesByVersion>
            """)))!;

        manifest.Dependencies.Should().BeEquivalentTo("ludeon.rimworld", "dep.one");
        manifest.LoadBefore.Should().Equal("ludeon.rimworld");
        manifest.LoadAfter.Should().BeEquivalentTo("some.other", "x.y");
        manifest.ForceLoadAfter.Should().Equal("force.after");
        manifest.ForceLoadBefore.Should().Equal("force.before");
        manifest.IncompatibleWith.Should().Equal("bad.mod");
    }

    [Fact]
    public void ABlankListEntryNamesNoMod()
    {
        var manifest = Reader.Read(Bytes(About("a.b", "<loadBefore><li> </li><li>X.Y</li></loadBefore><loadAfterByVersion><v1.5><li/></v1.5></loadAfterByVersion>")))!;

        manifest.LoadBefore.Should().Equal("x.y");
        manifest.LoadAfter.Should().BeEmpty();
    }

    [Fact]
    public void AByteOrderMarkIsAccepted()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Bytes(About("A.B"))).ToArray();

        Reader.Read(bytes)!.Id.Should().Be("a.b");
    }

    [Theory]
    [InlineData("not xml")]
    [InlineData("")]
    [InlineData("<ModMetaData/>")]
    [InlineData("<ModMetaData><packageId>  </packageId></ModMetaData>")]
    public void UnreadableOrIdlessNamesNothing(string text)
    {
        Reader.Read(Bytes(text)).Should().BeNull();
    }
}
