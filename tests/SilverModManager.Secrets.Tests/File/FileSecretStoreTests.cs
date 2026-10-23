using Xunit;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Secrets;
using NexusMods.Paths;
using NexusMods.Sdk;
using R3;
using NexusMods.Secrets.File;

namespace NexusMods.Secrets.Tests.File;

// Real filesystem on purpose: the file modes are what these tests are about.
public class FileSecretStoreTests : IDisposable
{
    private static readonly SecretKey ApiKey = new("nexus.api-key");
    private static readonly SecretKey Token = new("github.token");
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _scratch = Directory.CreateTempSubdirectory("file-secret-store-").FullName;
    private readonly string _dataHome;
    private readonly string _appDirectory;
    private readonly string _secretsPath;
    private readonly FileSecretStore _store;

    public FileSecretStoreTests()
    {
        _dataHome = Path.Combine(_scratch, "data");
        _appDirectory = Path.Combine(_dataHome, ApplicationConstants.DataDirectoryName);
        _secretsPath = Path.Combine(_appDirectory, "secrets.json");
        _store = CreateStore();
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_scratch, recursive: true);
    }

    private FileSecretStore CreateStore() => new(CreateFileSystem(), NullLogger<FileSecretStore>.Instance);

    private IFileSystem CreateFileSystem()
    {
        var shared = FileSystem.Shared;
        return shared.CreateOverlayFileSystem(
            new Dictionary<AbsolutePath, AbsolutePath>(),
            new Dictionary<KnownPath, AbsolutePath> { [KnownPath.XDG_DATA_HOME] = shared.FromUnsanitizedFullPath(_dataHome) });
    }

    private void CreateAppDirectory(UnixFileMode mode)
    {
        Directory.CreateDirectory(_appDirectory);
        System.IO.File.SetUnixFileMode(_appDirectory, mode);
    }

    private static Task<SecretWriteResult> Set(FileSecretStore store, SecretKey key, string value) => store.SetAsync(key, value, CancellationToken.None).AsTask();

    [Fact]
    public async Task The_first_set_creates_the_file_as_0600()
    {
        (await Set(_store, ApiKey, "abc")).Should().BeOfType<SecretWriteResult.Written>();

        System.IO.File.GetUnixFileMode(_secretsPath).Should().Be(OwnerOnly);
        (await _store.GetAsync(ApiKey, CancellationToken.None)).Should().Be(new SecretResult.Found("abc"));
    }

    [Fact]
    public async Task The_first_set_creates_a_missing_app_directory()
    {
        Directory.Exists(_appDirectory).Should().BeFalse();
        await Set(_store, ApiKey, "abc");
        Directory.Exists(_appDirectory).Should().BeTrue();
    }

    [Fact]
    public async Task Setting_does_not_change_the_mode_of_an_existing_app_directory()
    {
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        CreateAppDirectory(mode);

        await Set(_store, ApiKey, "abc");

        System.IO.File.GetUnixFileMode(_appDirectory).Should().Be(mode);
    }

    [Fact]
    public async Task Setting_on_an_existing_file_keeps_0600_and_leaves_no_temp_file()
    {
        await Set(_store, ApiKey, "abc");
        await Set(_store, Token, "def");
        await Set(_store, ApiKey, "changed");

        System.IO.File.GetUnixFileMode(_secretsPath).Should().Be(OwnerOnly);
        Directory.GetFiles(_appDirectory).Should().Equal(_secretsPath);
        (await _store.GetAsync(ApiKey, CancellationToken.None)).Should().Be(new SecretResult.Found("changed"));
        (await _store.GetAsync(Token, CancellationToken.None)).Should().Be(new SecretResult.Found("def"));
    }

    [Fact]
    public async Task A_missing_file_reads_as_not_found()
    {
        (await _store.GetAsync(ApiKey, CancellationToken.None)).Should().BeOfType<SecretResult.NotFound>();
    }

    [Theory]
    [InlineData(0x1A0, "640")] // 0640
    [InlineData(0x1A4, "644")] // 0644
    [InlineData(0x1B0, "660")] // 0660
    [InlineData(0x184, "604")] // 0604
    public async Task A_file_readable_by_group_or_other_is_refused(int mode, string octal)
    {
        await Set(_store, ApiKey, "abc");
        System.IO.File.SetUnixFileMode(_secretsPath, (UnixFileMode)mode);

        var result = await _store.GetAsync(ApiKey, CancellationToken.None);

        var unavailable = result.Should().BeOfType<SecretResult.Unavailable>().Subject;
        unavailable.Kind.Should().Be(SecretUnavailableKind.Faulted);
        unavailable.Reason.Should().Contain(_secretsPath).And.Contain(octal).And.EndWith($"chmod 600 {_secretsPath}");
    }

    [Fact]
    public async Task A_loose_file_is_not_written_and_its_mode_is_not_changed()
    {
        await Set(_store, ApiKey, "abc");
        System.IO.File.SetUnixFileMode(_secretsPath, (UnixFileMode)0x1A4);
        var before = await System.IO.File.ReadAllTextAsync(_secretsPath);

        (await Set(_store, Token, "def")).Should().BeOfType<SecretWriteResult.Unavailable>()
            .Which.Kind.Should().Be(SecretUnavailableKind.Faulted);
        (await _store.DeleteAsync(ApiKey, CancellationToken.None)).Should().BeOfType<SecretWriteResult.Unavailable>();

        System.IO.File.GetUnixFileMode(_secretsPath).Should().Be((UnixFileMode)0x1A4);
        (await System.IO.File.ReadAllTextAsync(_secretsPath)).Should().Be(before);
    }

    [Fact]
    public async Task A_symlink_to_a_0600_file_is_refused()
    {
        var target = Path.Combine(_scratch, "elsewhere.json");
        await System.IO.File.WriteAllTextAsync(target, """{"nexus.api-key":"abc"}""");
        System.IO.File.SetUnixFileMode(target, OwnerOnly);
        Directory.CreateDirectory(_appDirectory);
        System.IO.File.CreateSymbolicLink(_secretsPath, target);

        var result = await _store.GetAsync(ApiKey, CancellationToken.None);

        var unavailable = result.Should().BeOfType<SecretResult.Unavailable>().Subject;
        unavailable.Kind.Should().Be(SecretUnavailableKind.Faulted);
        unavailable.Reason.Should().Contain(_secretsPath).And.Contain("symbolic link").And.EndWith($"chmod 600 {_secretsPath}");
    }

    [Fact]
    public async Task A_file_owned_by_another_user_is_refused()
    {
        // chown needs root, so the foreign owner is simulated through the store's owner check instead.
        await Set(_store, ApiKey, "abc");
        using var store = new FileSecretStore(CreateFileSystem(), NullLogger<FileSecretStore>.Instance, effectiveUid: UnixFileOwner.EffectiveUid() + 1);

        var result = await store.GetAsync(ApiKey, CancellationToken.None);

        var unavailable = result.Should().BeOfType<SecretResult.Unavailable>().Subject;
        unavailable.Kind.Should().Be(SecretUnavailableKind.Faulted);
        unavailable.Reason.Should().Contain(_secretsPath).And.Contain("owned by").And.EndWith($"chmod 600 {_secretsPath}");
    }

    [Fact]
    public async Task A_file_that_is_not_json_is_malformed()
    {
        Directory.CreateDirectory(_appDirectory);
        await System.IO.File.WriteAllTextAsync(_secretsPath, "not json");
        System.IO.File.SetUnixFileMode(_secretsPath, OwnerOnly);

        var result = await _store.GetAsync(ApiKey, CancellationToken.None);

        result.Should().BeOfType<SecretResult.Unavailable>().Which.Kind.Should().Be(SecretUnavailableKind.Malformed);
    }

    [Fact]
    public async Task Concurrent_writes_of_different_keys_never_tear_the_file()
    {
        for (var round = 0; round < 50; round++)
        {
            using var store = CreateStore();
            await Task.WhenAll(
                Task.Run(() => Set(store, ApiKey, $"a-{round}")),
                Task.Run(() => Set(store, Token, $"b-{round}")));

            var state = JsonSerializer.Deserialize<Dictionary<string, string>>(await System.IO.File.ReadAllTextAsync(_secretsPath))!;
            state.Should().Contain("nexus.api-key", $"a-{round}");
            state.Should().Contain("github.token", $"b-{round}");
            System.IO.File.GetUnixFileMode(_secretsPath).Should().Be(OwnerOnly);
            Directory.GetFiles(_appDirectory).Should().Equal(_secretsPath);
        }
    }

    [Fact]
    public async Task Concurrent_writes_through_separate_store_instances_leave_one_full_state()
    {
        // Two stores share no semaphore; the file must still be one writer's complete result, never torn.
        for (var round = 0; round < 50; round++)
        {
            using var first = CreateStore();
            using var second = CreateStore();
            await Task.WhenAll(
                Task.Run(() => Set(first, ApiKey, $"a-{round}")),
                Task.Run(() => Set(second, Token, $"b-{round}")));

            var state = JsonSerializer.Deserialize<Dictionary<string, string>>(await System.IO.File.ReadAllTextAsync(_secretsPath))!;
            var expectedA = state.TryGetValue("nexus.api-key", out var av) && av == $"a-{round}";
            var expectedB = state.TryGetValue("github.token", out var bv) && bv == $"b-{round}";
            (expectedA || expectedB).Should().BeTrue("the file is one writer's complete state");
            state.Keys.Should().OnlyContain(k => k == "nexus.api-key" || k == "github.token");
            Directory.GetFiles(_appDirectory).Should().Equal(_secretsPath);
        }
    }

    [Fact]
    public async Task Deleting_the_only_key_removes_the_file()
    {
        await Set(_store, ApiKey, "abc");

        (await _store.DeleteAsync(ApiKey, CancellationToken.None)).Should().BeOfType<SecretWriteResult.Written>();

        System.IO.File.Exists(_secretsPath).Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_one_of_two_keys_keeps_the_other()
    {
        await Set(_store, ApiKey, "abc");
        await Set(_store, Token, "def");

        await _store.DeleteAsync(ApiKey, CancellationToken.None);

        (await _store.GetAsync(ApiKey, CancellationToken.None)).Should().BeOfType<SecretResult.NotFound>();
        (await _store.GetAsync(Token, CancellationToken.None)).Should().Be(new SecretResult.Found("def"));
    }

    [Fact]
    public async Task Deleting_an_absent_key_succeeds()
    {
        (await _store.DeleteAsync(ApiKey, CancellationToken.None)).Should().BeOfType<SecretWriteResult.Written>();
    }

    [Fact]
    public async Task List_returns_the_stored_keys()
    {
        await Set(_store, ApiKey, "abc");
        await Set(_store, Token, "def");

        (await _store.ListAsync(CancellationToken.None)).Should().BeEquivalentTo([ApiKey, Token]);
    }

    [Fact]
    public async Task List_is_empty_when_the_file_is_refused()
    {
        await Set(_store, ApiKey, "abc");
        System.IO.File.SetUnixFileMode(_secretsPath, (UnixFileMode)0x1A4);

        (await _store.ListAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Changed_fires_after_set_and_delete()
    {
        var seen = new List<SecretKey>();
        using var subscription = _store.Changed.Subscribe(seen.Add);

        await Set(_store, ApiKey, "abc");
        await _store.DeleteAsync(ApiKey, CancellationToken.None);

        seen.Should().Equal(ApiKey, ApiKey);
    }

    [Fact]
    public async Task Describe_and_unlock_report_an_unencrypted_writable_file()
    {
        foreach (var status in new[] { await _store.DescribeAsync(CancellationToken.None), await _store.UnlockAsync(CancellationToken.None) })
        {
            status.Backend.Should().Be(SecretBackendKind.File);
            status.Detail.Should().Be(_secretsPath);
            status.Unencrypted.Should().BeTrue();
            status.Writable.Should().BeTrue();
            status.Locked.Should().BeFalse();
            status.OverriddenKeys.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Status_changed_never_fires()
    {
        var fired = false;
        using var subscription = _store.StatusChanged.Subscribe(_ => fired = true);

        await _store.DescribeAsync(CancellationToken.None);
        await _store.UnlockAsync(CancellationToken.None);

        fired.Should().BeFalse();
    }
}
