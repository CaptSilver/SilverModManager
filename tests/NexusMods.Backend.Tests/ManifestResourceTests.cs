using System.Reflection;
using FluentAssertions;
using NexusMods.Backend.Games;
using NexusMods.Backend.OS;
using NexusMods.Sdk;

namespace NexusMods.Backend.Tests;

/// <summary>
/// Embedded resources are named from the assembly's root namespace, and the code that opens them holds the
/// name as a string, so a mismatch only shows when the resource is first read. Names are built from the
/// assembly's own name.
/// </summary>
public class ManifestResourceTests
{
    private static readonly Assembly Backend = typeof(LinuxInterop).Assembly;

    private static string Name(string suffix) => $"{Backend.GetName().Name}.{suffix}";

    private static int Length(string name)
    {
        using var stream = Backend.GetManifestResourceStream(name);
        stream.Should().NotBeNull($"the Backend assembly must embed `{name}`; it has: {string.Join(", ", Backend.GetManifestResourceNames())}");
        return (int)stream!.Length;
    }

    [Fact]
    public void Test_MissingNameIsNotFound()
        => Backend.GetManifestResourceStream(Name("Resources.game-tile-placeholder.png.missing")).Should().BeNull();

    [Fact]
    public void Test_DesktopFileConstantMatchesTheManifest()
    {
        LinuxInterop.DesktopFileResourceName.Should().Be(Name($"{ApplicationConstants.ApplicationId}.desktop"));
        Length(LinuxInterop.DesktopFileResourceName).Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("Resources.game-tile-placeholder.png")]
    [InlineData("Resources.game-icon-placeholder.png")]
    public void Test_GamePlaceholdersAreEmbedded(string suffix)
        => Length(Name(suffix)).Should().BeGreaterThan(0);

    [Fact]
    public async Task Test_SteamArtSourceOpensItsPlaceholders()
    {
        // With no Steam folder to look in, both lookups fall back to the embedded placeholders, which the
        // source names by string literal.
        var source = new SteamLibraryCacheArtSource(() => []);
        foreach (var factory in new[] { source.Tile([]), source.Icon([]) })
        {
            await using var stream = await factory.GetStreamAsync();
            stream.Length.Should().BeGreaterThan(0);
        }
    }
}
