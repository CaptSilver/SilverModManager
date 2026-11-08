using SilverModManager.Sdk.Settings;

namespace SilverModManager.Platform.Proton;

/// <summary>
/// The way past a wrong "this machine cannot run x86-64 programs" answer from <see cref="HostExecution"/>.
/// </summary>
/// <remarks>
/// Steam can start Proton through its own x86-64 emulator without registering it with the kernel, and the
/// check only sees the kernel's table. Set this to run Windows programs anyway.
/// </remarks>
public record HostExecutionSettings : ISettings
{
    /// <summary>Skip the x86-64 check and try to run Windows programs.</summary>
    public bool AssumeX86_64Runs { get; init; }

    public static string SettingsId => "SilverModManager.Platform.Proton.HostExecutionSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder.ConfigureProperty(
            x => x.AssumeX86_64Runs,
            new PropertyOptions<HostExecutionSettings, bool>
            {
                Section = Sections.General,
                DisplayName = "Assume this machine can run x86-64 programs",
                DescriptionFactory = _ => "Windows programs and Proton are x86-64. On a machine with another CPU, such as an ARM headset, they are refused unless an x86-64 emulator such as FEX is registered with the kernel. Turn this on when Proton runs there anyway, for example through Steam's own emulator.",
            },
            new BooleanContainerOptions()
        );
    }
}
