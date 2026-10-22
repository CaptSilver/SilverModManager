using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mutagen.Bethesda;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Sorting;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.PluginLists;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NSubstitute;

namespace NexusMods.Games.CreationEngine.Tests.SkyrimFamily;

/// <summary>
/// Each title writes its plugin list at its own path, with or without the active marker, and reads
/// plugin headers through the shared header reader.
/// </summary>
public class SkyrimFamilyPluginsFileTests(IServiceProvider serviceProvider)
{
    public static TheoryData<Type, GameRelease, string, string> Titles => new()
    {
        { typeof(CreationEngine.SkyrimLE.SkyrimLE), GameRelease.SkyrimLE, "plugins.txt", "" },
        { typeof(CreationEngine.SkyrimVR.SkyrimVR), GameRelease.SkyrimVR, "plugins.txt", "*" },
        { typeof(CreationEngine.Enderal.Enderal), GameRelease.EnderalLE, "plugins.txt", "" },
        { typeof(CreationEngine.EnderalSE.EnderalSE), GameRelease.EnderalSE, "Plugins.txt", "*" },
    };

    public static TheoryData<Type, GameRelease> Releases => new()
    {
        { typeof(CreationEngine.SkyrimLE.SkyrimLE), GameRelease.SkyrimLE },
        { typeof(CreationEngine.SkyrimVR.SkyrimVR), GameRelease.SkyrimVR },
        { typeof(CreationEngine.Enderal.Enderal), GameRelease.EnderalLE },
        { typeof(CreationEngine.EnderalSE.EnderalSE), GameRelease.EnderalSE },
    };

    public static TheoryData<Type, string> Paths => new()
    {
        { typeof(CreationEngine.SkyrimLE.SkyrimLE), "plugins.txt" },
        { typeof(CreationEngine.SkyrimVR.SkyrimVR), "plugins.txt" },
        { typeof(CreationEngine.Enderal.Enderal), "plugins.txt" },
        { typeof(CreationEngine.EnderalSE.EnderalSE), "Plugins.txt" },
    };

    private ICreationEngineGame Registered(Type type) => serviceProvider.GetServices<IGame>().OfType<ICreationEngineGame>().Single(g => g.GetType() == type);

    // A fresh instance of the title whose plugin reads come from these bytes, so ParsePlugin runs the
    // class's own header format rather than one the test picks.
    private ICreationEngineGame Reading(Type type, byte[] bytes, out Hash hash)
    {
        var stored = bytes.xxHash3();
        var source = Substitute.For<IStreamSourceDispatcher>();
        source.OpenAsync(stored).Returns(_ => ValueTask.FromResult<Stream?>(new MemoryStream(bytes)));
        hash = stored;
        return (ICreationEngineGame)System.Activator.CreateInstance(type, new WithStreamSource(serviceProvider, source))!;
    }

    [Theory]
    [MemberData(nameof(Titles))]
    public void PluginsFileAndFormatAreTheTitlesOwn(Type type, GameRelease _, string file, string marker)
    {
        var game = Registered(type);

        game.PluginsFile.Should().Be(new GamePath(LocationId.AppData, file));
        game.PluginList(null!).Should().BeOfType(marker == "*" ? typeof(AsteriskMechanism) : typeof(PlainMechanism));
        game.DataPath.Should().Be(new GamePath(LocationId.Game, "Data"));
    }

    [Theory]
    [MemberData(nameof(Releases))]
    public async Task PluginHeadersAreReadForTheTitle(Type type, GameRelease release)
    {
        var game = Reading(type, BethesdaPluginBytes.Bytes(release, "A.esp", "Skyrim.esm"), out var hash);

        var header = await game.ParsePlugin(hash, RelativePath.FromUnsanitizedInput("A.esp"));

        header!.FileName.Should().Be("A.esp");
        header.Masters.Should().Equal("Skyrim.esm");
        header.Format.Should().Be(PluginFormat.Tes4);
    }

    [Theory]
    [MemberData(nameof(Titles))]
    public async Task WriteListsAPluginWithTheTitlesMarker(Type type, GameRelease release, string file, string marker)
    {
        var bytes = BethesdaPluginBytes.Bytes(release, "A.esp", "Skyrim.esm");
        var game = Reading(type, bytes, out var hash);
        var plugins = new PluginsFile(NullLogger<PluginsFile>.Instance, game, serviceProvider.GetRequiredService<ISorter>(), marker);
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(LocationId.Game, "Data/A.esp")] = new()
            {
                Loadout = new SyncNodePart { Hash = hash, Size = NexusMods.Paths.Size.From((ulong)bytes.Length), LastModifiedTicks = 0 },
                SourceItemType = LoadoutSourceItemType.Game,
            },
        };

        using var stream = new MemoryStream();
        await plugins.Write(stream, default, tree);
        var lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        plugins.Path.Should().Be(new GamePath(LocationId.AppData, file));
        lines.Where(l => !l.StartsWith('#')).Should().Equal($"{marker}A.esp");
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void TheTitlesMechanismOffersThePluginsFileAtTheTitlesPath(Type type, string file)
    {
        var game = Registered(type);

        var offered = game.PluginList(null!).IntrinsicFiles(game, default, serviceProvider).ToList();

        offered.Select(f => f.Path).Should().Equal(new GamePath(LocationId.AppData, file));
        offered.Single().Should().BeOfType<PluginsFile>();
    }

    [Theory]
    [InlineData(typeof(CreationEngine.SkyrimLE.SkyrimLE))]
    [InlineData(typeof(CreationEngine.SkyrimVR.SkyrimVR))]
    [InlineData(typeof(CreationEngine.Enderal.Enderal))]
    [InlineData(typeof(CreationEngine.EnderalSE.EnderalSE))]
    public void MissingMastersAreChecked(Type type)
    {
        var game = (IGame)Registered(type);

        game.DiagnosticEmitters.Select(e => e.GetType()).Should().Contain(typeof(Emitters.MissingMasterEmitter));
    }

    private sealed class WithStreamSource(IServiceProvider inner, IStreamSourceDispatcher source) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IStreamSourceDispatcher) ? source : inner.GetService(serviceType);
    }
}
