using Xunit;
using FluentAssertions;
using SilverModManager.Networking.Thunderstore;

namespace SilverModManager.Networking.Thunderstore.Tests;

public class ThunderstoreRowTextTests
{
    [Theory]
    [InlineData("Zehs", "Unlimited_Orbs", "1.0.0", "Zehs-Unlimited_Orbs-1.0.0.zip", "Unlimited Orbs")]
    [InlineData("Zehs", "Unlimited_Orbs", "1.0.0", "zehs-unlimited_orbs-1.0.0.ZIP", "Unlimited Orbs")]
    [InlineData("Zehs", "Unlimited_Orbs", "1.0.0", "Zehs-Unlimited_Orbs", "Unlimited Orbs")]
    [InlineData("Zehs", "Unlimited_Orbs", "1.0.0", "", "Unlimited Orbs")]
    [InlineData("Zehs", "Unlimited_Orbs", "1.0.0", "My orbs", "My orbs")]
    // The name of another version's archive is not the default of this one, so it counts as the user's.
    [InlineData("Zehs", "Unlimited_Orbs", "1.0.0", "Zehs-Unlimited_Orbs-0.9.0.zip", "Zehs-Unlimited_Orbs-0.9.0.zip")]
    [InlineData("Zehs", "___", "1.0.0", "Zehs-___-1.0.0.zip", "Zehs-___")]
    public void RowName_IsTheReadableNameUnlessTheUserRenamedTheItem(string @namespace, string name, string version, string itemName, string expected)
    {
        ThunderstoreRowText.RowName(@namespace, name, version, itemName).Should().Be(expected);
    }

    [Theory]
    [InlineData("PEAK_Unlimited", "glarmer", "PEAK Unlimited")]
    [InlineData("BepInExPack_PEAK", "BepInEx", "BepInExPack PEAK")]
    [InlineData("REPOLib", "Zehs", "REPOLib")]
    [InlineData("__Odd__name__", "x", "Odd name")]
    [InlineData("  spaced   out  ", null, "spaced out")]
    // The owner is not repeated when a package name starts with it.
    [InlineData("glarmer_PEAK_Unlimited", "glarmer", "PEAK Unlimited")]
    [InlineData("GLARMER_PEAK", "glarmer", "PEAK")]
    [InlineData("glarmer-PEAK", "glarmer", "PEAK")]
    // ... unless that would leave nothing, or the owner is only the start of a longer word.
    [InlineData("glarmer", "glarmer", "glarmer")]
    [InlineData("glarmers_tools", "glarmer", "glarmers tools")]
    [InlineData("", "glarmer", "")]
    [InlineData("___", "glarmer", "")]
    public void DisplayName_TurnsUnderscoresIntoSpacesAndDropsARepeatedOwner(string name, string? owner, string expected)
    {
        ThunderstoreRowText.DisplayName(name, owner).Should().Be(expected);
    }
}
