using System.Text;
using FluentAssertions;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;

namespace NexusMods.DataModel.Tests;

public class GameFileStreamSourceTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<GameFileStreamSourceTests>(helper)
{
    private const string Content = "a game file the diagnostics read";
    private static readonly GamePath FilePath = new(LocationId.Game, "Data/plugin.esm");

    protected override async Task GenerateGameFiles()
    {
        var file = GameInstallation.Locations.ToAbsolutePath(FilePath);
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(Content);
    }

    [Fact]
    public async Task ResolvesAnIndexedGameFileByItsHash()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(loadout);

        var source = new GameFileStreamSource(Connection, ServiceProvider);
        var hash = Encoding.UTF8.GetBytes(Content).xxHash3();

        source.Resolve(hash).Should().Be(GameInstallation.Locations.ToAbsolutePath(FilePath));

        await using var stream = await source.OpenAsync(hash);
        stream.Should().NotBeNull();
        using var reader = new StreamReader(stream!);
        (await reader.ReadToEndAsync()).Should().Be(Content);
    }
}
