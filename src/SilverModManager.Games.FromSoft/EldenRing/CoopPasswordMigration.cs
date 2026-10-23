using System.Text.Json;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Secrets;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.FromSoft.EldenRing;

/// <summary>
/// Moves the co-op password that older versions kept in the FromSoft settings file into the secret store, and
/// removes it from the file.
/// </summary>
internal sealed class CoopPasswordMigration(IFileSystem fileSystem, ILogger<CoopPasswordMigration> logger) : ISecretMigrationStep
{
    // The property the settings type had. It is read here as plain JSON because the type no longer has it.
    private const string PropertyName = "CoopPassword";

    // The path JsonStorageBackend builds for an unkeyed settings value: <XDG_DATA_HOME>/<data dir>/Configs/<id>.json.
    // The backend lives in a project this one does not reference, so the rule is repeated here.
    private readonly AbsolutePath _settingsFile = fileSystem.GetKnownPath(KnownPath.XDG_DATA_HOME)
                                                  / ApplicationConstants.DataDirectoryName / "Configs"
                                                  / $"{SettingsIds.For<FromSoftSettings>(null, '_')}.json";

    public string Name => "fromsoft-coop-password";

    public async Task<SecretMigrationOutcome> RunAsync(ISecretStore store, CancellationToken cancellationToken)
    {
        if (!_settingsFile.FileExists) return new SecretMigrationOutcome.NothingToMove();

        using var document = JsonDocument.Parse(await _settingsFile.ReadAllBytesAsync(cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(PropertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || property.GetString() is not { Length: > 0 } password)
            return new SecretMigrationOutcome.NothingToMove();

        switch (await store.SetAsync(FromSoftSecretKeys.CoopPassword, password, cancellationToken))
        {
            case SecretWriteResult.Written:
                break;
            case SecretWriteResult.Unavailable unavailable:
                return new SecretMigrationOutcome.Deferred(unavailable.Reason);
            case SecretWriteResult.ReadOnly readOnly:
                return new SecretMigrationOutcome.Deferred($"the secret is provided by {readOnly.Source} and is read-only");
            default:
                throw new InvalidOperationException("Unknown secret write result");
        }

        await RewriteWithoutPassword(document.RootElement, cancellationToken);
        logger.LogInformation("Moved the Seamless Co-op password into the secret store.");
        return new SecretMigrationOutcome.Moved();
    }

    private async Task RewriteWithoutPassword(JsonElement root, CancellationToken cancellationToken)
    {
        var temp = _settingsFile.AppendExtension(new Extension(".tmp"));
        await using (var stream = temp.Open(FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject().Where(p => p.Name != PropertyName))
                property.WriteTo(writer);
            writer.WriteEndObject();
        }

        await temp.MoveToAsync(_settingsFile, overwrite: true, token: cancellationToken);
    }
}
