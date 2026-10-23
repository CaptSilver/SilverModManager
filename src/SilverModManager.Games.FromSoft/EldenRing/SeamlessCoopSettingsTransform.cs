using System.Text;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Games.FromSoft.Me3;
using SilverModManager.Games.FromSoft.Overlay;

namespace SilverModManager.Games.FromSoft.EldenRing;

/// <summary>
/// Puts the co-op password into Seamless Co-op's settings file as it is written for a session. The deployed file and
/// the file store keep the template; the password goes in only here.
/// </summary>
public sealed class SeamlessCoopSettingsTransform(ISecretStore secrets) : IConfigurationLayerTransform
{
    public bool AppliesTo(string relativePath)
        => relativePath.Equals($"{SeamlessCoop.Folder}/{SeamlessCoop.Settings}", StringComparison.OrdinalIgnoreCase);

    public async Task WriteAsync(string sourceFile, string targetFile, CancellationToken cancellationToken)
    {
        // Read before touching the target: a store that cannot answer must not leave a file that starts a session
        // without the password.
        var password = await secrets.GetAsync(FromSoftSecretKeys.CoopPassword, cancellationToken) switch
        {
            SecretResult.Found found => found.Value,
            SecretResult.NotFound => "",
            SecretResult.Unavailable unavailable => throw new InvalidOperationException(
                $"The Seamless Co-op password could not be read ({unavailable.Reason}); the settings file was not written so a session without the password is not started"),
            _ => throw new InvalidOperationException("Unknown secret result"),
        };

        // The file is created private rather than narrowed afterwards, so the password is never readable by others.
        File.Delete(targetFile);
        var privateFile = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite };
        await using var writer = new StreamWriter(targetFile, new UTF8Encoding(false), privateFile);
        await writer.WriteAsync(SeamlessCoopSettings.Inject(await File.ReadAllTextAsync(sourceFile, cancellationToken), password).AsMemory(), cancellationToken);
    }
}
