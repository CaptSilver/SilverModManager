using DynamicData.Kernel;
using FluentAssertions;
using SilverModManager.Abstractions.Loadouts.Ids;
using SilverModManager.DataModel.Synchronizer;

namespace SilverModManager.DataModel.Synchronizer.Tests;

public class SynchronizerStateNotificationTests
{
    [Fact]
    public void Test_LastApplied_RaisesPropertyChanged()
    {
        var state = new SynchronizerState();
        var raised = new List<string?>();
        state.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        state.LastApplied = Optional<LoadoutWithTxId>.Create(new LoadoutWithTxId(default, default));

        raised.Should().Equal(nameof(SynchronizerState.LastApplied));
    }
}
