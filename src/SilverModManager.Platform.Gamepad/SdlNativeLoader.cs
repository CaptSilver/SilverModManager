using System.Reflection;
using System.Runtime.InteropServices;
using SDL;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Platform.Gamepad;

/// <summary>
/// Points the SDL binding at the libSDL3 shipped next to the app, falling back to the system's. The binding asks the
/// runtime for "SDL3", which on Linux would only find an unversioned libSDL3.so that most distributions do not install.
/// </summary>
internal static class SdlNativeLoader
{
    private const string BundledName = "libSDL3.so";
    private const string SystemName = "libSDL3.so.0";
    private static readonly object Gate = new();
    private static bool _installed;

    public static void Install()
    {
        lock (Gate)
        {
            if (_installed) return;
            // The runtime allows one resolver per assembly, and a second call throws.
            NativeLibrary.SetDllImportResolver(typeof(SDL3).Assembly, Resolve);
            _installed = true;
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.StartsWith("SDL3", StringComparison.Ordinal)) return 0;

        if (BundledNativeLibrary.TryLoad(BundledName, assembly, searchPath, out var handle, out _)) return handle;
        return NativeLibrary.TryLoad(SystemName, assembly, searchPath, out handle) ? handle : 0;
    }
}
