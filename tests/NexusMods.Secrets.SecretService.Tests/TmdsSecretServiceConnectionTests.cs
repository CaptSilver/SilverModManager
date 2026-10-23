using FluentAssertions;
using Tmds.DBus.Protocol;
using Xunit;

namespace NexusMods.Secrets.SecretService.Tests;

public class TmdsSecretServiceConnectionTests
{
    [Theory]
    [InlineData("org.freedesktop.DBus.Error.ServiceUnknown", SecretServiceFailure.ServiceUnknown)]
    [InlineData("org.freedesktop.DBus.Error.NoReply", SecretServiceFailure.NoReply)]
    [InlineData("org.freedesktop.DBus.Error.Disconnected", SecretServiceFailure.Disconnected)]
    [InlineData("org.freedesktop.Secret.Error.IsLocked", SecretServiceFailure.Locked)]
    [InlineData("org.freedesktop.Secret.Error.NoSuchObject", SecretServiceFailure.Other)]
    [InlineData("org.freedesktop.DBus.Error.AccessDenied", SecretServiceFailure.Other)]
    public void Bus_error_names_map_onto_failure_kinds(string errorName, SecretServiceFailure expected)
    {
        TmdsSecretServiceConnection.Translate(new DBusException(errorName, "message")).Failure.Should().Be(expected);
    }

    [Fact]
    public void A_closed_connection_is_Disconnected()
    {
        TmdsSecretServiceConnection.Translate(new ObjectDisposedException("Connection")).Failure.Should().Be(SecretServiceFailure.Disconnected);
    }
}
