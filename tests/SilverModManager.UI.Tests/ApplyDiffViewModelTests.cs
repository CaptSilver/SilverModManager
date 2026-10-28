using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Files.Diff;
using SilverModManager.App.UI.Pages.Diff.ApplyDiff;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.UI.Tests;

/// <summary>
/// Building the diff tree walks the install and the loadout, so the Apply Diff page must not do it on the UI thread,
/// neither when it opens nor when Refresh is pressed.
/// </summary>
public class ApplyDiffViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task TheDiffTreeIsBuiltOffTheUiThreadOnOpenAndOnRefresh()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Apply Diff Off UI");
        var onUiThread = new List<bool>();
        var sync = Substitute.For<ISynchronizerService>();
        sync.GetApplyDiffTree(Arg.Any<LoadoutId>()).Returns(_ =>
        {
            lock (onUiThread) onUiThread.Add(Dispatcher.UIThread.CheckAccess());
            return FileDiffTree.Create([]);
        });

        var services = new ServiceCollection()
            .AddSingleton(sync)
            .AddSingleton(Connection)
            .BuildServiceProvider();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var page = new ApplyDiffViewModel(Substitute.For<IWindowManager>(), services);
            page.Initialize(loadout.LoadoutId);
            _ = page.RefreshCommand.Execute();
        });

        await Eventually(() =>
        {
            lock (onUiThread) onUiThread.Should().HaveCount(2);
        });
        lock (onUiThread) onUiThread.Should().AllBeEquivalentTo(false);
    }
}
