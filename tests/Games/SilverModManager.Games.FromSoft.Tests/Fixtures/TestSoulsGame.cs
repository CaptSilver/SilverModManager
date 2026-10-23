using DynamicData.Kernel;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FromSoft.Me3;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft.Tests.Fixtures;

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

    /// <remarks><c>new</c> on purpose, as for Elden Ring: the static is read by tests that have no game instance.</remarks>
    public new static readonly FromSoftGameDefinition Definition = TestSoulsDefinition.Definition;

    public TestSoulsGame(IServiceProvider provider)
        : this(provider, _ => new Me3Loader(provider, new Me3LoaderOptions("testsouls", []), Definition)) { }

    /// <summary>For tests that need the game to run through a loader of their own.</summary>
    public TestSoulsGame(IServiceProvider provider, Func<IFromSoftGame, IFromSoftModLoader> loader)
        : base(provider, Definition, loader) { }

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
