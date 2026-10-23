using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Abstractions.Steam.DTOs;
using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.Networking.Steam.DTOs;
using SilverModManager.Networking.Steam.Exceptions;
using NexusMods.Paths;
using SilverModManager.Sdk.IO;
using Polly;
using Polly.Retry;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;

namespace SilverModManager.Networking.Steam;

/// <summary>
/// Base class for a Steam session. Steam works in a rather strange way. Internally it communicates
/// via websockets. This means that many of the operations require sending a message, then listening
/// on a separate callback handler for the response. This class attempts to abstract this process and
/// provide a cleaner interface for other parts of the app.
/// </summary>
public class Session : ISteamSession
{
    private bool _isConnected = false;
    private bool _isLoggedOn = false;
    private volatile ExceptionDispatchInfo? _loginFailure;

    internal readonly ILogger<Session> _logger;
    private readonly IAuthInterventionHandler _handler;

    /// <summary>
    /// Base steam component, this is used for communicating with the Steam network.
    /// </summary>
    private readonly SteamClient _steamClient;
    
    /// <summary>
    /// The component used for user related operations.
    /// </summary>
    private readonly SteamUser _steamUser;
    
    /// <summary>
    /// The component used for getting information about apps (games)
    /// </summary>
    private readonly SteamApps _steamApps;
    
    /// <summary>
    /// Component for getting content information (actual game data)
    /// </summary>
    private readonly SteamContent _steamContent;
    
    /// <summary>
    /// CDN data, used to get download locations for game data.
    /// </summary>
    private readonly Client _cdnClient;

    private readonly CallbackManager _callbacks;
    private readonly IAuthStorage _authStorage;
    private readonly CDNPool _cdnPool;

    private ConcurrentDictionary<(AppId, DepotId), byte[]> _depotKeys = new();
    private ConcurrentDictionary<(AppId, DepotId, ManifestId, string Branch), ulong> _manifestRequestCodes = new();
    internal readonly ResiliencePipeline _pipeline;

    public Session(ILogger<Session> logger, IAuthInterventionHandler handler, IAuthStorage storage)
    {
        _logger = logger;
        _handler = handler;
        _authStorage = storage;

        var steamConfiguration = SteamConfiguration.Create(configurator =>
        {
            // The client will dispose of these on its own
            configurator.WithHttpClientFactory(_ => new HttpClient());
        });
        _steamClient = new SteamClient(steamConfiguration);
        _steamUser = RequireHandler<SteamUser>(_steamClient);
        _steamApps = RequireHandler<SteamApps>(_steamClient);
        _steamContent = RequireHandler<SteamContent>(_steamClient);
        _cdnClient = new Client(_steamClient);
        _cdnPool = new CDNPool(this);
        
        // Some parts of this interface use callbacks instead of more natural async methods. So we need to register
        // those callbacks here.
        _callbacks = new CallbackManager(_steamClient);
        _callbacks.Subscribe(WrapAsync<SteamClient.ConnectedCallback>(ConnectedCallback));
        _callbacks.Subscribe(WrapAsync<SteamClient.DisconnectedCallback>(DisconnectedCallback));
        _callbacks.Subscribe(WrapAsync<SteamUser.LoggedOnCallback>(LoggedOnCallback));
        _callbacks.Subscribe(WrapAsync<SteamApps.LicenseListCallback>(LicenseListCallback));

        _pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions())
            .AddTimeout(TimeSpan.FromSeconds(10))
            .Build();
    }

    /// <summary>
    /// The Steam Content module
    /// </summary>
    internal SteamContent Content => _steamContent;
    
    /// <summary>
    /// The CDN client, used for downloading game data.
    /// </summary>
    internal Client CDNClient => _cdnClient;
    
    /// <summary>
    /// The CDN pool, used for downloading game data.
    /// </summary>
    internal CDNPool CDNPool => _cdnPool;

    private Task LicenseListCallback(SteamApps.LicenseListCallback arg)
    {
        return Task.CompletedTask;
    }
    
    private async Task LoggedOnCallback(SteamUser.LoggedOnCallback callback)
    {
        _isLoggedOn = true;
        _logger.LogInformation("Logged on to Steam network.");
    }

    private async Task DisconnectedCallback(SteamClient.DisconnectedCallback callback)
    {
        _logger.LogInformation("Disconnected from Steam network.");
    }
    
    private async Task ConnectedCallback(SteamClient.ConnectedCallback callback)
    {
        _isConnected = true;
        try
        {
            await new SteamLogin(_authStorage, new SteamKitAuthenticator(this), _logger, Environment.GetEnvironmentVariable).RunAsync();
        }
        catch (Exception ex)
        {
            // This runs detached from the caller, so an exception left here would vanish and leave ConnectedAsync
            // waiting forever. Hand it over instead; disconnecting delivers a callback so the waiter wakes up and
            // sees it, and the next ConnectedAsync starts a fresh connection.
            _logger.LogWarning(ex, "Steam login failed");
            _loginFailure = ExceptionDispatchInfo.Capture(ex);
            _isConnected = false;
            _steamClient.Disconnect();
        }
    }

    /// <summary>
    /// Runs the authentication steps against the real Steam network.
    /// </summary>
    private sealed class SteamKitAuthenticator(Session session) : ISteamAuthenticator
    {
        public async Task<SteamCredentials> AuthenticateWithPasswordAsync(string username, string password)
        {
            var authSession = await session._steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
            {
                Username = username,
                Password = password,
            });

            var pollResponse = await authSession.PollingWaitForResultAsync();
            return new SteamCredentials(pollResponse.AccountName, pollResponse.RefreshToken);
        }

        public async Task<SteamCredentials> AuthenticateWithQrAsync()
        {
            var authSession = await session._steamClient.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails());

            authSession.ChallengeURLChanged = () => { session._handler.ShowQRCode(new Uri(authSession.ChallengeURL, UriKind.Absolute), CancellationToken.None); };

            session._handler.ShowQRCode(new Uri(authSession.ChallengeURL, UriKind.Absolute), CancellationToken.None);
            var pollResponse = await authSession.PollingWaitForResultAsync();
            return new SteamCredentials(pollResponse.AccountName, pollResponse.RefreshToken);
        }

        public void LogOn(SteamCredentials credentials) => session._steamUser.LogOn(new SteamUser.LogOnDetails
        {
            Username = credentials.AccountName,
            AccessToken = credentials.RefreshToken,
        });
    }

    // SteamClient registers these handlers itself; a missing one means the SteamKit version changed its defaults.
    private static T RequireHandler<T>(SteamClient client) where T : ClientMsgHandler =>
        client.GetHandler<T>() ?? throw new InvalidOperationException($"SteamClient has no {typeof(T).Name} handler registered");

    /// <summary>
    /// Wraps an async action in a synchronous action.
    /// </summary>
    private Action<T> WrapAsync<T>(Func<T, Task> action)
    {
        return arg => Task.Run(async () => await action(arg));
    }

    /// <inheritdoc />
    public async Task Connect(CancellationToken token)
    {
        await ConnectedAsync(token);
    }

    public async Task<ProductInfo> GetProductInfoAsync(AppId appId, CancellationToken cancellationToken = default)
    {
        await ConnectedAsync(cancellationToken);

        var jobs = await _steamApps.PICSGetProductInfo(new SteamApps.PICSRequest(appId.Value), null);
        if (jobs.Failed) throw new Exception($"Failed to get product info for app `{appId}`");

        var results = jobs.Results;
        if (results is null || results.Count == 0) throw new Exception($"Found no product info for app `{appId}`");

        return ProductInfoParser.Parse(results[0]);
    }

    /// <summary>
    /// Performs a login if required, returns once the login is complete
    /// </summary>
    private async Task ConnectedAsync(CancellationToken cancellationToken)
    {
        if (!_isConnected)
        {
            _loginFailure = null;
            _steamClient.Connect();
        }
        
        while (!_isLoggedOn)
        {
            _loginFailure?.Throw();
            await _callbacks.RunWaitCallbackAsync(cancellationToken);
        }
    }

    public async Task<ulong> GetManifestRequestCodeAsync(AppId appId, DepotId depotId, ManifestId manifestId, string branch)
    {
        if (_manifestRequestCodes.TryGetValue((appId, depotId, manifestId, branch), out var found))
            return found;
        await ConnectedAsync(CancellationToken.None);
        
        var requestCodeResult = await _steamContent.GetManifestRequestCode(depotId.Value, appId.Value, manifestId.Value, branch);
        if (requestCodeResult == 0)
        {
            _logger.LogWarning("Failed to get request code for depot {0} manifest {1}", depotId.Value, manifestId.Value);
            throw new FailedToGetRequestCode(appId, depotId, manifestId);
        }

        _logger.LogInformation("Got request code depot {1} manifest {2}", depotId.Value, manifestId.Value);

        _manifestRequestCodes.TryAdd((appId, depotId, manifestId, branch), requestCodeResult);
        return requestCodeResult;
    }
    
    /// <summary>
    /// Get a depot decryption key for a given depot.
    /// </summary>
    public async Task<byte[]> GetDepotKey(AppId appId, DepotId depotId)
    {
        // Try to get the cached key first
        
        if (_depotKeys.TryGetValue((appId, depotId), out var keyBytes))
            return keyBytes;
        await ConnectedAsync(CancellationToken.None);
        
        var key = await _steamApps.GetDepotDecryptionKey(depotId.Value, appId.Value);
        if (key.Result != EResult.OK)
        {
            _logger.LogWarning("Failed to get depot key for depot `{DepotId}`", depotId.Value);
            throw new Exception($"Failed to get depot key for depot `{depotId.Value}`");
        }

        _logger.LogInformation("Got depot key for depot `{DepotId}`", depotId.Value);
        _depotKeys.TryAdd((appId, depotId), key.DepotKey);
        
        return key.DepotKey;
    }

    public async Task<Manifest> GetManifestContents(AppId appId, DepotId depotId, ManifestId manifestId, string branch, CancellationToken token = default)
    {
        await ConnectedAsync(token);
        return await _pipeline.ExecuteAsync(async token => await _cdnPool.GetManifestContents(appId, depotId, manifestId,
                branch, token
            ), token
        );
    }

    public Stream GetFileStream(AppId appId, Manifest manifest, RelativePath file)
    {
        var chunkedProvider = new DepotChunkProvider(this, appId, manifest.DepotId,
            manifest, file
        );
        // 48 1MB chunks, 32 preloaded
        return new ChunkedStream<DepotChunkProvider>(chunkedProvider, capacity: 48, preFetch: 32);
    }
}
