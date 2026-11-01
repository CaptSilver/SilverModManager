using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Games.Generic.Tests;

/// <summary>
/// The stubbed locator finds a game that is an instance built from a definition, not only a type it can ask the
/// container for.
/// </summary>
public class UniversalStubbedGameLocatorTests(IServiceProvider serviceProvider, IFileSystem fileSystem, TemporaryFileManager fileManager)
{
    private static readonly Dictionary<RelativePath, byte[]> Files = new() { [(RelativePath)"bin/fixture.exe"] = [1, 2, 3] };

    [Fact]
    public void Locate_WithAGameFactory_ReturnsTheGameTheFactoryBuilds()
    {
        var game = FixtureGame.Create(serviceProvider, "Tests.Factory");
        using var locator = new UniversalStubbedGameLocator(serviceProvider, fileSystem, fileManager, _ => game, "Tests.Factory", Files);

        var result = locator.Locate().Should().ContainSingle().Subject;

        result.Game.Should().BeSameAs(game);
        result.Path.Combine("bin/fixture.exe").FileExists.Should().BeTrue("the stubbed files are written under the located game folder");
    }

    [Fact]
    public void Locate_ByGameId_ResolvesTheRegisteredGameWithThatId()
    {
        var wanted = FixtureGame.Create(serviceProvider, "Tests.Wanted");
        var other = FixtureGame.Create(serviceProvider, "Tests.Other");
        var provider = new ServiceCollection().AddSingleton<IGame>(other).AddSingleton<IGame>(wanted).BuildServiceProvider();
        using var locator = new UniversalStubbedGameLocator(provider, fileSystem, fileManager, GameId.From("Tests.Wanted"), Files);

        locator.Locate().Single().Game.Should().BeSameAs(wanted);
    }

    [Fact]
    public void Locate_ByGameId_NamesTheGameFolderAfterTheId()
    {
        var provider = new ServiceCollection()
            .AddSingleton<IGame>(FixtureGame.Create(serviceProvider, "Tests.First"))
            .AddSingleton<IGame>(FixtureGame.Create(serviceProvider, "Tests.Second"))
            .BuildServiceProvider();
        using var first = new UniversalStubbedGameLocator(provider, fileSystem, fileManager, GameId.From("Tests.First"), Files);
        using var second = new UniversalStubbedGameLocator(provider, fileSystem, fileManager, GameId.From("Tests.Second"), Files);

        var firstPath = first.Locate().Single().Path;
        var secondPath = second.Locate().Single().Path;

        firstPath.Should().NotBe(secondPath);
        firstPath.FileName.Should().Contain("Tests.First");
        secondPath.FileName.Should().Contain("Tests.Second");
    }

    [Fact]
    public void Constructing_ForAnIdNoGameHas_NamesTheId()
    {
        var provider = new ServiceCollection().AddSingleton<IGame>(FixtureGame.Create(serviceProvider, "Tests.Present")).BuildServiceProvider();

        var act = () => new UniversalStubbedGameLocator(provider, fileSystem, fileManager, GameId.From("Tests.Missing"), Files);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Tests.Missing*");
    }

    [Fact]
    public void GenericLocator_StillResolvesItsGameFromTheContainer()
    {
        var game = FixtureGame.Create(serviceProvider, "Tests.Typed");
        var provider = new ServiceCollection().AddSingleton(game).BuildServiceProvider();
        using var locator = new UniversalStubbedGameLocator<FixtureGame>(provider, fileSystem, fileManager, Files);

        var result = locator.Locate().Single();

        result.Game.Should().BeSameAs(game);
        result.Path.FileName.Should().StartWith(nameof(FixtureGame), "the typed form keeps naming the folder after the type");
    }
}
