using Avalonia.Input;
using FluentAssertions;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>The mapping between XDND action atoms and the effects Avalonia's drag events carry.</summary>
public class XdndActionsTests
{
    private readonly XdndAtoms _atoms = new(new FakeXTransport());
    private readonly XdndActions _actions;

    public XdndActionsTests() => _actions = new XdndActions(_atoms);

    [Fact]
    public void CopyMoveAndLinkAtomsMapToTheirEffects()
    {
        _actions.ToEffects(_atoms.XdndActionCopy).Should().Be(DragDropEffects.Copy);
        _actions.ToEffects(_atoms.XdndActionMove).Should().Be(DragDropEffects.Move);
        _actions.ToEffects(_atoms.XdndActionLink).Should().Be(DragDropEffects.Link);
    }

    [Fact]
    public void AskPrivateAndUnknownAtomsAreTreatedAsCopy()
    {
        _actions.ToEffects(_atoms.XdndActionAsk).Should().Be(DragDropEffects.Copy);
        _actions.ToEffects(_atoms.XdndActionPrivate).Should().Be(DragDropEffects.Copy);
        _actions.ToEffects(9999).Should().Be(DragDropEffects.Copy);
        _actions.ToEffects(0).Should().Be(DragDropEffects.Copy);
    }

    [Fact]
    public void EachEffectMapsBackToItsAtomAndNoneToNoAtom()
    {
        _actions.ToAtom(DragDropEffects.Copy).Should().Be(_atoms.XdndActionCopy);
        _actions.ToAtom(DragDropEffects.Move).Should().Be(_atoms.XdndActionMove);
        _actions.ToAtom(DragDropEffects.Link).Should().Be(_atoms.XdndActionLink);
        _actions.ToAtom(DragDropEffects.None).Should().Be(0);
    }

    [Fact]
    public void AnEffectSetPicksCopyBeforeMoveBeforeLink()
    {
        _actions.ToAtom(DragDropEffects.Copy | DragDropEffects.Move).Should().Be(_atoms.XdndActionCopy);
        _actions.ToAtom(DragDropEffects.Move | DragDropEffects.Link).Should().Be(_atoms.XdndActionMove);
    }
}
