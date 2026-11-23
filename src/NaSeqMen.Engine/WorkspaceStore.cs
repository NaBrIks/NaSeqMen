using System.Text.Json;
using NaSeqMen.Core.Graph;

namespace NaSeqMen.Engine;

public sealed class WorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _filePath;

    public WorkspaceStore(string filePath)
    {
        _filePath = filePath;
    }

    public async Task<WorkspaceDocument> LoadOrCreateDefaultAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_filePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
                var doc = JsonSerializer.Deserialize<WorkspaceDocument>(json, JsonOptions);
                if (doc is not null && doc.Flows.Count > 0)
                    return doc;
            }
            catch
            {
                // Fall back to default starter workspace if file is corrupted
            }
        }

        var starter = CreateStarterWorkspace();
        await SaveAsync(starter, cancellationToken);
        return starter;
    }

    public async Task SaveAsync(WorkspaceDocument document, CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(document, JsonOptions);
        var tempPath = _filePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    public static WorkspaceDocument CreateStarterWorkspace()
    {
        static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

        return new WorkspaceDocument
        {
            SchemaVersion = 1,
            Name = "NaSeqMen",
            Flows =
            [
                CreateKeyboardCapsFlow(J),
                CreateInterceptionLoopFlow(J),
                CreateHubSwitchProbeFlow(J),
                CreateObsControlsFlow(J),
            ],
        };
    }

    private static FlowTab CreateKeyboardCapsFlow(Func<object, JsonElement> J) => new()
    {
        Id = "flow-main",
        Name = "Keyboard & CAPS",
        Enabled = true,
        Nodes =
        [
            new NodeInstance
            {
                Id = "n-kbd",
                TypeId = "basic.keyboard-input",
                Title = "Keyboard Input",
                X = 60,
                Y = 140,
                Enabled = true,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["captureGlobal"] = J(true),
                    ["printableOnly"] = J(true),
                    ["onlyKeyDown"] = J(true),
                    ["filterKey"] = J(""),
                    ["testText"] = J("hello naseqmen"),
                },
            },
            new NodeInstance
            {
                Id = "n-caps",
                TypeId = "basic.to-upper",
                Title = "CAPS (ToUpper)",
                X = 400,
                Y = 90,
                Enabled = true,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["trim"] = J(false),
                    ["prefix"] = J(""),
                    ["suffix"] = J(""),
                },
            },
            new NodeInstance
            {
                Id = "n-kbd-out",
                TypeId = "basic.keyboard-output",
                Title = "Keyboard Output",
                X = 740,
                Y = 40,
                Enabled = true,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["active"] = J(false),
                    ["delayMs"] = J(0),
                },
            },
            new NodeInstance
            {
                Id = "n-dbg-upper",
                TypeId = "basic.debug",
                Title = "Debug: CAPS",
                X = 740,
                Y = 230,
                Enabled = true,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["active"] = J(true),
                    ["label"] = J("CAPS"),
                    ["fullBasket"] = J(true),
                },
            },
            new NodeInstance
            {
                Id = "n-dbg-raw",
                TypeId = "basic.debug",
                Title = "Debug: Raw",
                X = 400,
                Y = 340,
                Enabled = true,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["active"] = J(true),
                    ["label"] = J("RAW"),
                    ["fullBasket"] = J(true),
                },
            },
        ],
        Edges =
        [
            new EdgeConnection { Id = "e-1", FromNodeId = "n-kbd", FromPortId = "out", ToNodeId = "n-caps", ToPortId = "in" },
            new EdgeConnection { Id = "e-2", FromNodeId = "n-caps", FromPortId = "out", ToNodeId = "n-dbg-upper", ToPortId = "in" },
            new EdgeConnection { Id = "e-3", FromNodeId = "n-kbd", FromPortId = "out", ToNodeId = "n-dbg-raw", ToPortId = "in" },
            new EdgeConnection { Id = "e-4", FromNodeId = "n-caps", FromPortId = "out", ToNodeId = "n-kbd-out", ToPortId = "in" },
        ],
    };

    private static FlowTab CreateInterceptionLoopFlow(Func<object, JsonElement> J) => new()
    {
        Id = "flow-interception",
        Name = "Interception Loop",
        Enabled = true,
        Nodes =
        [
            new NodeInstance
            {
                Id = "n-ic-dev",
                TypeId = "interception.device",
                Title = "Interception Device",
                X = 80,
                Y = 120,
                Enabled = true,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["active"] = J(false),
                    ["deviceKind"] = J("keyboard"),
                    ["hardwareIdFilter"] = J(""),
                    ["instanceIndex"] = J(1),
                    ["explicitSlot"] = J(0),
                    ["captureMove"] = J(false),
                    ["defaultAction"] = J("pass"),
                    ["decisionTimeoutMs"] = J(60),
                    ["dllPath"] = J(""),
                    ["testKey"] = J("D"),
                },
            },
            new NodeInstance
            {
                Id = "n-ic-chord",
                TypeId = "interception.chord-matcher",
                Title = "Chord Matcher",
                X = 440,
                Y = 70,
                Enabled = true,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["modifierKey"] = J("LWin"),
                    ["triggerKey"] = J(""),
                    ["suppressModifier"] = J(true),
                    ["suppressTrigger"] = J(true),
                    ["sendAction"] = J(""),
                    ["chordWindowMs"] = J(400),
                },
            },
            new NodeInstance
            {
                Id = "n-ic-script",
                TypeId = "script.csharp",
                Title = "C# Decision Script",
                X = 440,
                Y = 340,
                Enabled = true,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["code"] = J("// Example: inspect msg.Payload / props[\"phase\"], call Suppress(true) and SendKeys(\"LCtrl+X\")\nreturn msg;"),
                },
            },
            new NodeInstance
            {
                Id = "n-ic-dbg",
                TypeId = "basic.debug",
                Title = "Debug: Interception",
                X = 800,
                Y = 130,
                Enabled = true,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["active"] = J(true),
                    ["label"] = J("IC"),
                    ["fullBasket"] = J(true),
                },
            },
        ],
        Edges =
        [
            new EdgeConnection { Id = "e-ic-1", FromNodeId = "n-ic-dev", FromPortId = "out", ToNodeId = "n-ic-chord", ToPortId = "in" },
            new EdgeConnection { Id = "e-ic-2", FromNodeId = "n-ic-chord", FromPortId = "matched", ToNodeId = "n-ic-dev", ToPortId = "return" },
            new EdgeConnection { Id = "e-ic-3", FromNodeId = "n-ic-chord", FromPortId = "other", ToNodeId = "n-ic-dev", ToPortId = "return" },
            new EdgeConnection { Id = "e-ic-4", FromNodeId = "n-ic-chord", FromPortId = "matched", ToNodeId = "n-ic-dbg", ToPortId = "in" },
        ],
    };

    private static FlowTab CreateHubSwitchProbeFlow(Func<object, JsonElement> J) => new()
    {
        Id = "flow-hub-switch",
        Name = "Hub, Switch & Probe",
        Enabled = true,
        Nodes =
        [
            new NodeInstance
            {
                Id = "n-hub",
                TypeId = "interception.hub",
                Title = "Interception Hub",
                X = 50,
                Y = 90,
                Width = 250,
                Enabled = true,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["active"] = J(false),
                    ["monitorAll"] = J(false),
                    ["defaultAction"] = J("pass"),
                    ["kb1Filter"] = J(""),
                    ["kb2Filter"] = J(""),
                    ["kb3Filter"] = J(""),
                    ["kb4Filter"] = J(""),
                    ["mouse1Filter"] = J(""),
                    ["mouse2Filter"] = J(""),
                    ["mouse3Filter"] = J(""),
                    ["mouse4Filter"] = J(""),
                    ["captureMove"] = J(false),
                    ["decisionTimeoutMs"] = J(60),
                    ["dllPath"] = J(""),
                },
            },
            new NodeInstance
            {
                Id = "n-hsw-kb",
                TypeId = "interception.hotkey-switch",
                Title = "Hotkey Switch (KB)",
                X = 430,
                Y = 30,
                Width = 265,
                Enabled = true,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["rules"] = J(Array.Empty<object>()),
                    ["replayUnmatchedModifier"] = J(true),
                    ["chordWindowMs"] = J(400),
                    ["targetKeyboardFilter"] = J(""),
                },
            },
            new NodeInstance
            {
                Id = "n-hsw-mouse",
                TypeId = "interception.hotkey-switch",
                Title = "Hotkey Switch (Mouse)",
                X = 430,
                Y = 220,
                Width = 265,
                Enabled = true,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["rules"] = J(Array.Empty<object>()),
                    ["replayUnmatchedModifier"] = J(false),
                    ["chordWindowMs"] = J(400),
                    ["targetKeyboardFilter"] = J(""),
                },
            },
            new NodeInstance
            {
                Id = "n-probe",
                TypeId = "interception.probe",
                Title = "Input Probe (Monitor / Trap)",
                X = 430,
                Y = 410,
                Width = 295,
                Enabled = true,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["blockWhenArmed"] = J(true),
                    ["idleTimeoutSec"] = J(15),
                    ["maxDurationSec"] = J(45),
                    ["maxLines"] = J(8),
                },
            },
            new NodeInstance
            {
                Id = "n-dbg-hsw",
                TypeId = "basic.debug",
                Title = "Debug: Switch Matched",
                X = 830,
                Y = 110,
                Enabled = true,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["active"] = J(true),
                    ["label"] = J("SWITCH"),
                    ["fullBasket"] = J(true),
                },
            },
            new NodeInstance
            {
                Id = "n-dbg-probe",
                TypeId = "basic.debug",
                Title = "Debug: Probe",
                X = 830,
                Y = 430,
                Enabled = true,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["active"] = J(true),
                    ["label"] = J("PROBE"),
                    ["fullBasket"] = J(true),
                },
            },
        ],
        Edges =
        [
            // KB 1 -> Hotkey Switch (KB) -> Return + Debug
            new EdgeConnection { Id = "e-hub-1", FromNodeId = "n-hub", FromPortId = "kb1", ToNodeId = "n-hsw-kb", ToPortId = "in" },
            new EdgeConnection { Id = "e-hub-2", FromNodeId = "n-hsw-kb", FromPortId = "return", ToNodeId = "n-hub", ToPortId = "return" },
            new EdgeConnection { Id = "e-hub-3", FromNodeId = "n-hsw-kb", FromPortId = "matched", ToNodeId = "n-dbg-hsw", ToPortId = "in" },
            // Mouse 1 -> Hotkey Switch (Mouse) -> Return + Debug
            new EdgeConnection { Id = "e-hub-4", FromNodeId = "n-hub", FromPortId = "mouse1", ToNodeId = "n-hsw-mouse", ToPortId = "in" },
            new EdgeConnection { Id = "e-hub-5", FromNodeId = "n-hsw-mouse", FromPortId = "return", ToNodeId = "n-hub", ToPortId = "return" },
            new EdgeConnection { Id = "e-hub-6", FromNodeId = "n-hsw-mouse", FromPortId = "matched", ToNodeId = "n-dbg-hsw", ToPortId = "in" },
            // All Events -> Input Probe (single wire!) -> Return + Debug
            new EdgeConnection { Id = "e-hub-7", FromNodeId = "n-hub", FromPortId = "all", ToNodeId = "n-probe", ToPortId = "in" },
            new EdgeConnection { Id = "e-hub-9", FromNodeId = "n-probe", FromPortId = "return", ToNodeId = "n-hub", ToPortId = "return" },
            new EdgeConnection { Id = "e-hub-10", FromNodeId = "n-probe", FromPortId = "observed", ToNodeId = "n-dbg-probe", ToPortId = "in" },
        ],
    };

    private static FlowTab CreateObsControlsFlow(Func<object, JsonElement> J) => new()
    {
        Id = "flow-obs",
        Name = "OBS & Controls",
        Enabled = true,
        Nodes =
        [
            new NodeInstance
            {
                Id = "n-btn-mute",
                TypeId = "controls.button",
                Title = "Mute Button",
                X = 50,
                Y = 60,
                Enabled = true,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["buttonLabel"] = J("Toggle Mute"),
                    ["mode"] = J("toggle"),
                    ["payload"] = J("toggle"),
                    ["offPayload"] = J("toggle"),
                    ["topic"] = J("obs.mute"),
                },
            },
            new NodeInstance
            {
                Id = "n-sld-vol",
                TypeId = "controls.slider",
                Title = "Volume Fader",
                X = 50,
                Y = 280,
                Enabled = true,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["min"] = J(0),
                    ["max"] = J(100),
                    ["step"] = J(1),
                    ["defaultValue"] = J(75),
                    ["unit"] = J("%"),
                    ["topic"] = J("obs.volume"),
                },
            },
            new NodeInstance
            {
                Id = "n-obs",
                TypeId = "obs.studio",
                Title = "OBS Studio",
                X = 410,
                Y = 70,
                Enabled = true,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["enabled"] = J(false),
                    ["host"] = J("127.0.0.1"),
                    ["port"] = J(4455),
                    ["password"] = J(""),
                    ["audioSource"] = J(""),
                    ["sceneName"] = J(""),
                    ["subscribeMeters"] = J(true),
                    ["meterThrottleMs"] = J(50),
                    ["volumeScale"] = J("percent"),
                },
            },
            new NodeInstance
            {
                Id = "n-vu-obs",
                TypeId = "controls.vu-meter",
                Title = "OBS Audio VU",
                X = 790,
                Y = 60,
                Enabled = true,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["sourceFilter"] = J(""),
                    ["min"] = J(0),
                    ["max"] = J(100),
                    ["unit"] = J("%"),
                },
            },
            new NodeInstance
            {
                Id = "n-dbg-obs",
                TypeId = "basic.debug",
                Title = "Debug: OBS",
                X = 790,
                Y = 280,
                Enabled = true,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["active"] = J(true),
                    ["label"] = J("OBS"),
                    ["fullBasket"] = J(true),
                },
            },
        ],
        Edges =
        [
            new EdgeConnection { Id = "e-obs-1", FromNodeId = "n-btn-mute", FromPortId = "press", ToNodeId = "n-obs", ToPortId = "mute" },
            new EdgeConnection { Id = "e-obs-2", FromNodeId = "n-sld-vol", FromPortId = "value", ToNodeId = "n-obs", ToPortId = "volume" },
            new EdgeConnection { Id = "e-obs-3", FromNodeId = "n-obs", FromPortId = "vuLevel", ToNodeId = "n-vu-obs", ToPortId = "in" },
            new EdgeConnection { Id = "e-obs-4", FromNodeId = "n-obs", FromPortId = "status", ToNodeId = "n-dbg-obs", ToPortId = "in" },
            new EdgeConnection { Id = "e-obs-5", FromNodeId = "n-sld-vol", FromPortId = "value", ToNodeId = "n-vu-obs", ToPortId = "in" },
        ],
    };
}
