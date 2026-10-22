using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Games.Bethesda.LoadOrder.Tests;
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

    /// <summary>What the roots that hold an openmw.cfg have in it.</summary>
    protected virtual string ConfigText => ConfigFixture;

    /// <summary>Plugins already in Data Files when the game is first managed, besides Morrowind.esm.</summary>
    protected virtual string[] PluginsOnDisk => [];

    /// <summary>The text of a Morrowind.ini placed in the game folder before the game is managed, or null for none.</summary>
    protected virtual string? MorrowindIni => null;

    /// <summary>A Morrowind plugin: the header record, flagged as a master when <paramref name="master"/>.</summary>
    internal static byte[] Plugin(bool master = false, params string[] masters) =>
        PluginBytes.Write(PluginFormat.Tes3, new PluginSpec { RecordFlags = master ? 1u : 0u, Masters = masters });

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
                    File.WriteAllText((roots[index] / "openmw.cfg").GetFullPath(), ConfigText);
                return new TestOpenMwConfigRoots(roots);
            });
    }

    // The stub locator does not create folders, so the game's own plugin and a plain file go in here. The
    // plugin list reads every plugin's header, so the game's master has to be a real one.
    protected override async Task GenerateGameFiles()
    {
        var dataFiles = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "Data Files"));
        dataFiles.CreateDirectory();
        await (dataFiles / "Morrowind.esm").WriteAllBytesAsync(Plugin(master: true));
        foreach (var name in PluginsOnDisk)
            await (dataFiles / name).WriteAllBytesAsync(Plugin());
        await (dataFiles / "vanilla.txt").WriteAllBytesAsync("vanilla"u8.ToArray());
        if (MorrowindIni is { } ini)
            await GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "Morrowind.ini")).WriteAllTextAsync(ini);
    }

    private sealed class TestOpenMwConfigRoots(IReadOnlyList<AbsolutePath> candidates) : IOpenMwConfigRoots
    {
        public IReadOnlyList<AbsolutePath> Candidates { get; } = candidates;
    }
}
