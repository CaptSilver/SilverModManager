using System.Text;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Secrets;
using NexusMods.Paths;
using NexusMods.Sdk;

namespace NexusMods.Networking.Steam.Migration;

/// <summary>
/// Moves the login that older versions kept in a plain file into the secret store.
/// </summary>
internal sealed class SteamAuthFileMigration(IFileSystem fileSystem, ILogger<SteamAuthFileMigration> logger) : ISecretMigrationStep
{
    // Where the login lived before the secret store existed.
    private readonly AbsolutePath _legacyPath = fileSystem.GetKnownPath(KnownPath.LocalApplicationDataDirectory)
                                                / ApplicationConstants.DataDirectoryName
                                                / "steam/auth";

    public string Name => "steam-auth-file";

    public async Task<SecretMigrationOutcome> RunAsync(ISecretStore store, CancellationToken cancellationToken)
    {
        if (!_legacyPath.FileExists) return new SecretMigrationOutcome.NothingToMove();

        var value = Encoding.UTF8.GetString(await _legacyPath.ReadAllBytesAsync(cancellationToken));
        switch (await store.SetAsync(SteamSecretKeys.Auth, value, cancellationToken))
        {
            case SecretWriteResult.Written:
                _legacyPath.Delete();
                logger.LogInformation("Moved the Steam login into the secret store.");
                return new SecretMigrationOutcome.Moved();
            case SecretWriteResult.Unavailable unavailable:
                return new SecretMigrationOutcome.Deferred(unavailable.Reason);
            case SecretWriteResult.ReadOnly readOnly:
                return new SecretMigrationOutcome.Deferred($"the secret is provided by {readOnly.Source} and is read-only");
            default:
                throw new InvalidOperationException("Unknown secret write result");
        }
    }
}
