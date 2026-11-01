using Avalonia;
using FluentAssertions;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>The recording fake the session tests lean on: what it records and what it lets a test script.</summary>
public class FakeXTransportTests
{
    [Fact]
    public void SendClientMessageIsRecordedWithAllFields()
    {
        var x = new FakeXTransport();

        x.SendClientMessage(10, 20, 1, 2, 3, 4, 5);

        x.SentMessages.Should().Equal(new SentClientMessage(10, 20, 1, 2, 3, 4, 5));
    }

    [Fact]
    public void ConvertSelectionIsRecorded()
    {
        var x = new FakeXTransport();

        x.ConvertSelection(1, 2, 3, 4, 5);

        x.Conversions.Should().Equal(new SelectionConversion(1, 2, 3, 4, 5));
    }

    [Fact]
    public void ScriptedPropertyIsReturnedAndKeptWhenNotDeleted()
    {
        var x = new FakeXTransport();
        var property = new XProperty(31, 8, [1, 2]);
        x.SetProperty(7, 8, property);

        x.GetProperty(7, 8, delete: false).Should().BeSameAs(property);
        x.GetProperty(7, 8, delete: false).Should().BeSameAs(property);
    }

    [Fact]
    public void GetPropertyWithDeleteRemovesItAndRecordsTheRequest()
    {
        var x = new FakeXTransport();
        x.SetProperty(7, 8, new XProperty(31, 8, [1]));

        x.GetProperty(7, 8, delete: true).Should().NotBeNull();

        x.GetProperty(7, 8, delete: false).Should().BeNull();
        x.PropertyReads.Should().Equal(new PropertyRead(7, 8, true), new PropertyRead(7, 8, false));
    }

    [Fact]
    public void UnscriptedPropertyIsGone()
    {
        new FakeXTransport().GetProperty(1, 2, delete: false).Should().BeNull();
    }

    [Fact]
    public void DeletePropertyRemovesAndRecords()
    {
        var x = new FakeXTransport();
        x.SetProperty(7, 8, new XProperty(31, 8, [1]));

        x.DeleteProperty(7, 8);

        x.DeletedProperties.Should().Equal((7, 8));
        x.GetProperty(7, 8, delete: false).Should().BeNull();
    }

    [Fact]
    public void SetAtomsRecordsAndPublishesAFormat32Property()
    {
        var x = new FakeXTransport();

        x.SetAtoms(5, 6, [11, 12]);

        var set = x.AtomsSet.Should().ContainSingle().Subject;
        (set.Window, set.Property).Should().Be((5, 6));
        set.Atoms.Should().Equal(11, 12);
        var property = x.GetProperty(5, 6, delete: false)!;
        property.Longs().Should().Equal(11, 12);
        property.Type.Should().Be(4, "XA_ATOM is predefined, so the real call interns nothing");
        x.InternCalls.Should().BeEmpty();
    }

    [Fact]
    public void CallsKeepsFromRootAndFlushInOrderWithMessages()
    {
        var x = new FakeXTransport();

        x.FromRoot(9, 1, 2);
        x.SendClientMessage(9, 20, 1, 2, 3, 4, 5);
        x.Flush();

        x.Calls.Should().HaveCount(3);
        x.Calls[0].Should().Be(new FromRootCall(9, 1, 2));
        x.Calls[1].Should().BeOfType<SentClientMessage>();
        x.Calls[2].Should().Be(new FlushCall());
    }

    [Fact]
    public void CallsRecordsDispose()
    {
        var x = new FakeXTransport();

        x.Dispose();

        x.Calls.Should().Equal(new DisposeCall());
    }

    [Fact]
    public void FromRootUsesTheScriptedMapping()
    {
        var x = new FakeXTransport { RootToWindow = (_, rx, ry) => new PixelPoint(rx - 100, ry - 50) };

        x.FromRoot(9, 130, 70).Should().Be(new PixelPoint(30, 20));
        x.FromRootCalls.Should().Equal((9, 130, 70));
    }

    [Fact]
    public void FromRootDefaultsToIdentity()
    {
        new FakeXTransport().FromRoot(9, 3, 4).Should().Be(new PixelPoint(3, 4));
    }

    [Fact]
    public void FromRootIsNullForAGoneWindow()
    {
        var x = new FakeXTransport();
        x.GoneWindows.Add(9);

        x.FromRoot(9, 3, 4).Should().BeNull();
    }

    [Fact]
    public void FlushAndDisposeAreCounted()
    {
        var x = new FakeXTransport();

        x.Flush();
        x.Flush();
        x.Dispose();

        x.Flushes.Should().Be(2);
        x.Disposed.Should().BeTrue();
    }

    [Fact]
    public void CallsKeepTheOrderAcrossRequestKinds()
    {
        var x = new FakeXTransport();

        x.ConvertSelection(1, 2, 3, 4, 5);
        x.SendClientMessage(1, 2, 0, 0, 0, 0, 0);
        x.DeleteProperty(1, 2);

        x.Calls.Select(c => c.GetType()).Should().Equal(typeof(SelectionConversion), typeof(SentClientMessage), typeof(PropertyDeleted));
    }
}
