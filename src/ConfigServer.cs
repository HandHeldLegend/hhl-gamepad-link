// The config app's way to a gamepad open on this PC: a local server, so the app works as it does
// over USB.
//
//   GET  http://127.0.0.1:51702/pads      The gamepads open here: [{id, name, title, mode}]
//   WS   ws://127.0.0.1:51702/pad/<id>    One binary message per WebUSB packet, both ways: what the
//                                         app would send over USB, and what the gamepad answers
//
// Only the HOJA config app's site and local development pages may connect.

using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace HhlGamepadLink;

sealed class ConfigServer : IDisposable
{
    public const int Port = 51702;

    static readonly string[] AllowedOrigins = { "https://handheldlegend.github.io" };

    readonly BluetoothHub _hub;
    readonly HttpListener _listener = new();
    readonly CancellationTokenSource _stop = new();

    public ConfigServer(BluetoothHub hub)
    {
        _hub = hub;
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    static bool OriginAllowed(string? origin)
    {
        if (string.IsNullOrEmpty(origin))
            return false;
        if (AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
               (uri.Host == "localhost" || uri.Host == "127.0.0.1");
    }

    async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext http;
            try { http = await _listener.GetContextAsync(); }
            catch { return; } // Stopped

            _ = Task.Run(() => Handle(http));
        }
    }

    async Task Handle(HttpListenerContext http)
    {
        var request = http.Request;
        var response = http.Response;
        string? origin = request.Headers["Origin"];

        if (!OriginAllowed(origin))
        {
            response.StatusCode = 403;
            response.Close();
            return;
        }
        response.AddHeader("Access-Control-Allow-Origin", origin!);
        // Chrome asks before a public site may reach this PC's own addresses
        response.AddHeader("Access-Control-Allow-Private-Network", "true");

        string path = request.Url?.AbsolutePath ?? "";
        if (request.HttpMethod == "OPTIONS")
        {
            response.AddHeader("Access-Control-Allow-Methods", "GET");
            response.StatusCode = 204;
            response.Close();
        }
        else if (path == "/pads" && request.HttpMethod == "GET")
        {
            byte[] body = Encoding.UTF8.GetBytes(PadsJson());
            response.ContentType = "application/json";
            response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body);
            response.Close();
        }
        else if (path.StartsWith("/pad/") && request.IsWebSocketRequest)
        {
            await Tunnel(http, path["/pad/".Length..]);
        }
        else
        {
            response.StatusCode = 404;
            response.Close();
        }
    }

    string PadsJson()
    {
        var open = _hub.Snapshot().Where(e => e.Session is { State: SessionState.Open });
        return "[" + string.Join(",", open.Select(e =>
            $"{{\"id\":\"{e.Pad.Id}\",\"name\":\"{Escape(e.Pad.Name)}\",\"title\":\"{Escape(e.Pad.Title)}\"," +
            $"\"mode\":\"{Protocol.FormatName(e.Pad.Format)}\"}}")) + "]";
    }

    static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // One app connection to one gamepad, for as long as both stay
    async Task Tunnel(HttpListenerContext http, string id)
    {
        var session = _hub.Snapshot().FirstOrDefault(e => e.Pad.Id == id)?.Session;
        if (session is not { State: SessionState.Open })
        {
            http.Response.StatusCode = 409; // Not open on this PC
            http.Response.Close();
            return;
        }

        var ws = (await http.AcceptWebSocketAsync(subProtocol: null)).WebSocket;
        var toApp = Channel.CreateBounded<byte[]>(256);
        void Forward(byte[] packet) => toApp.Writer.TryWrite(packet);
        session.ConfigFromGamepad += Forward;

        var sender = Task.Run(async () =>
        {
            await foreach (var packet in toApp.Reader.ReadAllAsync(_stop.Token))
                await ws.SendAsync(packet, WebSocketMessageType.Binary, endOfMessage: true, _stop.Token);
        });

        try
        {
            var buffer = new byte[64];
            while (ws.State == WebSocketState.Open && session.State != SessionState.Closed)
            {
                var got = await ws.ReceiveAsync(buffer, _stop.Token);
                if (got.MessageType == WebSocketMessageType.Close)
                    break;
                if (got.MessageType == WebSocketMessageType.Binary && got.Count > 0)
                    session.SendConfig(buffer.AsSpan(0, got.Count).ToArray());
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        finally
        {
            session.ConfigFromGamepad -= Forward;
            toApp.Writer.TryComplete();
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
            ws.Dispose();
        }
        await sender.ContinueWith(_ => { });
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
    }
}
