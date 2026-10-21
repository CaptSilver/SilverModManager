using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Games.CreationEngine.Morrowind.OpenMw;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.Morrowind.OpenMw;

/// <summary>
/// Morrowind over a stubbed Steam install with two OpenMW config roots in temp folders, so nothing reaches the
/// real ones. The roots are registered after the production ones (the last registration wins) and the chosen
/// roots get an openmw.cfg before the game's locations are worked out.
/// </summary>
public abstract class AOpenMwTest<TTest>(ITestOutputHelper helper)
    : AIsolatedGameTest<TTest, CreationEngine.Morrowind.Morrowind>(helper)
    where TTest : class
{
    public const string ConfigFixture = "data=\"/games/Morrowind/Data Files\"\ncontent=builtin.omwscripts\ncontent=Morrowind.esm\n";

    /// <summary>Indexes (0 or 1) of the roots that hold an openmw.cfg.</summary>
    protected abstract int[] RootsWithConfig { get; }

    protected IOpenMwConfigRoots Roots => ServiceProvider.GetRequiredService<IOpenMwConfigRoots>();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        var withConfig = RootsWithConfig;
        return base.AddServices(services)
            .AddCreationEngine()
            .AddUniversalGameLocator<CreationEngine.Morrowind.Morrowind>(new Version("1.6.0.1820"), new Dictionary<RelativePath, byte[]>
            {
                { "Morrowind.exe", "game executable"u8.ToArray() },
            })
            .AddSingleton<IOpenMwConfigRoots>(s =>
            {
                var temp = s.GetRequiredService<TemporaryFileManager>();
                var roots = new[] { temp.CreateFolder("openmw-a").Path, temp.CreateFolder("openmw-b").Path };
                foreach (var index in withConfig)
                    File.WriteAllText((roots[index] / "openmw.cfg").GetFullPath(), ConfigFixture);
                return new TestOpenMwConfigRoots(roots);
            });
    }

    // The stub locator does not create folders, so the game's own plugin and a plain file go in here. The
    // stub bytes are fine as a plugin: nothing reads Morrowind plugin headers.
    protected override async Task GenerateGameFiles()
    {
        var dataFiles = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "Data Files"));
        dataFiles.CreateDirectory();
        await (dataFiles / "Morrowind.esm").WriteAllBytesAsync("stub master"u8.ToArray());
        await (dataFiles / "vanilla.txt").WriteAllBytesAsync("vanilla"u8.ToArray());
    }

    private sealed class TestOpenMwConfigRoots(IReadOnlyList<AbsolutePath> candidates) : IOpenMwConfigRoots
    {
        public IReadOnlyList<AbsolutePath> Candidates { get; } = candidates;
    }
}
