using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Mutagen.Bethesda;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.CreationEngine.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.IO;
using NSubstitute;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>
/// The plugin header reader reads what Mutagen wrote for every release a title in this project targets.
/// Mutagen stays in this project as the writer, so a header layout the reader got wrong would not round-trip.
/// </summary>
public class PluginHeaderParsingTests(IServiceProvider serviceProvider)
{
    public static TheoryData<Type, GameRelease> Titles => new()
    {
        { typeof(CreationEngine.SkyrimLE.SkyrimLE), GameRelease.SkyrimLE },
        { typeof(CreationEngine.SkyrimSE.SkyrimSE), GameRelease.SkyrimSE },
        { typeof(CreationEngine.SkyrimVR.SkyrimVR), GameRelease.SkyrimVR },
        { typeof(CreationEngine.Enderal.Enderal), GameRelease.EnderalLE },
        { typeof(CreationEngine.EnderalSE.EnderalSE), GameRelease.EnderalSE },
        { typeof(CreationEngine.Fallout4.Fallout4), GameRelease.Fallout4 },
        { typeof(CreationEngine.Fallout4VR.Fallout4VR), GameRelease.Fallout4VR },
    };

    private ICreationEngineGame Reading(Type type, Stream? stream, Hash hash)
    {
        var source = Substitute.For<IStreamSourceDispatcher>();
        source.OpenAsync(hash).Returns(_ => ValueTask.FromResult(stream));
        return (ICreationEngineGame)System.Activator.CreateInstance(type, new WithStreamSource(serviceProvider, source))!;
    }

    [Theory]
    [MemberData(nameof(Titles))]
    public async Task ParsePlugin_ReturnsTheNameAndMastersOfAPluginWrittenForTheRelease(Type type, GameRelease release)
    {
        var bytes = BethesdaPluginBytes.Bytes(release, "Patch.esp", "Base.esm", "Other.esm");
        var hash = bytes.xxHash3();
        var game = Reading(type, new MemoryStream(bytes), hash);

        var header = await game.ParsePlugin(hash, RelativePath.FromUnsanitizedInput("Patch.esp"));

        header.Should().NotBeNull();
        header!.FileName.Should().Be("Patch.esp");
        header.Masters.Should().Equal("Base.esm", "Other.esm");
        header.Format.Should().Be(PluginFormat.Tes4);
    }

    [Fact]
    public async Task ParsePlugin_NoStreamSourceHasTheBytes_ReturnsNull()
    {
        var game = Reading(typeof(CreationEngine.SkyrimSE.SkyrimSE), null, Hash.From(2));

        var header = await game.ParsePlugin(Hash.From(2), RelativePath.FromUnsanitizedInput("A.esp"));

        header.Should().BeNull();
    }

    private sealed class WithStreamSource(IServiceProvider inner, IStreamSourceDispatcher source) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IStreamSourceDispatcher) ? source : inner.GetService(serviceType);
    }
}
