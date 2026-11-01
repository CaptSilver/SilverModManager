using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tools;

/// <summary>
/// Brings a game's Wine prefix to what a tool declares in <see cref="ToolPrefixRequirements"/> before the tool
/// starts. Components are only checked: installing one changes a prefix that belongs to Steam or Heroic and
/// needs the user's confirmation, which the prefix setup asks for. Registry defaults are written, because
/// they are small, per-exe and cannot touch the game.
/// </summary>
public sealed class ToolPrefixSetup(IToolRunner toolRunner, IPrefixResolver prefixResolver, IPrefixInspector inspector)
{
    /// <exception cref="InvalidOperationException">The prefix is unusable, a component is missing or a registry value could not be written; the message is for the user.</exception>
    public async Task Ensure(GameInstallation installation, string toolName, ToolPrefixRequirements requirements, CancellationToken cancellationToken)
    {
        if (requirements.Components.Count == 0 && requirements.RegistryDefaults.Count == 0) return;

        var resolution = prefixResolver.Resolve(installation);
        if (resolution.Prefix is not { } prefix)
            throw new InvalidOperationException($"Cannot run {toolName} for {installation.Game.DisplayName}: {resolution.Diagnostic ?? "this installation does not run in a Wine prefix."}");

        RequireComponents(prefix, toolName, requirements.Components);
        foreach (var registryDefault in requirements.RegistryDefaults)
            await WriteRegistryDefault(installation, prefix, toolName, registryDefault, cancellationToken);
    }

    private void RequireComponents(ResolvedPrefix prefix, string toolName, IReadOnlyList<PrefixComponentId> components)
    {
        if (components.Count == 0) return;

        // Unknown means some evidence but not enough to say either way. It does not stop the run: the user
        // can see the same state on the prefix diagnostic, and a false refusal would be worse than a failed start.
        var missing = inspector.Inspect(prefix, components)
            .Where(status => status.State is PrefixComponentState.Missing)
            .Select(status => PrefixComponents.Get(status.Id).DisplayName)
            .ToList();
        if (missing.Count == 0) return;

        throw new InvalidOperationException($"{toolName} needs the {string.Join(" and the ", missing)} in the game's Wine prefix, and it is not there. Set it up in the prefix, then run the tool again.");
    }

    private async Task WriteRegistryDefault(GameInstallation installation, ResolvedPrefix prefix, string toolName, PrefixRegistryDefault registryDefault, CancellationToken cancellationToken)
    {
        var current = inspector.ReadAppDefault(prefix, registryDefault.ExeName, registryDefault.Value);
        if (string.Equals(current, registryDefault.Data, StringComparison.Ordinal)) return;

        // A verb's arguments pass untranslated, which a registry path needs.
        var command = PrefixCommand.ForVerb("reg",
        [
            "add",
            $@"HKCU\Software\Wine\AppDefaults\{registryDefault.ExeName}",
            "/v", registryDefault.Value,
            "/d", registryDefault.Data,
            "/f",
        ]);
        var result = await toolRunner.RunInPrefix(installation, command, cancellationToken);
        if (result.ExitCode == 0) return;

        var output = (result.Stderr.Length > 0 ? result.Stderr : result.Stdout).Trim();
        throw new InvalidOperationException($"Cannot run {toolName}: setting {registryDefault.Value} to {registryDefault.Data} for {registryDefault.ExeName} in the prefix failed with exit code {result.ExitCode}." + (output.Length > 0 ? $" {output}" : ""));
    }
}
