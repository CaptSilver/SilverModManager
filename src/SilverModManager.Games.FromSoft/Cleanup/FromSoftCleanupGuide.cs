using SilverModManager.Sdk.Games;
using SilverModManager.Platform.Proton.Doctor;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Cleanup;

/// <summary>What Health Check shows for "Show cleanup steps": the plan, and the command that runs it.</summary>
public sealed class FromSoftCleanupGuide(CleanupService service) : IGameCleanupGuide
{
    public bool AppliesTo(GameInstallation installation) => installation.Game is IFromSoftGame { Definition.CleanupVerb: not null };

    public IReadOnlyList<string> Describe(Loadout.ReadOnly loadout)
    {
        var verb = ((IFromSoftGame)loadout.InstallationInstance.Game).Definition.CleanupVerb
            ?? throw new InvalidOperationException($"{loadout.InstallationInstance.Game.DisplayName} has no cleanup verb.");
        var plan = service.Plan(loadout);
        var lines = new List<string> { "These steps turn the game folder back into the plain Steam install:", "" };
        lines.AddRange(CleanupPlanText.Lines(plan));

        if (plan.Steps.Any(s => s.Acts))
        {
            lines.Add("");
            lines.Add("Nothing is changed from this window; the Fix button on the Health Check entry runs these steps after you confirm them. To list the folder and run them from a terminal, use:");
            lines.Add($"silvermod {verb} --apply --loadout {SteamLaunchOptionLine.ShellQuote(loadout.Name)}");
            lines.Add("It prints everything first and applies it once you run it again with --yes.");
            if (plan.Steps.Any(s => s.Kind == CleanupStepKind.ReplaceLaunchOption))
                lines.Add("Steam's launch options are written with \"Use this loadout from Steam\", which can close Steam for you.");
        }

        return lines;
    }
}
