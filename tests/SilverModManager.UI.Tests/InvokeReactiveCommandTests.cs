using System.Reactive;
using System.Reactive.Subjects;
using FluentAssertions;
using ReactiveUI;
using SilverModManager.App.UI.Extensions;
using SilverModManager.UI.Sdk;

namespace SilverModManager.UI.Tests;

public class InvokeReactiveCommandTests
{
    private sealed class FakeViewModel : ReactiveObject
    {
        private ReactiveCommand<int, Unit>? _command;

        public ReactiveCommand<int, Unit>? Command
        {
            get => _command;
            set => this.RaiseAndSetIfChanged(ref _command, value);
        }
    }

    private sealed class FakeView : ReactiveObject, IViewFor<FakeViewModel>
    {
        private FakeViewModel? _viewModel;

        public FakeViewModel? ViewModel
        {
            get => _viewModel;
            set => this.RaiseAndSetIfChanged(ref _viewModel, value);
        }

        object? IViewFor.ViewModel
        {
            get => ViewModel;
            set => ViewModel = (FakeViewModel?)value;
        }
    }

    private static ReactiveCommand<int, Unit> Recording(List<int> inputs)
        => ReactiveCommand.Create<int>(inputs.Add, outputScheduler: System.Reactive.Concurrency.Scheduler.Immediate);

    [Fact]
    public void Test_InvokeReactiveCommand_DropsValuesWhileThereIsNoCommand()
    {
        var view = new FakeView();
        var source = new Subject<int>();
        using var _ = source.InvokeReactiveCommand(view.WhenViewModel(vm => vm.WhenAnyValue(x => x.Command)));

        source.OnNext(1);
        view.ViewModel = new FakeViewModel();
        source.OnNext(2);

        var inputs = new List<int>();
        view.ViewModel.Command = Recording(inputs);
        inputs.Should().BeEmpty();
    }

    [Fact]
    public void Test_InvokeReactiveCommand_InvokesACommandThatArrivesLaterWithLaterValues()
    {
        var view = new FakeView();
        var source = new Subject<int>();
        var inputs = new List<int>();
        using var _ = source.InvokeReactiveCommand(view.WhenViewModel(vm => vm.WhenAnyValue(x => x.Command)));

        source.OnNext(1);
        view.ViewModel = new FakeViewModel { Command = Recording(inputs) };
        source.OnNext(2);
        source.OnNext(3);

        inputs.Should().Equal(2, 3);
    }

    [Fact]
    public void Test_InvokeReactiveCommand_RoutesToTheNewCommandAfterASwap()
    {
        var view = new FakeView();
        var source = new Subject<int>();
        var first = new List<int>();
        var second = new List<int>();
        using var _ = source.InvokeReactiveCommand(view.WhenViewModel(vm => vm.WhenAnyValue(x => x.Command)));

        view.ViewModel = new FakeViewModel { Command = Recording(first) };
        source.OnNext(1);
        view.ViewModel = new FakeViewModel { Command = Recording(second) };
        source.OnNext(2);

        first.Should().Equal(1);
        second.Should().Equal(2);
    }
}
