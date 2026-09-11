using System.Net;
using System.Net.Sockets;

namespace Tf.Core.Tests;

/// <summary>
/// Race-free loopback listener factory for tests. The tempting
/// "TcpListener on port 0 → read the port → stop → bind HttpListener"
/// snippet releases the port before the real listener binds it, so two
/// tests constructing fake servers concurrently can both win their probes
/// and then fight over the bind. Taking a static lock around the bind and
/// retrying with a fresh probe when the bind loses closes that window.
/// </summary>
internal static class TestHttpListenerFactory
{
    private static readonly object BindGate = new();

    public static (HttpListener Listener, int Port) CreateOnFreeLoopbackPort()
    {
        for (var attempt = 0; ; attempt++)
        {
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();

            lock (BindGate)
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    return (listener, port);
                }
                catch (HttpListenerException) when (attempt < 20)
                {
                    // Another process grabbed the port in the gap — probe a new one.
                }
            }

            Thread.Sleep(25);
        }
    }
}
