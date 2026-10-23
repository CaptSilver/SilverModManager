using FluentAssertions;
using NexusMods.Backend.OS;
using NexusMods.Sdk;
using NexusMods.Sdk.IO;

namespace NexusMods.Backend.Tests.OS;

public class DesktopFileResourceTests
{
    // The desktop file is embedded under a name derived from the application id. If the id
    // and the embedded file name drift apart, registering the nxm handler throws at runtime.
    [Fact]
    public async Task Test_EmbeddedDesktopFileMatchesApplicationId()
    {
        LinuxInterop.DesktopFileResourceName.Should().Be($"NexusMods.Backend.{ApplicationConstants.ApplicationId}.desktop");

        await using var stream = await new EmbeddedResourceStreamFactory<LinuxInterop>(LinuxInterop.DesktopFileResourceName).GetStreamAsync();
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync();

        text.Should().Contain("MimeType=x-scheme-handler/nxm");
        text.Should().Contain("Name=SilverModManager");
        text.Should().Contain("StartupWMClass=SilverModManager");
        text.Should().Contain($"Icon={ApplicationConstants.ApplicationId}");
        text.Should().Contain("${INSTALL_EXEC}");
    }

    // xdg-settings only leaves the file alone when the scheme is followed by a ';'. Without one it
    // appends the scheme a second time and rewrites the file through mktemp, which leaves it mode 600.
    // The desktop entry spec terminates list values with ';' anyway.
    [Fact]
    public async Task Test_EmbeddedDesktopFileTerminatesItsMimeTypeList()
    {
        await using var stream = await new EmbeddedResourceStreamFactory<LinuxInterop>(LinuxInterop.DesktopFileResourceName).GetStreamAsync();
        using var reader = new StreamReader(stream);
        var lines = (await reader.ReadToEndAsync()).Split('\n').Select(line => line.TrimEnd('\r'));

        lines.Should().ContainSingle(line => line.StartsWith("MimeType="))
            .Which.Should().Be("MimeType=x-scheme-handler/nxm;");
    }
}
