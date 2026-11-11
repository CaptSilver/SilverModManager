using System.Diagnostics.CodeAnalysis;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.ToolRunner;

/// <summary>
/// Runs a program through the <c>proton</c> script of the build Steam uses for the game, with the
/// environment Steam gives it.
/// </summary>
public sealed class SteamProtonStrategy(IProtonResolver protonResolver) : IToolRunStrategy
{
    internal const string StrategyName = "SteamProton";

    public CompatibilityLayerKind Kind => CompatibilityLayerKind.SteamProton;

    public ToolRunVia? Via => ToolRunVia.SteamProton;

    public string? CheckRunnable(GameInstallation installation, ResolvedPrefix prefix)
        => Resolve(installation, prefix).Diagnostic;

    public ToolRunPlan Plan(
        GameInstallation installation,
        ResolvedPrefix prefix,
        PrefixCommand command)
    {
        var resolution = Resolve(installation, prefix);
        if (!resolution.Found) return ToolRunPlan.Failed(resolution.Diagnostic);

        // Proton records the paths it was started through in the prefix's config_info. Steam starts it by fully
        // resolved paths, so a symlinked parent (/home on an ostree host) would make every tool run rewrite them.
        var argv = WineRunSupport.Argv([Resolved(resolution.Build.Launcher), "run"], command, prefix.Path);

        // Proton wants the app's compatdata directory, which is the parent of the pfx directory.
        var environment = new Dictionary<string, string>
        {
            ["STEAM_COMPAT_DATA_PATH"] = Resolved(prefix.Path.Parent),
            ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = Resolved(resolution.SteamInstallPath.Value),
        };
        WineRunSupport.ProtonDefaults(environment);
        WineRunSupport.Environment(environment, command.Env);

        return ToolRunPlan.Runnable(StrategyName, argv, WineRunSupport.WorkingDirectory(command, prefix), environment);
    }

    private static string Resolved(AbsolutePath path)
    {
        // Links exist only on the real file system; a fake one has nothing to follow, and following the host's
        // would read the machine the tests run on.
        if (path.FileSystem is not NexusMods.Paths.FileSystem) return path.ToString();

        try
        {
            return ResolvedPath.Of(path.ToString());
        }
        catch (IOException)
        {
            // Looping links: Proton gets the path as written and fails with its own message.
            return path.ToString();
        }
    }

    private sealed record ProtonResolution(ProtonBuild? Build, AbsolutePath? SteamInstallPath, string? Diagnostic)
    {
        [MemberNotNullWhen(true, nameof(Build), nameof(SteamInstallPath))]
        [MemberNotNullWhen(false, nameof(Diagnostic))]
        public bool Found => Diagnostic is null;
    }

    private ProtonResolution Resolve(GameInstallation installation, ResolvedPrefix prefix)
    {
        var gameName = installation.Game.DisplayName;

        if (prefix.ProtonAppId is not { } appId)
            return new ProtonResolution(null, null, $"{gameName} has a Steam prefix at '{prefix.Path}' but its Steam app id is unknown.");

        // Without the provider there is no telling which Steam installation owns the game, so no way to
        // find its Proton builds.
        if (installation.LocatorResult.LinuxCompatabilityDataProvider is not { } provider)
            return new ProtonResolution(null, null, $"{gameName} has a Steam prefix at '{prefix.Path}' but the Steam installation that owns it was not found.");

        var steamInstallPath = provider.SteamInstallPath;
        var resolution = protonResolver.ResolveForPrefix(steamInstallPath, appId, prefix.Path.Parent);
        return resolution.IsResolved
            ? new ProtonResolution(resolution.Build, steamInstallPath, null)
            : new ProtonResolution(null, null, resolution.Diagnostic);
    }
}
