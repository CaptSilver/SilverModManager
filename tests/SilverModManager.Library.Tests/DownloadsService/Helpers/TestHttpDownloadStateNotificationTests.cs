using System.ComponentModel;
using DynamicData.Kernel;
using FluentAssertions;
using NexusMods.Paths;
using Xunit;

namespace SilverModManager.Library.Tests.DownloadsService.Helpers;

public class TestHttpDownloadStateNotificationTests
{
    [Fact]
    public void Test_ContentLength_RaisesPropertyChanged()
    {
        var state = new TestHttpDownloadState();
        var raised = Watch(state);

        state.ContentLength = Optional<Size>.Create(Size.From(100));

        raised.Should().Equal(nameof(TestHttpDownloadState.ContentLength));
    }

    [Fact]
    public void Test_TotalBytesDownloaded_RaisesPropertyChanged()
    {
        var state = new TestHttpDownloadState();
        var raised = Watch(state);

        state.TotalBytesDownloaded = Size.From(42);

        raised.Should().Equal(nameof(TestHttpDownloadState.TotalBytesDownloaded));
    }

    private static List<string?> Watch(INotifyPropertyChanged source)
    {
        var raised = new List<string?>();
        source.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        return raised;
    }
}
