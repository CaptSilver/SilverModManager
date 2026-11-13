using FluentAssertions;
using SilverModManager.Games.FromSoft.Cleanup;

namespace SilverModManager.Games.FromSoft.Tests.Cleanup;

public class ModEngineIniTests
{
    [Theory]
    [InlineData("[files]\nmodOverrideDirectory=\\mods", "mods")]
    [InlineData("[files]\r\nmodOverrideDirectory = \"\\Mods\\\"", "Mods")]
    [InlineData("[files]\nmodOverrideDirectory=/mods/", "mods")]
    [InlineData("[files]\nuseModOverrideDirectory=1", "doa")]
    [InlineData("[other]\nmodOverrideDirectory=\\nope\n[files]\nuseModOverrideDirectory=1", "doa")]
    [InlineData("", "doa")]
    public void TheOverrideDirectoryIsReadFromTheFilesSection(string ini, string expected)
        => ModEngineIni.OverrideDirectory(ini).Should().Be(expected);
}
