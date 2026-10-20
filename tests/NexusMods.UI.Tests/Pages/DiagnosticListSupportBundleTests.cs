using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.App.UI;
using NexusMods.App.UI.Pages.Diagnostics;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Support;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace NexusMods.UI.Tests.Pages;

public class DiagnosticListSupportBundleTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly AbsolutePath BundlePath = FileSystem.Shared.FromUnsanitizedFullPath("/state/SilverModManager/SupportBundles/support-bundle-1.zip");

    private async Task<(DiagnosticListViewModel Vm, ISupportBundleWriter Writer, IAvaloniaInterop Interop)> Create()
    {
        var writer = Substitute.For<ISupportBundleWriter>();
        var interop = Substitute.For<IAvaloniaInterop>();
        // The view model binds on construction, which has to happen on the UI thread.
        var vm = await OnUi(() => Task.FromResult(ActivatorUtilities.CreateInstance<DiagnosticListViewModel>(Provider, writer, interop)));
        return (vm, writer, interop);
    }

    [Fact]
    public async Task Test_CopySupportBundle_WritesTheBundleAndPutsItsPathOnTheClipboard()
    {
        var (vm, writer, interop) = await Create();
        writer.WriteAsync(Arg.Any<CancellationToken>()).Returns(BundlePath);

        await OnUi(async () => await vm.CopySupportBundleCommand.Execute(System.Reactive.Unit.Default));

        await writer.Received(1).WriteAsync(Arg.Any<CancellationToken>());
        await interop.Received(1).SetClipboardTextAsync(BundlePath.ToString());
        vm.SupportBundleMessage.Should().Contain(BundlePath.ToString());
    }

    [Fact]
    public async Task Test_CopySupportBundle_WriterFailureIsShownAndNothingIsCopied()
    {
        var (vm, writer, interop) = await Create();
        writer.WriteAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk full"));

        await OnUi(async () => await vm.CopySupportBundleCommand.Execute(System.Reactive.Unit.Default));

        await interop.DidNotReceiveWithAnyArgs().SetClipboardTextAsync(default!);
        vm.SupportBundleMessage.Should().Contain("disk full");
    }

    [Fact]
    public async Task Test_CopySupportBundle_ClipboardFailureStillReportsWhereTheBundleIs()
    {
        var (vm, writer, interop) = await Create();
        writer.WriteAsync(Arg.Any<CancellationToken>()).Returns(BundlePath);
        interop.SetClipboardTextAsync(Arg.Any<string>()).ThrowsAsync(new InvalidOperationException("No clipboard registered!"));

        await OnUi(async () => await vm.CopySupportBundleCommand.Execute(System.Reactive.Unit.Default));

        vm.SupportBundleMessage.Should().Contain(BundlePath.ToString()).And.Contain("clipboard");
    }
}
