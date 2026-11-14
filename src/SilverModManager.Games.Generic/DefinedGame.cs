using System.Collections.Immutable;
using System.Runtime.InteropServices;
using DynamicData.Kernel;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Generic;

/// <summary>
/// A game whose identity, stores, executable and locations are a record, not a type. One class serves every record of
/// its kind, registered once per definition. A family subclass adds only what differs between families.
/// </summary>
public abstract class DefinedGame<TDefinition>(IServiceProvider provider, TDefinition definition) : AGameBase(provider), IHasGameSources
    where TDefinition : IGameDefinition
{
    public TDefinition Definition { get; } = definition;

    protected sealed override GameId GameIdValue => Definition.GameId;
    protected sealed override string DisplayNameValue => Definition.DisplayName;
    protected sealed override Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameIdValue => Definition.NexusModsGameId;

    /// <inheritdoc />
    public GameSources Sources => Definition.Sources;

    public sealed override StoreIdentifiers StoreIdentifiers => Definition.Stores;
    protected sealed override RelativePath PrimaryExe => Definition.Executable;

    public override bool HasNativeLinuxBuild => Definition.NativeLinuxExecutable is not null;
    public override RelativePath? NativeLinuxPrimaryFile => Definition.NativeLinuxExecutable;

    /// <summary>
    /// The native executable on a Linux install, the Windows one under Proton. A game with no native build never reads
    /// <paramref name="installation"/>, so a caller that has none keeps working.
    /// </summary>
    public override GamePath GetPrimaryFile(GameInstallation installation)
    {
        if (Definition.NativeLinuxExecutable is not { } native || installation.LocatorResult.Platform != OSPlatform.Linux)
            return base.GetPrimaryFile(installation);
        return new GamePath(LocationId.Game, native);
    }

    public override IReadOnlyDictionary<string, string> LocationNames => Definition.LocationNames;
    public override IReadOnlyDictionary<string, string> LocationSentenceNames => Definition.LocationSentenceNames;

    public override ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) =>
        Definition.GetLocations(fileSystem, gameLocatorResult);
}
