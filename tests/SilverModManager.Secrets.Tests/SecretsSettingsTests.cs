using FluentAssertions;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Sdk.Settings;
using NSubstitute;
using Xunit;

namespace SilverModManager.Secrets.Tests;

public class SecretsSettingsTests
{
    private static (PropertyOptions Options, IContainerOptions? Container) Configure()
    {
        var builder = Substitute.For<ISettingsBuilder>();
        builder.ConfigureBackend(Arg.Any<StorageBackendOptions>()).Returns(builder);
        builder.ConfigureProperty(
            Arg.Any<System.Linq.Expressions.Expression<Func<SecretsSettings, SecretBackendChoice>>>(),
            Arg.Any<PropertyOptions<SecretsSettings, SecretBackendChoice>>(),
            Arg.Any<IContainerOptions?>()).Returns(builder);

        SecretsSettings.Configure(builder);

        var call = builder.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(ISettingsBuilder.ConfigureProperty));
        var args = call.GetArguments();
        return ((PropertyOptions)args[1]!, (IContainerOptions?)args[2]);
    }

    [Fact]
    public void The_backend_defaults_to_auto()
    {
        new SecretsSettings().Backend.Should().Be(SecretBackendChoice.Auto);
    }

    [Fact]
    public void The_settings_id_uses_the_application_namespace()
    {
        SecretsSettings.SettingsId.Should().Be("SilverModManager.Secrets.SecretsSettings");
    }

    [Fact]
    public void The_backend_is_offered_in_advanced_as_auto_or_file_only_and_never_memory()
    {
        var (options, container) = Configure();

        options.Section.Should().Be(Sections.Advanced);
        options.RequiresRestart.Should().BeTrue();
        var choices = container.Should().BeOfType<SingleValueMultipleChoiceContainerOptions>().Subject;
        choices.AllowedValues.Should().Equal(SecretBackendChoice.Auto, SecretBackendChoice.FileOnly);
    }

    [Fact]
    public void The_backend_description_explains_both_choices()
    {
        var (options, _) = Configure();

        var description = ((PropertyOptions<SecretsSettings, SecretBackendChoice>)options).DescriptionFactory(SecretBackendChoice.Auto);

        description.Should().Contain("keyring").And.Contain("readable by any program running as you");
    }

    [Fact]
    public void The_settings_use_the_json_backend()
    {
        var builder = Substitute.For<ISettingsBuilder>();
        builder.ConfigureBackend(Arg.Any<StorageBackendOptions>()).Returns(builder);
        builder.ConfigureProperty(
            Arg.Any<System.Linq.Expressions.Expression<Func<SecretsSettings, SecretBackendChoice>>>(),
            Arg.Any<PropertyOptions<SecretsSettings, SecretBackendChoice>>(),
            Arg.Any<IContainerOptions?>()).Returns(builder);

        SecretsSettings.Configure(builder);

        builder.Received().ConfigureBackend(Arg.Any<StorageBackendOptions>());
    }
}
