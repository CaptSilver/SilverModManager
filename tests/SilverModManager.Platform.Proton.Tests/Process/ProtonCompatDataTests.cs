using FluentAssertions;
using NexusMods.Paths;

namespace SilverModManager.Platform.Proton.Tests.Processes;

public class ProtonCompatDataTests
{
    [Theory]
    [InlineData("/g/Prefixes/x", "/g/Prefixes/x", "/g/Prefixes/x/pfx")]
    [InlineData("/g/compatdata/42/pfx", "/g/compatdata/42", "/g/compatdata/42/pfx")]
    public void Of_YieldsTheCompatDataFolderAndItsPfxForEitherSpelling(string recorded, string compatData, string prefix)
        => ProtonCompatData.Of(recorded).Should().Be((compatData, prefix));

    [Theory]
    [InlineData("/g/Prefixes/x", "/g/Prefixes/x")]
    [InlineData("/g/compatdata/42/pfx", "/g/compatdata/42")]
    public void CompatDataOf_IsTheFolderThatHoldsPfx(string recorded, string compatData)
        => ProtonCompatData.CompatDataOf(FileSystem.Shared.FromUnsanitizedFullPath(recorded)).ToString().Should().Be(compatData);
}
