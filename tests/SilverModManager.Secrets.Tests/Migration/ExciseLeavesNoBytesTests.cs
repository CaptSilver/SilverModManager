using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.MnemonicDB;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Storage.Abstractions;
using SilverModManager.MnemonicDB.Storage.RocksDbBackend;
using SilverModManager.Networking.NexusWebApi.Auth;
using SilverModManager.Networking.NexusWebApi.Migration;
using SilverModManager.Secrets.Chain;
using SilverModManager.Secrets.Migration;
using Xunit;

namespace SilverModManager.Secrets.Tests.Migration;

/// <summary>
/// Moving a credential out of the database has to leave none of its bytes behind in the files RocksDB wrote.
/// This measures what is actually on disk after the Nexus steps run, rather than trusting that excise worked.
/// </summary>
public class ExciseLeavesNoBytesTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("excise-bytes-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private ServiceProvider BuildProvider()
    {
        var path = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_folder, "MnemonicDB.rocksdb"));
        path.CreateDirectory();

        return new ServiceCollection()
            .AddLogging()
            .AddMnemonicDB()
            .AddSingleton<IStoreBackend>(_ => new Backend())
            .AddDatomStoreSettings(new DatomStoreSettings { Path = path })
            .AddJWTTokenModel()
            .AddApiKeyModel()
            .BuildServiceProvider();
    }

    private static async Task WriteLegacyRows(IConnection connection, string accessToken, string refreshToken, string apiKey)
    {
        using var tx = connection.BeginTransaction();
        var token = tx.TempId();
        tx.Add(token, JWTToken.AccessToken, accessToken);
        tx.Add(token, JWTToken.RefreshToken, refreshToken);
        tx.Add(token, JWTToken.ExpiresAt, new DateTimeOffset(2031, 5, 6, 7, 8, 9, TimeSpan.Zero));
        var key = tx.TempId();
        tx.Add(key, ApiKey.Key, apiKey);
        await tx.Commit();
    }

    // Kept out of the test body so that no IDb the migration touched is still reachable from a local when the
    // database is compacted: a live snapshot pins the old version of the data, and the old bytes with it.
    private static async Task<bool> RunNexusSteps(IConnection connection, IPersistentSecretStore store)
    {
        var steps = new ISecretMigrationStep[]
        {
            new NexusApiKeyMigration(connection, NullLogger<NexusApiKeyMigration>.Instance),
            new NexusOAuthTokenMigration(connection, NullLogger<NexusOAuthTokenMigration>.Instance),
        };
        return await new SecretMigrationRunner(store, steps, NullLogger<SecretMigrationRunner>.Instance).RunAsync(CancellationToken.None);
    }

    private static string[] Hits(string folder, string extension, params string[] markers)
    {
        var needles = markers.Select(Encoding.UTF8.GetBytes).ToArray();
        return Directory.EnumerateFiles(folder, $"*.{extension}", SearchOption.AllDirectories)
            .Where(file =>
            {
                var bytes = System.IO.File.ReadAllBytes(file);
                return needles.Any(needle => bytes.AsSpan().IndexOf(needle) >= 0);
            })
            .Select(file => Path.GetRelativePath(folder, file))
            .ToArray();
    }

    [Fact]
    public async Task AMovedCredentialLeavesNoBytesInTheDatabaseFilesOnceItIsCompacted()
    {
        var id = Guid.NewGuid().ToString("N");
        var accessToken = $"SECRETMARKER-{id}-access";
        var refreshToken = $"SECRETMARKER-{id}-refresh";
        var apiKey = $"SECRETMARKER-{id}-key";
        string[] markers = [accessToken, refreshToken, apiKey];
        var store = new InMemorySecretStore();

        await using (var provider = BuildProvider())
        {
            var connection = provider.GetRequiredService<IConnection>();
            await WriteLegacyRows(connection, accessToken, refreshToken, apiKey);

            // Without this the scan below could pass because the values were never on disk to begin with.
            await connection.FlushAndCompact(false);
            Hits(_folder, "sst", markers).Should().NotBeEmpty("the rows are in the database files before the move, so the scan can see them");

            var moved = await RunNexusSteps(connection, new PersistentSecretStore(store));
            moved.Should().BeTrue();

            // A snapshot held by a collected-but-unfinalised object would pin the old version of the data.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await connection.FlushAndCompact(false);
        }

        (await store.GetAsync(NexusSecretKeys.ApiKey, CancellationToken.None)).Should().Be(new SecretResult.Found(apiKey));
        Hits(_folder, "sst", markers).Should().BeEmpty("excise plus compaction removes the values from the sorted tables");

        // The write-ahead log only rotates on a flush, so it can still hold the first write. That is recorded, not failed.
        var logHits = Hits(_folder, "log", markers);
        TestContext.Current.TestOutputHelper?.WriteLine(logHits.Length == 0
            ? "No write-ahead log file holds a marker."
            : $"Write-ahead log files that still hold a marker: {string.Join(", ", logHits)}");
    }
}
