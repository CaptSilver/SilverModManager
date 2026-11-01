using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Games.FromSoft.Doctor;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Tests.Heal;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Games.FromSoft.Tests;

public sealed class SteamFolderCheckTests : IDisposable
{
    private static readonly FromSoftGameDefinition Definition = EldenRingGame.Definition;

    private static readonly byte[] Regulation = "regulation as shipped"u8.ToArray();
    private static readonly byte[] Library = "a library as shipp"u8.ToArray();
    private static readonly byte[] Archive = "archive as shipped"u8.ToArray();
    private static readonly byte[] Exe = "game executable"u8.ToArray();

    private readonly string _root = Directory.CreateTempSubdirectory("steam-folder-check").FullName;
    private readonly SteamFolderCheck _check = new(NullLogger<SteamFolderCheck>.Instance);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AbsolutePath Root => FileSystem.Shared.FromUnsanitizedFullPath(_root);

    private string Disk(string relative) => Path.Combine(_root, relative);

    /// <summary>What Steam shipped; the same files are written to the folder unless a test changes them.</summary>
    private static readonly (string Path, byte[] Content)[] Shipped =
    [
        ("Game/eldenring.exe", Exe),
        ("Game/regulation.bin", Regulation),
        ("Game/steam_api64.dll", Library),
        ("Game/Data0.bdt", Archive),
        ("Game/sd/sd.bdt", Archive),
        ("ArtbookOST/readme.txt", "artbook"u8.ToArray()),
    ];

    private static IReadOnlyList<ManifestFile> Manifest(bool encrypted = false, bool backslashes = false)
        => Shipped.Select(f => new ManifestFile(
            encrypted ? null : backslashes ? f.Path.Replace('/', '\\') : f.Path,
            (ulong)f.Content.Length,
            FakeManifestSource.Sha1(f.Content))).ToArray();

    private void Install()
    {
        foreach (var (path, content) in Shipped)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Disk(path))!);
            File.WriteAllBytes(Disk(path), content);
        }
    }

    private void Replace(string relative, byte[] content, DateTime? keepTime = null)
    {
        var time = keepTime ?? File.GetLastWriteTimeUtc(Disk(relative));
        File.WriteAllBytes(Disk(relative), content);
        File.SetLastWriteTimeUtc(Disk(relative), time);
    }

    private async Task<SteamFolderFindings> Run(IReadOnlyList<ManifestFile>? manifest = null) => await _check.Check(Definition, Root, manifest ?? Manifest());

    private static byte[] Same(byte[] shipped) => shipped.Select(b => (byte)(b ^ 1)).ToArray();

    [Fact]
    public async Task AFolderOfSteamsFilesHasNothingToReport()
    {
        Install();

        var found = await Run();

        found.Changed.Should().BeEmpty();
        found.ModFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task AFileOfAnotherSizeIsChanged()
    {
        Install();
        Replace("Game/steam_api64.dll", [..Library, 1]);

        (await Run()).Changed.Should().Equal("steam_api64.dll");
    }

    [Fact]
    public async Task ARegulationOfTheSameSizeWithOtherBytesIsChanged()
    {
        Install();
        Replace("Game/regulation.bin", Same(Regulation));

        (await Run()).Changed.Should().Equal("regulation.bin");
    }

    [Fact]
    public async Task ARegulationIsHashedEvenWhenAnEarlierRunSawItUnchanged()
    {
        Install();
        (await Run()).Changed.Should().BeEmpty();

        Replace("Game/regulation.bin", Same(Regulation));

        (await Run()).Changed.Should().Equal("regulation.bin");
    }

    [Fact]
    public async Task AnExecutableIsHashedEvenWhenAnEarlierRunSawItUnchanged()
    {
        Install();
        await Run();

        Replace("Game/eldenring.exe", Same(Exe));

        (await Run()).Changed.Should().Equal("eldenring.exe");
    }

    [Fact]
    public async Task ADllOfTheSameSizeWithOtherBytesIsChangedTheFirstTimeItIsSeen()
    {
        Install();
        Replace("Game/steam_api64.dll", Same(Library));

        (await Run()).Changed.Should().Equal("steam_api64.dll");
    }

    [Fact]
    public async Task AVerdictIsReusedWhileSizeAndTimeAreTheSame()
    {
        Install();
        (await Run()).Changed.Should().BeEmpty();

        // Swapped under the same size and time: the cache is the only thing that could still call it fine.
        Replace("Game/steam_api64.dll", Same(Library));

        (await Run()).Changed.Should().BeEmpty();
    }

    [Fact]
    public async Task AVerdictIsDroppedWhenTheTimeMoves()
    {
        Install();
        await Run();

        Replace("Game/steam_api64.dll", Same(Library), keepTime: DateTime.UtcNow.AddDays(1));

        (await Run()).Changed.Should().Equal("steam_api64.dll");
    }

    [Fact]
    public async Task AVerdictIsDroppedWhenTheManifestListsAnotherHash()
    {
        Install();
        await Run();

        var updated = Manifest().Select(f => f.Path == "Game/steam_api64.dll" ? f with { Hash = FakeManifestSource.Sha1(Same(Library)) } : f).ToArray();

        (await _check.Check(Definition, Root, updated)).Changed.Should().Equal("steam_api64.dll");
    }

    [Fact]
    public async Task AnArchiveIsComparedBySizeAndNeverHashed()
    {
        Install();
        Replace("Game/Data0.bdt", Same(Archive));
        Replace("Game/sd/sd.bdt", [..Archive, 1]);

        (await Run()).Changed.Should().Equal("sd/sd.bdt");
    }

    [Fact]
    public async Task AFileSteamListsThatIsNotThereIsNotChanged()
    {
        Install();
        File.Delete(Disk("Game/steam_api64.dll"));

        (await Run()).Changed.Should().BeEmpty();
    }

    [Fact]
    public async Task FilesOutsideTheGameFolderAreNotLookedAt()
    {
        Install();
        Replace("ArtbookOST/readme.txt", "something else"u8.ToArray());

        (await Run()).Changed.Should().BeEmpty();
    }

    [Fact]
    public async Task ManifestPathsWithBackslashesAreMatched()
    {
        Install();
        Replace("Game/regulation.bin", Same(Regulation));

        (await Run(Manifest(backslashes: true))).Changed.Should().Equal("regulation.bin");
    }

    [Fact]
    public async Task WithoutFileNamesOnlyTheRegulationAndTheExecutablesAreComparedByHash()
    {
        Install();
        Replace("Game/steam_api64.dll", Same(Library));
        (await Run(Manifest(encrypted: true))).Changed.Should().BeEmpty();

        Replace("Game/regulation.bin", Same(Regulation));

        (await Run(Manifest(encrypted: true))).Changed.Should().Equal("regulation.bin");
    }

    // Steam gives an empty file an all-zero hash in the depot manifest, which no content ever hashes to.
    private static ManifestFile EmptyEntry(string path) => new(path, 0, Sha1Value.From(new byte[20]));

    [Theory]
    [InlineData("Game/empty.dat")]
    [InlineData("Game/regulation.bin")]
    [InlineData("Game/empty.exe")]
    public async Task AnEmptyFileSteamListsWithAZeroHashIsNotChanged(string path)
    {
        Install();
        File.WriteAllBytes(Disk(path), []);
        var manifest = Manifest().Where(f => f.Path != path).Append(EmptyEntry(path)).ToArray();

        var found = await Run(manifest);

        found.Changed.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEmptyFileWithoutFileNamesIsNotChangedWhenTheManifestListsAnEmptyOne()
    {
        Install();
        File.WriteAllBytes(Disk("Game/regulation.bin"), []);
        var manifest = Manifest(encrypted: true).Append(EmptyEntry("hidden")).ToArray();

        (await Run(manifest)).Changed.Should().BeEmpty();
    }

    [Fact]
    public async Task FilesOnlyModsPutInTheGameFolderAreListedBesideTheChangedOnes()
    {
        Install();
        File.WriteAllText(Disk("Game/dinput8.dll"), "hook");
        Directory.CreateDirectory(Disk("Game/mods"));
        Replace("Game/regulation.bin", Same(Regulation));

        var found = await Run();

        found.Changed.Should().Equal("regulation.bin");
        found.ModFiles.Should().BeEquivalentTo("dinput8.dll", "mods/");
    }

    [Fact]
    public async Task AMissingGameFolderHasNothingToReport()
    {
        var found = await Run();

        found.Changed.Should().BeEmpty();
        found.ModFiles.Should().BeEmpty();
    }
}
