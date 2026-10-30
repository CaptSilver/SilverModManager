using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SilverModManager.Abstractions.NexusWebApi.DTOs.Interfaces;


// ReSharper disable UnusedAutoPropertyAccessor.Global
namespace SilverModManager.Abstractions.NexusWebApi.DTOs;

/// <summary>
/// Represents the collection of downloadable files tied to a mod.
/// </summary>
public class ModFiles : IJsonSerializable<ModFiles>
{
    /// <summary/>
    [JsonPropertyName("files")]
    public required ModFile[] Files { get; set; }

    /// <inheritdoc />
    public static JsonTypeInfo<ModFiles> GetTypeInfo() => ModFilesContext.Default.ModFiles;
}

/// <summary/>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ModFiles))]
public partial class ModFilesContext : JsonSerializerContext { }
