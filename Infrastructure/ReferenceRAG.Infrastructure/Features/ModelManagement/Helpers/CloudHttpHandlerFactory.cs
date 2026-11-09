using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ReferenceRAG.Infrastructure.Features.ModelManagement.Helpers;

/// <summary>
/// 云端与远程 HTTP 传输管道工厂
/// 针对 Windows 下 Cloudflare/公网双栈解析优先选用 IPv4，杜绝无路由 IPv6 握手超时挂起。
/// </summary>
public static class CloudHttpHandlerFactory
{
    public static SocketsHttpHandler CreateHandler(int connectTimeoutSeconds = 8)
    {
        return new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(Math.Max(3, connectTimeoutSeconds)),
            ConnectCallback = async (context, cancellationToken) =>
            {
                IPAddress[] targetAddresses;
                try
                {
                    var ipv4 = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellationToken);
                    targetAddresses = ipv4.Length > 0
                        ? ipv4
                        : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
                }
                catch
                {
                    targetAddresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(targetAddresses, context.DnsEndPoint.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
    }
}
