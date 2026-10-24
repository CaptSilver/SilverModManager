using FluentAssertions;
using LinuxDesktopUtils.XDGDesktopPortal;
using SilverModManager.Backend.OS;

namespace SilverModManager.Backend.Tests.OS;

/// <summary>
/// The wrapper serialises the D-Bus connect. The connect is a fake that fails, so no D-Bus connection is made.
/// </summary>
public class DesktopPortalConnectionManagerWrapperTests
{
    [Fact]
    public async Task FailedConnect_DoesNotBlockTheNextAttempt()
    {
        var attempts = 0;
        var wrapper = new DesktopPortalConnectionManagerWrapper(
            connect: () => { attempts++; throw new InvalidOperationException("no session bus"); },
            connectTimeout: TimeSpan.FromSeconds(30));

        await wrapper.Invoking(w => w.GetInstance().AsTask()).Should().ThrowAsync<InvalidOperationException>();

        // Had the first attempt kept the semaphore, this one would wait out the timeout.
        var second = wrapper.GetInstance().AsTask();
        (await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(second);
        await second.Invoking(t => t).Should().ThrowAsync<InvalidOperationException>();
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task WaiterThatTimesOut_ThrowsAndLeavesTheSemaphoreToItsHolder()
    {
        var connecting = new TaskCompletionSource<DesktopPortalConnectionManager>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrapper = new DesktopPortalConnectionManagerWrapper(
            connect: () => connecting.Task,
            connectTimeout: TimeSpan.FromMilliseconds(50));

        var holder = wrapper.GetInstance().AsTask();

        // Without the timeout check the waiter goes on to await the holder's connect, which never finishes here.
        await wrapper.Invoking(w => w.GetInstance().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).Should().ThrowAsync<TimeoutException>()
            .WithMessage("*portal*");

        // A waiter that released a semaphore it never got would make the holder's own release overflow it.
        connecting.SetException(new InvalidOperationException("no session bus"));
        await holder.Invoking(t => t.WaitAsync(TimeSpan.FromSeconds(10))).Should().ThrowAsync<InvalidOperationException>();
    }
}
