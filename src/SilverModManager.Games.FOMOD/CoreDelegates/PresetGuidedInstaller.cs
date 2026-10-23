using SilverModManager.Abstractions.GuidedInstallers;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Games.FOMOD.CoreDelegates;

/// <summary>
/// A IGuidedInstaller implementation that uses a preset list of steps to make the same choices
/// a user previously made for specific steps.
/// </summary>
/// <remarks>
/// Throws <see cref="SavedChoicesDoNotApplyException"/> when the FOMOD shows something the saved
/// choices can't answer, instead of guessing.
/// </remarks>
public class PresetGuidedInstaller : IGuidedInstaller
{
    private readonly FomodOption[] _steps;
    private int _currentStep = 0;

    /// <summary>
    /// Constructor.
    /// </summary>
    public PresetGuidedInstaller(FomodOption[] steps)
    {
        _steps = steps;
    }

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public bool SupportsUserInteraction => false;

    /// <inheritdoc/>
    public void SetupInstaller(string name) { }

    /// <inheritdoc/>
    public void SetupInstaller(string name, string? notice) { }

    /// <inheritdoc/>
    public void CleanupInstaller() { }

    /// <inheritdoc/>
    public Task<UserChoice> RequestUserChoice(GuidedInstallationStep installationStep, Percent progress, CancellationToken cancellationToken)
    {
        var step = FindSavedStep(installationStep);

        var selected = new List<SelectedOption>();
        foreach (var savedGroup in step.groups)
        {
            var installGroup = installationStep.Groups.FirstOrDefault(g => NamesMatch(g.Name, savedGroup.name))
                ?? throw new SavedChoicesDoNotApplyException(installationStep.Name, savedGroup.name, null, "the FOMOD has no such group");

            var groupSelection = new List<SelectedOption>();
            foreach (var savedChoice in savedGroup.choices)
            {
                var installOption = installGroup.Options.FirstOrDefault(o => NamesMatch(o.Name, savedChoice.name))
                    ?? throw new SavedChoicesDoNotApplyException(installationStep.Name, savedGroup.name, savedChoice.name, "the group has no such option");

                groupSelection.Add(new SelectedOption(installGroup.Id, installOption.Id));
            }

            if (!GuidedInstallerValidation.IsValidGroupSelection(installGroup, groupSelection))
                throw new SavedChoicesDoNotApplyException(installationStep.Name, savedGroup.name, null, $"{groupSelection.Count} saved option(s) break the group type {installGroup.Type}");

            selected.AddRange(groupSelection);
        }

        return Task.FromResult(new UserChoice(new UserChoice.GoToNextStep(selected.ToArray())));
    }

    /// <summary>
    /// Saved steps are matched by name. Only when a name is missing on either side, which older saved
    /// data can have, does the position decide. Several steps can share a title, so the saved step at
    /// the same position wins when its name matches.
    /// </summary>
    private FomodOption FindSavedStep(GuidedInstallationStep installationStep)
    {
        var position = _currentStep++;

        if (!string.IsNullOrWhiteSpace(installationStep.Name))
        {
            if (position < _steps.Length && NamesMatch(_steps[position].name, installationStep.Name))
                return _steps[position];

            var named = _steps.FirstOrDefault(s => NamesMatch(s.name, installationStep.Name));
            if (named is not null) return named;
        }

        var installNameMissing = string.IsNullOrWhiteSpace(installationStep.Name);
        if (position < _steps.Length && (installNameMissing || string.IsNullOrWhiteSpace(_steps[position].name)))
            return _steps[position];

        throw new SavedChoicesDoNotApplyException(installationStep.Name, null, null, "no saved choices for this step");
    }

    private static bool NamesMatch(string installName, string savedName) =>
        string.Equals(installName.Trim(), savedName.Trim(), StringComparison.OrdinalIgnoreCase);
}
