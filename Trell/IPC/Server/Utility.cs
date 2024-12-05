using Grpc.Net.Client;
using System.Net.Http;
using System.Net.Sockets;

namespace Trell.IPC.Server;

static class Utility {
    /// <summary>
    /// Adapted from https://learn.microsoft.com/en-us/aspnet/core/grpc/interprocess-uds?view=aspnetcore-7.0
    /// </summary>
    /// <param name="settings"></param>
    /// <returns></returns>
    internal static GrpcChannel CreateUnixDomainSocketChannel(string socketPath) {
        var udsEndPoint = new UnixDomainSocketEndPoint(socketPath);
        var connectionFactory = new UnixDomainSocketsConnectionFactory(udsEndPoint);
        var socketsHttpHandler = new SocketsHttpHandler {
            ConnectCallback = connectionFactory.ConnectAsync
        };

        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions {
            HttpHandler = socketsHttpHandler
        });
    }
}
