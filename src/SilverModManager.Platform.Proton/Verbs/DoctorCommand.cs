using SilverModManager.Abstractions.Cli;
using SilverModManager.Platform.Proton.Doctor;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Platform.Proton.Verbs;

/// <summary>
/// Prints the doctor report: the managed games and, after them, what the game locators had to leave out.
/// </summary>
internal sealed class DoctorCommand(IManagedInstallations installations, DoctorReporter reporter, IGameRegistry registry)
{
    public async Task<int> RunAsync(IRenderer renderer, bool json)
    {
        // Listing the installations only reaches the registry through loadouts, so with none it never locates;
        // a game the locators dropped has no loadout, and its problem would go unreported.
        registry.LocateGameInstallations();
        var report = reporter.Build(installations.List()) with
        {
            Problems = registry.Problems.Select(problem => $"{problem.Locator}: {problem.Message}").ToArray(),
        };

        if (json)
        {
            await renderer.TextLine(report.ToJson());
            return 0;
        }

        foreach (var line in DoctorTextFormatter.Format(report)) await renderer.TextLine(line);
        return 0;
    }
}
