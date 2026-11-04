using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;

namespace SilverModManager.UI.Tests;

public class LoadoutNameProviderTests
{
    [Fact]
    public async Task TestNames()
    {
        const int numChars = 'Z' - 'A' + 1;
        const int count = numChars * numChars + numChars;
        var names = new string[count];
        Array.Fill(names, string.Empty);

        for (var i = 0; i < count; i++)
        {
            names[i] = LoadoutNameProvider.GetNewShortName(names.AsSpan(start: 0, length: i));
        }

        var result = names.Aggregate((a, b) => $"{a}\n{b}");
        await Verify(result);
    }

    [Fact]
    public void GetDefaultName_FreeName_IsTheGameName()
    {
        LoadoutNameProvider.GetDefaultName("Skyrim VR", ["Something else"]).Should().Be("Skyrim VR");
    }

    [Fact]
    public void GetDefaultName_TakenName_UsesTheFirstFreeNumber()
    {
        LoadoutNameProvider.GetDefaultName("Skyrim VR", ["Skyrim VR"]).Should().Be("Skyrim VR 2");
        LoadoutNameProvider.GetDefaultName("Skyrim VR", ["Skyrim VR", "Skyrim VR 2", "Skyrim VR 4"]).Should().Be("Skyrim VR 3");
    }

    [Fact]
    public void GetDefaultName_IgnoresCaseAndOuterWhitespaceOfTakenNames()
    {
        LoadoutNameProvider.GetDefaultName("Skyrim VR", ["  skyrim vr "]).Should().Be("Skyrim VR 2");
    }

    [Theory]
    [InlineData("Loadout A", true)]
    [InlineData("Loadout ZZ", true)]
    [InlineData("Loadout B (2)", true)]
    [InlineData("Loadout", false)]
    [InlineData("Loadout a", false)]
    [InlineData("Loadout ABC", false)]
    [InlineData("My Loadout A", false)]
    [InlineData("Loadout A (x)", false)]
    [InlineData("Loadout A ", false)]
    [InlineData("Skyrim VR", false)]
    public void IsAutomaticLegacyName_RecognisesOnlyTheShapesTheOldCodeProduced(string name, bool expected)
    {
        LoadoutNameProvider.IsLegacyDefaultName(name).Should().Be(expected);
    }
}
