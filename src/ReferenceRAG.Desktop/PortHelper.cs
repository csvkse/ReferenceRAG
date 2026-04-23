using System.Net;
using System.Net.Sockets;

namespace ReferenceRAG.Desktop;

public static class PortHelper
{
    /// <summary>
    /// 检查指定端口是否空闲（loopback）。
    /// </summary>
    public static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// 向 OS 申请一个空闲 loopback 端口号并返回。
    /// </summary>
    public static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}