using DynamicData.Kernel;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Generic.Tests.Simple;

/// <summary>Definitions the tests build themselves, because no shipped row uses a root outside the game folder yet.</summary>
internal static class SimpleDefinitions
{
    public static SimpleGameDefinition With(ModLocation modRoot, string id = "Tests.Simple") => new()
    {
        GameId = GameId.From(id),
        DisplayName = id,
        NexusModsGameId = Optional<Sdk.NexusModsApi.NexusModsGameId>.None,
        Stores = new StoreIdentifiers(GameId.From(id)),
        Executable = "bin/game.exe",
        ModRoot = modRoot,
        Install = new SimpleInstallRules(),
    };
}
