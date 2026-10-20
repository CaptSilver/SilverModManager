using FluentAssertions;
using System.Security.Cryptography;

namespace NexusMods.Sdk.Tests;

/// <summary>
/// Binary and vector art that was removed or replaced must not come back, for example through
/// a cherry-pick from upstream. A rename alone does not hide a file here because the check is
/// on content.
/// </summary>
public class RemovedAssetHashesTests
{
    private static readonly string[] ImageExtensions = ["*.png", "*.webp", "*.svg", "*.jpg", "*.ico"];
    private static readonly string[] ScannedFolders = ["src", ".idea"];

    private static readonly (string Sha256, string WasAt)[] Removed =
    [
        ("5a22eccfb001eacbf2756f2e21fa371dbafd9e84b20bb6a484d781bde33089cb", "Nexus logo, src/NexusMods.App.UI/Assets/nexus-logo.svg"),
        ("1020beb3e2220205683729286c9495b3d6ed9bef5f531e40bb8f7f8b2bb16ae5", "white Nexus logo, src/NexusMods.App.UI/Assets/nexus-logo-white.svg"),
        ("7b8fd45f45be062b8eca61f762155ca0fb1170da5a2100659f1d0933d5271ea2", "Nexus logo with text, src/NexusMods.App.UI/Assets/nexus-logo-with-text.svg"),
        ("f75653f0bf866419f32d11ebf47ab441d4a23039110b3bffd7eca140326dd2dc", "Nexus logo window icon, src/NexusMods.App.UI/Assets/nexus-logo.png"),
        ("b1f853bd621a61e9e348554efe43a0106ef43da7403b079ebf5ee2cbe7d5d153", "Nexus package icon, Nexus-Icon.png"),
        ("93814f936a3c5dc6e1a0cb0f8a94456e27db1de02ed91fd91d433d8a9450cc44", "Nexus IDE project icon, .idea/.idea.NexusMods.App/.idea/icon.svg"),
        ("3ae1df63cf2b5e1cf7a9da5560fa45bbe07855de21370844adbd7a8dd5d51a25", "Nexus Premium pictogram, src/NexusMods.App.UI/Assets/Pictograms/premium.svg"),
        ("176ca777861fc7d1b03f641e0b6f2394ae63093195e7c828c5f2e48a4183a6da", "design-time avatar photo, src/NexusMods.App.UI/Assets/DesignTime/avatar.webp"),
        ("60b930656aed033b996be72336d876a4276d59298a5dfa1353fbe5103b6821e5", "design-time collection tile, src/NexusMods.App.UI/Assets/DesignTime/collection_tile_image.png"),
        ("ccea3b43e0967da798efd7283cca6d8b49222f84a8b0f2ce73b92f8f3ad915a6", "publisher game art (cyberpunk_game.png), App.UI and Themes DesignTime folders"),
        ("8d1d6a4a7f6840e12d740e77e0e2ce24ee1bb73d14bc6bff6f340a7482374b4f", "publisher game art (fantasy_game.png), App.UI and Themes DesignTime folders"),
        ("f8c1133d5782d6552099303a5a0077d4f51a7839956687a10342eba36a968fb3", "design-time header background, src/NexusMods.App.UI/Assets/DesignTime/header-background.webp"),
        ("6c3b0293e610c3eef03c7eec51f07f9a66f89af500d53e6b75c735c7633916a9", "design-time thumbnail, src/NexusMods.App.UI/Assets/DesignTime/thumbnail.webp"),
        ("ec52f49af45c5d521fa7b0aa810573007b51a263d0fe2e163251a7c60f1778ac", "publisher game tile, src/NexusMods.App.UI/Assets/DesignTime/tile.webp"),
        ("21e2fa4af6ee5e6f2bedd325871f8426f809f5145708ed89f475841068c879bd", "FOMOD placeholder naming the upstream project, src/NexusMods.Games.FOMOD/Assets/InvalidImagePlaceholder.png"),
        ("c48d57e167d249c2d5159e02687239dc404d3b568ed9b544b3f89707fd1c9b1d", "Nexus-marked default collection image, src/NexusMods.Collections/default-collection-image.webp"),
    ];

    /// <summary>
    /// Files that share bytes with a removed design-time placeholder but are the supported game's
    /// own picker art, which the game module ships.
    /// </summary>
    private static readonly HashSet<string> SharedWithRemoved =
    [
        "src/NexusMods.Games.RedEngine/Resources/Cyberpunk2077/thumbnail.webp",
        "src/NexusMods.Games.RedEngine/Resources/Cyberpunk2077/tile.webp",
    ];

    [Fact]
    public void Test_NoRemovedAssetComesBack()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var files = ScannedFolders
            .Where(folder => Directory.Exists(Path.Combine(root, folder)))
            .SelectMany(folder => RepoScanGuard.EnumerateFiles(root, folder, ImageExtensions))
            .Concat(ImageExtensions.SelectMany(pattern => Directory.EnumerateFiles(root, pattern)).Select(path => RepoScanGuard.ToRelative(root, path)));

        var hashes = Removed.ToDictionary(r => r.Sha256, r => r.WasAt);
        var hits = files
            .Where(relative => !SharedWithRemoved.Contains(relative))
            .Select(relative => (relative, hash: Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, relative))))))
            .Where(x => hashes.ContainsKey(x.hash))
            .Select(x => $"{x.relative} is the removed {hashes[x.hash]}")
            .ToArray();

        hits.Should().BeEmpty("removed upstream art must not return under any name");
        SharedWithRemoved.Should().OnlyContain(relative => File.Exists(Path.Combine(root, relative)), "a stale allowlist entry hides nothing");
    }
}
