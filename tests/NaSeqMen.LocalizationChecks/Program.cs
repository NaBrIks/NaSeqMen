// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Resources;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.Scripting;
using NaSeqMen.Contracts;
using NaSeqMen.Desktop;
using NaSeqMen.Desktop.Windows;
using NaSeqMen.Input.Windows;
using NaSeqMen.Obs;
using NaSeqMen.Runtime;
using NaSeqMen.Scripting;
using static NaSeqMen.Contracts.MessageCatalog;

var checks = 0;
var en = Catalog["en"];
var ru = Catalog["ru"];
Check(en.Keys.Order().SequenceEqual(ru.Keys.Order()), "catalog languages have identical stable keys");
Check(en.Values.All(text => !Regex.IsMatch(text, "[А-Яа-яЁё]")), "canonical English catalog contains no Russian display text");
Check(en.All(pair => Tokens(pair.Value).SequenceEqual(Tokens(ru[pair.Key]))), "both languages use identical named placeholders");
var userName = "Мой микрофон {detail}";
var nested = M("runtime.connectionUnavailable", ("connection", userName), ("detail", M("obs.passwordRequired")));
Check(nested.Message.Contains(userName) && nested.Message.Contains("OBS requires a password"), "English fallback preserves user data and formats nested cause");
Check(Format(nested.Code, nested.Args, "ru").Contains(userName) && Format(nested.Code, nested.Args, "ru").Contains("OBS требует пароль"), "Russian nested cause uses stable codes without retranslating user data");
Check(Format("obs.passwordRequired", language: "unknown") == en["obs.passwordRequired"], "unknown locale falls back to English");
var error = Error<IOException>(M("obs.passwordRequired"));
Check(FromException(error).Code == "obs.passwordRequired", "typed exceptions preserve structured message metadata");
Check(FromException(new IOException(userName)).Args["detail"] as string == userName, "unknown external error text is preserved as data");

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var diagnostic = new Diagnostic(DateTimeOffset.UtcNow, "signal", "node", "error", nested);
var diagnosticJson = JsonSerializer.Serialize(diagnostic, jsonOptions);
var roundTrip = JsonSerializer.Deserialize<Diagnostic>(diagnosticJson, jsonOptions)!;
Check(roundTrip.MessageCode == nested.Code && roundTrip.Message == nested.Message && roundTrip.MessageArgs?.ContainsKey("detail") == true, "diagnostic JSON round-trip retains fallback, code and arguments");
var action = new ActionResult(false, M("runtime.suppressed"), Suppressed: true);
var actionRoundTrip = JsonSerializer.Deserialize<ActionResult>(JsonSerializer.Serialize(action, jsonOptions), jsonOptions)!;
Check(actionRoundTrip.Suppressed && actionRoundTrip.MessageCode == "runtime.suppressed", "action JSON retains structured dry-run result");
var modules = new INaModule[] { new InputModule(), new DesktopModule(), new ObsModule() };
foreach (var module in modules)
{
    var descriptor = module.Descriptor;
    Check(descriptor.NameKey is not null && en[descriptor.NameKey] == descriptor.Name && descriptor.Actions.Concat(descriptor.Conditions).All(operation => operation.NameKey is not null && en[operation.NameKey] == operation.Name), descriptor.Id + " exposes keyed English descriptor names");
    var decoded = JsonSerializer.Deserialize<ModuleDescriptor>(JsonSerializer.Serialize(descriptor, jsonOptions), jsonOptions)!;
    Check(decoded.NameKey == descriptor.NameKey && decoded.Actions.Length == descriptor.Actions.Length, descriptor.Id + " descriptor JSON remains readable");
}
var device = new DeviceDescriptor("test", M("device.mouse"), "mouse", "TEST", false);
Check(JsonSerializer.Deserialize<DeviceDescriptor>(JsonSerializer.Serialize(device, jsonOptions), jsonOptions)?.NameKey == "device.mouse", "device name key round-trips independently of hardware identity");

var path = Path.Combine(AppContext.BaseDirectory, "localization-config.json");
await using var runtime = new GraphRuntime([], path);
var config = new Configuration
{
    Nodes = [new() { Id = "source", Name = userName, Kind = "source" }, new() { Id = "condition", Name = "Condition", Kind = "condition", Operation = "equals", Settings = new() { ["field"] = "phase", ["value"] = "down" } }],
    Edges = [new() { From = "source", To = "condition" }]
};
await runtime.StartAsync(config);
using var trace = runtime.Diagnostics.Subscribe();
await runtime.ProcessAsync(new Signal { Phase = "down" });
var messages = new List<Diagnostic>();
while (trace.Reader.TryRead(out var item)) messages.Add(item);
Check(messages.Any(item => item.Stage == "passed" && item.MessageCode == "runtime.conditionPassed" && item.Message == en["runtime.conditionPassed"]), "runtime emits keyed condition result with English fallback");
Check(messages.Any(item => item.Stage == "received" && item.Message == userName && item.MessageCode is null), "user node names are not converted to translation keys");
var invalid = runtime.GetConfiguration();
invalid.Nodes[0].InputType = "invalid";
var validation = runtime.Validate(invalid);
Check(!validation.Valid && validation.Issues?.Any(issue => issue.Code == "runtime.portType" && issue.Args["node"] as string == userName) == true, "validation identifies stable issue code and preserves node name");
Check(validation.Errors.SequenceEqual(validation.Issues!.Select(issue => issue.Message)), "legacy validation errors remain English fallback of structured issues");

var nativeResources = new ResourceManager("NaSeqMen.Desktop.DesktopText", typeof(DesktopServices).Assembly);
Check(nativeResources.GetString("retry", CultureInfo.GetCultureInfo("en")) == "Retry" && nativeResources.GetString("retry", CultureInfo.GetCultureInfo("ru")) == "Повторить", "native startup has English and Russian resources");
var originalCulture = CultureInfo.CurrentUICulture;
try
{
    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
    try { new ScriptCompiler().Compile("this is not valid C#;"); throw new Exception("Invalid code unexpectedly compiled"); }
    catch (CompilationErrorException ex) { Check(ex.Message.Contains("CS") && !Regex.IsMatch(ex.Message, "[А-Яа-яЁё]"), "Roslyn fallback is English independently of OS UI culture"); }
}
finally { CultureInfo.CurrentUICulture = originalCulture; }
Console.WriteLine($"LOCALIZATION_CHECKS_OK {checks}; catalog keys={en.Count}");

static string[] Tokens(string text) => Regex.Matches(text, @"\{([A-Za-z][A-Za-z0-9_]*)\}").Select(match => match.Groups[1].Value).Distinct().Order().ToArray();
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
