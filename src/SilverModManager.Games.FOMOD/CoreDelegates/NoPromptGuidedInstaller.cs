using SilverModManager.Abstractions.GuidedInstallers;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Games.FOMOD.CoreDelegates;

/// <summary>
/// Answers no FOMOD step: the first one ends the install with <see cref="ReviewRequiredException"/>, so an
/// unattended install of a FOMOD that has choices to make leaves the decision to a person.
/// </summary>
/// <param name="modName">The mod the install is for, named in the exception.</param>
internal sealed class NoPromptGuidedInstaller(string modName) : IGuidedInstaller
{
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

    /// <remarks>
    /// Not async, so <c>UiDelegates.AskStep</c> catches the throw and records the failure before it
    /// schedules a continuation, and the executor cannot move on to the next step first.
    /// </remarks>
    public Task<UserChoice> RequestUserChoice(GuidedInstallationStep installationStep, Percent progress, CancellationToken cancellationToken)
        => throw new ReviewRequiredException(ReviewKind.NeedsChoices, modName, "the installer has choices to make");
}
