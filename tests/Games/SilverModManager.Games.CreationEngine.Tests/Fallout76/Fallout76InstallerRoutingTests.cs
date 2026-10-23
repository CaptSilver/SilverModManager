using FluentAssertions;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.Fallout76;

/// <summary>
/// Fallout 76 mods are almost always one .ba2, often inside a folder named after the mod. The game has no
/// script extender, so nothing is routed to the game root.
/// </summary>
public class Fallout76InstallerRoutingTests(ITestOutputHelper helper)
    : ABethesdaInstallerRoutingTest<Fallout76InstallerRoutingTests, CreationEngine.Fallout76.Fallout76>(helper)
{
    protected override Version GameVersion => new("1.7.0");

    [Fact]
    public async Task ALoneArchiveLandsInData() =>
        (await InstallLayout("Mod.ba2")).Should().Equal("Game/Data/Mod.ba2");

    [Fact]
    public async Task ALoneArchiveInAWrapperFolderIsSteppedInto() =>
        (await InstallLayout("Wrapper/Mod.ba2")).Should().Equal("Game/Data/Mod.ba2");

    [Fact]
    public async Task AnArchiveInDataLandsInData() =>
        (await InstallLayout("Data/Mod.ba2")).Should().Equal("Game/Data/Mod.ba2");

    [Fact]
    public async Task AFolderNamedAfterTheGameIsSteppedInto() =>
        (await InstallLayout("Fallout 76/Data/Mod.ba2")).Should().Equal("Game/Data/Mod.ba2");

    [Fact]
    public async Task ATextureFolderLandsInData() =>
        (await InstallLayout("textures/x.dds")).Should().Equal("Game/Data/textures/x.dds");

    [Fact]
    public async Task ADllIsNotTakenForAnEngineFile() =>
        (await InstallLayout("Wrapper/x.dll", "Wrapper/Mod.ba2")).Should().Equal("Game/Data/Mod.ba2", "Game/Data/x.dll");
}
