using System.Runtime.InteropServices;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Platform.Proton;

/// <summary>
/// The way past a wrong "this machine cannot run x86-64 programs" answer from <see cref="HostExecution"/>.
/// </summary>
/// <remarks>
/// The check reads the kernel's binfmt_misc table. In a sandbox that does not mount /proc/sys/fs/binfmt_misc
/// the table is unreadable although the kernel may well run x86-64 programs; this setting skips the check.
/// It cannot make an x86-64 program start on a machine with no translator at all.
/// </remarks>
public record HostExecutionSettings : ISettings
{
    /// <summary>Skip the x86-64 check and try to run Windows programs.</summary>
    public bool AssumeX86_64Runs { get; init; }

    public static string SettingsId => "SilverModManager.Platform.Proton.HostExecutionSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
        => Configure(settingsBuilder, RuntimeInformation.ProcessArchitecture);

    // On an x86-64 process the check always passes, so the row would be noise; it keeps its switch where the
    // check can be wrong. The key stays settable from the command line everywhere.
    internal static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder, Architecture architecture)
    {
        return settingsBuilder.ConfigureProperty(
            x => x.AssumeX86_64Runs,
            new PropertyOptions<HostExecutionSettings, bool>
            {
                Section = Sections.General,
                DisplayName = "Skip the x86-64 check",
                DescriptionFactory = _ => "Windows programs run through launchers that are usually x86-64. On a machine with another CPU the app checks /proc/sys/fs/binfmt_misc for an x86-64 interpreter such as FEX and refuses the launch when it finds none. Turn this on when x86-64 programs do run here but the app cannot read that table, as in a sandbox.",
            },
            architecture == Architecture.X64 ? null : new BooleanContainerOptions()
        );
    }
}
