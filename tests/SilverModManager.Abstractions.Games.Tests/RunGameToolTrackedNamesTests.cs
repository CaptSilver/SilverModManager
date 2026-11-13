using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace SilverModManager.Abstractions.Games.Tests;

/// <summary>
/// After the program a tool starts returns, the tool waits on processes of these names, because a loader can hand
/// the game to a process of another name.
/// </summary>
public class RunGameToolTrackedNamesTests(ITestOutputHelper helper) : AIsolatedGameTest<RunGameToolTrackedNamesTests, StubbedGame>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddGame<StubbedGame>()
        .AddSingleton<IGameLocator>(s => new UniversalStubbedGameLocator<StubbedGame>(
            s, s.GetRequiredService<IFileSystem>(), s.GetRequiredService<TemporaryFileManager>()));

    private sealed class ExposedTool(IServiceProvider provider, StubbedGame game) : RunGameTool<StubbedGame>(provider, game)
    {
        public IReadOnlySet<string> Names(AbsolutePath program, AbsolutePath primaryFile) => TrackedProcessNames(program, primaryFile);
    }

    [Fact]
    public void ByDefaultTheProgramAndThePrimaryFileAreTrackedWithAndWithoutTheExtension()
    {
        var tool = new ExposedTool(ServiceProvider, Game);

        tool.Names(FileSystem.FromUnsanitizedFullPath("/g/skse64_loader.exe"), FileSystem.FromUnsanitizedFullPath("/g/SkyrimSE.exe"))
            .Should().BeEquivalentTo("skse64_loader.exe", "skse64_loader", "SkyrimSE.exe", "SkyrimSE");
    }
}
