using FluentAssertions;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unity.Tests.Fixtures;

/// <summary>
/// What every row of <see cref="UnityGames"/> must do against its own definition, whatever its loader: be found by its
/// executable and take the loader pack into the places the loader reads it from. A game's test class derives from this
/// through <see cref="ABepInExRowTests{TTest}"/> or <see cref="AMelonLoaderRowTests{TTest}"/>, so a row cannot be added
/// without these running against it.
/// </summary>
public abstract class AUnityRowTests<TTest>(ITestOutputHelper outputHelper, UnityGameDefinition definition)
    : AUnityGameTests<TTest>(outputHelper, definition)
{
    protected abstract ALibraryArchiveInstaller PackInstaller { get; }

    protected abstract ALibraryArchiveInstaller ModInstaller { get; }

    /// <summary>The files of the loader pack as its Thunderstore package ships them.</summary>
    protected abstract string[] PackPaths { get; }

    /// <summary>Where <see cref="PackPaths"/> land in the game folder.</summary>
    protected abstract string[] ExpectedPackTargets { get; }

    /// <summary>A mod for some other loader: nothing either installer of this row takes.</summary>
    protected abstract string[] ForeignModPaths { get; }

    [Fact]
    public void TheLocatedFolderHoldsTheExecutableTheGameLaunches()
    {
        var primaryFile = Game.GetPrimaryFile(GameInstallation);

        primaryFile.Should().Be(new GamePath(LocationId.Game, Definition.Executable));
        GameInstallation.Locations[LocationId.Game].Path.Combine(primaryFile.Path).FileExists
            .Should().BeTrue($"{Definition.DisplayName} is found by {Definition.Executable}");
    }

    [Fact]
    public async Task ALoaderPackNamedAfterTheGamesPackInstallsAtTheGameRoot()
    {
        var archive = await AddArchiveNamed($"{Definition.ThunderstoreLoaderPackage}-5.4.2305.zip", PackPaths);

        var (result, targets) = await TryInstall(PackInstaller, archive);

        result.IsSuccess.Should().BeTrue();
        targets.Should().BeEquivalentTo(ExpectedPackTargets.Select(OnGame));
    }

    [Fact]
    public async Task ALayoutNeitherInstallerRecognisesIsNotSupported()
    {
        var archive = await AddArchiveNamed("Someone-OtherLoaderMod-1.0.0.zip", ForeignModPaths);

        var (mod, _) = await TryInstall(ModInstaller, archive);
        var (pack, _) = await TryInstall(PackInstaller, archive);

        mod.IsNotSupported(out _).Should().BeTrue();
        pack.IsNotSupported(out _).Should().BeTrue();
    }

    [Fact]
    public async Task AModArchiveIsNotTakenForTheLoaderPack()
    {
        var archive = await AddArchiveNamed("Someone-SomeMod-1.0.0.zip", "SomeMod.dll");

        var (result, _) = await TryInstall(PackInstaller, archive);

        result.IsNotSupported(out _).Should().BeTrue();
    }
}
