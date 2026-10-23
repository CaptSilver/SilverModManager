using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Secrets;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;
using SilverModManager.Secrets.Chain;
using SilverModManager.Secrets.Overlay;
using NSubstitute;
using Xunit;

namespace SilverModManager.Secrets.Tests.Chain;

public class AddSecretsTests : IDisposable
{
    private static readonly SecretKey ApiKey = new("nexus.api-key");

    private readonly string _scratch = Directory.CreateTempSubdirectory("add-secrets-").FullName;
    private readonly FakeEnvironment _env = new();
    private readonly InMemorySecretStore _service = new();

    public void Dispose() => Directory.Delete(_scratch, recursive: true);

    private string DataHome => Path.Combine(_scratch, "data");

    private ServiceProvider Build(SecretStoreOptions options, bool withService = false, SecretBackendChoice? setting = null)
    {
        var shared = FileSystem.Shared;
        var fileSystem = shared.CreateOverlayFileSystem(
            new Dictionary<AbsolutePath, AbsolutePath>(),
            new Dictionary<KnownPath, AbsolutePath> { [KnownPath.XDG_DATA_HOME] = shared.FromUnsanitizedFullPath(DataHome) });

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(fileSystem)
            .AddSingleton<IEnvironmentReader>(_env);

        if (withService) services.AddSingleton<ISecretServiceStore>(new FakeServiceStore(_service));

        var settings = Substitute.For<ISettingsManager>();
        settings.Get<SecretsSettings>(Arg.Any<string?>()).Returns(new SecretsSettings { Backend = setting ?? SecretBackendChoice.Auto });
        services.AddSingleton(settings);

        services.AddSecrets(options);
        return services.BuildServiceProvider();
    }

    private static SecretStoreOptions Options(SecretBackendChoice? backend = null, AbsolutePath? file = null, SecretStoreMode mode = SecretStoreMode.NonInteractive)
        => new() { Mode = mode, Backend = backend, SecretsFile = file };

    private string SecretsJson => Path.Combine(DataHome, ApplicationConstants.DataDirectoryName, "secrets.json");

    [Fact]
    public async Task Memory_composes_an_overlay_over_an_in_memory_store_and_registers_no_service_store()
    {
        var options = Options(SecretBackendChoice.Memory);
        await using var provider = Build(options);

        var store = provider.GetRequiredService<ISecretStore>().Should().BeOfType<OverlaySecretStore>().Subject;
        store.Inner.Should().BeOfType<InMemorySecretStore>();
        provider.GetService<ISecretServiceStore>().Should().BeNull();
    }

    [Fact]
    public async Task Memory_wins_over_the_users_setting()
    {
        await using var provider = Build(Options(SecretBackendChoice.Memory), setting: SecretBackendChoice.FileOnly);

        provider.GetRequiredService<ISecretStore>().Should().BeOfType<OverlaySecretStore>().Which.Inner.Should().BeOfType<InMemorySecretStore>();
    }

    [Fact]
    public async Task The_options_instance_is_registered_for_other_projects_to_read()
    {
        var options = Options(SecretBackendChoice.Memory, mode: SecretStoreMode.Interactive);
        await using var provider = Build(options);

        provider.GetRequiredService<SecretStoreOptions>().Should().BeSameAs(options);
    }

    [Fact]
    public async Task A_graph_without_an_environment_reader_gets_the_process_one()
    {
        var services = new ServiceCollection().AddLogging().AddSingleton(FileSystem.Shared).AddSecrets(Options(SecretBackendChoice.Memory));
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IEnvironmentReader>().Should().BeOfType<ProcessEnvironmentReader>();
    }

    [Fact]
    public async Task Auto_writes_to_the_service_and_leaves_no_file()
    {
        await using var provider = Build(Options(), withService: true);

        (await provider.GetRequiredService<ISecretStore>().SetAsync(ApiKey, "v", CancellationToken.None)).Should().BeOfType<SecretWriteResult.Written>();

        _service.Calls.Should().Contain("Set nexus.api-key");
        System.IO.File.Exists(SecretsJson).Should().BeFalse();
    }

    [Fact]
    public async Task Auto_without_a_service_store_uses_the_file()
    {
        await using var provider = Build(Options());

        (await provider.GetRequiredService<ISecretStore>().SetAsync(ApiKey, "v", CancellationToken.None)).Should().BeOfType<SecretWriteResult.Written>();

        System.IO.File.Exists(SecretsJson).Should().BeTrue();
    }

    [Fact]
    public async Task File_only_from_the_options_never_touches_the_service()
    {
        await using var provider = Build(Options(SecretBackendChoice.FileOnly), withService: true);
        var store = provider.GetRequiredService<ISecretStore>();

        await store.SetAsync(ApiKey, "v", CancellationToken.None);
        await store.GetAsync(ApiKey, CancellationToken.None);
        await store.DescribeAsync(CancellationToken.None);

        _service.Calls.Should().BeEmpty();
        System.IO.File.Exists(SecretsJson).Should().BeTrue();
    }

    [Fact]
    public async Task File_only_from_the_users_setting_never_touches_the_service()
    {
        await using var provider = Build(Options(), withService: true, setting: SecretBackendChoice.FileOnly);
        var store = provider.GetRequiredService<ISecretStore>();

        await store.SetAsync(ApiKey, "v", CancellationToken.None);

        _service.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_persistent_store_is_the_inner_chain_and_writes_a_key_the_overlay_pins()
    {
        _env.Values["NEXUS_API_KEY"] = "pinned";
        await using var provider = Build(Options(SecretBackendChoice.Memory));
        var overlay = provider.GetRequiredService<ISecretStore>();
        var persistent = provider.GetRequiredService<IPersistentSecretStore>();

        (await overlay.SetAsync(ApiKey, "x", CancellationToken.None)).Should().BeOfType<SecretWriteResult.ReadOnly>();
        (await persistent.SetAsync(ApiKey, "migrated", CancellationToken.None)).Should().BeOfType<SecretWriteResult.Written>();

        (await persistent.GetAsync(ApiKey, CancellationToken.None)).Should().Be(new SecretResult.Found("migrated"));
        (await overlay.GetAsync(ApiKey, CancellationToken.None)).Should().Be(new SecretResult.Found("pinned"));
    }

    [Fact]
    public async Task The_nexus_and_github_environment_aliases_are_built_in()
    {
        _env.Values["NEXUS_API_KEY"] = "n";
        _env.Values["GITHUB_TOKEN"] = "g";
        await using var provider = Build(Options(SecretBackendChoice.Memory));
        var store = provider.GetRequiredService<ISecretStore>();

        (await store.GetAsync(ApiKey, CancellationToken.None)).Should().Be(new SecretResult.Found("n"));
        (await store.GetAsync(new SecretKey("github.token"), CancellationToken.None)).Should().Be(new SecretResult.Found("g"));
    }

    [Fact]
    public async Task A_secrets_file_from_the_options_is_consulted()
    {
        var path = Path.Combine(_scratch, "options.env");
        System.IO.File.WriteAllText(path, "NEXUS_API_KEY=from-options\n");
        await using var provider = Build(Options(SecretBackendChoice.Memory, FileSystem.Shared.FromUnsanitizedFullPath(path)));

        (await provider.GetRequiredService<ISecretStore>().GetAsync(ApiKey, CancellationToken.None)).Should().Be(new SecretResult.Found("from-options"));
    }

    [Fact]
    public async Task A_secrets_file_from_the_environment_is_consulted_and_the_option_wins_over_it()
    {
        var fromEnv = Path.Combine(_scratch, "env.env");
        var fromOption = Path.Combine(_scratch, "option.env");
        System.IO.File.WriteAllText(fromEnv, "NEXUS_API_KEY=from-env-file\n");
        System.IO.File.WriteAllText(fromOption, "NEXUS_API_KEY=from-option-file\n");
        _env.Values["SILVERMM_SECRETS_FILE"] = fromEnv;

        await using (var provider = Build(Options(SecretBackendChoice.Memory)))
            (await provider.GetRequiredService<ISecretStore>().GetAsync(ApiKey, CancellationToken.None)).Should().Be(new SecretResult.Found("from-env-file"));

        await using (var provider = Build(Options(SecretBackendChoice.Memory, FileSystem.Shared.FromUnsanitizedFullPath(fromOption))))
            (await provider.GetRequiredService<ISecretStore>().GetAsync(ApiKey, CancellationToken.None)).Should().Be(new SecretResult.Found("from-option-file"));
    }

    [Fact]
    public async Task A_secrets_file_that_does_not_exist_makes_reads_unavailable()
    {
        _env.Values["SILVERMM_SECRETS_FILE"] = Path.Combine(_scratch, "nope.env");
        await using var provider = Build(Options(SecretBackendChoice.Memory));

        var result = await provider.GetRequiredService<ISecretStore>().GetAsync(ApiKey, CancellationToken.None);

        result.Should().BeOfType<SecretResult.Unavailable>().Which.Kind.Should().Be(SecretUnavailableKind.Malformed);
    }
}
