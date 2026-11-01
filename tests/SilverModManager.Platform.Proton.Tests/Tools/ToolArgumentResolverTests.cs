using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Games.Generic;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Games.TestFramework;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Platform.Proton.Tests.Tools;

/// <summary>
/// A tool's arguments are written with placeholders and filled in from the loadout. The game supplies the paths it
/// alone knows, the resolver adds the loadout's, and what comes out is host paths: translating them for Wine is the
/// tool runner's job.
/// </summary>
public class ToolArgumentResolverTests(ITestOutputHelper helper)
    : AIsolatedDefinedGameTest<ToolArgumentResolverTests>(helper, Definition.GameId)
{
    // A folder with a space in it: a path substituted before the split would fall apart into two words.
    private static readonly SimpleGameDefinition Definition = new()
    {
        GameId = GameId.From("Tests.ToolVariables"),
        DisplayName = "Tool Variables",
        NexusModsGameId = Optional<Sdk.NexusModsApi.NexusModsGameId>.None,
        Stores = new StoreIdentifiers(GameId.From("Tests.ToolVariables")),
        Executable = "bin/game.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "My Mods"),
        Install = new SimpleInstallRules(),
    };

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddDefinedGame(Definition, (provider, definition) => new SimpleGame(provider, definition))
            .AddUniversalGameLocator(Definition.GameId, locationsFileSystem: IsolatedKnownFolders.Create);
    }

    private static ToolArgumentResolver Resolver => new();

    private AbsolutePath GameFolder => GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, RelativePath.Empty));

    [Fact]
    public async Task GameFolder_IsTheInstallationsGameLocation()
    {
        var loadout = await CreateLoadout();

        Resolver.Resolve(ArgumentTemplate.Of("{GameFolder}"), loadout).Should().Equal(GameFolder.GetFullPath());
    }

    [Fact]
    public async Task DataFolder_ComesFromTheGameAsAHostPath()
    {
        var loadout = await CreateLoadout();

        Resolver.Resolve(ArgumentTemplate.Of("{DataFolder}"), loadout)
            .Should().Equal(GameFolder.Combine("My Mods").GetFullPath());
    }

    [Fact]
    public async Task LoadoutName_IsThePlainText()
    {
        var loadout = await CreateLoadout();

        Resolver.Resolve(ArgumentTemplate.Of("{LoadoutName}"), loadout).Should().Equal(loadout.Name);
    }

    [Fact]
    public async Task CallerVariables_AreBoundBesideTheLoadoutOnes()
    {
        var loadout = await CreateLoadout();
        var extra = new Dictionary<string, string> { ["OutputFolder"] = "/prefix/drive_c/out", ["Exe"] = "/tools/x.exe" };

        Resolver.Resolve(ArgumentTemplate.Of("-O:{OutputFolder}", "{Exe}"), loadout, extra)
            .Should().Equal("-O:/prefix/drive_c/out", "/tools/x.exe");
    }

    [Fact]
    public async Task UnknownVariable_IsAnErrorNamingIt()
    {
        var loadout = await CreateLoadout();

        var act = () => Resolver.Resolve(ArgumentTemplate.Of("{Nonsense}"), loadout);

        act.Should().Throw<UnknownToolVariableException>().Which.VariableName.Should().Be("Nonsense");
    }

    [Fact]
    public async Task VariableTheGameDoesNotSupply_IsAnErrorNamingIt()
    {
        var loadout = await CreateLoadout();

        var act = () => Resolver.Resolve(ArgumentTemplate.Of("-P:{PluginsFile}"), loadout);

        act.Should().Throw<UnknownToolVariableException>().Which.VariableName.Should().Be("PluginsFile");
    }

    [Fact]
    public async Task UserLine_IsSplitBeforeSubstituting()
    {
        var loadout = await CreateLoadout();
        var dataFolder = GameFolder.Combine("My Mods").GetFullPath();

        var words = Resolver.ResolveLine("-D:{DataFolder} \"-x y\"", loadout);

        words.Should().Equal($"-D:{dataFolder}", "-x y");
    }

    [Fact]
    public async Task ResolvedPaths_AreHostPathsNotWindowsOnes()
    {
        var loadout = await CreateLoadout();

        var words = Resolver.Resolve(ArgumentTemplate.Of("-D:{DataFolder}"), loadout);

        words.Single().Should().NotContain(":\\").And.NotContain("Z:");
    }
}
