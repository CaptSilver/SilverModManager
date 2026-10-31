using System.ComponentModel;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NexusMods.Hashing.xxHash3;
using R3;
using ReactiveUI;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.Networking.Forums;
using SilverModManager.Sdk.Hashes;
using SilverModManager.UI.Tests.Helpers;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.UI.Tests.Pages;

// Forum rows that wait for a sign-in: the row's CanSignIn, the line's button, the command and the re-check on sign-in.
public partial class WabbajackModlistViewModelTests
{
    private static readonly Uri ForumPage = new("https://www.loverslab.com/files/file/1-x/");

    private static ArchivePlanRow ForumRow(ArchiveState state, string name = "a.7z", bool vectorPlexus = false)
    {
        DownloadState download = vectorPlexus ? new VectorPlexusState(ForumPage, name, 1, false) : new LoversLabState(ForumPage, name, 1, false);
        return new ArchivePlanRow(new ArchiveEntry(FileIdentity.XxHash64(Hash.From((ulong)name.GetHashCode())), 1_000, name, "", download), state, null, ForumPage, null);
    }

    private sealed class SignInRig
    {
        public IForumSignInFlow Flow { get; } = Substitute.For<IForumSignInFlow>();
        public IForumAccounts Accounts { get; } = Substitute.For<IForumAccounts>();
        public R3.Subject<ForumSite> Changed { get; } = new();
        public IServiceProvider Services { get; }

        public SignInRig(IServiceProvider inner)
        {
            Accounts.Changed.Returns(Changed);
            Services = new OverridingServiceProvider(inner, (typeof(IForumSignInFlow), Flow), (typeof(IForumAccounts), Accounts));
        }
    }

    [Theory]
    [InlineData(ArchiveState.SignIn, true)]
    [InlineData(ArchiveState.Manual, false)]
    [InlineData(ArchiveState.Automatic, false)]
    [InlineData(ArchiveState.Browser, false)]
    [InlineData(ArchiveState.Have, false)]
    public void CanSignIn_IsTrueOnlyForARowThatNeedsASignIn(ArchiveState state, bool expected)
        => new WabbajackArchiveRow(ForumRow(state)).CanSignIn.Should().Be(expected);

    [Fact]
    public void AnInPlaceUpdateIntoOrOutOfSignIn_RaisesCanSignInOnTheSameRow()
    {
        var row = new WabbajackArchiveRow(ForumRow(ArchiveState.Manual));
        var raised = new List<string?>();
        ((INotifyPropertyChanged)row).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.Update(ForumRow(ArchiveState.SignIn));
        raised.Should().Contain(nameof(WabbajackArchiveRow.CanSignIn));
        row.CanSignIn.Should().BeTrue();

        raised.Clear();
        row.Update(ForumRow(ArchiveState.Automatic));
        raised.Should().Contain(nameof(WabbajackArchiveRow.CanSignIn));
        row.CanSignIn.Should().BeFalse();

        raised.Clear();
        row.Update(ForumRow(ArchiveState.Manual));
        raised.Should().NotContain(nameof(WabbajackArchiveRow.CanSignIn), "Automatic to Manual does not touch it");
    }

    [Fact]
    public async Task TheSignInButtonOfARealizedLine_FollowsTheRowWithoutTheLineBeingRecreated()
    {
        var plan = BigPlan(1153, ArchiveState.Browser);
        var (vm, _, acquirer) = await Open(plan);
        using var activation = vm.Activator.Activate();
        var view = await ShowView(vm);
        Control[] before = [];
        await EventuallyOnUi(() =>
        {
            Render((Window)view.GetVisualRoot()!);
            before = Lines(view).ToArray();
            before.Should().NotBeEmpty();
            SignInButton(before[0]).IsVisible.Should().BeFalse();
        });

        acquirer.Change(WithState(plan, 0, ArchiveState.SignIn, "Sign in to LoversLab to download this"));
        await EventuallyOnUi(() =>
        {
            Render((Window)view.GetVisualRoot()!);
            // The panel may swap the spare element at the end when row heights change; the lines of the changed rows stay.
            Lines(view).Take(2).Should().Equal(before.Take(2), "a state change must not rebuild the lines");
            SignInButton(before[0]).IsVisible.Should().BeTrue();
            SignInButton(before[1]).IsVisible.Should().BeFalse();
        });

        acquirer.Change(WithState(plan, 0, ArchiveState.Automatic));
        await EventuallyOnUi(() =>
        {
            Render((Window)view.GetVisualRoot()!);
            Lines(view).Take(2).Should().Equal(before.Take(2));
            SignInButton(before[0]).IsVisible.Should().BeFalse();
        });
    }

    private static Button SignInButton(Control line) => line.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Sign in"));

    [Theory]
    [InlineData(false, ForumSite.LoversLab)]
    [InlineData(true, ForumSite.VectorPlexus)]
    public async Task SignInToSource_RunsTheSignInOfTheRowsSite(bool vectorPlexus, ForumSite site)
    {
        var rig = new SignInRig(Provider);
        var (vm, _, _) = await Open(Plan(ForumRow(ArchiveState.SignIn, vectorPlexus: vectorPlexus)), services: rig.Services);

        await vm.SignInToSourceCommand.Execute(vm.Archives[0]);

        await rig.Flow.Received(1).Run(site, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignInToSource_OnARowOfAnotherSource_SignsInNowhere()
    {
        var rig = new SignInRig(Provider);
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Manual)), services: rig.Services);

        await vm.SignInToSourceCommand.Execute(vm.Archives[0]);

        await rig.Flow.DidNotReceive().Run(Arg.Any<ForumSite>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASignInThatChangesAnAccount_ChecksTheListAgainForThatSitesRows()
    {
        var rig = new SignInRig(Provider);
        var (vm, service, _) = await Open(Plan(ForumRow(ArchiveState.SignIn)), services: rig.Services);
        using var activation = vm.Activator.Activate();
        var checks = service.PlannedFor.Count;

        rig.Changed.OnNext(ForumSite.LoversLab);

        await EventuallyOnUi(() => service.PlannedFor.Count.Should().Be(checks + 1));
    }

    [Fact]
    public async Task AnAccountChangedWhileTheListIsChecked_ChecksTheListAgainWhenTheCheckEnds()
    {
        var rig = new SignInRig(Provider);
        var (vm, service, _) = await Open(Plan(ForumRow(ArchiveState.SignIn)), services: rig.Services);
        using var activation = vm.Activator.Activate();
        var gate = new TaskCompletionSource();
        service.PlanGate = gate;
        vm.SelectLoadoutCommand.Execute(vm.SelectedLoadout!).Subscribe();
        await WaitUntil(() => vm.IsPlanning);
        await service.PlanReachedGate.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var checks = service.PlannedFor.Count;

        rig.Changed.OnNext(ForumSite.LoversLab);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        service.PlannedFor.Count.Should().Be(checks, "the running check owns the plan");
        gate.SetResult();

        await EventuallyOnUi(() => service.PlannedFor.Count.Should().Be(checks + 1, "one more check, for the sign-in"));
    }

    [Fact]
    public async Task AnAccountChangeOfASiteTheListDoesNotUse_LeavesThePlanAlone()
    {
        var rig = new SignInRig(Provider);
        var (vm, service, _) = await Open(Plan(ForumRow(ArchiveState.SignIn)), services: rig.Services);
        using var activation = vm.Activator.Activate();
        var checks = service.PlannedFor.Count;

        rig.Changed.OnNext(ForumSite.VectorPlexus);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        service.PlannedFor.Count.Should().Be(checks);
    }
}
