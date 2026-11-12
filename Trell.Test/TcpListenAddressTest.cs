using System.Net;

namespace Trell.Test;

public class TcpListenAddressTest {
    [Theory]
    [InlineData("127.0.0.1:3000", "127.0.0.1", 3000)]
    [InlineData("0.0.0.0:3000", "0.0.0.0", 3000)]
    [InlineData("[::1]:8080", "::1", 8080)]
    [InlineData("[::]:1", "::", 1)]
    public void ParsesIpAddresses(string text, string address, int port) {
        Assert.True(TcpListenAddress.TryParse(text, out var parsed));
        Assert.Equal(IPAddress.Parse(address), parsed.Address);
        Assert.Equal(port, parsed.Port);
        Assert.Equal(text, parsed.ToString());
    }

    [Theory]
    [InlineData("localhost:3000", "localhost")]
    [InlineData("LocalHost:3000", "localhost")]
    [InlineData("*:3000", "*")]
    public void ParsesNamedHosts(string text, string host) {
        Assert.True(TcpListenAddress.TryParse(text, out var parsed));
        Assert.Equal(host, parsed.Host);
        Assert.Null(parsed.Address);
        Assert.Equal(3000, parsed.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("3000")]
    [InlineData(":3000")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:")]
    [InlineData("127.0.0.1:0")]
    [InlineData("127.0.0.1:65536")]
    [InlineData("127.0.0.1:+80")]
    [InlineData("127.1:3000")]
    [InlineData("::1:3000")]
    [InlineData("[127.0.0.1]:3000")]
    [InlineData("example.com:3000")]
    [InlineData(" 127.0.0.1:3000")]
    public void RejectsOtherText(string text) {
        Assert.False(TcpListenAddress.TryParse(text, out _));
        var ex = Assert.Throws<FormatException>(() => TcpListenAddress.Parse(text));
        Assert.Contains("listen", ex.Message);
    }
}
