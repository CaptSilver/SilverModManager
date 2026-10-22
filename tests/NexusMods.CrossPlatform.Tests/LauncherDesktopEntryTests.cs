using FluentAssertions;
using NexusMods.Backend.OS;

namespace NexusMods.CrossPlatform.Tests;

using Xunit;

public class LauncherDesktopEntryTests : IDisposable
{
    private const string AppImage = "/home/someone/AppImages/silvermodmanager.appimage";
    private readonly string _tempDirectory = Directory.CreateTempSubdirectory("launcher-entry").FullName;

    public void Dispose() => Directory.Delete(_tempDirectory, recursive: true);

    private static string Entry(string exec, string mimeType = "x-scheme-handler/nxm;") => $"""
        [Desktop Entry]
        Type=Application
        Name=SilverModManager
        Exec={exec}
        MimeType={mimeType}
        """;

    private static string? Find(string appImage, string scheme, params (string, string)[] entries)
        => LinuxInterop.FindLauncherDesktopEntry(entries, appImage, scheme, path => path);

    [Fact]
    public void AnEnvPrefixedExecLineMatches()
    {
        Find(AppImage, "nxm", ("silvermodmanager.desktop", Entry($"env DESKTOPINTEGRATION=1 {AppImage} %u")))
            .Should().Be("silvermodmanager.desktop");
    }

    [Fact]
    public void AQuotedPathMatches()
    {
        Find("/home/someone/My Apps/smm.appimage", "nxm", ("smm.desktop", Entry("env DESKTOPINTEGRATION=1 \"/home/someone/My Apps/smm.appimage\" %u")))
            .Should().Be("smm.desktop");
    }

    [Theory]
    [InlineData("/home/someone/My Apps/smm.appimage")]
    [InlineData(@"/home/someone/odd\dir/smm.appimage")]
    [InlineData("/home/someone/$HOME/smm.appimage")]
    public void AnExecWrittenWithTheDesktopEntryEscapingMatches(string appImage)
    {
        var exec = $"env DESKTOPINTEGRATION=1 {LinuxInterop.EscapeDesktopExecFilePath(appImage)} %u";
        Find(appImage, "nxm", ("smm.desktop", Entry(exec))).Should().Be("smm.desktop");
    }

    [Fact]
    public void ABareExecWithoutEnvMatches()
    {
        Find(AppImage, "nxm", ("a.desktop", Entry($"{AppImage} %u"))).Should().Be("a.desktop");
    }

    [Fact]
    public void AnEntryForAnotherAppImageIsNotMatched()
    {
        Find(AppImage, "nxm", ("other.desktop", Entry("env DESKTOPINTEGRATION=1 /home/someone/AppImages/other.appimage %u")))
            .Should().BeNull();
    }

    [Fact]
    public void AnEntryWithoutTheSchemeIsNotMatched()
    {
        Find(AppImage, "nxm", ("a.desktop", Entry($"{AppImage} %u", mimeType: "x-scheme-handler/http;")))
            .Should().BeNull();
    }

    [Fact]
    public void ThePrimaryEntryNeedsTheRequestedScheme()
    {
        Find(AppImage, "nxm", ("a.desktop", Entry($"{AppImage} %u", mimeType: "text/plain;x-scheme-handler/nxm;")))
            .Should().Be("a.desktop");
    }

    [Fact]
    public void OurOwnDesktopFileIsNeverChosen()
    {
        var own = $"{NexusMods.Sdk.ApplicationConstants.ApplicationId}.desktop";
        Find(AppImage, "nxm", (own, Entry($"{AppImage} %u"))).Should().BeNull();
    }

    [Fact]
    public void AnExecInAnActionGroupIsIgnored()
    {
        var contents = $"""
            [Desktop Entry]
            Type=Application
            Exec=/usr/bin/true
            MimeType=x-scheme-handler/nxm;

            [Desktop Action new-window]
            Exec={AppImage} --new-window
            """;
        Find(AppImage, "nxm", ("a.desktop", contents)).Should().BeNull();
    }

    [Fact]
    public void ASymlinkedSpellingOfTheSamePathMatches()
    {
        var real = Directory.CreateDirectory(Path.Combine(_tempDirectory, "var", "home")).FullName;
        File.WriteAllText(Path.Combine(real, "smm.appimage"), "");
        var link = Path.Combine(_tempDirectory, "home");
        Directory.CreateSymbolicLink(link, real);

        var viaLink = Path.Combine(link, "smm.appimage");
        var viaReal = Path.Combine(real, "smm.appimage");

        LinuxInterop.FindLauncherDesktopEntry([("a.desktop", Entry($"env DESKTOPINTEGRATION=1 {viaLink} %u"))], viaReal, "nxm", LinuxInterop.ResolveRealPath)
            .Should().Be("a.desktop");
    }

    [Fact]
    public void ResolveRealPathLeavesAMissingFileAlone()
    {
        var missing = Path.Combine(_tempDirectory, "nope", "x.appimage");
        LinuxInterop.ResolveRealPath(missing).Should().Be(missing);
    }
}
