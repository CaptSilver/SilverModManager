using FluentAssertions;
using SilverModManager.Games.CreationEngine.CrashLogs;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.CrashLogs;

public class LoadoutFitTests
{
    private static IEnumerable<string> Names(int count) => Enumerable.Range(0, count).Select(i => $"Plugin {i}.esp");

    private static CrashReport Report(int plugins, IEnumerable<SksePlugin>? skse = null) =>
        new() { Plugins = Names(plugins).ToList(), SksePlugins = skse?.ToList() ?? [] };

    [Fact]
    public void TwoAbsentPluginsAreNotAnotherLoadout()
    {
        var index = LoadoutFileIndex.FromNames(Names(10), []);

        var fit = LoadoutFit.Of(Report(12), index)!.Value;

        fit.Should().Be(new LoadoutFit(2, 12));
        fit.IsAnotherLoadout.Should().BeFalse();
    }

    [Fact]
    public void ThreeAbsentOfTwelveIsAnotherLoadout()
    {
        var fit = LoadoutFit.Of(Report(12), LoadoutFileIndex.FromNames(Names(9), []))!.Value;

        fit.Should().Be(new LoadoutFit(3, 12));
        fit.IsAnotherLoadout.Should().BeTrue();
    }

    [Fact]
    public void ThreeAbsentOfTwoHundredIsNot()
    {
        var fit = LoadoutFit.Of(Report(200), LoadoutFileIndex.FromNames(Names(197), []))!.Value;

        fit.Should().Be(new LoadoutFit(3, 200));
        fit.IsAnotherLoadout.Should().BeFalse("3 * 20 = 60 is not more than 200");
    }

    [Fact]
    public void TheReverseDirectionIsNotCounted()
    {
        var fit = LoadoutFit.Of(Report(10), LoadoutFileIndex.FromNames(Names(50), []))!.Value;

        fit.Absent.Should().Be(0);
        fit.IsAnotherLoadout.Should().BeFalse();
    }

    [Fact]
    public void AnEmptyPluginListFallsBackToSkseDlls()
    {
        var report = Fixtures.Parse("renderer-init.log");
        report.Plugins.Should().BeEmpty();
        report.SksePlugins.Should().NotBeEmpty();

        var fit = LoadoutFit.Of(report, LoadoutFileIndex.FromNames(Names(5), []))!.Value;

        fit.Total.Should().Be(report.SksePlugins.Count);
        fit.IsAnotherLoadout.Should().BeTrue();
    }

    [Fact]
    public void NamesAreComparedWithoutRegardToCase()
    {
        var report = new CrashReport { Plugins = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "Dragonborn.esm"] };

        var fit = LoadoutFit.Of(report, LoadoutFileIndex.FromNames(["SKYRIM.ESM", "update.esm", "DAWNGUARD.esm", "dragonborn.ESM"], []))!.Value;

        fit.Absent.Should().Be(0);
    }

    [Fact]
    public void BothEmptyGivesNoFit()
    {
        LoadoutFit.Of(new CrashReport(), LoadoutFileIndex.FromNames(Names(5), ["a.dll"])).Should().BeNull();
    }
}
