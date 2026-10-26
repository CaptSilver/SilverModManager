using SilverModManager.Abstractions.Cli;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Platform.Proton.PrefixSetup;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Platform.Proton.Verbs;

/// <summary>
/// Prints what a prefix setup would do to a loadout's game and, with <c>--write</c> and the prefix path it printed,
/// does it. Refusals are thrown for the error presenter, like the runner's own.
/// </summary>
public sealed class PrefixSetupCommand(LoadoutResolver resolver, IPrefixSetupRunner runner)
{
    // The resolver only reads the command to look for a SteamLaunch app id; there is none to find here.
    private static readonly string[] NoCommand = ["%command%"];

    public async Task<int> RunAsync(IRenderer renderer, string loadout, string? game, bool write, string? prefix, CancellationToken cancellationToken = default)
    {
        if (write && string.IsNullOrWhiteSpace(prefix))
            throw new PrefixSetupRefusedException("--write needs --prefix <path> naming the prefix to change. Run without --write to see the path.");
        if (!write && prefix is not null)
            throw new PrefixSetupRefusedException("--prefix only applies with --write. Run without it to see what would change.");

        var resolution = resolver.Resolve(new LaunchArgs(loadout, game, NoCommand));
        if (resolution.Error is not null || resolution.Installation is not { } installation)
        {
            await renderer.TextLine(resolution.Error ?? "The loadout's game could not be found.");
            return resolution.ExitCode;
        }

        // Every component the table knows: the game classes do not declare theirs yet.
        var components = PrefixComponents.All.Select(c => c.Id).ToList();

        var dryRun = await runner.Run(installation, new PrefixSetupRequest { Components = components }, cancellationToken);
        var plan = dryRun.Plan;
        await PrintPlan(renderer, installation, plan);

        var actual = plan.Prefix.Path.ToString();
        if (!write)
        {
            await renderer.TextLine($"Nothing was changed. To apply this with the game closed, run: silvermod prefix setup --loadout {SteamLaunchOptionLine.ShellQuote(loadout)} --write --prefix {SteamLaunchOptionLine.ShellQuote(actual)}");
            return 0;
        }

        // Checked here as well as by the runner, so a wrong path is refused before the plan is acted on.
        if (!string.Equals(prefix?.TrimEnd('/'), actual.TrimEnd('/'), StringComparison.Ordinal))
            throw new PrefixSetupRefusedException($"--prefix names {prefix}, but this game's prefix is {actual}. Nothing was changed.");

        var result = await runner.Run(
            installation,
            new PrefixSetupRequest { Components = components, Write = true, ConfirmedPrefixPath = prefix },
            cancellationToken,
            step => renderer.TextLine(DescribeStep(step)));

        await renderer.Table(["Component", "Result"], result.Steps.Select(s => new object[] { s.Name, DescribeStatus(s) }));
        if (result.Failure is { } failure)
            throw new PrefixSetupFailedException($"Setting up {failure.Name} failed: {failure.Error}");

        return 0;
    }

    private static async Task PrintPlan(IRenderer renderer, GameInstallation installation, PrefixSetupPlan plan)
    {
        await renderer.TextLine($"Prefix of {installation.Game.DisplayName}: {plan.Prefix.Path}");
        if (plan.SharedNote is { } note) await renderer.TextLine(note);
        foreach (var step in plan.Steps)
        {
            await renderer.TextLine($"{step.Name}: {step.Detected.State}, {step.Detected.Evidence}{(step.WillInstall ? "" : " (nothing to do)")}");
            if (!step.WillInstall) continue;

            foreach (var download in step.Downloads) await renderer.TextLine($"  fetch {download.Url} (sha256 {download.Sha256})");
            foreach (var action in step.Actions) await renderer.TextLine($"  {action}");
        }

        if (plan.BackupDirectory is { } backup) await renderer.TextLine($"The registry files are copied to {backup} first.");
    }

    private static string DescribeStep(PrefixSetupStepResult step) => $"{step.Name}: {DescribeStatus(step)}";

    private static string DescribeStatus(PrefixSetupStepResult step) => step.Status switch
    {
        PrefixSetupStepStatus.AlreadyInstalled => "Already installed",
        PrefixSetupStepStatus.Installed => "Installed",
        PrefixSetupStepStatus.Failed => $"Failed: {step.Error}",
        _ => "Not run",
    };
}
