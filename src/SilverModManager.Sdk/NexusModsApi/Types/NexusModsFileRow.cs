namespace SilverModManager.Sdk.NexusModsApi;

/// <summary>A file of a Nexus mod as its Files tab lists it.</summary>
public sealed record NexusModsFileRow(FileId Id, string Name, string Version, NexusModsFileCategory Category, long SizeBytes, DateTimeOffset Date);
