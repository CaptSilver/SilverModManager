using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.LoadOrder.Tests;
using SilverModManager.Games.CreationEngine.OpenMw;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Tests.OpenMw;

/// <summary>
/// A Morrowind folder in a temp folder, with the three official masters in Data Files, that both the Morrowind row
/// and the OpenMW row of a test can sit on. Registering it replaces the stubbed per-game locators of the other tests,
/// which would each invent a folder of their own.
/// </summary>
public sealed class StubbedMorrowindInstall(TemporaryFileManager temp)
{
    public static readonly string[] Masters = ["Morrowind.esm", "Tribunal.esm", "Bloodmoon.esm"];

    public AbsolutePath Folder { get; } = temp.CreateFolder("morrowind").Path;

    public AbsolutePath DataFiles => Folder / "Data Files";

    /// <summary>A Morrowind plugin: the header record, flagged as a master when <paramref name="master"/>.</summary>
    public static byte[] Plugin(bool master = false, params string[] masters) =>
        PluginBytes.Write(PluginFormat.Tes3, new PluginSpec { RecordFlags = master ? 1u : 0u, Masters = masters });

    public void Write(IEnumerable<string>? plugins = null, string? iniText = null)
    {
        DataFiles.CreateDirectory();
        foreach (var master in Masters)
            File.WriteAllBytes((DataFiles / master).GetFullPath(), Plugin(master: true));
        foreach (var plugin in plugins ?? [])
            File.WriteAllBytes((DataFiles / plugin).GetFullPath(), Plugin());
        File.WriteAllText((DataFiles / "vanilla.txt").GetFullPath(), "vanilla");
        File.WriteAllText((Folder / "Morrowind.exe").GetFullPath(), "game executable");
        if (iniText is not null)
            File.WriteAllText((Folder / "Morrowind.ini").GetFullPath(), iniText);
    }

    /// <summary>The text of an openmw.cfg that names this install's Data Files and lists its masters, as the launcher's wizard leaves it.</summary>
    public string Config(string extra = "") =>
        $"data=\"{DataFiles.GetFullPath()}\"\ncontent=builtin.omwscripts\ncontent=Morrowind.esm\ncontent=Tribunal.esm\ncontent=Bloodmoon.esm\n{extra}";
}

/// <summary>Locates the Morrowind row over a <see cref="StubbedMorrowindInstall"/>.</summary>
public sealed class StubbedMorrowindLocator(IServiceProvider provider, StubbedMorrowindInstall install) : IGameLocator
{
    public IEnumerable<GameLocatorResult> Locate()
    {
        yield return new GameLocatorResult
        {
            Game = provider.GetServices<IGame>().OfType<CreationEngine.Morrowind.Morrowind>().Single(),
            Path = install.Folder,
            Store = GameStore.Unknown,
            StoreIdentifier = "stubbed-morrowind",
            LocatorIds = [LocatorId.From("StubbedGameState.zip")],
            Locator = this,
        };
    }
}

/// <summary>
/// Gives what the real locator finds the one locator id the stubbed file hashes know. A real OpenMW install has none,
/// but the stub reads the first id of every installation it is asked about.
/// </summary>
public sealed class StubbedIdsLocator(IGameLocator inner) : IGameLocator
{
    public IEnumerable<GameLocatorResult> Locate() => Locate(NullSink.Instance);

    public IEnumerable<GameLocatorResult> Locate(IGameLocatorProblemSink problems) => inner.Locate(problems).Select(result => new GameLocatorResult
    {
        Game = result.Game,
        Path = result.Path,
        Store = result.Store,
        StoreIdentifier = result.StoreIdentifier,
        LocatorIds = [LocatorId.From("StubbedGameState.zip")],
        Locator = this,
        Platform = result.Platform,
    });

    private sealed class NullSink : IGameLocatorProblemSink
    {
        public static readonly NullSink Instance = new();
        public void Report(string locator, string message) { }
    }
}

public static class StubbedMorrowindExtensions
{
    /// <summary>Puts the Morrowind row on a stubbed install, and the OpenMW locator behind the stub's locator id.</summary>
    public static IServiceCollection AddStubbedMorrowind(this IServiceCollection services)
    {
        // The locator is the app graph's to add, so the test graph builds it here and gives it the stub's locator id.
        return services
            .AddSingleton<OpenMwLocator>()
            .AddSingleton<IGameLocator>(s => new StubbedIdsLocator(s.GetRequiredService<OpenMwLocator>()))
            .AddSingleton(s => new StubbedMorrowindInstall(s.GetRequiredService<TemporaryFileManager>()))
            .AddSingleton<IGameLocator>(s => new StubbedMorrowindLocator(s, s.GetRequiredService<StubbedMorrowindInstall>()));
    }
}
