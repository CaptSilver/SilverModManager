using FluentAssertions;
using NexusMods.Games.Declarative.Capabilities;
using NexusMods.Games.Declarative.Schema;

namespace NexusMods.Games.Declarative.Tests;

public class DefinitionValidatorTests
{
    private static readonly DefinitionValidator Validator = new(new CapabilityRegistry([new SampleCapability()]));

    private static GameDefinition Valid() => DefinitionSerializer.Read(Samples.Full);

    private static IReadOnlyList<string> Problems(GameDefinition definition) => Validator.Validate(definition);

    [Fact]
    public void TheSampleDefinitionIsValid() => Problems(Valid()).Should().BeEmpty();

    [Theory]
    [InlineData("Oblivion")]
    [InlineData("tes iv")]
    [InlineData("tes_iv")]
    [InlineData("")]
    public void IdMustBeLowercaseLettersDigitsAndDashes(string id)
        => Problems(Valid() with { Id = id }).Should().ContainSingle().Which.Should().Contain("id");

    [Fact]
    public void LocationsMustDeclareGame()
    {
        var definition = Valid();
        definition.Locations.Remove("Game");

        Problems(definition).Should().Contain(problem => problem.Contains("locations.Game is required"));
    }

    [Fact]
    public void GameLocationMustBeRootedAtInstall()
    {
        var definition = Valid();
        definition.Locations["Game"] = new LocationDefinition { Root = "%MYGAMES%" };

        Problems(definition).Should().Contain(problem => problem.Contains("locations.Game must have root: install"));
    }

    [Theory]
    [InlineData("%HOME%")]
    [InlineData("/etc")]
    [InlineData("")]
    public void EveryLocationRootMustBeAKnownToken(string root)
    {
        var definition = Valid();
        definition.Locations["Elsewhere"] = new LocationDefinition { Root = root };

        Problems(definition).Should().ContainSingle().Which.Should().Contain("locations.Elsewhere").And.Contain("unknown root");
    }

    [Theory]
    [InlineData("%LOCALAPPDATA%")]
    [InlineData("%APPDATA%")]
    [InlineData("%MYGAMES%")]
    [InlineData("%DOCUMENTS%")]
    [InlineData("%SAVEDGAMES%")]
    [InlineData("install")]
    public void EveryDocumentedRootTokenIsAccepted(string root)
    {
        var definition = Valid();
        definition.Locations["Elsewhere"] = new LocationDefinition { Root = root, Path = "Sub" };

        Problems(definition).Should().BeEmpty();
    }

    [Fact]
    public void ALocationPathMayNotLeaveItsRoot()
    {
        var definition = Valid();
        definition.Locations["Elsewhere"] = new LocationDefinition { Root = "%MYGAMES%", Path = "../Other" };

        Problems(definition).Should().ContainSingle().Which.Should().Contain("locations.Elsewhere.path");
    }

    [Theory]
    [InlineData("/games/Oblivion.exe")]
    [InlineData("C:\\Oblivion.exe")]
    [InlineData("..\\Oblivion.exe")]
    [InlineData("bin/../../Oblivion.exe")]
    [InlineData("")]
    public void PrimaryExeMustBeRelativeWithoutParentSegments(string exe)
        => Problems(Valid() with { PrimaryExe = exe }).Should().ContainSingle().Which.Should().Contain("primaryExe");

    [Fact]
    public void PrimaryExeMayLiveInASubfolder() => Problems(Valid() with { PrimaryExe = "bin/x64/Game.exe" }).Should().BeEmpty();

    [Theory]
    [InlineData("Nope")]
    [InlineData("Nope/Data")]
    public void InstallerTargetMustNameADeclaredLocation(string target)
    {
        var definition = Valid() with { Installer = new InstallerDefinition { Target = target } };

        Problems(definition).Should().ContainSingle().Which.Should().Contain("installer.target").And.Contain("'Nope'");
    }

    [Fact]
    public void InstallerFallbackMustNameADeclaredLocation()
    {
        var definition = Valid() with { Installer = new InstallerDefinition { Target = "Game", Fallback = "Nope" } };

        Problems(definition).Should().ContainSingle().Which.Should().Contain("installer.fallback");
    }

    [Fact]
    public void ACapabilityNotInTheRegistryIsAnError()
    {
        var definition = Valid();
        definition.Capabilities["openmw"] = null;

        Problems(definition).Should().ContainSingle().Which.Should().Contain("capability 'openmw' is not registered").And.Contain("creationEngine");
    }

    [Fact]
    public void ACapabilityBlockThatDoesNotFitItsConfigTypeIsAnError()
    {
        var definition = Valid();
        definition.Capabilities["creationEngine"] = new Dictionary<object, object> { ["noSuchSetting"] = 1 };

        Problems(definition).Should().ContainSingle().Which.Should().Contain("capabilities.creationEngine").And.Contain("noSuchSetting");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NexusIdMustBePositive(long id)
        => Problems(Valid() with { Nexus = new NexusDefinition { Domain = "oblivion", Id = id } }).Should().ContainSingle().Which.Should().Contain("nexus.id");

    [Theory]
    [InlineData("Oblivion")]
    [InlineData("old-blion")]
    [InlineData("")]
    public void NexusDomainMustBeLowercaseLettersAndDigits(string domain)
        => Problems(Valid() with { Nexus = new NexusDefinition { Domain = domain, Id = 101 } }).Should().ContainSingle().Which.Should().Contain("nexus.domain");

    [Fact]
    public void NexusIsOptional() => Problems(Valid() with { Nexus = null }).Should().BeEmpty();

    [Fact]
    public void AGameWithNoStoreIdMustSayItIsManualOnly()
    {
        var definition = Valid() with { Stores = new StoreDefinitions() };

        Problems(definition).Should().ContainSingle().Which.Should().Contain("manualOnly");
        Problems(definition with { Stores = new StoreDefinitions { ManualOnly = true } }).Should().BeEmpty();
    }

    [Fact]
    public void ZeroIsNotASteamAppId()
        => Problems(Valid() with { Stores = new StoreDefinitions { Steam = [0u] } }).Should().ContainSingle().Which.Should().Contain("stores.steam");

    [Fact]
    public void ANameIsRequired() => Problems(Valid() with { Name = " " }).Should().ContainSingle().Which.Should().Contain("name");
}
