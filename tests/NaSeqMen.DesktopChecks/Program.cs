// SPDX-License-Identifier: MIT
using System.Net.Http;
using System.Text.Json;
using NaSeqMen.Desktop;

var root = Path.GetFullPath(args.FirstOrDefault() ?? Environment.CurrentDirectory);
var editor = new Uri("http://127.0.0.1:17964/");
var checks = 0;
Check(NavigationPolicy.IsEditor("http://127.0.0.1:17964/#devices", editor), "editor route allowed");
foreach (var address in new[] { "https://127.0.0.1:17964/", "http://127.0.0.1:17963/", "http://localhost:17964/", "http://127.0.0.1.example.invalid:17964/", "file:///C:/local.txt", "javascript:alert(1)", "http://name@127.0.0.1:17964/" })
    Check(!NavigationPolicy.IsEditor(address, editor), "non-editor navigation rejected: " + address);
Check(NavigationPolicy.CanOpenExternal("https://example.invalid/help", true, false), "clicked HTTP(S) link may open in external browser");
Check(!NavigationPolicy.CanOpenExternal("https://example.invalid/help", false, false), "scripted external navigation cannot launch browser");
Check(!NavigationPolicy.CanOpenExternal("https://example.invalid/help", true, true), "redirect cannot launch browser");
foreach (var address in new[] { "file:///C:/local.txt", "javascript:alert(1)", "shell:AppsFolder", "mailto:test@example.invalid", "https://user:secret@example.invalid/" })
    Check(!NavigationPolicy.CanOpenExternal(address, true, false), "external unsafe scheme or credentials rejected");

// Only the temporary web port is owned by this test. The real executor is read-only and remains running.
using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
using var executorBefore = JsonDocument.Parse(await http.GetStringAsync("http://127.0.0.1:17963/api/health"));
Check(executorBefore.RootElement.GetProperty("name").GetString() == "NaSeqMen.Executor", "pre-existing executor required; test never starts it");
var owner = new DesktopServices(root, 17966);
await owner.EnsureReadyAsync(CancellationToken.None);
Check(owner.OwnsWebHost, "first desktop service owns its new embedded host");
Check((await http.GetStringAsync("http://127.0.0.1:17966/web-health")).Contains("NaSeqMen.Editor.Web"), "embedded web host reports expected identity");
await using (var guest = new DesktopServices(root, 17966))
{
    await guest.EnsureReadyAsync(CancellationToken.None);
    Check(!guest.OwnsWebHost, "second desktop service reuses host without ownership");
}
Check((await http.GetAsync("http://127.0.0.1:17966/web-health")).IsSuccessStatusCode, "disposing non-owner leaves reused host running");
await owner.DisposeAsync();
var stopped = false;
try { await http.GetAsync("http://127.0.0.1:17966/web-health"); }
catch (HttpRequestException) { stopped = true; }
Check(stopped, "disposing owner stops only its embedded host");
using var executorAfter = JsonDocument.Parse(await http.GetStringAsync("http://127.0.0.1:17963/api/health"));
Check(executorAfter.RootElement.GetProperty("name").GetString() == "NaSeqMen.Executor", "executor still runs after all desktop services close");
Console.WriteLine($"DESKTOP_CHECKS_OK {checks}");

void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
