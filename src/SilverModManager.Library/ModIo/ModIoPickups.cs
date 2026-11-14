using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Library.Pickup;
using SilverModManager.Networking.ModIo;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Library.ModIo;

/// <summary>
/// Waits for the user to download a mod.io file in the browser when mod.io's own address would not serve it. The file
/// that turns up in the Downloads folder under exactly the listed name and size is checked against the listed MD5 and
/// added to the library; the folder's copy is never touched.
/// </summary>
internal sealed class ModIoPickups(IServiceProvider provider, IExpectedDownloads expected, ILogger<ModIoPickups> logger)
{
    /// <summary>
    /// Starts watching for the file. Only a download the user started may arm this: an update that nobody asked for must
    /// not leave the app waiting for a file.
    /// </summary>
    public IDisposable Arm(uint gameId, uint modId, ModIoFile file, long dateUpdated, IAcquisitionContext acquisition)
    {
        var sink = new Sink(provider, gameId, modId, file, dateUpdated, acquisition);
        sink.Registration = expected.Expect(new ExpectedDownload(file.FileName, file.FileSize, null, sink));

        // The pickup only looks at the expectations once something follows them, and until now only a modlist plan did.
        // It still watches nothing while the user's pick-up-from-Downloads setting is off.
        if (provider.GetService<DownloadsPickup>() is { } pickup)
            pickup.Follow();
        else
            logger.LogWarning("Nothing in this host watches the Downloads folder, so {File} has to be added to the library by hand", file.FileName);

        return sink.Registration;
    }

    private sealed class Sink(IServiceProvider provider, uint gameId, uint modId, ModIoFile file, long dateUpdated, IAcquisitionContext acquisition) : IPickupSink
    {
        public IDisposable? Registration { get; set; }

        public async Task Accept(AbsolutePath picked, Func<string, string, int> link, CancellationToken cancellationToken)
        {
            // The Downloads folder belongs to the user and the add may move what it is given, so a copy goes in.
            await using var folder = provider.GetRequiredService<TemporaryFileManager>().CreateFolder();
            var copy = folder.Path.Combine(file.FileName);
            await using (var source = picked.Read())
            await using (var destination = copy.Create())
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            await ModIoFiles.VerifyAndAdd(provider, copy, file, gameId, modId, dateUpdated, pinned: null, acquisition, cancellationToken);
            Registration?.Dispose();
        }
    }
}
