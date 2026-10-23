using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Secrets;
using Xunit;

namespace NexusMods.Secrets.SecretService.Tests.Integration;

/// <summary>
/// The store against a real gnome-keyring on a private bus. Excluded from the default run because it needs
/// gnome-keyring-daemon and dbus-run-session; scripts/test.sh filters the trait out.
/// </summary>
[Trait("RequiresSecretService", "True")]
public class SecretServiceIntegrationTests(PrivateSecretServiceFixture fixture) : IClassFixture<PrivateSecretServiceFixture>
{
    /// <summary>Process start and scheduling noise on a loaded machine.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(1);

    private SecretServiceStore CreateStore(string? address = null)
    {
        Assert.SkipWhen(fixture.Service.SkipReason is not null, fixture.Service.SkipReason ?? "");
        return NewStore(address ?? fixture.Service.Address);
    }

    private static SecretServiceStore NewStore(string address) => new(
        new TmdsSecretServiceConnection(),
        new FakeEnvironment(address),
        new SecretStoreOptions { Mode = SecretStoreMode.NonInteractive },
        NullLogger.Instance);

    [Fact]
    public async Task Set_then_Get_returns_the_value()
    {
        using var store = CreateStore();
        var key = new SecretKey("roundtrip.value");

        (await store.SetAsync(key, "first", TestContext.Current.CancellationToken)).Should().BeOfType<SecretWriteResult.Written>();

        (await store.GetAsync(key, TestContext.Current.CancellationToken)).Should().Be(new SecretResult.Found("first"));
    }

    [Fact]
    public async Task Set_twice_keeps_one_item_and_returns_the_second_value()
    {
        using var store = CreateStore();
        var key = new SecretKey("replace.twice");

        await store.SetAsync(key, "one", TestContext.Current.CancellationToken);
        await store.SetAsync(key, "two", TestContext.Current.CancellationToken);

        (await store.GetAsync(key, TestContext.Current.CancellationToken)).Should().Be(new SecretResult.Found("two"));
        (await store.ListAsync(TestContext.Current.CancellationToken)).Count(k => k == key).Should().Be(1);
    }

    [Fact]
    public async Task Delete_then_Get_is_NotFound()
    {
        using var store = CreateStore();
        var key = new SecretKey("delete.me");
        await store.SetAsync(key, "gone soon", TestContext.Current.CancellationToken);

        (await store.DeleteAsync(key, TestContext.Current.CancellationToken)).Should().BeOfType<SecretWriteResult.Written>();

        (await store.GetAsync(key, TestContext.Current.CancellationToken)).Should().BeOfType<SecretResult.NotFound>();
    }

    [Fact]
    public async Task List_includes_a_stored_key()
    {
        using var store = CreateStore();
        var key = new SecretKey("list.me");
        await store.SetAsync(key, "x", TestContext.Current.CancellationToken);

        (await store.ListAsync(TestContext.Current.CancellationToken)).Should().Contain(key);
    }

    [Fact]
    public async Task Values_with_non_ascii_text_survive_the_round_trip()
    {
        using var store = CreateStore();
        var key = new SecretKey("unicode.value");
        const string value = "pässwörd ☃ \U0001F511";

        await store.SetAsync(key, value, TestContext.Current.CancellationToken);

        (await store.GetAsync(key, TestContext.Current.CancellationToken)).Should().Be(new SecretResult.Found(value));
    }

    [Fact]
    public async Task Missing_key_is_NotFound()
    {
        using var store = CreateStore();

        (await store.GetAsync(new SecretKey("never.stored"), TestContext.Current.CancellationToken)).Should().BeOfType<SecretResult.NotFound>();
    }

    [Fact]
    public async Task Item_is_readable_by_secret_tool_with_the_agreed_attributes()
    {
        using var store = CreateStore();
        await store.SetAsync(new SecretKey("nexus.api-key"), "from-the-app", TestContext.Current.CancellationToken);

        var start = new ProcessStartInfo("secret-tool")
        {
            ArgumentList = { "lookup", "application", "SilverModManager", "key", "nexus.api-key" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // The bus address goes on the child explicitly; nothing may fall back to the real session bus.
        start.Environment["DBUS_SESSION_BUS_ADDRESS"] = fixture.Service.Address;
        start.Environment["HOME"] = fixture.Service.HomeDirectory;
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        process.ExitCode.Should().Be(0, await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken));
        output.Should().Be("from-the-app");
    }

    [Fact]
    public async Task Describe_reports_the_unlocked_default_collection()
    {
        using var store = CreateStore();

        var status = await store.DescribeAsync(TestContext.Current.CancellationToken);

        status.Backend.Should().Be(SecretBackendKind.SecretService);
        status.Detail.Should().Contain("/org/freedesktop/secrets/collection/");
        status.Locked.Should().BeFalse();
        status.Writable.Should().BeTrue();
    }

    [Fact]
    public async Task A_bogus_bus_address_is_Unavailable_within_the_non_interactive_timeout()
    {
        using var store = NewStore("unix:path=/nonexistent/silvermm-test-bus");
        var watch = Stopwatch.StartNew();

        var result = await store.GetAsync(new SecretKey("nexus.api-key"), TestContext.Current.CancellationToken);

        result.Should().BeOfType<SecretResult.Unavailable>().Which.Kind.Should().Be(SecretUnavailableKind.NoBus);
        watch.Elapsed.Should().BeLessThan(SecretServiceTimeouts.Default.NonInteractive + Slack);
    }

    [Fact]
    public async Task A_bus_where_nothing_owns_the_Secret_Service_is_ServiceAbsent_rather_than_NoBus()
    {
        await using var bus = new EmptyBus();
        await bus.StartAsync();
        Assert.SkipWhen(bus.SkipReason is not null, bus.SkipReason ?? "");
        using var store = NewStore(bus.Address);

        var result = await store.GetAsync(new SecretKey("nexus.api-key"), TestContext.Current.CancellationToken);

        // The bus answered; only the keyring is missing. NoBus would mean the store never tried to start it.
        result.Should().BeOfType<SecretResult.Unavailable>().Which.Kind.Should().Be(SecretUnavailableKind.ServiceAbsent);
    }

    [Fact]
    public async Task Get_after_the_daemon_is_killed_is_Unavailable_rather_than_an_exception()
    {
        await using var service = new PrivateSecretService();
        await service.StartAsync();
        Assert.SkipWhen(service.SkipReason is not null, service.SkipReason ?? "");
        using var store = NewStore(service.Address);
        var key = new SecretKey("outlives.nothing");
        await store.SetAsync(key, "v", TestContext.Current.CancellationToken);
        (await store.GetAsync(key, TestContext.Current.CancellationToken)).Should().Be(new SecretResult.Found("v"));

        service.Kill();

        (await store.GetAsync(key, TestContext.Current.CancellationToken)).Should().BeOfType<SecretResult.Unavailable>();
    }
}
