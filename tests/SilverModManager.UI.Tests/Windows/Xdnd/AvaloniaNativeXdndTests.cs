using FluentAssertions;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>The retirement check: once Avalonia's X11 backend speaks XDND itself, the receiver must stay out of its way.</summary>
public class AvaloniaNativeXdndTests
{
    private sealed class AtomsWithXdnd
    {
#pragma warning disable CS0649
        public nint XdndAware;
#pragma warning restore CS0649
    }

    private sealed class AtomsWithoutXdnd
    {
#pragma warning disable CS0649
        public nint WmDeleteWindow;
#pragma warning restore CS0649
    }

    private sealed class AtomsWithXdndProperty
    {
        public nint XdndAware { get; } = 0;
    }

    [Fact]
    public void IsPresentIn_ATypeWithAnXdndAwareFieldMeansAvaloniaHandlesXdnd() =>
        new AvaloniaNativeXdnd().IsPresentIn(typeof(AtomsWithXdnd)).Should().BeTrue();

    [Fact]
    public void IsPresentIn_ATypeWithAnXdndAwarePropertyCountsToo() =>
        new AvaloniaNativeXdnd().IsPresentIn(typeof(AtomsWithXdndProperty)).Should().BeTrue();

    [Fact]
    public void IsPresentIn_ATypeWithoutItMeansTheReceiverIsNeeded() =>
        new AvaloniaNativeXdnd().IsPresentIn(typeof(AtomsWithoutXdnd)).Should().BeFalse();

    [Fact]
    public void IsPresentIn_NoAtomsTypeMeansNoNativeXdnd() =>
        new AvaloniaNativeXdnd().IsPresentIn((Type?)null).Should().BeFalse();

    [Fact]
    public void IsPresentIn_TheReferencedAvaloniaX11HasNoXdnd() =>
        new AvaloniaNativeXdnd().IsPresentIn(typeof(Avalonia.X11PlatformOptions).Assembly).Should().BeFalse(
            "the receiver exists because Avalonia.X11 11.3.5 has no XdndAware; when this turns true, delete Windows/Xdnd");
}
