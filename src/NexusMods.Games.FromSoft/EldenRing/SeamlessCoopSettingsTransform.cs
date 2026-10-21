using System.Text;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.FromSoft.EldenRing;

/// <summary>
/// Puts the co-op password into Seamless Co-op's settings file as it is written for a session. The deployed file and
/// the file store keep the template; the password goes in only here.
/// </summary>
public sealed class SeamlessCoopSettingsTransform(ISettingsManager settings) : IConfigurationLayerTransform
{
    public bool AppliesTo(string relativePath)
        => relativePath.Equals($"{SeamlessCoop.Folder}/{SeamlessCoop.Settings}", StringComparison.OrdinalIgnoreCase);

    public async Task WriteAsync(string sourceFile, string targetFile, CancellationToken cancellationToken)
    {
        // The file is created private rather than narrowed afterwards, so the password is never readable by others.
        File.Delete(targetFile);
        var privateFile = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite };
        await using var writer = new StreamWriter(targetFile, new UTF8Encoding(false), privateFile);
        var password = settings.Get<FromSoftSettings>().CoopPassword;
        await writer.WriteAsync(SeamlessCoopSettings.Inject(await File.ReadAllTextAsync(sourceFile, cancellationToken), password).AsMemory(), cancellationToken);
    }
}
