using System.Text.Json;
using System.Text.Json.Serialization;
using JetBrains.Annotations;
using OneOf;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Library;

namespace SilverModManager.App.UI.WorkspaceSystem;

/// <summary>
/// Writes the file a text editor tab has open as <c>{"Loadout": id}</c> or <c>{"Library": id}</c>. The default
/// object converter walks every <c>AsT*</c> accessor of a <see cref="OneOf{T0,T1}"/>, and the ones for the
/// alternative that is not held throw.
/// </summary>
[UsedImplicitly]
public class TextEditorFileIdJsonConverter : JsonConverter<OneOf<LoadoutFileId, LibraryFileId>>
{
    private const string LoadoutName = "Loadout";
    private const string LibraryName = "Library";

    public override OneOf<LoadoutFileId, LibraryFileId> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected a file id object");
        reader.Read();
        if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected a file id kind");

        var kind = reader.GetString();
        reader.Read();

        OneOf<LoadoutFileId, LibraryFileId> result = kind switch
        {
            LoadoutName => JsonSerializer.Deserialize<LoadoutFileId>(ref reader, options),
            LibraryName => JsonSerializer.Deserialize<LibraryFileId>(ref reader, options),
            _ => throw new JsonException($"Unknown file id kind: {kind}"),
        };

        reader.Read();
        if (reader.TokenType != JsonTokenType.EndObject) throw new JsonException("Expected the end of the file id object");
        return result;
    }

    public override void Write(Utf8JsonWriter writer, OneOf<LoadoutFileId, LibraryFileId> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        value.Switch(
            loadoutFile =>
            {
                writer.WritePropertyName(LoadoutName);
                JsonSerializer.Serialize(writer, loadoutFile, options);
            },
            libraryFile =>
            {
                writer.WritePropertyName(LibraryName);
                JsonSerializer.Serialize(writer, libraryFile, options);
            });
        writer.WriteEndObject();
    }
}
