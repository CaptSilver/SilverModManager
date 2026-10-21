using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Mutagen.Bethesda;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.IO;
using NSubstitute;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests;

public class MutagenPluginReaderTests
{
    private static (IStreamSourceDispatcher Source, Hash Hash) Stored(byte[] bytes)
    {
        var hash = bytes.xxHash3();
        var source = Substitute.For<IStreamSourceDispatcher>();
        source.OpenAsync(hash).Returns(_ => ValueTask.FromResult<Stream?>(new MemoryStream(bytes)));
        return (source, hash);
    }

    [Theory]
    [InlineData(GameRelease.SkyrimLE)]
    [InlineData(GameRelease.SkyrimSE)]
    [InlineData(GameRelease.SkyrimVR)]
    [InlineData(GameRelease.EnderalLE)]
    [InlineData(GameRelease.EnderalSE)]
    [InlineData(GameRelease.Fallout4)]
    [InlineData(GameRelease.Fallout4VR)]
    public async Task ReadHeader_ReturnsTheKeyAndMastersOfAPluginWrittenForTheRelease(GameRelease release)
    {
        var (source, hash) = Stored(BethesdaPluginBytes.Bytes(release, "Patch.esp", "Base.esm", "Other.esm"));

        var header = await MutagenPluginReader.ReadHeader(source, hash, RelativePath.FromUnsanitizedInput("Patch.esp"), release);

        header.Should().NotBeNull();
        header!.ModKey.FileName.ToString().Should().Be("Patch.esp");
        header.MasterReferences.Select(m => m.Master.FileName.ToString()).Should().Equal("Base.esm", "Other.esm");
    }

    [Theory]
    [InlineData(GameRelease.Oblivion)]
    [InlineData(GameRelease.OblivionRE)]
    [InlineData(GameRelease.Starfield)]
    public async Task ReadHeader_ThrowsForAReleaseWithoutAReferencedPackage(GameRelease release)
    {
        var source = Substitute.For<IStreamSourceDispatcher>();

        var act = async () => await MutagenPluginReader.ReadHeader(source, Hash.From(1), RelativePath.FromUnsanitizedInput("A.esp"), release);

        (await act.Should().ThrowAsync<NotSupportedException>()).Which.Message.Should().Contain(release.ToString());
    }

    [Fact]
    public async Task ReadHeader_NoStreamSourceHasTheBytes_ReturnsNull()
    {
        var source = Substitute.For<IStreamSourceDispatcher>();
        source.OpenAsync(Hash.From(2)).Returns(_ => ValueTask.FromResult<Stream?>(null));

        var header = await MutagenPluginReader.ReadHeader(source, Hash.From(2), RelativePath.FromUnsanitizedInput("A.esp"), GameRelease.SkyrimSE);

        header.Should().BeNull();
    }
}
