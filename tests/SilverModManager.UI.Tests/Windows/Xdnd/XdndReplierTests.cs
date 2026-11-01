using Avalonia.Input;
using FluentAssertions;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>The XdndStatus message layout the receiver sends back to the source.</summary>
public class XdndReplierTests
{
    private const nint Target = 500;
    private const nint Source = 600;

    private readonly FakeXTransport _x = new();
    private readonly XdndAtoms _atoms;
    private readonly XdndReplier _replier;

    public XdndReplierTests()
    {
        _atoms = new XdndAtoms(_x);
        _replier = new XdndReplier(Target, _x, _atoms, new XdndActions(_atoms));
    }

    [Fact]
    public void AnAcceptedCopyIsAddressedToTheSourceFromTheTargetWithBothFlagBitsSet()
    {
        _replier.Status(Source, DragDropEffects.Copy);

        _x.SentMessages.Should().ContainSingle().Which.Should().Be(
            new SentClientMessage(Source, _atoms.XdndStatus, Target, 3, 0, 0, _atoms.XdndActionCopy));
    }

    [Fact]
    public void AMoveCarriesTheMoveAction()
    {
        _replier.Status(Source, DragDropEffects.Move);

        _x.SentMessages.Single().L4.Should().Be(_atoms.XdndActionMove);
    }

    [Fact]
    public void ARefusalClearsTheAcceptBitKeepsContinuousPositionsAndSendsNoAction()
    {
        _replier.Status(Source, DragDropEffects.None);

        _x.SentMessages.Should().ContainSingle().Which.Should().Be(
            new SentClientMessage(Source, _atoms.XdndStatus, Target, 2, 0, 0, 0));
    }

    [Fact]
    public void TheMessageIsFlushedSoTheSourceSeesItAtOnce()
    {
        _replier.Status(Source, DragDropEffects.Copy);

        _x.Calls.Last().Should().BeOfType<FlushCall>();
    }
}
