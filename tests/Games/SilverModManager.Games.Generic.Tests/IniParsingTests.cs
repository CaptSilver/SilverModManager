using FluentAssertions;
using SilverModManager.Games.Generic.FileAnalyzers;
using NexusMods.Paths;
using SilverModManager.Sdk.IO;
using Xunit;

namespace SilverModManager.Games.Generic.Tests;

public class IniParsingTests
{
    private readonly IFileSystem _fs;

    public IniParsingTests(IFileSystem fs)
    {
        _fs = fs;
    }

    private async Task<IniAnalysisData> AnalyzeSample()
    {
        var assetsPath = _fs.GetKnownPath(KnownPath.EntryDirectory).Combine("Assets");
        var testIniPath = assetsPath.Combine("IniSamples/enblocal.ini");
        return await IniAnalzyer.AnalyzeAsync(new NativeFileStreamFactory(testIniPath));
    }

    [Fact]
    public async Task ParseIniTest()
    {
        var act = AnalyzeSample;

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EverySectionOfTheSampleIsReportedOnceEvenWhenItRepeats()
    {
        var data = await AnalyzeSample();

        data.Sections.Should().BeEquivalentTo(
            "ADAPTIVEQUALITY", "ANTIALIASING", "EmptySection", "ENGINE", "FILE", "FIX", "GLOBAL", "GUI", "INPUT", "LIMITER",
            "LONGEXPOSURE", "MEMORY", "MULTIGPU", "MULTIHEAD", "PERFORMANCE", "PROXY", "THREADS", "WINDOW");
    }

    [Fact]
    public async Task KeysIncludeThoseBeforeTheFirstSectionAndRepeatedKeysCountOnce()
    {
        var data = await AnalyzeSample();

        data.Keys.Should().Contain(["keyWithoutSection", "EnableProxyLibrary", "emptyKey", "duplicate", "VideoMemorySizeMb"]);
        data.Keys.Count(k => k == "duplicateKey").Should().Be(1);
    }

    [Fact]
    public async Task KeyAndSectionLookupsIgnoreCase()
    {
        var data = await AnalyzeSample();

        data.Keys.Should().Contain("ENABLEPROXYLIBRARY");
        data.Sections.Should().Contain("proxy");
    }
}
