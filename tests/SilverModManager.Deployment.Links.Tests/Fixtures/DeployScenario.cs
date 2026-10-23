using System.Text;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;

namespace NexusMods.Deployment.Links.Tests.Fixtures;

/// <summary>
/// One stubbed-game graph that applies a mod, disables it and resets the game, recording what the game and
/// preferences folders hold after each step. Two runs differing only in the backend can be compared
/// byte for byte.
/// </summary>
public sealed class DeployScenario : LinkedGameTest<DeployScenario>
{
    // The graph is built inside the base constructor, which cannot take the placement as an argument.
    [ThreadStatic]
    private static (Placement Game, Placement Store, DeploymentBackendId? Pinned)? _pending;

    private DeployScenario(ITestOutputHelper helper)
        : base(helper, _pending!.Value.Game, _pending.Value.Store, _pending.Value.Pinned)
    {
    }

    /// <param name="pinned">The backend to force for the game folder, or null to let the selection chain choose.</param>
    public static async Task<Result> Run(ITestOutputHelper helper, Placement game, Placement store, DeploymentBackendId? pinned)
    {
        _pending = (game, store, pinned);
        DeployScenario scenario;
        try { scenario = new DeployScenario(helper); }
        finally { _pending = null; }

        await scenario.InitializeAsync();
        try { return await scenario.Execute(); }
        finally { await scenario.DisposeAsync(); }
    }

    protected override async Task GenerateGameFiles()
    {
        var originalFile = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "bin/originalGameFile.txt"));
        originalFile.Parent.CreateDirectory();
        await originalFile.WriteAllTextAsync("Hello World!");

        var settings = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Preferences, "settings.ini"));
        settings.Parent.CreateDirectory();
        await settings.WriteAllTextAsync("[settings]\nvolume=7\n");
    }

    private async Task<Result> Execute()
    {
        var loadout = await NewLoadout();
        var (group, _) = await AddMod(loadout, "Deployed tree mod",
            ("Data/mod.pak", "mod pak bytes"),
            ("Data/again.pak", "mod pak bytes"),
            ("bin/launcher", "#!/bin/sh\necho launcher\n"),
            ("README.txt", "README overridden by the mod")
        );

        loadout = await Synchronizer.Synchronize(loadout);

        var gameRoot = GameInstallation.Locations[LocationId.Game].Path;
        var preferencesRoot = GameInstallation.Locations[LocationId.Preferences].Path;
        var sb = new StringBuilder();

        await Section(sb, "## 1 applied", gameRoot, preferencesRoot);
        var symlinked = new[] { "Data/mod.pak", "Data/again.pak", "bin/launcher", "README.txt" }.Where(p => IsSymlink(OnDisk(p))).ToArray();
        var plan = Plans.Load(Connection.Db, MetadataId);

        await DisableItem(group);
        await Synchronizer.Synchronize(loadout);
        await Section(sb, "## 2 mod disabled", gameRoot, preferencesRoot);

        await Synchronizer.ResetToOriginalGameState(GameInstallation, GameInstallation.LocatorResult.LocatorIds.Distinct().ToArray());
        await Section(sb, "## 3 reset", gameRoot, preferencesRoot);

        return new Result(sb.ToString(), plan, symlinked);
    }

    private static async Task Section(StringBuilder sb, string title, AbsolutePath game, AbsolutePath preferences)
    {
        sb.AppendLine(title);
        sb.AppendLine("### Game");
        sb.Append(await DirectoryDescription.Describe(game));
        sb.AppendLine("### Preferences");
        sb.Append(await DirectoryDescription.Describe(preferences));
        sb.AppendLine();
    }

    /// <param name="Snapshot">The three sections, as the deployed-tree snapshot renders them.</param>
    /// <param name="Plan">The plan rows after the first apply.</param>
    /// <param name="Symlinked">The mod's paths that were symlinks after the first apply.</param>
    public sealed record Result(string Snapshot, DeploymentPlan Plan, IReadOnlyList<string> Symlinked);
}
