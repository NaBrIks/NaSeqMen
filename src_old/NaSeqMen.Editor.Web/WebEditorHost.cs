// SPDX-License-Identifier: MIT
using System.Net;
using NaSeqMen.Contracts;
using static NaSeqMen.Contracts.MessageCatalog;
using System.Net.WebSockets;

namespace NaSeqMen.Editor.Web;

public static class WebEditorHost
{
    public static WebApplication Build(string root, int port = 17964, int executorPort = 17963)
    {
        root = Path.GetFullPath(root);
        if (port is < 1024 or > 65535 || executorPort is < 1024 or > 65535)
            throw Error<ArgumentException>(M("common.invalidPort"));
        if (port == executorPort) throw Error<ArgumentException>(M("web.samePort"));
        var tokenPath = Path.Combine(root, "local", "api-token.txt");
        var initialToken = File.ReadAllText(tokenPath).Trim();
        if (initialToken.Length < 32 || initialToken.Any(c => !Uri.IsHexDigit(c))) throw Error<IOException>(M("common.invalidToken"));
        var publishedWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var webRoot = Directory.Exists(publishedWebRoot) ? publishedWebRoot : Path.Combine(root, "src", "NaSeqMen.Editor.Web", "wwwroot");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = root, WebRootPath = webRoot });
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.Limits.MaxRequestBodySize = 2 * 1024 * 1024;
        });
        builder.Services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{executorPort}"), Timeout = TimeSpan.FromSeconds(30)
        });
        var app = builder.Build();
        var http = app.Services.GetRequiredService<HttpClient>();
        var origin = $"http://127.0.0.1:{port}";
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            // Do not trust arbitrary Host or forwarded headers even though the listener is loopback-only.
            if (request.Host.Host != "127.0.0.1" || request.Host.Port != port || request.Scheme != "http")
            { context.Response.StatusCode = 403; return; }
            var origins = request.Headers.Origin;
            if (origins.Count > 0 && (origins.Count != 1 || origins[0] != origin))
            { context.Response.StatusCode = 403; return; }
            var site = request.Headers["Sec-Fetch-Site"].ToString();
            // An external link may open the editor document. It cannot read/call its protected API.
            var entryNavigation = HttpMethods.IsGet(request.Method)
                && request.Path.Value is "/" or "/index.html"
                && request.Headers["Sec-Fetch-Mode"] == "navigate"
                && request.Headers["Sec-Fetch-Dest"] == "document";
            if (site.Length > 0 && site is not ("same-origin" or "none") && !entryNavigation)
            { context.Response.StatusCode = 403; return; }
            if ((!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)) || context.WebSockets.IsWebSocketRequest)
            {
                if (origins.Count != 1 || origins[0] != origin) { context.Response.StatusCode = 403; return; }
            }
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = $"default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self' ws://127.0.0.1:{port}; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            context.Response.Headers.CacheControl = "no-store";
            await next(context);
        });
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapGet("/web-health", () => new { name = "NaSeqMen.Editor.Web", version = "0.1.0-alpha", executor = $"http://127.0.0.1:{executorPort}" });
        var routes = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["/api/localization"] = ["GET"], ["/api/health"] = ["GET"], ["/api/config"] = ["GET", "PUT"],
            ["/api/config/validate"] = ["POST"], ["/api/modules"] = ["GET"], ["/api/devices/capture"] = ["POST"],
            ["/api/devices"] = ["GET"], ["/api/inject"] = ["POST"],
            ["/api/workspace"] = ["GET", "PUT"], ["/api/workspace/catalog"] = ["GET"], ["/api/workspace/adapters"] = ["GET"],
            ["/api/workspace/trace"] = ["GET", "DELETE"], ["/api/workspace/watch"] = ["POST"],
            ["/api/workspace/activity"] = ["GET"],
            ["/api/workspace/mode"] = ["POST"], ["/api/workspace/play"] = ["POST"], ["/api/workspace/state"] = ["GET"],
            ["/api/workspace/inject"] = ["POST"], ["/api/workspace/validate"] = ["POST"]
        };
        foreach (var route in routes)
            app.MapMethods(route.Key, route.Value, ProxyHttp);
        app.MapGet("/api/diagnostics", ProxyWebSocket);
        return app;

        async Task ProxyHttp(HttpContext context)
        {
            if (context.Request.QueryString.HasValue) { context.Response.StatusCode = 400; return; }
            var hasBody = context.Request.Method is "POST" or "PUT";
            if (hasBody && !context.Request.HasJsonContentType()) { context.Response.StatusCode = 415; return; }
            try
            {
                // URI comes exclusively from the fixed whitelist, never browser input or a supplied destination.
                using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), context.Request.Path.Value!);
                request.Headers.Add("X-NaSeqMen-Token", await ReadToken(context.RequestAborted));
                if (hasBody)
                {
                    var body = await ReadBounded(context.Request.Body, 2 * 1024 * 1024, context.RequestAborted);
                    request.Content = new ByteArrayContent(body);
                    request.Content.Headers.ContentType = new("application/json");
                }
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                var content = await ReadBounded(await response.Content.ReadAsStreamAsync(context.RequestAborted), 4 * 1024 * 1024, context.RequestAborted, upstreamResponse: true);
                context.Response.StatusCode = (int)response.StatusCode;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.Body.WriteAsync(content, context.RequestAborted);
            }
            catch (BadHttpRequestException) { context.Response.StatusCode = 413; }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                context.Response.StatusCode = 502;
                await context.Response.WriteAsJsonAsync(ApiError(M("web.executorUnavailable")), context.RequestAborted);
            }
        }

        async Task ProxyWebSocket(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest || context.Request.QueryString.HasValue)
            { context.Response.StatusCode = 400; return; }
            // WebSocket middleware is after the common guard, so require Origin again at the upgrade boundary.
            if (context.Request.Headers.Origin.Count != 1 || context.Request.Headers.Origin[0] != origin)
            { context.Response.StatusCode = 403; return; }
            using var upstream = new ClientWebSocket();
            upstream.Options.Proxy = null;
            try
            {
                upstream.Options.SetRequestHeader("X-NaSeqMen-Token", await ReadToken(context.RequestAborted));
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                await upstream.ConnectAsync(new Uri($"ws://127.0.0.1:{executorPort}/api/diagnostics"), connectTimeout.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
            { if (!context.RequestAborted.IsCancellationRequested) context.Response.StatusCode = 502; return; }
            using var browser = await context.WebSockets.AcceptWebSocketAsync();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
            using var browserWrites = new SemaphoreSlim(1);
            using var upstreamWrites = new SemaphoreSlim(1);
            var toBrowser = Relay(upstream, browser, browserWrites, stop.Token);
            var toExecutor = Relay(browser, upstream, upstreamWrites, stop.Token);
            var completed = await Task.WhenAny(toBrowser, toExecutor);
            var close = await completed;
            // Finish the initiating close before cancellation aborts a pending receive on the opposite peer.
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            foreach (var (socket, writes) in new[] { (browser, browserWrites), ((WebSocket)upstream, upstreamWrites) })
            {
                try
                {
                    await writes.WaitAsync(closeTimeout.Token);
                    try
                    {
                        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                            await socket.CloseOutputAsync(close.Status, close.Description, closeTimeout.Token);
                    }
                    finally { writes.Release(); }
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
            }
            stop.Cancel();
            await Task.WhenAll(toBrowser, toExecutor);
        }

        static async Task<(WebSocketCloseStatus Status, string Description)> Relay(WebSocket source, WebSocket destination, SemaphoreSlim writes, CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            var messageBytes = 0;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var message = await source.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    if (message.MessageType == WebSocketMessageType.Close)
                        return (message.CloseStatus is null or WebSocketCloseStatus.Empty ? WebSocketCloseStatus.NormalClosure : message.CloseStatus.Value, message.CloseStatusDescription ?? "");
                    messageBytes += message.Count;
                    if (messageBytes > 2 * 1024 * 1024) return (WebSocketCloseStatus.MessageTooBig, "Message exceeds 2 MiB");
                    await writes.WaitAsync(cancellationToken);
                    try { await destination.SendAsync(new ArraySegment<byte>(buffer, 0, message.Count), message.MessageType, message.EndOfMessage, cancellationToken); }
                    finally { writes.Release(); }
                    if (message.EndOfMessage) messageBytes = 0;
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
            return (WebSocketCloseStatus.EndpointUnavailable, "Connection ended");
        }

        async Task<string> ReadToken(CancellationToken cancellationToken)
        {
            var token = (await File.ReadAllTextAsync(tokenPath, cancellationToken)).Trim();
            if (token.Length < 32 || token.Any(c => !Uri.IsHexDigit(c))) throw Error<IOException>(M("common.invalidToken"));
            return token;
        }
        static async Task<byte[]> ReadBounded(Stream source, int limit, CancellationToken cancellationToken, bool upstreamResponse = false)
        {
            using var result = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int count;
            while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
            {
                if (result.Length + count > limit)
                {
                    if (upstreamResponse) throw Error<IOException>(M("web.responseTooLarge"));
                    throw WithMessage(new BadHttpRequestException(M("web.requestTooLarge").Message, 413), M("web.requestTooLarge"));
                }
                result.Write(buffer, 0, count);
            }
            return result.ToArray();
        }
    }
}
