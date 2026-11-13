using FluentAssertions;

namespace SilverModManager.Wabbajack.Tests.Install;

public class MeminfoShmemTests
{
    [Fact]
    public void ParseReadsShmemInBytes()
    {
        const string text = "MemTotal:       65536000 kB\nShmem:           3522160 kB\nShmemHugePages:        0 kB\n";

        MeminfoShmem.Parse(text).Should().Be(3522160L * 1024);
    }

    [Fact]
    public void ParseReturnsNullWithoutAShmemLineEvenWithShmemHugePages()
    {
        MeminfoShmem.Parse("ShmemHugePages:        0 kB\n").Should().BeNull();
        MeminfoShmem.Parse("").Should().BeNull();
    }
}
