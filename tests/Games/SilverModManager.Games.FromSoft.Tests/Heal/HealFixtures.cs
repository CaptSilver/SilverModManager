using System.Collections.Immutable;
using System.Security.Cryptography;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Tests.Build;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using NSubstitute;

namespace SilverModManager.Games.FromSoft.Tests.Heal;

public static class HealFixtures
{
    public static string Sha(byte[] blob) => Convert.ToHexStringLower(SHA256.HashData(blob));

    /// <summary>The identity that names exactly the bytes of <paramref name="blob"/>.</summary>
    public static BuildIdentity Identity(byte[] blob, string version = BuildIds.RegulationVersion)
        => BuildIds.Make(regulationVersion: version, regulationHash: Sha(blob));

    /// <summary>An Elden Ring installation under <paramref name="root"/>; the regulation is written only when given.</summary>
    public static GameInstallation Game(string root, byte[]? regulation = null, AbsolutePath? storeRoot = null, params ulong[] manifestIds)
    {
        var folder = Path.Combine(root, EldenRingPaths.GameSubfolder);
        Directory.CreateDirectory(folder);
        if (regulation is not null) File.WriteAllBytes(Path.Combine(folder, "regulation.bin"), regulation);

        // NSubstitute does not run default interface members, so Layout and Formats are configured beside Definition.
        var game = Substitute.For<IFromSoftGame>();
        game.Definition.Returns(EldenRingGame.Definition);
        game.Layout.Returns(EldenRingGame.Definition.Layout);
        game.Formats.Returns(EldenRingGame.Definition.Formats);
        game.DisplayName.Returns("Elden Ring");

        var path = FileSystem.Shared.FromUnsanitizedFullPath(root);
        var result = new GameLocatorResult
        {
            Game = game,
            Path = path,
            Store = GameStore.Steam,
            StoreIdentifier = "1245620",
            LocatorIds = manifestIds.Select(id => LocatorId.From(id.ToString())).ToImmutableArray(),
            StoreRoot = storeRoot,
            Locator = Substitute.For<IGameLocator>(),
            StoreBuildId = "23850278",
        };
        var locations = GameLocations.Create(new Dictionary<LocationId, AbsolutePath> { { LocationId.Game, path } }.ToImmutableDictionary());
        return new GameInstallation(result, locations);
    }

    public static void WriteRegulation(string root, byte[] blob)
        => File.WriteAllBytes(Path.Combine(root, EldenRingPaths.GameSubfolder, "regulation.bin"), blob);
}
