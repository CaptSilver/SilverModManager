using JetBrains.Annotations;
using NexusMods.Sdk.Jobs;

namespace NexusMods.Abstractions.GuidedInstallers;

/// <summary>
/// Interface for a guided installer.
/// </summary>
[PublicAPI]
public interface IGuidedInstaller : IDisposable
{
    /// <summary>
    /// Whether a person answers the steps. Automated installers and the headless host answer
    /// <c>false</c>, so callers know not to hand them a FOMOD that needs a decision.
    /// </summary>
    public bool SupportsUserInteraction { get; }

    /// <summary>
    /// Sets up the installer.
    /// </summary>
    /// <param name="name">Name of the installer.</param>
    public void SetupInstaller(string name);

    /// <summary>
    /// Sets up the installer and shows <paramref name="notice"/> to the user for the whole installation,
    /// for example why they are being asked at all.
    /// </summary>
    /// <param name="name">Name of the installer.</param>
    /// <param name="notice">Text to show above the steps, <c>null</c> for none.</param>
    public void SetupInstaller(string name, string? notice);

    /// <summary>
    /// Cleans up the installer.
    /// </summary>
    public void CleanupInstaller();

    /// <summary>
    /// Requests the user for a choice.
    /// </summary>
    public Task<UserChoice> RequestUserChoice(GuidedInstallationStep installationStep, Percent progress, CancellationToken cancellationToken);
}
