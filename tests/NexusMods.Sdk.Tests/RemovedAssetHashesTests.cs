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
            .Select(relative => (relative, hash: Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, relative))))))
            .Where(x => hashes.ContainsKey(x.hash))
            .Select(x => $"{x.relative} is the removed {hashes[x.hash]}")
            .ToArray();

        hits.Should().BeEmpty("removed upstream art must not return under any name");
    }
}
