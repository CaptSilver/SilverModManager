using System.Reflection;
using FluentAssertions;
using SilverModManager.Backend.OS;
using SilverModManager.Sdk;

namespace SilverModManager.Backend.Tests;

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
        => Backend.GetManifestResourceStream(Name("Resources.no-such-resource")).Should().BeNull();

    [Fact]
    public void Test_DesktopFileConstantMatchesTheManifest()
    {
        LinuxInterop.DesktopFileResourceName.Should().Be(Name($"{ApplicationConstants.ApplicationId}.desktop"));
        Length(LinuxInterop.DesktopFileResourceName).Should().BeGreaterThan(0);
    }
}
