using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using R3;
using SilverModManager.Abstractions.Library;
using SilverModManager.CLI.Types;
using SilverModManager.CLI.Types.IpcHandlers;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.EventBus;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.CLI.Tests;

/// <summary>Each link handler reads its own settings record to say whether it wants its scheme.</summary>
public class ProtocolHandlerSettingsTests
{
    private static NxmIpcProtocolHandler Nxm()
        => new(new ServiceCollection().AddSingleton(Substitute.For<IEventBus>()).BuildServiceProvider(),
            NullLogger<NxmIpcProtocolHandler>.Instance, null!, null!, null!, null!);

    private static Ror2mmIpcProtocolHandler Ror2mm()
        => new(NullLogger<Ror2mmIpcProtocolHandler>.Instance, Substitute.For<IEventBus>(),
            Substitute.For<IThunderstoreInstaller>(), null!);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void NxmWantsRegistrationOnlyFromItsOwnSetting(bool nxm, bool ror2mm)
    {
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<NxmHandlerSettings>(Arg.Any<string?>()).Returns(new NxmHandlerSettings { RegisterAsDefaultHandler = nxm });
        settings.Get<Ror2mmHandlerSettings>(Arg.Any<string?>()).Returns(new Ror2mmHandlerSettings { RegisterAsDefaultHandler = ror2mm });

        Nxm().IsRegistrationWanted(settings).Should().Be(nxm);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Ror2mmWantsRegistrationOnlyFromItsOwnSetting(bool nxm, bool ror2mm)
    {
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<NxmHandlerSettings>(Arg.Any<string?>()).Returns(new NxmHandlerSettings { RegisterAsDefaultHandler = nxm });
        settings.Get<Ror2mmHandlerSettings>(Arg.Any<string?>()).Returns(new Ror2mmHandlerSettings { RegisterAsDefaultHandler = ror2mm });

        Ror2mm().IsRegistrationWanted(settings).Should().Be(ror2mm);
    }

    [Fact]
    public void NxmReportsOnlyTheChangesThatTurnItOn()
    {
        var changes = new Subject<NxmHandlerSettings>();
        var settings = Substitute.For<ISettingsManager>();
        settings.GetChanges<NxmHandlerSettings>(Arg.Any<string?>(), Arg.Any<bool>()).Returns(changes);
        var turnedOn = 0;
        using var _ = Nxm().RegistrationTurnedOn(settings).Subscribe(_ => turnedOn++);

        changes.OnNext(new NxmHandlerSettings { RegisterAsDefaultHandler = false });
        changes.OnNext(new NxmHandlerSettings { RegisterAsDefaultHandler = true });

        turnedOn.Should().Be(1);
    }

    [Fact]
    public void Ror2mmReportsOnlyTheChangesThatTurnItOn()
    {
        var changes = new Subject<Ror2mmHandlerSettings>();
        var settings = Substitute.For<ISettingsManager>();
        settings.GetChanges<Ror2mmHandlerSettings>(Arg.Any<string?>(), Arg.Any<bool>()).Returns(changes);
        var turnedOn = 0;
        using var _ = Ror2mm().RegistrationTurnedOn(settings).Subscribe(_ => turnedOn++);

        changes.OnNext(new Ror2mmHandlerSettings { RegisterAsDefaultHandler = false });
        changes.OnNext(new Ror2mmHandlerSettings { RegisterAsDefaultHandler = true });

        turnedOn.Should().Be(1);
    }

    [Fact]
    public void Ror2mmIsOffByDefaultAndKeepsItsStoredId()
    {
        new Ror2mmHandlerSettings().RegisterAsDefaultHandler.Should().BeFalse();
        Ror2mmHandlerSettings.SettingsId.Should().Be("SilverModManager.CLI.Ror2mmHandlerSettings");
    }

    [Fact]
    public void TheNoticeSettingsStartWithNoReportedHolder()
    {
        new ProtocolNoticeSettings().LastReportedHolder.Should().BeEmpty();
        ProtocolNoticeSettings.SettingsId.Should().Be("SilverModManager.CLI.ProtocolNoticeSettings");
    }
}
