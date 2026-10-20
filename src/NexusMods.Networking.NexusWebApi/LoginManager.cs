using System.Diagnostics;
using System.Net;
using DynamicData.Aggregation;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.DTOs.OAuth;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.Query;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Networking.NexusWebApi.Auth;
using NexusMods.Sdk;
using NexusMods.Sdk.Threading;
using R3;

namespace NexusMods.Networking.NexusWebApi;
using UserInDb = NexusMods.Abstractions.NexusModsLibrary.Models.User;

/// <summary>
/// Component for handling login and logout from the Nexus Mods
/// </summary>
[PublicAPI]
public sealed class LoginManager : IDisposable, ILoginManager
{
    private readonly ILogger<LoginManager> _logger;
    private readonly OAuth _oauth;
    private readonly NexusApiClient _nexusApiClient;
    private readonly IAuthenticatingMessageFactory _msgFactory;
    private readonly IApiKeyPrompt _apiKeyPrompt;

    private readonly BehaviorSubject<UserInfo?> _userInfo = new(initialValue: null);

    /// <inheritdoc/>
    public Observable<UserInfo?> UserInfoObservable => _userInfo;

    /// <inheritdoc/>
    public UserInfo? UserInfo => _cachedUserInfo.Get();

    private readonly IDisposable _observeDatomDisposable;

    // Timing Constants
    
    /// <summary>
    /// How long UserInfo is cached before requiring refresh (in minutes).
    /// </summary>
    private const int CacheExpiryMinutes = 60;

    /// <summary>
    /// How often to proactively refresh UserInfo to prevent cache expiry (in minutes).
    /// Must be less than CacheExpiryMinutes to prevent cache expiration.
    /// </summary>
    private const int PeriodicRefreshIntervalMinutes = 59;

    /// <summary>
    /// Maximum number of retry attempts when refreshing UserInfo fails.
    /// </summary>
    private const int MaxRefreshRetries = 3;

    /// <summary>
    /// Initial delay between retry attempts when refreshing UserInfo fails (in seconds).
    /// </summary>
    private const int InitialRetryDelaySeconds = 3;

    /// <summary>
    /// Constructor.
    /// </summary>
    public LoginManager(
        IConnection conn,
        NexusApiClient nexusApiClient,
        IAuthenticatingMessageFactory msgFactory,
        OAuth oauth,
        IApiKeyPrompt apiKeyPrompt,
        ILogger<LoginManager> logger)
    {
        _oauth = oauth;
        _apiKeyPrompt = apiKeyPrompt;
        _conn = conn;
        _msgFactory = msgFactory;
        _nexusApiClient = nexusApiClient;
        _logger = logger;

        // Signed in means a token or a key is stored. A plain merge of the two streams would let the
        // token stream's "empty" evict the user info right after the key stream's "present".
        var hasToken = _conn.ObserveDatoms(JWTToken.PrimaryAttribute).IsNotEmpty().ToObservable();
        var hasKey = _conn.ObserveDatoms(ApiKey.PrimaryAttribute).IsNotEmpty().ToObservable();

        _observeDatomDisposable = hasToken
            .CombineLatest(hasKey, static (token, key) => token || key)
            .DistinctUntilChanged()
            .SubscribeAwait(async (hasValue, cancellationToken) =>
            {
                if (!hasValue)
                {
                    _cachedUserInfo.Evict();
                    _userInfo.OnNext(value: null);
                }
                else
                {
                    UserInfo? userInfo;
                    try
                    {
                        userInfo = await Verify(cancellationToken);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        // A pasted key can be wrong. That is not an unhandled error in a subscriber.
                        _logger.LogWarning(e, "Unable to verify the stored credentials");
                        userInfo = null;
                    }

                    // A key verified while this check was in flight is newer than its result.
                    userInfo = _cachedUserInfo.Get() ?? userInfo;
                    _cachedUserInfo.Store(userInfo);
                    _userInfo.OnNext(userInfo);
                }
            }, awaitOperation: AwaitOperation.Sequential, configureAwait: false);

        // Set up periodic refresh to prevent cache expiry
        _periodicRefreshDisposable = Observable
            .Timer(TimeSpan.FromMinutes(PeriodicRefreshIntervalMinutes), TimeSpan.FromMinutes(PeriodicRefreshIntervalMinutes))
            .SubscribeAwait(async (_, cancellationToken) =>
            {
                await TryRefreshUserInfoSafely(cancellationToken, "periodic update");
            }, configureAwait: false);
    }

    private CachedObject<UserInfo> _cachedUserInfo = new(TimeSpan.FromMinutes(CacheExpiryMinutes));
    private readonly SemaphoreSlim _verifySemaphore = new(initialCount: 1, maxCount: 1);
    private readonly IDisposable _periodicRefreshDisposable;
    private readonly IConnection _conn;

    private async ValueTask<UserInfo?> Verify(CancellationToken cancellationToken)
    {
        var cachedValue = _cachedUserInfo.Get();
        if (cachedValue is not null) return cachedValue;

        using var _ = await _verifySemaphore.WaitAsyncDisposable(cancellationToken);
        cachedValue = _cachedUserInfo.Get();
        if (cachedValue is not null) return cachedValue;

        var isAuthenticated = await _msgFactory.IsAuthenticated();
        if (!isAuthenticated) return null;

        var userInfo = await _msgFactory.Verify(_nexusApiClient, cancellationToken);
        _cachedUserInfo.Store(userInfo);

        if (userInfo is not null) await AddUserToDb(userInfo);
        return userInfo;
    }

    private async Task RefreshUserInfoWithRetry(CancellationToken cancellationToken)
    {
        const int maxRetries = MaxRefreshRetries;
        var delay = TimeSpan.FromSeconds(InitialRetryDelaySeconds);
        
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                // Force cache eviction to trigger a fresh API call
                _cachedUserInfo.Evict();
                var userInfo = await Verify(cancellationToken);

                if (userInfo is null) 
                    continue;

                _cachedUserInfo.Store(userInfo);
                _userInfo.OnNext(userInfo);
                return; // Success, exit retry loop
            }
            catch (TaskCanceledException)
            {
                // Cancellation requested, exit gracefully
                return;
            }
            catch (Exception ex) when (attempt < maxRetries - 1)
            {
                _logger.LogWarning(ex, "Error refreshing user info, attempt {Attempt}/{MaxAttempts}", 
                    attempt + 1, maxRetries);
                
                // Exponential backoff for all retryable exceptions
                // Base delay: 3s, multiplied by 2^attempt
                // Attempt 0: 3s, Attempt 1: 6s, Attempt 2: 12s
                // Total delay if all retries fail: 21 seconds
                var exponentialDelay = TimeSpan.FromMilliseconds(
                    delay.TotalMilliseconds * Math.Pow(2, attempt));
                await Task.Delay(exponentialDelay, cancellationToken);
            }
        }
        
        _logger.LogWarning("Failed to refresh user info after {MaxAttempts} attempts", maxRetries);
    }

    private async Task TryRefreshUserInfoSafely(CancellationToken cancellationToken, string context)
    {
        try
        {
            // Only refresh if we have a cached value (user is logged in)
            if (_cachedUserInfo.Get() is not null)
                await RefreshUserInfoWithRetry(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh user info during {Context}", context);
        }
    }

    private async ValueTask AddUserToDb(UserInfo userInfo)
    {
        using var tx = _conn.BeginTransaction();

        var existingDatoms = _conn.Db.Datoms(UserInDb.NexusId, userInfo.UserId.Value);
        Debug.Assert(existingDatoms.Count <= 1, "ID should be unique");

        EntityId entityId;
        if (existingDatoms.TryGetFirst(out var existingDatom))
        {
            entityId = existingDatom.E;
        }
        else
        {
            entityId = tx.TempId();
            tx.Add(entityId, UserInDb.NexusId, userInfo.UserId.Value);
        }

        tx.Add(entityId, UserInDb.Name, userInfo.Name);

        if (userInfo.AvatarUrl is not null)
            tx.Add(entityId, UserInDb.AvatarUri, userInfo.AvatarUrl);

        await tx.Commit();
    }

    /// <inheritdoc />
    public ValueTask<UserInfo?> GetUserInfoAsync(CancellationToken token) => Verify(token);

    /// <inheritdoc />
    public async Task<bool> GetIsUserLoggedInAsync(CancellationToken token = default)
    {
        return await GetUserInfoAsync(token) is not null;
    }

    /// <summary>
    /// Enables automatic refresh of user info based on an observable boolean trigger.
    /// </summary>
    /// <param name="triggerObservable">Observable that triggers refresh when true</param>
    /// <returns>IDisposable to stop the refresh subscription</returns>
    public IDisposable RefreshOnObservable(Observable<bool> triggerObservable)
    {
        return triggerObservable
            .DistinctUntilChanged()
            .Where(isActive => isActive)
            .SubscribeAwait(async (_, cancellationToken) =>
            {
                await TryRefreshUserInfoSafely(cancellationToken, "window focus");
            }, configureAwait: false);
    }

    /// <summary>
    /// Show a browser and log into Nexus Mods
    /// </summary>
    /// <param name="token"></param>
    public async Task LoginAsync(CancellationToken token = default)
    {
        // OAuth needs a client id registered for this app. Without one the key dialog is the only
        // way in, so go straight to it instead of starting a flow that refuses.
        if (!_oauth.HasClientId)
        {
            await LoginWithApiKeyAsync(token);
            return;
        }

        JwtTokenReply? jwtToken;
        try
        {
            jwtToken = await _oauth.AuthorizeRequest(token);
        }
        catch (TaskCanceledException e)
        {
            _logger.LogError(e, "Unable to login: task was canceled");
            return;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Exception while logging in");
            return;
        }
        
        if (jwtToken is null)
        {
            _logger.LogError("Invalid new token in Login Manager");
            return;
        }
        
        using var tx = _conn.BeginTransaction();

        var newTokenEntity = JWTToken.Create(_conn.Db, tx, jwtToken);
        if (!newTokenEntity.HasValue)
        {
            _logger.LogError("Invalid new token data");
            return;
        }

        await tx.Commit();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Exceptions from the prompt propagate: a host with nobody to ask must fail loudly.
    /// </remarks>
    public async Task LoginWithApiKeyAsync(CancellationToken token = default)
    {
        // The key is checked against Nexus Mods while the dialog is open and stored only once it
        // passes. Storing first and verifying afterwards let an unchecked key win over a working
        // OAuth session (a stored key beats the token) for as long as the cached user was served.
        UserInfo? verified = null;
        var key = (await _apiKeyPrompt.PromptAsync(async (typed, cancellationToken) =>
        {
            verified = null;
            try
            {
                var response = await _nexusApiClient.Validate(typed.Trim(), cancellationToken);
                verified = ApiKeyMessageFactory.ToUserInfo(response.Data);
                return null;
            }
            catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogWarning(e, "Nexus Mods rejected the API key");
                return "Nexus Mods did not accept this API key. Check that it was copied completely.";
            }
            catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient reports its timeout as a cancellation; nobody cancelled the dialog.
                _logger.LogWarning(e, "Timed out verifying the API key");
                return "Nexus Mods did not answer in time. Try again.";
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogWarning(e, "Unable to verify the API key");
                return $"Could not check the API key with Nexus Mods: {e.Message}";
            }
        }, token))?.Trim();

        if (string.IsNullOrEmpty(key) || verified is null) return;

        // Publish the verified account first so the credential observer, which fires once the key
        // lands, finds it cached instead of asking again, and so it replaces any earlier user. The
        // semaphore makes a check still running against the old credential store its result first.
        using var _ = await _verifySemaphore.WaitAsyncDisposable(token);
        _cachedUserInfo.Store(verified);
        _userInfo.OnNext(verified);
        await ApiKey.Set(_conn, key);
        await AddUserToDb(verified);
    }

    /// <summary>
    ///  Log out of Nexus Mods
    /// </summary>
    public async Task Logout()
    {
        _cachedUserInfo.Evict();

        // The key is a stored secret, so it is excised along with the token rather than only retracted.
        await ExciseCredentials(JWTToken.All(_conn.Db).Select(e => e.Id).Concat(ApiKey.All(_conn.Db).Select(e => e.Id)));
    }

    private async Task ExciseCredentials(IEnumerable<EntityId> entities)
    {
        var tokenEntities = entities.ToArray();

        // Retract the entities first, so the UI updates, then excise them
        using var tx = _conn.BeginTransaction();
        foreach (var entity in tokenEntities)
            tx.Delete(entity, false);
        await tx.Commit();


        await _conn.Excise(tokenEntities);
    }

    /// <inheritdoc />
    public async Task<bool> EnsureLoggedIn(string message, CancellationToken token = default)
    {
        if (await GetIsUserLoggedInAsync(token)) return true;
        
        // TODO: Improve this to show a special dialog to inform the user they need to log in to perform the operation
        // https://github.com/Nexus-Mods/NexusMods.App/issues/2562
        _logger.LogWarning("This operation requires login: {Message}", message);
        await LoginAsync(token);
        return await GetIsUserLoggedInAsync(token);
    }
    
    
    /// <inheritdoc/>
    public void Dispose()
    {
        _verifySemaphore.Dispose();
        _observeDatomDisposable.Dispose();
        _periodicRefreshDisposable.Dispose();
    }
}
