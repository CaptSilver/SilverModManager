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
}
