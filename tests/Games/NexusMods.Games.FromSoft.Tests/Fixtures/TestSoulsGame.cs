using DynamicData.Kernel;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.FromSoft.Tests.Fixtures;

/// <summary>
/// A FromSoft game that shares no value with Elden Ring: id, name, layout, save names, check and cleanup rules,
/// overlay allowlist, format traits and the name me3 is told. A test that passes with it cannot be passing because the
/// base carries an Elden Ring value. Registered only by the tests that use it; nothing is ever launched, since me3
/// does not know the game.
/// </summary>
public class TestSoulsGame : AFromSoftGame<TestSoulsGame>, IGameData<TestSoulsGame>
{
    public static GameId GameId { get; } = GameId.From("FromSoft.TestSouls");
    public static string DisplayName => "Test Souls";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Optional<Sdk.NexusModsApi.NexusModsGameId>.None;

    public static readonly FromSoftGameDefinition Definition = TestSoulsDefinition.Definition;

    public TestSoulsGame(IServiceProvider provider)
        : base(provider, Definition, _ => new Me3Loader(provider, new Me3LoaderOptions("testsouls", []), Definition)) { }

    protected override ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider) => new TestSoulsSynchronizer(provider, this);

    protected override ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider) =>
    [
        Loader.HostInstaller(),
        ..Loader.ModInstallers(),
    ];
}

public class TestSoulsSynchronizer : AFromSoftSynchronizer
{
    public TestSoulsSynchronizer(IServiceProvider provider, IFromSoftGame game)
        : base(provider, game, game.Loader.IntrinsicFiles()) { }
}
