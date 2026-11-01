using FluentAssertions;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>The atoms the receiver interns through the transport, and the property helper that decodes format-32 data.</summary>
public class XdndAtomsTests
{
    [Fact]
    public void EveryNameIsInternedOnce()
    {
        var x = new FakeXTransport();

        _ = new XdndAtoms(x);

        x.InternCalls.Should().BeEquivalentTo(
        [
            "XdndAware", "XdndEnter", "XdndPosition", "XdndStatus", "XdndLeave", "XdndDrop", "XdndFinished",
            "XdndTypeList", "XdndSelection", "XdndActionCopy", "XdndActionMove", "XdndActionLink", "XdndActionAsk",
            "XdndActionPrivate", "text/uri-list", "INCR", "SilverModManager_XDND_0", "SilverModManager_XDND_1",
        ]);
        x.InternCalls.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void DataPropertiesAreTheTwoAlternatingNames()
    {
        var x = new FakeXTransport();

        var atoms = new XdndAtoms(x);

        atoms.DataProperties.Select(x.AtomName).Should().Equal("SilverModManager_XDND_0", "SilverModManager_XDND_1");
    }

    [Fact]
    public void AtomsCarryTheValuesTheTransportGave()
    {
        var x = new FakeXTransport();

        var atoms = new XdndAtoms(x);

        x.AtomName(atoms.XdndDrop).Should().Be("XdndDrop");
        x.AtomName(atoms.TextUriList).Should().Be("text/uri-list");
        x.AtomName(atoms.Incr).Should().Be("INCR");
    }

    [Fact]
    public void LongsDecodesFormat32AsPointerSizedValues()
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(7L).CopyTo(bytes, 0);
        BitConverter.GetBytes(-2L).CopyTo(bytes, 8);

        new XProperty(4, 32, bytes).Longs().Should().Equal(7, -2);
    }

    [Fact]
    public void LongsRefusesOtherFormats()
    {
        var act = () => new XProperty(31, 8, [1, 2, 3]).Longs();

        act.Should().Throw<InvalidOperationException>().WithMessage("*format 8*");
    }
}
