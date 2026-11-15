using Avalonia.Controls.Notifications;
using FluentAssertions;
using SilverModManager.App.UI.Notifications;
using SilverModManager.UI.Sdk;

namespace SilverModManager.UI.Tests.Notifications;

public class ToastVariantTests
{
    [Theory]
    [InlineData(ToastNotificationVariant.Neutral, NotificationType.Information)]
    [InlineData(ToastNotificationVariant.Success, NotificationType.Success)]
    [InlineData(ToastNotificationVariant.Failure, NotificationType.Error)]
    public void EachVariantMapsToItsNotificationType(ToastNotificationVariant variant, NotificationType expected)
    {
        var notification = WindowNotificationService.BuildNotification("Done.", null, variant);

        notification.Type.Should().Be(expected);
    }

    [Fact]
    public void EveryVariantHasAMapping()
    {
        foreach (var variant in Enum.GetValues<ToastNotificationVariant>())
        {
            var act = () => WindowNotificationService.BuildNotification("x", null, variant);
            act.Should().NotThrow();
        }
    }
}
