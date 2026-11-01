using System.Collections.Immutable;
using DynamicData.Kernel;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Generic;

/// <summary>
/// A game whose identity, stores, executable and locations are a record, not a type. One class serves every record of
/// its kind, registered once per definition. A family subclass adds only what differs between families.
/// </summary>
public abstract class DefinedGame<TDefinition>(IServiceProvider provider, TDefinition definition) : AGameBase(provider)
    where TDefinition : IGameDefinition
{
    public TDefinition Definition { get; } = definition;

    protected sealed override GameId GameIdValue => Definition.GameId;
    protected sealed override string DisplayNameValue => Definition.DisplayName;
    protected sealed override Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameIdValue => Definition.NexusModsGameId;

    public sealed override StoreIdentifiers StoreIdentifiers => Definition.Stores;
    protected sealed override RelativePath PrimaryExe => Definition.Executable;

    public override bool HasNativeLinuxBuild => Definition.NativeLinuxExecutable is not null;
    public override RelativePath? NativeLinuxPrimaryFile => Definition.NativeLinuxExecutable;
    public override IReadOnlyDictionary<string, string> LocationNames => Definition.LocationNames;
    public override IReadOnlyDictionary<string, string> LocationSentenceNames => Definition.LocationSentenceNames;

    public override ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) =>
        Definition.GetLocations(fileSystem, gameLocatorResult);
}
