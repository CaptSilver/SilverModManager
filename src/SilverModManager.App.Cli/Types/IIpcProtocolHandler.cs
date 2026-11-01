using R3;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.CLI.Types;

/// <summary>
/// Defines a protocol handler for protocols which need passing messages to the main application.
/// </summary>
public interface IIpcProtocolHandler
{
    /// <summary>
    /// The protocol to handle, e.g. 'nxm'
    /// </summary>
    public string Protocol { get; }
    
    /// <summary>
    /// Handles the given URL.
    /// </summary>
    /// <param name="url">The URL.</param>
    /// <param name="token">Allows to cancel the operation.</param>
    public Task Handle(string url, CancellationToken token);

    /// <summary>
    /// Whether the user asked this app to own the scheme. Each handler reads its own settings record.
    /// </summary>
    public bool IsRegistrationWanted(ISettingsManager settings);

    /// <summary>
    /// Emits each time the user turns the handler's setting on.
    /// </summary>
    public Observable<Unit> RegistrationTurnedOn(ISettingsManager settings);
}
