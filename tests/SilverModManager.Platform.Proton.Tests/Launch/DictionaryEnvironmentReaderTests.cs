using FluentAssertions;
using SilverModManager.Platform.Proton.Launch;

namespace SilverModManager.Platform.Proton.Tests.Launch;

public class DictionaryEnvironmentReaderTests
{
    [Fact]
    public void ReadsOnlyTheCapturedValues_AndNullForTheRest()
    {
        var reader = new DictionaryEnvironmentReader(new Dictionary<string, string> { ["SteamAppId"] = "1091500" });

        reader.Get("SteamAppId").Should().Be("1091500");
        reader.Get("PATH").Should().BeNull("the process's own environment is not consulted");
    }
}
