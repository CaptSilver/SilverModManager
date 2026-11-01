using System.Numerics;
using FluentAssertions;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Fixtures;

namespace SilverModManager.Games.FromSoft.Tests;

/// <summary>Every FromSoft game beyond Elden Ring has the layout, save names and file formats its sources give it.</summary>
public class FromSoftGameTableTests
{
    public static IEnumerable<object[]> Games => FromSoftGameFacts.All;

    [Theory]
    [MemberData(nameof(Games))]
    public void TheLayoutIsTheGamesOwn(FromSoftGameFacts facts)
        => facts.Definition().Layout.Should().Be(facts.Layout);

    [Theory]
    [MemberData(nameof(Games))]
    public void TheSaveNamesAreTheGamesOwn(FromSoftGameFacts facts)
        => facts.Definition().SaveNames.Should().Be(facts.SaveNames);

    [Theory]
    [MemberData(nameof(Games))]
    public void TheLayoutCarriesTheSteamAppIdOfTheGame(FromSoftGameFacts facts)
        => facts.Definition().Layout.SteamAppId.Should().Be(facts.SteamAppId);

    [Theory]
    [MemberData(nameof(Games))]
    public void TheFormatTraitsAreTheGamesOwn(FromSoftGameFacts facts)
    {
        var traits = facts.Definition().Formats;

        traits.Binder.Should().Be(facts.Binder);
        traits.ReadableCompressions.Should().Equal(facts.ReadableCompressions);
        traits.PackedWrite.Should().Be(facts.PackedWrite);
        traits.RegulationFileName.Should().Be(facts.RegulationFileName);
        traits.RegulationWrite.Should().Be(facts.RegulationWrite);
        Convert.ToHexString(traits.RegulationKey.AsSpan()).Should().Be(facts.RegulationKeyHex);
        traits.ParamdefSet.Should().Be(facts.ParamdefSet);
        traits.PathHashBits.Should().Be(facts.PathHashBits);
        traits.PathHashPrime.Should().Be(facts.PathHashPrime);
        traits.ArchiveLayout.Should().Be(facts.ArchiveLayout);
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void TheArchivesAreListedInTheOrderALookupWalksThemWithTheirOwnKeys(FromSoftGameFacts facts)
    {
        var archives = facts.Definition().Formats.Archives;

        archives.Select(a => a.Name).Should().Equal(facts.Archives.Select(a => a.Name));
        foreach (var (actual, expected) in archives.Zip(facts.Archives))
        {
            var hex = actual.Modulus.ToString("x").TrimStart('0');
            actual.Modulus.GetByteCount(isUnsigned: true).Should().Be(256, $"{expected.Name} is a 2048-bit key");
            hex.Should().StartWith(expected.ModulusStart, expected.Name).And.EndWith(expected.ModulusEnd, expected.Name);
            actual.Exponent.Should().Be(new BigInteger(expected.Exponent), expected.Name);
        }
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void TheRegulationRoundTripsThroughTheGamesKeyAndWrapper(FromSoftGameFacts facts)
    {
        var traits = facts.Definition().Formats;
        var payload = RegulationFixture.Payload(RegulationFixture.GoldenEntries, RegulationFixture.GoldenVersion);

        var packed = Regulation.Pack(payload, RegulationFixture.ZeroIv, traits);

        Regulation.Unpack(packed, traits).Should().Equal(payload);
        Regulation.Version(Regulation.Unpack(packed, traits)).Should().Be(RegulationFixture.GoldenVersion);
    }
}
