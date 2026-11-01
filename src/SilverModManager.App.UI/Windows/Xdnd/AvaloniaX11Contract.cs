using System.Collections;
using System.Reflection;
using Avalonia.Platform;

namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>
/// The Avalonia.X11 internals the event tap relies on, resolved once by reflection. Written against the 11.3.5
/// decompile; <c>AvaloniaX11ContractTests</c> runs it against the referenced assembly so an upgrade that moves any of
/// this fails the build instead of the user's drag.
/// </summary>
public sealed class AvaloniaX11Contract
{
    private const string X11WindowName = "Avalonia.X11.X11Window";
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private AvaloniaX11Contract(Type x11WindowType, FieldInfo platformField, PropertyInfo windows, Type eventHandlerType, Type xEventType)
    {
        X11WindowType = x11WindowType;
        PlatformField = platformField;
        PlatformType = platformField.FieldType;
        Windows = windows;
        EventHandlerType = eventHandlerType;
        XEventType = xEventType;
    }

    /// <summary>Avalonia.X11.X11Window, what TopLevel.PlatformImpl is on X11.</summary>
    public Type X11WindowType { get; }

    /// <summary>X11Window._platform : AvaloniaX11Platform, a private instance field.</summary>
    public FieldInfo PlatformField { get; }

    /// <summary>Avalonia.X11.AvaloniaX11Platform.</summary>
    public Type PlatformType { get; }

    /// <summary>AvaloniaX11Platform.Windows : Dictionary&lt;nint, X11EventDispatcher.EventHandler&gt;, keyed by XID.</summary>
    public PropertyInfo Windows { get; }

    /// <summary>delegate void EventHandler(ref XEvent), nested in X11EventDispatcher.</summary>
    public Type EventHandlerType { get; }

    /// <summary>Avalonia.X11.XEvent: a blittable struct at least as big as Xlib's 192-byte XEvent (Avalonia pads it to 264).</summary>
    public Type XEventType { get; }

    /// <param name="failure">What did not resolve and which Avalonia.X11 version was looked at; null on success.</param>
    public static AvaloniaX11Contract? TryResolve(Assembly avaloniaX11, out string? failure)
    {
        var version = avaloniaX11.GetName().Version;
        string? missing = null;
        var contract = Resolve(avaloniaX11, ref missing);
        failure = contract is null ? $"{avaloniaX11.GetName().Name} {version}: {missing}" : null;
        return contract;
    }

    private static AvaloniaX11Contract? Resolve(Assembly assembly, ref string? missing)
    {
        var windowType = assembly.GetType(X11WindowName);
        if (windowType is null) return Missing(ref missing, $"type {X11WindowName} not found");
        if (typeof(ITopLevelImpl).IsAssignableFrom(windowType) is false) return Missing(ref missing, $"{X11WindowName} no longer implements ITopLevelImpl");

        var platformField = windowType.GetField("_platform", Instance);
        if (platformField is null || platformField.IsStatic) return Missing(ref missing, $"instance field {X11WindowName}._platform not found");

        var windows = platformField.FieldType.GetProperty("Windows", Instance);
        if (windows is null) return Missing(ref missing, $"property {platformField.FieldType.FullName}.Windows not found");

        var dictionary = windows.PropertyType;
        if (dictionary.IsGenericType is false || dictionary.GetGenericTypeDefinition() != typeof(Dictionary<,>) || dictionary.GetGenericArguments()[0] != typeof(nint))
            return Missing(ref missing, $"{platformField.FieldType.FullName}.Windows is {dictionary}, expected Dictionary<nint, handler>");

        var handlerType = dictionary.GetGenericArguments()[1];
        var parameters = handlerType.GetMethod("Invoke")?.GetParameters();
        if (typeof(Delegate).IsAssignableFrom(handlerType) is false || parameters is not [{ ParameterType.IsByRef: true } parameter])
            return Missing(ref missing, $"{handlerType} is not a delegate taking one event by reference");

        var eventType = parameter.ParameterType.GetElementType();
        if (eventType is null || eventType.IsValueType is false) return Missing(ref missing, $"{handlerType} does not take a struct by reference");

        return new AvaloniaX11Contract(windowType, platformField, windows, handlerType, eventType);
    }

    private static AvaloniaX11Contract? Missing(ref string? missing, string what)
    {
        missing = what;
        return null;
    }

    /// <summary>The handler dictionary of the platform behind this window; null when the impl isn't an X11Window.</summary>
    public IDictionary? WindowsOf(ITopLevelImpl impl)
    {
        if (X11WindowType.IsInstanceOfType(impl) is false) return null;
        var platform = PlatformField.GetValue(impl);
        return platform is null ? null : Windows.GetValue(platform) as IDictionary;
    }
}
