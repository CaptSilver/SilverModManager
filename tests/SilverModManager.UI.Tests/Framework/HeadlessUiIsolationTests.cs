using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using FluentAssertions;
using SilverModManager.UI.Tests.Framework;
using SilverModManager.UI.Tests.LeftMenu;
using SilverModManager.UI.Tests.Windows;

namespace SilverModManager.UI.Tests;

/// <summary>
/// The headless platform has one keyboard focus, one pointer and one dispatcher for the whole test run, so tests
/// that show windows and drive input must not overlap or leave windows behind.
/// </summary>
public class HeadlessUiIsolationTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class Probe(IServiceProvider provider) : AUiTest(provider)
    {
        public Window Open() => Track(new Window { Width = 100, Height = 100 });
    }

    [Theory]
    [InlineData(typeof(DialogWindowChromeTests))]
    [InlineData(typeof(WindowChromeResizeGripTests))]
    [InlineData(typeof(MainWindowDropTargetTests))]
    [InlineData(typeof(LoadoutLeftMenuDragTests))]
    public void ClassesThatDriveHeadlessInput_RunOneAtATime(Type testClass)
    {
        var collection = testClass.GetCustomAttribute<CollectionAttribute>();
        collection.Should().NotBeNull($"{testClass.Name} drives pointer or keyboard input on the shared headless platform");
        collection!.Name.Should().Be(HeadlessUi.Collection);
    }

    [Fact]
    public void TheHeadlessCollection_DisablesParallelization()
    {
        var definition = typeof(HeadlessUi).Assembly.GetTypes()
            .Single(type => type.GetCustomAttribute<CollectionDefinitionAttribute>()?.Name == HeadlessUi.Collection);
        definition.GetCustomAttribute<CollectionDefinitionAttribute>()!.DisableParallelization.Should().BeTrue();
    }

    [Fact]
    public async Task WindowsAShowingTestOpens_AreClosedWhenItEnds()
    {
        var probe = new Probe(Provider);
        var window = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var opened = probe.Open();
            opened.Show();
            opened.IsVisible.Should().BeTrue();
            return opened;
        });

        await probe.DisposeAsync();

        (await Dispatcher.UIThread.InvokeAsync(() => window.IsVisible)).Should().BeFalse();
    }
}
