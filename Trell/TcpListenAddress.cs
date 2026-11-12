using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Trell;

/// <summary>
/// The TCP address from the <c>listen</c> setting in Trell.toml, written as "host:port".
/// Host is "*" for every address, "localhost" for both loopback addresses, or an IP address,
/// with IPv6 addresses in brackets. <see cref="Address"/> is null for "*" and "localhost".
/// </summary>
record TcpListenAddress(string Host, IPAddress? Address, int Port) {
    internal static TcpListenAddress Parse(string text) => TryParse(text, out var address)
        ? address
        : throw new FormatException(
            $"listen = \"{text}\" in Trell.toml is not a TCP address. Write it as \"host:port\", "
                + "where host is an IP address, \"localhost\", or \"*\". "
                + "For example: listen = \"127.0.0.1:3000\"."
        );

    internal static bool TryParse(string text, [NotNullWhen(true)] out TcpListenAddress? result) {
        result = null;
        var colon = text.LastIndexOf(':');
        if (colon <= 0
            || !int.TryParse(
                text[(colon + 1)..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var port
            )
            || port is < 1 or > 65535
        ) {
            return false;
        }

        var host = text[..colon];
        if (host == "*" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) {
            result = new(host.ToLowerInvariant(), null, port);
            return true;
        }

        // IPv6 addresses contain colons, so they must be bracketed to separate the port.
        // IPv4 addresses must have four parts; IPAddress.TryParse also accepts "127.1".
        var bracketed = host.StartsWith('[') && host.EndsWith(']');
        var ip = bracketed ? host[1..^1] : host;
        if (!IPAddress.TryParse(ip, out var address)) {
            return false;
        }
        var valid = address.AddressFamily switch {
            AddressFamily.InterNetworkV6 => bracketed,
            AddressFamily.InterNetwork => !bracketed && ip.Count(c => c == '.') == 3,
            _ => false,
        };
        if (valid) {
            result = new(ip, address, port);
        }
        return valid;
    }

    internal void Listen(KestrelServerOptions options, Action<ListenOptions> configure) {
        if (Address is not null) {
            options.Listen(Address, Port, configure);
        } else if (Host == "localhost") {
            options.ListenLocalhost(Port, configure);
        } else {
            options.ListenAnyIP(Port, configure);
        }
    }

    public override string ToString() => Address?.AddressFamily == AddressFamily.InterNetworkV6
        ? $"[{Host}]:{Port}"
        : $"{Host}:{Port}";
}
