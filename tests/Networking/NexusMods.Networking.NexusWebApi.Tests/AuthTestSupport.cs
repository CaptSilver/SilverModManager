using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.NexusWebApi.DTOs.OAuth;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Networking.NexusWebApi.Auth;
using NexusMods.Sdk;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Settings;
using NSubstitute;

namespace NexusMods.Networking.NexusWebApi.Tests;

/// <summary>
/// The credential tests share one in-memory database, so they run in a single collection and
/// start from a database with no stored credentials.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class AuthStateCollection
{
    public const string Name = "Auth state";
}

internal static class AuthTestSupport
{
    public static async Task ClearCredentials(IConnection conn)
    {
        var ids = ApiKey.All(conn.Db).Select(e => e.Id)
            .Concat(JWTToken.All(conn.Db).Select(e => e.Id))
            .ToArray();
        if (ids.Length == 0) return;

        using (var tx = conn.BeginTransaction())
        {
            foreach (var id in ids) tx.Delete(id, false);
            await tx.Commit();
        }

        await conn.Excise(ids);
    }

    public static async Task StoreToken(IConnection conn, string accessToken, TimeSpan validFor)
    {
        using var tx = conn.BeginTransaction();
        var reply = new JwtTokenReply
        {
            AccessToken = accessToken,
            RefreshToken = "stored-refresh",
            Type = "Bearer",
            ExpiresIn = 3600,
            // CreatedAt is back-dated so that CreatedAt + ExpiresIn lands validFor from now.
            CreatedAt = DateTimeOffset.UtcNow.Add(validFor).ToUnixTimeSeconds() - 3600,
        };
        JWTToken.Create(conn.Db, tx, reply);
        await tx.Commit();
    }

    public static OAuth CreateOAuth(IJobMonitor jobMonitor, HttpMessageHandler handler)
    {
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<NexusAuthSettings>(Arg.Any<string?>()).Returns(new NexusAuthSettings { OAuthClientId = "test-client" });
        return new OAuth(jobMonitor, NullLogger<OAuth>.Instance, new HttpClient(handler), Substitute.For<IIDGenerator>(), Substitute.For<IOSInterop>(), settings);
    }

    public static CredentialRoutedMessageFactory CreateRouter(IConnection conn, OAuth oauth)
    {
        return new CredentialRoutedMessageFactory(
            conn,
            new ApiKeyMessageFactory(conn),
            new OAuth2MessageFactory(conn, oauth, NullLogger<OAuth2MessageFactory>.Instance)
        );
    }
}
