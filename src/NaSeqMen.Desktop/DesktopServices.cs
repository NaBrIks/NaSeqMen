// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using NaSeqMen.Editor.Web;

namespace NaSeqMen.Desktop;

public sealed class DesktopServices(string root, int webPort = 17964, int executorPort = 17963) : IAsyncDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
    private WebApplication? _ownedHost;
    public Uri EditorUri { get; } = new($"http://127.0.0.1:{webPort}/");
    public bool OwnsWebHost => _ownedHost is not null;

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        await EnsureExecutorAsync(cancellationToken);
        if (await HasServiceAsync(new Uri(EditorUri, "web-health"), "NaSeqMen.Editor.Web", cancellationToken, $"http://127.0.0.1:{executorPort}")) return;
        var host = WebEditorHost.Build(root, webPort, executorPort);
        try
        {
            await host.StartAsync(cancellationToken);
            _ownedHost = host;
        }
        catch
        {
            await host.DisposeAsync();
            // Another desktop/browser instance may have claimed the loopback port meanwhile.
            if (await HasServiceAsync(new Uri(EditorUri, "web-health"), "NaSeqMen.Editor.Web", cancellationToken, $"http://127.0.0.1:{executorPort}")) return;
            throw;
        }
    }

    private async Task EnsureExecutorAsync(CancellationToken cancellationToken)
    {
        var health = new Uri($"http://127.0.0.1:{executorPort}/api/health");
        if (await HasServiceAsync(health, "NaSeqMen.Executor", cancellationToken)) return;
        var executable = Path.Combine(root, "artifacts", "alpha", "executor", "NaSeqMen.Executor.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException(NativeText.Get("executorMissing"), executable);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("--root"); start.ArgumentList.Add(root);
        start.ArgumentList.Add("--port"); start.ArgumentList.Add(executorPort.ToString());
        using var process = Process.Start(start) ?? throw new IOException(NativeText.Get("executorStartFailed"));
        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await HasServiceAsync(health, "NaSeqMen.Executor", cancellationToken)) return;
            if (process.HasExited) throw new IOException(NativeText.Get("executorExited", process.ExitCode));
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException(NativeText.Get("executorTimeout"));
    }

    private async Task<bool> HasServiceAsync(Uri uri, string expectedName, CancellationToken cancellationToken, string? expectedExecutor = null)
    {
        // A closed local port may time out through host network filters instead of refusing promptly.
        // Only absence of a listener means we may start a service; an unresponsive listener is an error.
        if (!IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == uri.Port && (IPAddress.IsLoopback(endpoint.Address) || endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any))))
            return false;
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK) throw new IOException(NativeText.Get("portHttpError", uri.Port, (int)response.StatusCode));
            await response.Content.LoadIntoBufferAsync(16 * 1024, cancellationToken);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!json.RootElement.TryGetProperty("name", out var name) || name.GetString() != expectedName)
                throw new IOException(NativeText.Get("portUnknownService", uri.Port));
            if (expectedExecutor is not null && (!json.RootElement.TryGetProperty("executor", out var executor) || executor.GetString() != expectedExecutor))
                throw new IOException(NativeText.Get("wrongExecutor"));
            return true;
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }) { return false; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException(NativeText.Get("portNotReady", uri.Port)); }
        catch (JsonException) { throw new IOException(NativeText.Get("portInvalidResponse", uri.Port)); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownedHost is not null)
        {
            var host = _ownedHost;
            _ownedHost = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try { await host.StopAsync(timeout.Token); }
            catch (OperationCanceledException) { }
            finally { await host.DisposeAsync(); }
        }
        _http.Dispose();
        // Neither a reused web server nor the independent executor belongs to this window.
    }
}
