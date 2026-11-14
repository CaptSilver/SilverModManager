using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Abstractions.Steam.DTOs;
using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.Networking.Steam.Exceptions;
using NexusMods.Paths;
using NexusMods.Paths.Extensions;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Networking.Steam.CLI;

public static class Verbs
{
    internal static IServiceCollection AddSteamVerbs(this IServiceCollection collection) =>
        collection
            .AddModule("steam", "Verbs for interacting with Steam")
            .AddModule("steam app", "Verbs for querying app data")
            .AddVerb(() => IndexSteamApp)
            .AddVerb(() => Login)
            .AddVerb(() => FetchFile)
            .AddVerb(() => Logout);
    
    [Verb("steam login", "Starts the login process for Steam")]
    internal static async Task<int> Login(
        [Injected] IRenderer renderer,
        [Injected] ISteamSession steamSession,
        [Injected] CancellationToken token)
    {
        await steamSession.Connect(new RenderingAuthenticationHandler(renderer), token);
        return 0;
    }
    

    [Verb("steam fetch-file", "Fetches one file of a Steam depot manifest into the library, checked against the manifest's SHA-1; needs the Steam login")]
    internal static async Task<int> FetchFile(
        [Injected] IRenderer renderer,
        [Injected] ISteamDepotFiles files,
        [Injected] CancellationToken token,
        [Option("a", "app", "The Steam app id")] long app,
        [Option("d", "depot", "The depot id")] long depot,
        [Option("m", "manifest", "The manifest id, from Steam's depotcache folder or the build's page")] string manifest,
        [Option("p", "path", "The file's path inside the depot")] string path,
        [Option("l", "loadout", "The loadout whose game the file is for; leave it out to add the file to the library unassigned", true)] Loadout.ReadOnly? loadout)
    {
        if (app is <= 0 or > uint.MaxValue) throw new SteamVerbRefusedException($"'{app}' is not a Steam app id");
        if (depot is <= 0 or > uint.MaxValue) throw new SteamVerbRefusedException($"'{depot}' is not a Steam depot id");
        if (!ulong.TryParse(manifest.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var manifestId) || manifestId == 0)
            throw new SteamVerbRefusedException($"'{manifest}' is not a manifest id; it is a number of about 19 digits");
        if (string.IsNullOrWhiteSpace(path)) throw new SteamVerbRefusedException("The file's path inside the depot is empty");

        IAcquisitionContext acquisition = loadout is { } chosen ? new LoadoutAcquisition(chosen.LoadoutId) : UnassignedAcquisition.Instance;
        var file = await files.FetchFile(
            AppId.From((uint)app), DepotId.From((uint)depot), ManifestId.From(manifestId),
            RelativePath.FromUnsanitizedInput(path.Trim()), expected: null, acquisition, token,
            // The terminal that asked is the one to show the QR code on, not the window's overlay.
            new RenderingAuthenticationHandler(renderer));

        await renderer.TextLine($"Added {file.FileName} ({file.Size}) to the library, SHA-256 {file.Sha256.Value}");
        return 0;
    }

    [Verb("steam logout", "Forgets the stored Steam login")]
    private static async Task<int> Logout(
        [Injected] IRenderer renderer,
        [Injected] IAuthStorage authStorage,
        [Injected] ISteamSession steamSession,
        [Injected] CancellationToken token)
    {
        try
        {
            await authStorage.DeleteAsync(token);
        }
        catch (InvalidOperationException ex)
        {
            await renderer.Text(ex.Message);
            return 1;
        }

        // A verb forwarded to the window runs in its process, whose session would stay logged on with the forgotten login.
        steamSession.LogOff();

        await renderer.Text("The stored Steam login was removed.");
        return 0;
    }

    [Verb("steam app index", "Indexes a Steam app and updates the given output folder")]
    private static async Task<int> IndexSteamApp(
        [Injected] IRenderer renderer,
        [Injected] JsonSerializerOptions jsonSerializerOptions,
        [Injected] ISteamSession steamSession,
        [Option("a", "appId", "The steam app id to index")] long appId,
        [Option("o", "output", "The output folder to write the index to")] AbsolutePath output,
        [Injected] CancellationToken token)
    {
        var steamAppId = AppId.From((uint)appId);
        
        var indentedOptions = new JsonSerializerOptions(jsonSerializerOptions)
        {
            WriteIndented = true,
        };

        await steamSession.Connect(new RenderingAuthenticationHandler(renderer), token);

        await using (var _ = await renderer.WithProgress())
        {
            {
                var productInfo = await steamSession.GetProductInfoAsync(steamAppId, token);

                var hashFolder = output / "hashes";
                hashFolder.CreateDirectory();

                var existingHashes = await LoadExistingHashes(hashFolder, indentedOptions, token);

                // Write the product info to a file
                var productFile = output / "stores" / "steam" / "apps" / (productInfo.AppId + ".json").ToRelativePath();
                {
                    productFile.Parent.CreateDirectory();
                    await using var outputStream = productFile.Create();
                    await JsonSerializer.SerializeAsync(outputStream, productInfo, indentedOptions,
                        token
                    );
                }

                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = 4,
                    CancellationToken = token,
                };
                // For each depot and each manifest, download the manifest and index the files
                await Parallel.ForEachAsync(productInfo.Depots, options, async (depot, token) =>
                {
                    await Parallel.ForEachAsync(depot.Manifests, options, async (manifestInfo, token) =>
                    {
                        try
                        {
                            var manifest = await steamSession.GetManifestContents(steamAppId, depot.DepotId, manifestInfo.Value.ManifestId,
                                manifestInfo.Key, token
                            );

                            var manifestPath = output / "stores" / "steam" / "manifests" / (manifest.ManifestId + ".json").ToRelativePath();
                            {
                                manifestPath.Parent.CreateDirectory();
                                while (true)
                                {
                                    try
                                    {
                                        await using var outputStream = manifestPath.Create();
                                        await JsonSerializer.SerializeAsync(outputStream, manifest, indentedOptions,
                                            token
                                        );
                                        break;
                                    }
                                    catch (IOException)
                                    {
                                        await Task.Delay(1000, token);
                                    }
                                }
                            }

                            await IndexManifest(steamSession, renderer, steamAppId,
                                output, manifest, indentedOptions,
                                existingHashes, options
                            );
                        }
                        catch (FailedToGetRequestCode ex)
                        {
                            await renderer.Text($"Skipping because of: {ex.Message}");
                            return;
                        }
                    });
                });
            }
        }

        return 0;
    }

    private static async Task<ConcurrentBag<Sha1Value>> LoadExistingHashes(AbsolutePath folder, JsonSerializerOptions options, CancellationToken token)
    {
        var bag = new ConcurrentBag<Sha1Value>();
        var hashFiles = folder.EnumerateFiles("*.json", true);
        
        await Parallel.ForEachAsync(hashFiles, token, async (file, token) =>
        {
            try
            {
                await using var stream = file.Read();
                var hash = await JsonSerializer.DeserializeAsync<MultiHash>(stream, options, token);
                if (hash is null)
                    throw new InvalidDataException("the file holds no hash");
                bag.Add(hash.Sha1);
            }
            catch (Exception ex)
            {
                // Ignore errors
                Console.WriteLine($"Error loading hash file {file}: {ex.Message}");
            }
        });

        return bag;
    }

    private static async Task IndexManifest(ISteamSession session, IRenderer renderer, AppId appId, AbsolutePath output, Manifest manifest, JsonSerializerOptions indentedOptions, ConcurrentBag<Sha1Value> existingHashes, ParallelOptions options)
    {
        var writeLock = new SemaphoreSlim(1, 1);
        await Parallel.ForEachAsync(manifest.Files, options, async (file, token) =>
            {
                if (file.Size == Size.Zero)
                    return;
                if (existingHashes.Contains(file.Hash))
                    return;

                await using var progressTask = await renderer.StartProgressTask($"Hashing {file.Path}", maxValue: file.Size.Value);
                await using var stream = session.GetFileStream(appId, manifest, file.Path);
                await using var progressWrapper = new StreamProgressWrapper<ProgressTask>(stream, state: progressTask, notifyWritten: static (progressTask, values) =>
                {
                    var (current, _) = values;
                    var task = progressTask.Increment(current.Value);
                });

                var multiHash = await MultiHasher.HashStream(stream, cancellationToken: token);

                var fileName = multiHash.XxHash3 + ".json";
                var path = output / "hashes" / fileName[2..4] / fileName[2..];

                await writeLock.WaitAsync(token);
                if (!multiHash.Sha1.Equals(file.Hash))
                    throw new InvalidOperationException("Hash mismatch on downloaded file, expected: " + file.Hash + " got: " + multiHash.Sha1);
                
                try
                {
                    path.Parent.CreateDirectory();
                    {
                        await using var outputStream = path.Create();
                        await JsonSerializer.SerializeAsync(outputStream, multiHash, indentedOptions,
                            token
                        );
                    }
                    existingHashes.Add(multiHash.Sha1);
                }
                finally
                {
                    writeLock.Release();
                }
            }
        );
    }
}
