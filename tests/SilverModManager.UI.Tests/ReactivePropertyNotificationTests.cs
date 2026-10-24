using System.Reactive.Linq;
using System.ComponentModel;
using FluentAssertions;
using NSubstitute;
using ReactiveUI;

namespace SilverModManager.UI.Tests;

/// <summary>
/// What every <c>[Reactive]</c> property has to keep, whichever weaver or generator provides it:
/// one <c>PropertyChanged</c> per change, and <c>WhenAnyValue</c> sees the new value.
/// </summary>
public class ReactivePropertyNotificationTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class PlainCounter : ReactiveObject
    {
        public string Name { get; set; } = "";
    }

    // Names of the PropertyChanged events raised while `change` runs.
    private static List<string?> Raised(INotifyPropertyChanged source, Action change)
    {
        var raised = new List<string?>();
        void Handler(object? _, PropertyChangedEventArgs e) => raised.Add(e.PropertyName);
        source.PropertyChanged += Handler;
        try { change(); }
        finally { source.PropertyChanged -= Handler; }
        return raised;
    }

    [Fact]
    public void AnUnwovenAutoPropertyRaisesNothing()
    {
        // Guards the guard: the assertions below only mean something if a plain property fails them.
        var plain = new PlainCounter();
        var observed = new List<string>();
        using var _ = plain.WhenAnyValue(p => p.Name).Skip(1).Subscribe(observed.Add);

        var raised = Raised(plain, () => plain.Name = "changed");

        raised.Should().BeEmpty();
        observed.Should().BeEmpty();
    }

    [Fact]
    public void APublicSetterRaisesOneEventAndFeedsWhenAnyValue()
    {
        using var fixture = new FromSoftSavesViewModelTests.Fixture(Provider, steamRunning: false);
        fixture.Write("ER0000.sl2", "vanilla");
        fixture.Vm.RefreshCommand.Execute().Subscribe();
        var file = fixture.Vm.Files.Single();
        var observed = new List<object?>();
        using var _ = fixture.Vm.WhenAnyValue(vm => vm.SelectedFile).Skip(1).Subscribe(observed.Add);

        var raised = Raised(fixture.Vm, () => fixture.Vm.SelectedFile = file);

        raised.Where(name => name == nameof(fixture.Vm.SelectedFile)).Should().ContainSingle();
        observed.Should().Equal(file);
    }

    [Fact]
    public void APrivateSetterRaisesOneEventAndFeedsWhenAnyValue()
    {
        using var fixture = new FromSoftSavesViewModelTests.Fixture(Provider, steamRunning: true);
        fixture.Write("ER0000.sl2", "vanilla");
        fixture.Vm.IsSteamRunning.Should().BeTrue("the view model reads the process table when it is built");
        fixture.Tracker.IsSteamRunning().Returns(false);
        var observed = new List<bool>();
        using var _ = fixture.Vm.WhenAnyValue(vm => vm.IsSteamRunning).Skip(1).Subscribe(observed.Add);

        // Refresh is the view model's own path to the private IsSteamRunning setter.
        var raised = Raised(fixture.Vm, () => fixture.Vm.RefreshCommand.Execute().Subscribe());

        raised.Where(name => name == nameof(fixture.Vm.IsSteamRunning)).Should().ContainSingle();
        observed.Should().Equal(false);
    }
}
