using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using FormatRegulation = SilverModManager.Games.FromSoft.Formats.Regulation;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

public class GameOriginTests
{
    private static FromSoftFormatTraits ER => EldenRingGame.Definition.Formats;

    private static readonly byte[] Game = RegulationFixture.Build(new Dictionary<int, (string, IReadOnlyDictionary<int, byte[]>, int)>
    {
        [1] = ("SP_EFFECT_PARAM_ST", new Dictionary<int, byte[]> { [7] = Enumerable.Repeat((byte)0x77, 8).ToArray() }, 8),
    }, "11701000", ER);

    [Fact]
    public void AnswersRowsAndVersionFromTheGamesRegulation()
    {
        var game = new GameOrigin(Game, ER);

        game.Version.Should().Be("11701000");
        game.Row(1, 7).Should().Equal(Enumerable.Repeat((byte)0x77, 8));
        game.Row(1, 8).Should().BeNull();
        game.Rows(2).Should().BeNull("the game has no such entry");
    }

    [Fact]
    public void AnAdoptedPayloadIsUsedInsteadOfDecryptingTheFileAgain()
    {
        // Bytes that cannot be decrypted: any fallback to unpacking them throws.
        var game = new GameOrigin(new byte[64], ER);

        game.Adopt(FormatRegulation.Unpack(Game, ER));

        game.Row(1, 7).Should().Equal(Enumerable.Repeat((byte)0x77, 8));
        game.Version.Should().Be("11701000");
    }
}
