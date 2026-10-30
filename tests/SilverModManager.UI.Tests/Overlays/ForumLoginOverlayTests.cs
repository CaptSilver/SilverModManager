using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using R3;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Overlays.Login;
using SilverModManager.Backend.Jobs;
using SilverModManager.Networking.Forums;
using SilverModManager.Networking.Forums.Auth;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.UI.Tests.Overlays;

public class ForumLoginOverlayTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class FakeLoginJob(ForumSite site) : IForumLoginJob, IJobDefinitionWithStart<FakeLoginJob, bool>
    {
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ForumSite Site => site;
        public R3.BehaviorSubject<Uri?> LoginUri { get; } = new(initialValue: null);

        public async ValueTask<bool> StartAsync(IJobContext<FakeLoginJob> context)
        {
            await Finish.Task.WaitAsync(context.CancellationToken);
            return true;
        }

        public void Dispose() => LoginUri.Dispose();
    }

    private sealed class Rig : IAsyncDisposable
    {
        public JobMonitor Monitor { get; } = new(NullLogger<JobMonitor>.Instance);
        public IOverlayController Controller { get; } = Substitute.For<IOverlayController>();
        public IOSInterop Os { get; } = Substitute.For<IOSInterop>();
        public ForumLoginOverlayService Service { get; }
        public List<ForumLoginOverlayViewModel> Shown { get; } = [];

        public Rig()
        {
            Controller.When(c => c.Enqueue(Arg.Any<IOverlayViewModel>())).Do(call =>
            {
                var overlay = (ForumLoginOverlayViewModel)call.Arg<IOverlayViewModel>();
                overlay.Controller = Controller;
                lock (Shown) Shown.Add(overlay);
            });
            Service = new ForumLoginOverlayService(Controller, Monitor, Os);
        }

        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync(CancellationToken.None);
            Monitor.Dispose();
        }
    }

    private static async Task<ForumLoginOverlayViewModel> TheOverlay(Rig rig, AUiTest test)
    {
        await test.EventuallyOnUi(() => { lock (rig.Shown) rig.Shown.Should().ContainSingle(); });
        lock (rig.Shown) return rig.Shown[0];
    }

    [Fact]
    public async Task AForumSignInOpensAnOverlayNamingItsSite()
    {
        await using var rig = new Rig();
        await rig.Service.StartAsync(CancellationToken.None);
        var job = new FakeLoginJob(ForumSite.VectorPlexus);

        var running = rig.Monitor.Begin<FakeLoginJob, bool>(job);
        var overlay = await TheOverlay(rig, this);

        overlay.SiteName.Should().Be("VectorPlexus");
        job.Finish.SetResult();
    }

    [Fact]
    public async Task TheOverlayFollowsThePageTheBrowserWasSentTo_AndOpensItAgainOnRequest()
    {
        await using var rig = new Rig();
        await rig.Service.StartAsync(CancellationToken.None);
        var job = new FakeLoginJob(ForumSite.LoversLab);
        var running = rig.Monitor.Begin<FakeLoginJob, bool>(job);
        var overlay = await TheOverlay(rig, this);
        var authorize = new Uri("https://www.loverslab.com/oauth/authorize/?state=s");

        job.LoginUri.OnNext(authorize);
        await EventuallyOnUi(() => overlay.Uri.Should().Be(authorize));
        overlay.OpenAgain.Execute(Unit.Default);

        rig.Os.Received(1).OpenUri(authorize);
        job.Finish.SetResult();
    }

    [Fact]
    public async Task TheOverlayClosesWhenTheJobEnds()
    {
        await using var rig = new Rig();
        await rig.Service.StartAsync(CancellationToken.None);
        var job = new FakeLoginJob(ForumSite.LoversLab);
        var running = rig.Monitor.Begin<FakeLoginJob, bool>(job);
        var overlay = await TheOverlay(rig, this);

        job.Finish.SetResult();

        await EventuallyOnUi(() => overlay.CompletionTask.IsCompleted.Should().BeTrue());
        rig.Controller.Received(1).Remove(overlay);
    }

    [Fact]
    public async Task CancelStopsTheJobAndClosesTheOverlay()
    {
        await using var rig = new Rig();
        await rig.Service.StartAsync(CancellationToken.None);
        var job = new FakeLoginJob(ForumSite.LoversLab);
        var task = rig.Monitor.Begin<FakeLoginJob, bool>(job);
        var overlay = await TheOverlay(rig, this);

        overlay.Cancel.Execute(Unit.Default);

        await EventuallyOnUi(() => overlay.CompletionTask.IsCompleted.Should().BeTrue());
        var wait = async () => await task;
        await wait.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ASecondSignInReplacesTheFirstOverlay()
    {
        await using var rig = new Rig();
        await rig.Service.StartAsync(CancellationToken.None);
        var first = new FakeLoginJob(ForumSite.LoversLab);
        var second = new FakeLoginJob(ForumSite.VectorPlexus);
        var one = rig.Monitor.Begin<FakeLoginJob, bool>(first);
        await TheOverlay(rig, this);

        var two = rig.Monitor.Begin<FakeLoginJob, bool>(second);
        await EventuallyOnUi(() => { lock (rig.Shown) rig.Shown.Should().HaveCount(2); });

        lock (rig.Shown) rig.Shown[0].CompletionTask.IsCompleted.Should().BeTrue();
        first.Finish.SetResult();
        second.Finish.SetResult();
    }
}
