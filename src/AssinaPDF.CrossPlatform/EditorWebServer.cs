using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AssinaPDF.MacPrototype;

internal sealed class EditorWebServer : IAsyncDisposable
{
    private readonly string _root;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private Task? _acceptTask;

    public EditorWebServer(string root)
    {
        _root = Path.GetFullPath(root);
        _listener = new TcpListener(IPAddress.Loopback, 0);
    }

    public Uri BaseUri { get; private set; } = new("http://127.0.0.1/");

    public void Start()
    {
        if (!File.Exists(Path.Combine(_root, "index.html")))
            throw new FileNotFoundException("O editor web ainda não foi compilado. Execute npm run build na pasta EditorWeb.", Path.Combine(_root, "index.html"));

        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        BaseUri = new Uri($"http://127.0.0.1:{endpoint.Port}/");
        _acceptTask = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }

            _ = Task.Run(() => HandleClientAsync(client, _stop.Token));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(requestLine)) return;

                var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || (parts[0] != "GET" && parts[0] != "HEAD"))
                {
                    await WriteResponseAsync(stream, 405, "Method Not Allowed", "text/plain", Encoding.UTF8.GetBytes("Method not allowed"), cancellationToken);
                    return;
                }

                string line;
                do { line = await reader.ReadLineAsync(cancellationToken) ?? ""; } while (line.Length > 0);

                var rawPath = Uri.UnescapeDataString(parts[1].Split('?', 2)[0]).TrimStart('/');
                if (string.IsNullOrWhiteSpace(rawPath)) rawPath = "index.html";
                var candidate = Path.GetFullPath(Path.Combine(_root, rawPath.Replace('/', Path.DirectorySeparatorChar)));
                var rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
                if (!candidate.StartsWith(rootPrefix, StringComparison.Ordinal) && !string.Equals(candidate, Path.Combine(_root, "index.html"), StringComparison.Ordinal))
                {
                    await WriteResponseAsync(stream, 403, "Forbidden", "text/plain", Encoding.UTF8.GetBytes("Forbidden"), cancellationToken);
                    return;
                }

                if (!File.Exists(candidate))
                {
                    await WriteResponseAsync(stream, 404, "Not Found", "text/plain", Encoding.UTF8.GetBytes("Not found"), cancellationToken);
                    return;
                }

                var body = await File.ReadAllBytesAsync(candidate, cancellationToken);
                var type = Path.GetExtension(candidate).ToLowerInvariant() switch
                {
                    ".html" => "text/html; charset=utf-8",
                    ".js" or ".mjs" => "text/javascript; charset=utf-8",
                    ".css" => "text/css; charset=utf-8",
                    ".json" => "application/json",
                    ".svg" => "image/svg+xml",
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".woff2" => "font/woff2",
                    ".wasm" => "application/wasm",
                    _ => "application/octet-stream"
                };
                await WriteResponseAsync(stream, 200, "OK", type, parts[0] == "HEAD" ? Array.Empty<byte>() : body, cancellationToken, body.Length);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
        }
    }

    private static async Task WriteResponseAsync(Stream stream, int status, string reason, string contentType, byte[] body, CancellationToken token, int? contentLength = null)
    {
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {contentLength ?? body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token);
        if (body.Length > 0) await stream.WriteAsync(body, token);
        await stream.FlushAsync(token);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        if (_acceptTask is not null)
        {
            try { await _acceptTask; } catch (OperationCanceledException) { }
        }
        _stop.Dispose();
    }
}
