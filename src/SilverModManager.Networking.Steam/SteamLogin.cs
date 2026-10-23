using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Networking.Steam.DTOs;
using SilverModManager.Networking.Steam.Exceptions;

namespace SilverModManager.Networking.Steam;

/// <summary>
/// An account name and the refresh token that proves it.
/// </summary>
internal sealed record SteamCredentials(string AccountName, string RefreshToken);

/// <summary>
/// The parts of SteamKit the login needs, so the order of its steps can be tested.
/// </summary>
internal interface ISteamAuthenticator
{
    Task<SteamCredentials> AuthenticateWithPasswordAsync(string username, string password);

    /// <summary>
    /// Shows a QR code and waits for it to be scanned. Never times out on its own.
    /// </summary>
    Task<SteamCredentials> AuthenticateWithQrAsync();

    void LogOn(SteamCredentials credentials);
}

/// <summary>
/// What happens once the connection to Steam is up: log on with the stored login, or authenticate and store a new one.
/// </summary>
internal sealed class SteamLogin(IAuthStorage storage, ISteamAuthenticator authenticator, ILogger logger, Func<string, string?> getEnvironmentVariable)
{
    public async Task RunAsync()
    {
        switch (await storage.TryLoadAsync(CancellationToken.None))
        {
            case AuthLoadResult.Loaded loaded:
                logger.LogInformation("Using saved auth data to log in.");
                var authData = AuthData.Load(loaded.Data);
                authenticator.LogOn(new SteamCredentials(authData.Username, authData.RefreshToken));
                break;

            case AuthLoadResult.Unavailable unavailable:
                // Starting a QR login here would wait for a scan nobody can answer in a headless run, and
                // the token it produced could not be stored anyway.
                logger.LogWarning("The stored Steam login is unavailable: {Reason}", unavailable.Reason);
                throw new SteamLoginException($"The stored Steam login is unavailable: {unavailable.Reason}");

            default:
                await AuthenticateAsync();
                break;
        }
    }

    private async Task AuthenticateAsync()
    {
        SteamCredentials credentials;
        var user = getEnvironmentVariable("STEAM_USER");
        if (user is not null)
        {
            logger.LogInformation("Using environment variables to log in.");
            credentials = await authenticator.AuthenticateWithPasswordAsync(user, getEnvironmentVariable("STEAM_PASS") ?? "");
        }
        else
        {
            logger.LogInformation("No saved auth data, logging in via QR code.");
            credentials = await authenticator.AuthenticateWithQrAsync();
        }

        // Log on first: a store that refuses the token must not cost the user a login that already worked.
        authenticator.LogOn(credentials);

        try
        {
            await storage.SaveAsync(new AuthData { Username = credentials.AccountName, RefreshToken = credentials.RefreshToken }.Save(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not save the Steam login, it will be asked for again next run: {Reason}", ex.Message);
        }
    }
}
