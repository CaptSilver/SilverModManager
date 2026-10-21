using FluentAssertions;
using NexusMods.Games.Declarative.Capabilities;
using NexusMods.Games.Declarative.Schema;

namespace NexusMods.Games.Declarative.Tests;

public class SchemaTests
{
    [Fact]
    public void SampleDefinitionDeserialisesWithEveryFieldPopulated()
    {
        var definition = DefinitionSerializer.Read(Samples.Full);

        definition.Id.Should().Be("oblivion");
        definition.Name.Should().Be("The Elder Scrolls IV: Oblivion");
        definition.Nexus.Should().Be(new NexusDefinition { Domain = "oblivion", Id = 101 });
        definition.Stores.Steam.Should().Equal(22330u);
        definition.Stores.Gog.Should().Equal(1458058109L);
        definition.Stores.Epic.Should().Equal("abc123");
        definition.Stores.Amazon.Should().Equal("amzn1.adg.product.oblivion");
        definition.PrimaryExe.Should().Be("Oblivion.exe");
        definition.Locations.Keys.Should().BeEquivalentTo("Game", "AppData", "Preferences", "Saves");
        definition.Locations["Game"].Should().Be(new LocationDefinition { Root = "install" });
        definition.Locations["Saves"].Should().Be(new LocationDefinition { Root = "%MYGAMES%", Path = "Oblivion/Saves" });
        definition.Aliases.Should().Equal("Oblivion", "TES4");
        definition.Installer.Should().NotBeNull();
        definition.Installer!.DataMarkers.Should().Equal("meshes", "textures", "sound");
        definition.Installer.Target.Should().Be("Game/Data");
        definition.Installer.Fallback.Should().Be("Game");
        definition.Deployment.UsesVfs.Should().BeFalse();
        definition.Runtime.Proton.Should().Be(ProtonRequirement.Optional);
        definition.Capabilities.Keys.Should().Equal("creationEngine");
        definition.Tools.Should().Equal("Tools/Construction Set.exe");
    }

    [Fact]
    public void KeysTheSampleLeavesOutTakeTheirDefaults()
    {
        var definition = DefinitionSerializer.Read(Samples.Minimal("some-game", 1));

        definition.Nexus.Should().BeNull();
        definition.NativeLinux.Should().BeFalse();
        definition.Installer.Should().BeNull();
        definition.Deployment.UsesVfs.Should().BeTrue();
        definition.Runtime.Proton.Should().Be(ProtonRequirement.Required);
        definition.Capabilities.Should().BeEmpty();
    }

    [Fact]
    public void ACapabilityBlockBindsToItsConfigType()
    {
        var definition = DefinitionSerializer.Read(Samples.Full);

        var config = CapabilityConfig.Bind<SampleCapabilityConfig>(definition.Capabilities["creationEngine"]);

        config.DataFolder.Should().Be("Data");
        config.PluginsFile.Should().Be(new SamplePluginsFile { Location = "AppData", Path = "Plugins.txt", Format = "plain" });
        config.ScriptExtender!.Loader.Should().Be("obse_loader.exe");
        config.ScriptExtender.Files.Should().Equal(@"obse_loader\.exe", @"obse_1_2_416\.dll");
        config.StopPatterns.Should().Equal("(^|/)obse(/|$)");
    }

    [Fact]
    public void AnUnknownTopLevelKeyIsAnError()
    {
        var act = () => DefinitionSerializer.Read(Samples.Minimal("some-game", 1, extra: "nexsu: { domain: x, id: 1 }"));

        act.Should().Throw<DefinitionException>().WithMessage("*nexsu*");
    }

    [Fact]
    public void AnUnknownKeyInsideABlockIsAnError()
    {
        var act = () => DefinitionSerializer.Read(Samples.Minimal("some-game", 1, extra: "deployment: { usesVfss: true }"));

        act.Should().Throw<DefinitionException>().WithMessage("*usesVfss*");
    }

    [Fact]
    public void AnUnknownKeyInACapabilityBlockIsAnError()
    {
        var act = () => CapabilityConfig.Bind(new Dictionary<object, object> { ["dataFoldr"] = "Data" }, typeof(SampleCapabilityConfig));

        act.Should().Throw<DefinitionException>().WithMessage("*dataFoldr*");
    }

    [Fact]
    public void AnEmptyFileIsAnError()
    {
        var act = () => DefinitionSerializer.Read("");

        act.Should().Throw<DefinitionException>();
    }

    [Fact]
    public void WrittenDefinitionsReadBackTheSame()
    {
        var definition = DefinitionSerializer.Read(Samples.Full);

        var read = DefinitionSerializer.Read(DefinitionSerializer.Write(definition));

        read.Should().BeEquivalentTo(definition);
    }
}
