using System.Linq.Expressions;
using System.Runtime.InteropServices;
using FluentAssertions;
using NSubstitute;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Platform.Proton.Tests;

public class HostExecutionSettingsTests
{
    private static (PropertyOptions<HostExecutionSettings, bool> Options, IContainerOptions? Container, string Description) Configure(Architecture architecture)
    {
        var builder = Substitute.For<ISettingsBuilder>();
        builder.ConfigureProperty(
            Arg.Any<Expression<Func<HostExecutionSettings, bool>>>(),
            Arg.Any<PropertyOptions<HostExecutionSettings, bool>>(),
            Arg.Any<IContainerOptions?>()).Returns(builder);

        HostExecutionSettings.Configure(builder, architecture);

        var call = builder.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(ISettingsBuilder.ConfigureProperty));
        var args = call.GetArguments();
        var options = (PropertyOptions<HostExecutionSettings, bool>)args[1]!;
        return (options, (IContainerOptions?)args[2], options.DescriptionFactory(false));
    }

    [Fact]
    public void OnAnX86_64Process_TheSettingHasNoPageRow()
        => Configure(Architecture.X64).Container.Should().BeNull();

    [Fact]
    public void OnAnArm64Process_TheSettingIsASwitchOnThePage()
        => Configure(Architecture.Arm64).Container.Should().BeOfType<BooleanContainerOptions>();

    [Fact]
    public void TheDescriptionSaysWhatTheCheckReadsAndNotSteamsOwnEmulator()
    {
        var (options, _, description) = Configure(Architecture.Arm64);

        options.DisplayName.Should().Be("Skip the x86-64 check");
        description.Should().Contain("/proc/sys/fs/binfmt_misc").And.NotContain("Steam's own");
    }
}
