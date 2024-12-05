using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Trell.IPC.Server;

/// <summary>
/// Taken from: https://learn.microsoft.com/en-us/aspnet/core/grpc/interprocess-uds?view=aspnetcore-7.0
/// </summary>
public class UnixDomainSocketsConnectionFactory {
    readonly EndPoint endPoint;

    public UnixDomainSocketsConnectionFactory(EndPoint endPoint) {
        this.endPoint = endPoint;
    }

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext _,
        CancellationToken cancellationToken = default) {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try {
            await socket.ConnectAsync(this.endPoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, true);
        } catch {
            socket.Dispose();
            throw;
        }
    }
}
