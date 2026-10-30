using System.Reactive;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI;
using SilverModManager.Games.FOMOD.UI;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Resources;

namespace SilverModManager.UI.Tests;

[Collection("RxAppExceptionHandler")]
public class GuidedInstallerStepViewModelTests
{
    [Fact]
    public async Task GoToNext_WithoutAnInstallationStep_DoesNothing()
    {
        var thrown = new List<Exception>();
        var previousHandler = RxApp.DefaultExceptionHandler;
        RxApp.DefaultExceptionHandler = Observer.Create<Exception>(thrown.Add);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
            var viewModel = new GuidedInstallerStepViewModel(
                NullLogger<GuidedInstallerStepViewModel>.Instance,
                new NoImages<Uri>(),
                new NoImages<FileIdentity>());
            using var activation = viewModel.Activator.Activate();
            viewModel.InstallationStep.Should().BeNull();

            // Executing the command directly skips the CanGoNext gate the footer normally applies.
            viewModel.FooterStepperViewModel.GoToNextCommand.Execute().Subscribe(_ => { }, _ => { });

            thrown.Should().BeEmpty();
            viewModel.ShowInstallationCompleteScreen.Should().BeFalse();
            });
        }
        finally
        {
            RxApp.DefaultExceptionHandler = previousHandler;
        }
    }

    private sealed class NoImages<TIdentifier> : IResourceLoader<TIdentifier, Bitmap> where TIdentifier : notnull
    {
        public ValueTask<Resource<Bitmap>> LoadResourceAsync(TIdentifier resourceIdentifier, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
