using FluentAssertions;
using System.Text.Json;
using NexusMods.Sdk.Hashes;

namespace NexusMods.Sdk.Tests.Hashes;

public class Crc32Tests
{
    [Fact]
    public void Test_Json()
    {
        var value = Crc32Value.From(0xB0AB256A);

        var wrapper = JsonSerializer.Deserialize<Wrapper>("""{ "Value": "B0AB256A" }""");
        wrapper.Should().NotBeNull();
        wrapper!.Value.Should().Be(value);

        var json = JsonSerializer.Serialize(wrapper);
        var wrapper2 = JsonSerializer.Deserialize<Wrapper>(json);
        wrapper2.Should().NotBeNull();
        wrapper2!.Value.Should().Be(value);
    }
}

file class Wrapper
{
    public Crc32Value Value { get; set; }
}
