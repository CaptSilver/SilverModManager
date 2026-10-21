using NexusMods.Games.Declarative.Capabilities;
using NexusMods.Games.Declarative.Schema;

namespace NexusMods.Games.Declarative.Tests;

/// <summary>Config of the stand-in capability; mirrors the block the sample definition carries.</summary>
public sealed record SampleCapabilityConfig
{
    public string DataFolder { get; init; } = "";
    public SamplePluginsFile? PluginsFile { get; init; }
    public string Parser { get; init; } = "none";
    public SampleScriptExtender? ScriptExtender { get; init; }
    public string[] StopPatterns { get; init; } = [];
    public string[] OfficialContent { get; init; } = [];
}

public sealed record SamplePluginsFile
{
    public string Location { get; init; } = "";
    public string Path { get; init; } = "";
    public string Format { get; init; } = "";
}

public sealed record SampleScriptExtender
{
    public string Loader { get; init; } = "";
    public string[] Files { get; init; } = [];
}

public sealed class SampleCapability(string name = "creationEngine") : IGameCapability
{
    public string Name => name;
    public Type ConfigType => typeof(SampleCapabilityConfig);
    public CapabilityParts Attach(GameDefinition definition, object config, IServiceProvider services) => new();
}
