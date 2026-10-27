using FluentAssertions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.Merge.Tests;

public class MergedIntrinsicFileTests
{
    private sealed class Plans(byte[]? bytes) : IMergePlanSource
    {
        public int Asks;
        public string? Asked;

        public byte[]? MergedBytes(Loadout.ReadOnly loadout, string relPath)
        {
            Asks++;
            Asked = relPath;
            return bytes;
        }
    }

    private static readonly GamePath Out = new(LocationId.Game, "Mods/merged/scripts/x.ws");

    [Fact]
    public async Task WriteCopiesThePlannedBytesForItsRelativePath()
    {
        var plans = new Plans([1, 2, 3]);
        var stream = new MemoryStream();

        await new MergedIntrinsicFile(plans, Out, "scripts/x.ws").Write(stream, default, []);

        stream.ToArray().Should().Equal(1, 2, 3);
        plans.Asked.Should().Be("scripts/x.ws");
    }

    [Fact]
    public async Task AMissingPlanEntryIsAnError()
    {
        var act = () => new MergedIntrinsicFile(new Plans(null), Out, "scripts/x.ws").Write(new MemoryStream(), default, []);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Mods/merged/scripts/x.ws*disagree*");
    }

    [Fact]
    public void ItExposesItsPathAndRelativePath()
    {
        var file = new MergedIntrinsicFile(new Plans([1]), Out, "scripts/x.ws");

        file.Path.Should().Be(Out);
        file.RelPath.Should().Be("scripts/x.ws");
    }

    [Fact]
    public async Task IngestLeavesTheStreamAndThePlanAlone()
    {
        var plans = new Plans([1]);
        var edited = new MemoryStream([9, 8]);

        await new MergedIntrinsicFile(plans, Out, "scripts/x.ws").Ingest(edited, default, [], null!);

        edited.Position.Should().Be(0);
        edited.ToArray().Should().Equal(9, 8);
        plans.Asks.Should().Be(0);
    }
}
