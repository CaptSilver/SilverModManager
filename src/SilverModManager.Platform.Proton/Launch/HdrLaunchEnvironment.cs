using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// The environment a Proton launch needs for HDR when the loadout's HDR setting is on.
/// </summary>
/// <remarks>
/// <para>
/// Both variables are Proton-GE's (https://github.com/GloriousEggroll/proton-ge-custom, README "HDR" section).
/// <c>PROTON_ENABLE_WAYLAND=1</c> selects Wine's native Wayland driver, the one that reads the compositor's color
/// management on a Wayland desktop such as Plasma; plain X11/XWayland stays SDR outside Gamescope.
/// <c>PROTON_ENABLE_HDR=1</c> is the opt-in older builds need: their <c>proton</c> script maps it to the <c>hdr</c>
/// compat option and sets <c>DXVK_HDR=1</c> from that. Newer builds detect HDR on their own and still accept it.
/// </para>
/// <para>
/// <c>DXVK_HDR</c> is left out because Proton-GE sets it itself, <c>PROTON_FORCE_HDR</c> because it reports HDR whatever
/// the monitor can show, and <c>ENABLE_HDR_WSI</c> because it switches on a separate Vulkan HDR layer rather than
/// anything in Proton. Valve's Proton scripts read none of these, so the setting does nothing there.
/// </para>
/// <para>
/// The values are defaults: a value the launch already carries, such as one in the Steam launch options, is kept.
/// </para>
/// </remarks>
public static class HdrLaunchEnvironment
{
    private static readonly KeyValuePair<string, string>[] Variables =
    [
        new("PROTON_ENABLE_WAYLAND", "1"),
        new("PROTON_ENABLE_HDR", "1"),
    ];

    /// <summary>
    /// The variables to add: none when <paramref name="enabled"/> is false.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> For(bool enabled) => enabled ? Variables : [];

    /// <summary>
    /// The variables the loadout's HDR setting asks for.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> For(Loadout.ReadOnly loadout) => For(Loadout.EnableHdr.Get(loadout));
}
