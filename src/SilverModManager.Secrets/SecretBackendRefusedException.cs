using SilverModManager.Abstractions.Secrets;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Secrets;

/// <summary>
/// The secret storage setting names a backend that cannot be used, so no store is built.
/// </summary>
public sealed class SecretBackendRefusedException : UserFacingException
{
    private SecretBackendRefusedException(string message) : base(message) { }

    public override string Title => "Secret storage is not set up correctly";

    internal static SecretBackendRefusedException NotOffered(SecretBackendChoice choice) => new(
        $"Secret storage is set to {choice}, which is not a choice for users. Settings cannot be opened while this is wrong, so fix it with the command line: settings set -k SecretsSettings.Backend -v Auto for 'Keyring, then file', or -v FileOnly for 'File only'.");

    internal static SecretBackendRefusedException Unknown(SecretBackendChoice choice) => new(
        $"Secret storage names a backend this version does not know ({(int)choice}). Settings cannot be opened while this is wrong, so fix it with the command line: settings set -k SecretsSettings.Backend -v Auto for 'Keyring, then file', or -v FileOnly for 'File only'.");
}
