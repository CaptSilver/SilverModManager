using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using R3;
using SilverModManager.App.UI.Controls.Settings.SettingEntries;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Sdk.Settings;

namespace SilverModManager.UI.Tests;

public class SettingActionButtonTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class FakeSource : IActionButtonSource
    {
        public Subject<ActionButtonState> Changes { get; } = new();
        public ActionButtonState Current { get; set; } = new("Not signed in", "Sign in");
        public Observable<ActionButtonState> State => Changes.Prepend(Current);
        public Exception? Failure { get; set; }
        public int Invoked { get; private set; }

        public Task Invoke(CancellationToken cancellationToken)
        {
            Invoked++;
            return Failure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
        }
    }

    private static PropertyConfig Config(string value = "")
    {
        return new PropertyConfig(
            Name: "LoversLabClientId",
            PropertyType: typeof(string),
            Options: new PropertyOptionsWrapper
            {
                Section = Sections.Accounts,
                DisplayName = "LoversLab",
                DescriptionFactory = _ => "",
            },
            ContainerOptions: new ActionButtonContainerOptions { CreateSource = _ => new FakeSource() },
            GetValue: _ => value,
            GetDefaultValue: _ => "",
            Update: (_, _) => { });
    }

    private static SettingActionButtonViewModel Create(FakeSource source, IErrorPresenter? errors = null)
        => new(new ActionButtonContainer("", "", Config()), source, errors ?? Substitute.For<IErrorPresenter>());

    [Fact]
    public void ARowShowsTheSourcesTextAndItsButton()
    {
        var viewModel = Create(new FakeSource());

        viewModel.Text.Should().Be("Not signed in");
        viewModel.ButtonLabel.Should().Be("Sign in");
        viewModel.HasButton.Should().BeTrue();
    }

    [Fact]
    public async Task ARowThatOffersNoActionHasNoButton()
    {
        var viewModel = Create(new FakeSource { Current = new ActionButtonState("Sign-in for LoversLab isn't set up yet. The site hasn't issued this app a client.", null) });

        viewModel.HasButton.Should().BeFalse();
        (await viewModel.InvokeCommand.CanExecute.FirstAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task TheRowFollowsTheSourceWhileItIsOnScreen()
    {
        var source = new FakeSource();
        var viewModel = Create(source);
        using var activation = viewModel.Activator.Activate();

        source.Changes.OnNext(new ActionButtonState("Signed in. Stored in Secret Service.", "Sign out"));

        await EventuallyOnUi(() =>
        {
            viewModel.Text.Should().Be("Signed in. Stored in Secret Service.");
            viewModel.ButtonLabel.Should().Be("Sign out");
        });
    }

    [Fact]
    public async Task PressingTheButtonRunsTheSourcesAction()
    {
        var source = new FakeSource();
        var viewModel = Create(source);

        await viewModel.InvokeCommand.Execute();

        source.Invoked.Should().Be(1);
    }

    [Fact]
    public async Task AFailingActionIsReportedNotThrownIntoTheCommand()
    {
        var errors = Substitute.For<IErrorPresenter>();
        var failure = new InvalidOperationException("the store refused");
        var viewModel = Create(new FakeSource { Failure = failure }, errors);

        await viewModel.InvokeCommand.Execute();

        await errors.Received(1).Report(failure, "changing the account");
    }

    [Fact]
    public void TheSettingsPageHasAnAccountRowForEachForum()
    {
        var settings = Provider.GetRequiredService<ISettingsManager>();
        var forum = settings.Configs.Values.Single(config => config.Type == typeof(SilverModManager.Networking.Forums.ForumAuthSettings));

        var rows = forum.Properties.Where(property => property.ContainerOptions is ActionButtonContainerOptions).ToArray();

        rows.Select(row => row.Options.DisplayName).Should().Equal("LoversLab", "VectorPlexus");
        rows.Should().OnlyContain(row => row.Options.Section == Sections.Accounts);
    }

    [Fact]
    public void AnAccountsSectionIsRegisteredForTheRows()
    {
        Provider.GetServices<SilverModManager.UI.Sdk.Settings.SectionDescriptor>().Should().Contain(section => section.Id == Sections.Accounts && section.Name == "Accounts");
    }

    [Fact]
    public void TheFactoryBuildsARowFromTheOptionsSource()
    {
        var source = new FakeSource();
        var services = new ServiceCollection().AddSingleton(Substitute.For<IErrorPresenter>()).BuildServiceProvider();
        var options = new ActionButtonContainerOptions { CreateSource = _ => source };
        var settings = Substitute.For<ISettingsManager>();

        var control = new SettingActionButtonFactory().Create(services, settings, options, Config());

        control.Should().BeOfType<SettingActionButtonViewModel>().Which.Text.Should().Be("Not signed in");
        control.ValueContainer.HasChanged.Should().BeFalse();
    }
}
