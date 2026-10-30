using System.Runtime.InteropServices;
using FluentAssertions;
using SilverModManager.Deployment.Fuse.Kernel;

namespace SilverModManager.Deployment.Fuse.Tests.Kernel;

public class FuseRequestPlumbingTests
{
    [Fact]
    public void UserdataThatIsNotAMountIsNamedInTheFailure()
    {
        var handle = GCHandle.Alloc("not a mount");
        try
        {
            var act = () => LibFuseLowLevelMount.MountFromUserdata(GCHandle.ToIntPtr(handle));
            act.Should().Throw<InvalidOperationException>().WithMessage("*FUSE request userdata is not a mount*");
        }
        finally { handle.Free(); }
    }

    [Fact]
    public void ANullNamePointerIsNamedInTheFailure()
    {
        var act = () => LibFuseLowLevelMount.NameOf(IntPtr.Zero);
        act.Should().Throw<InvalidOperationException>().WithMessage("*null name pointer*");
    }

    [Fact]
    public void ANamePointerIsDecodedAsUtf8()
    {
        var pointer = Marshal.StringToCoTaskMemUTF8("Data/é.txt");
        try { LibFuseLowLevelMount.NameOf(pointer).Should().Be("Data/é.txt"); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
}
