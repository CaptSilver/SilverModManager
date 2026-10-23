namespace NexusMods.Secrets.SecretService;

/// <summary>
/// How long the store waits on the service. The keyring never shows UI for the plain calls, but a daemon
/// that is wedged or still starting would block them forever, and an unlock prompt can sit open as long as
/// the user takes to type a password.
/// </summary>
internal sealed record SecretServiceTimeouts
{
    public static SecretServiceTimeouts Default { get; } = new();

    public TimeSpan Interactive { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan NonInteractive { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan Unlock { get; init; } = TimeSpan.FromSeconds(60);
}
