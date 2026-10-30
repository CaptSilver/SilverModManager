using FluentAssertions;
using ReactiveUI;
using SilverModManager.UI.Sdk;

namespace SilverModManager.UI.Tests;

public class ViewModelObservationTests
{
    private sealed class FakeViewModel : ReactiveObject
    {
        private string _name = "";

        public FakeViewModel(string name) => _name = name;

        public string Name
        {
            get => _name;
            set => this.RaiseAndSetIfChanged(ref _name, value);
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

    [Fact]
    public void Test_WhenViewModel_EmitsNothingWhileThereIsNoViewModel()
    {
        var view = new FakeView();
        var received = new List<string>();
        using var _ = view.WhenViewModel(vm => vm.WhenAnyValue(x => x.Name)).Subscribe(received.Add);

        received.Should().BeEmpty();
    }

    [Fact]
    public void Test_WhenViewModel_EmitsCurrentAndLaterValuesOnceAViewModelArrives()
    {
        var view = new FakeView();
        var received = new List<string>();
        using var _ = view.WhenViewModel(vm => vm.WhenAnyValue(x => x.Name)).Subscribe(received.Add);

        var vm = new FakeViewModel("a");
        view.ViewModel = vm;
        vm.Name = "b";

        received.Should().Equal("a", "b");
    }

    [Fact]
    public void Test_WhenViewModel_SwitchesToTheNewViewModel()
    {
        var view = new FakeView();
        var received = new List<string>();
        using var _ = view.WhenViewModel(vm => vm.WhenAnyValue(x => x.Name)).Subscribe(received.Add);

        var first = new FakeViewModel("a");
        var second = new FakeViewModel("b");
        view.ViewModel = first;
        view.ViewModel = second;
        first.Name = "ignored";
        second.Name = "c";

        received.Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Test_WhenViewModel_StopsWhenTheViewModelIsCleared()
    {
        var view = new FakeView();
        var received = new List<string>();
        using var _ = view.WhenViewModel(vm => vm.WhenAnyValue(x => x.Name)).Subscribe(received.Add);

        var vm = new FakeViewModel("a");
        view.ViewModel = vm;
        view.ViewModel = null;
        vm.Name = "late";

        received.Should().Equal("a");
    }

    [Fact]
    public void Test_WhenViewModelValue_MatchesWhenAnyValueOnASwapToAnEqualValue()
    {
        var expected = new List<string>();
        var actual = new List<string>();
        var oldChainView = new FakeView();
        var helperView = new FakeView();
        using var _ = oldChainView.WhenAnyValue(v => v.ViewModel!.Name).Subscribe(expected.Add);
        using var __ = helperView.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.Name)).Subscribe(actual.Add);

        foreach (var view in new[] { oldChainView, helperView })
        {
            view.ViewModel = new FakeViewModel("same");
            view.ViewModel = new FakeViewModel("same");
        }

        actual.Should().Equal(expected);
    }
}
