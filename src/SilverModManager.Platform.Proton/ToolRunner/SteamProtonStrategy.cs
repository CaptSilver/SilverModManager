using System.Diagnostics.CodeAnalysis;
using NexusMods.Paths;
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

        var argv = WineRunSupport.Argv([resolution.Build.Launcher.ToString(), "run"], command, prefix.Path);

        // Proton wants the app's compatdata directory, which is the parent of the pfx directory.
        var environment = new Dictionary<string, string>
        {
            ["STEAM_COMPAT_DATA_PATH"] = prefix.Path.Parent.ToString(),
            ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = resolution.SteamInstallPath.Value.ToString(),
        };
        WineRunSupport.Environment(environment, command.Env);

        return ToolRunPlan.Runnable(StrategyName, argv, WineRunSupport.WorkingDirectory(command, prefix), environment);
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
        var resolution = protonResolver.ResolveForApp(steamInstallPath, appId);
        return resolution.IsResolved
            ? new ProtonResolution(resolution.Build, steamInstallPath, null)
            : new ProtonResolution(null, null, resolution.Diagnostic);
    }
}
