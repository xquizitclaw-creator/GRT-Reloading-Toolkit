using System.Net;
using System.Net.Sockets;
using System.Text;
using GrtPluginKit.Ipc;
using Xunit;

namespace GrtReloadingToolkit.Tests;

/// <summary>
/// GRT hands a plugin either <c>--ipcport</c> (a loopback TCP port, Windows and macOS) or
/// <c>--ipcfile</c> (a Unix socket file, GRT's Linux transport). The client must speak both:
/// each test plays GRT on one transport and answers one request.
/// </summary>
public class GrtClientTransportTests
{
    private const string TabOnTopReply =
        "{\"Result\":{\"status\":\"success\",\"values\":{\"tabhandle\":\"7\",\"caption\":\"Load A\",\"file\":\"a.grtload\"}}}";

    [Fact]
    public async Task ConnectsOverAUnixSocketFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "grt-test-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen();
        try
        {
            Task<string> host = AnswerOnce(listener);
            using var grt = new GrtClient(socketPath: path);
            grt.Connect();

            Assert.True(grt.Connected);
            Assert.Equal(0, grt.Port);
            Assert.Equal(path, grt.IpcFile);
            var (handle, caption, _) = await grt.GetTabOnTopAsync();
            Assert.Equal(7, handle);
            Assert.Equal("Load A", caption);
            Assert.Contains("Get_TabOnTop", await host);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task StillConnectsOverATcpPort()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen();
        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        Task<string> host = AnswerOnce(listener);
        using var grt = new GrtClient(port);
        grt.Connect();

        Assert.Equal(port, grt.Port);
        Assert.Null(grt.IpcFile);
        var (handle, _, _) = await grt.GetTabOnTopAsync();
        Assert.Equal(7, handle);
        Assert.Contains("Get_TabOnTop", await host);
    }

    /// <summary>Accepts one plugin, reads its request and sends the Get_TabOnTop reply.</summary>
    private static async Task<string> AnswerOnce(Socket listener)
    {
        using Socket s = await listener.AcceptAsync();
        var buf = new byte[4096];
        int n = await s.ReceiveAsync(buf, SocketFlags.None);
        string request = Encoding.UTF8.GetString(buf, 0, n);
        await s.SendAsync(Encoding.UTF8.GetBytes(TabOnTopReply), SocketFlags.None);
        await Task.Delay(200); // let the client read before the socket closes
        return request;
    }
}
