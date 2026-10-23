using System.Text;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Abstractions.Steam;

namespace SilverModManager.Networking.Steam;

internal sealed class SecretStoreAuthStorage(ISecretStore store, ILogger<SecretStoreAuthStorage> logger) : IAuthStorage
{
    public async Task<AuthLoadResult> TryLoadAsync(CancellationToken cancellationToken)
    {
        return await store.GetAsync(SteamSecretKeys.Auth, cancellationToken) switch
        {
            SecretResult.Found found => new AuthLoadResult.Loaded(Encoding.UTF8.GetBytes(found.Value)),
            SecretResult.NotFound => new AuthLoadResult.None(),
            SecretResult.Unavailable unavailable => new AuthLoadResult.Unavailable(unavailable.Reason),
            _ => throw new InvalidOperationException("Unknown secret result"),
        };
    }

    public async Task SaveAsync(byte[] data, CancellationToken cancellationToken)
    {
        var result = await store.SetAsync(SteamSecretKeys.Auth, Encoding.UTF8.GetString(data), cancellationToken);
        ThrowUnlessWritten(result, "save");
    }

    public async Task DeleteAsync(CancellationToken cancellationToken)
    {
        var result = await store.DeleteAsync(SteamSecretKeys.Auth, cancellationToken);
        ThrowUnlessWritten(result, "delete");
        logger.LogInformation("Removed the stored Steam login.");
    }

    private static void ThrowUnlessWritten(SecretWriteResult result, string action)
    {
        switch (result)
        {
            case SecretWriteResult.Written:
                return;
            case SecretWriteResult.Unavailable unavailable:
                throw new InvalidOperationException($"Could not {action} the Steam login: {unavailable.Reason}");
            case SecretWriteResult.ReadOnly readOnly:
                throw new InvalidOperationException($"Could not {action} the Steam login: it is provided by {readOnly.Source} and is read-only.");
            default:
                throw new InvalidOperationException("Unknown secret write result");
        }
    }
}
