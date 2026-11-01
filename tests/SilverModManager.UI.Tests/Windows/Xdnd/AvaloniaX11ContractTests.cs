using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Platform;
using FluentAssertions;
using NSubstitute;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>
/// The upgrade alarm: the receiver reflects into Avalonia.X11 internals, so these fail when the referenced assembly
/// moves. If Avalonia has grown its own XDND by then, delete Windows/Xdnd instead of updating the contract.
/// </summary>
public class AvaloniaX11ContractTests
{
    private static readonly Assembly AvaloniaX11 = typeof(X11PlatformOptions).Assembly;

    private static AvaloniaX11Contract Resolve()
    {
        var contract = AvaloniaX11Contract.TryResolve(AvaloniaX11, out var failure);
        failure.Should().BeNull();
        contract.Should().NotBeNull();
        return contract;
    }

    [Fact]
    public void TheReferencedAvaloniaX11Resolves()
    {
        var contract = AvaloniaX11Contract.TryResolve(AvaloniaX11, out var failure);

        contract.Should().NotBeNull(failure);
        failure.Should().BeNull();
    }

    [Fact]
    public void XEventIsAValueTypeAtLeastAsBigAsXlibs192Bytes()
    {
        var type = Resolve().XEventType;

        type.IsValueType.Should().BeTrue();
        // Xlib's XEvent union is 24 longs. Avalonia's mirror pads to 33 (264 bytes in 11.3.5), so XNextEvent never overruns it.
        Marshal.SizeOf(type).Should().BeGreaterThanOrEqualTo(192);
    }

    [Fact]
    public void TheHandlerTakesOneXEventByReference()
    {
        var contract = Resolve();

        var parameters = contract.EventHandlerType.GetMethod("Invoke")!.GetParameters();

        parameters.Should().ContainSingle();
        parameters[0].ParameterType.IsByRef.Should().BeTrue();
        parameters[0].ParameterType.GetElementType().Should().Be(contract.XEventType);
    }

    [Fact]
    public void WindowsIsADictionaryOfHandlersByXid()
    {
        var contract = Resolve();

        contract.Windows.PropertyType.Should().Be(typeof(Dictionary<,>).MakeGenericType(typeof(nint), contract.EventHandlerType));
    }

    [Fact]
    public void PlatformIsAnInstanceFieldOfTheWindow()
    {
        var contract = Resolve();

        contract.PlatformField.IsStatic.Should().BeFalse();
        contract.PlatformField.DeclaringType.Should().Be(contract.X11WindowType);
        contract.PlatformField.FieldType.Should().Be(contract.PlatformType);
        contract.Windows.DeclaringType.Should().Be(contract.PlatformType);
    }

    [Fact]
    public void TheX11WindowIsATopLevelImpl()
    {
        typeof(ITopLevelImpl).IsAssignableFrom(Resolve().X11WindowType).Should().BeTrue();
    }

    [Theory]
    [InlineData("XClientMessageEvent", "window", 32)]
    [InlineData("XClientMessageEvent", "message_type", 40)]
    [InlineData("XClientMessageEvent", "format", 48)]
    [InlineData("XClientMessageEvent", "ptr1", 56)]
    [InlineData("XClientMessageEvent", "ptr5", 88)]
    [InlineData("XSelectionEvent", "requestor", 32)]
    [InlineData("XSelectionEvent", "selection", 40)]
    [InlineData("XSelectionEvent", "target", 48)]
    [InlineData("XSelectionEvent", "property", 56)]
    [InlineData("XSelectionEvent", "time", 64)]
    [InlineData("XPropertyEvent", "window", 32)]
    [InlineData("XPropertyEvent", "atom", 40)]
    [InlineData("XPropertyEvent", "time", 48)]
    [InlineData("XPropertyEvent", "state", 56)]
    public void EventFieldsSitWhereXEventReaderReadsThem(string structName, string field, int offset)
    {
        var type = AvaloniaX11.GetType("Avalonia.X11." + structName);

        type.Should().NotBeNull();
        Marshal.OffsetOf(type, field).Should().Be(offset);
    }

    [Fact]
    public void AssemblyWithoutTheWindowTypeFailsWithAMessageNamingIt()
    {
        var contract = AvaloniaX11Contract.TryResolve(typeof(string).Assembly, out var failure);

        contract.Should().BeNull();
        failure.Should().Contain("Avalonia.X11.X11Window");
    }

    [Fact]
    public void FailureNamesTheAssemblyVersion()
    {
        AvaloniaX11Contract.TryResolve(typeof(string).Assembly, out var failure);

        failure.Should().Contain(typeof(string).Assembly.GetName().Version!.ToString());
    }

    [Fact]
    public void WindowsOfAnotherImplIsNull()
    {
        Resolve().WindowsOf(Substitute.For<ITopLevelImpl>()).Should().BeNull();
    }
}
