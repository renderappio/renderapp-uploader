using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Web;

namespace Renderapp.Uploader.Publish;

// One browser round trip to a page that sends the browser back to us. RFC 8252 §7.3: an IP
// literal, never "localhost", on whatever port is free. A bare socket rather than HttpListener,
// whose http.sys backend on Windows refuses an IP-literal prefix to a user without admin rights.
internal sealed class Loopback : IDisposable
{
    private static readonly byte[] Page = Encoding.UTF8.GetBytes(
        "<!doctype html><meta charset=utf-8><title>Renderapp</title>" +
        "<body style=\"font-family:sans-serif;text-align:center;margin-top:20vh\">" +
        "<p>You can close this tab and go back to the Visualiser.</p>");

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly string path;

    public string RedirectUri { get; }

    public Loopback(string path)
    {
        this.path = "/" + path;
        listener.Start();
        RedirectUri = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}{this.path}";
    }

    public async Task<NameValueCollection> WaitAsync(CancellationToken cancellation)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation);
            var target = await ReadTargetAsync(client, cancellation);
            var matches = target != null && target.Split('?', 2)[0].TrimEnd('/') == path;
            // The browser may already have moved on and dropped the connection.
            try
            {
                var head = Encoding.ASCII.GetBytes(matches
                    ? $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {Page.Length}\r\nConnection: close\r\n\r\n"
                    : "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                var stream = client.GetStream();
                await stream.WriteAsync(head, cancellation);
                if (matches) await stream.WriteAsync(Page, cancellation);
            }
            catch (IOException) { }
            if (matches) return HttpUtility.ParseQueryString(target!.Contains('?') ? target.Split('?', 2)[1] : "");
        }
    }

    // The request target from "GET /path?query HTTP/1.1"; the headers don't matter here.
    private static async Task<string?> ReadTargetAsync(TcpClient client, CancellationToken cancellation)
    {
        var buffer = new byte[8192];
        var read = 0;
        try
        {
            while (read < buffer.Length)
            {
                var count = await client.GetStream().ReadAsync(buffer.AsMemory(read), cancellation);
                if (count == 0) break;
                read += count;
                if (Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n")) break;
            }
        }
        catch (IOException)
        {
            return null;
        }
        var line = Encoding.ASCII.GetString(buffer, 0, read).Split("\r\n", 2)[0].Split(' ');
        return line.Length >= 2 && line[0] == "GET" ? line[1] : null;
    }

    public static void OpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "open" : "xdg-open") { ArgumentList = { url } });
        }
        catch (Win32Exception)
        {
            throw new PublishException($"Couldn't open a browser. Open this address yourself: {url}");
        }
    }

    public void Dispose() => listener.Stop();
}
