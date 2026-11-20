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
                new FlowTab
                {
                    Id = "flow-main",
                    Name = "Flow 1",
                    Enabled = true,
                    Nodes =
                    [
                        new NodeInstance
                        {
                            Id = "n-kbd",
                            TypeId = "basic.keyboard-input",
                            Title = "Keyboard Input",
                            X = 80,
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
                            X = 420,
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
                            X = 760,
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
                            X = 760,
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
                            X = 420,
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
                        new EdgeConnection
                        {
                            Id = "e-1",
                            FromNodeId = "n-kbd",
                            FromPortId = "out",
                            ToNodeId = "n-caps",
                            ToPortId = "in",
                        },
                        new EdgeConnection
                        {
                            Id = "e-2",
                            FromNodeId = "n-caps",
                            FromPortId = "out",
                            ToNodeId = "n-dbg-upper",
                            ToPortId = "in",
                        },
                        new EdgeConnection
                        {
                            Id = "e-3",
                            FromNodeId = "n-kbd",
                            FromPortId = "out",
                            ToNodeId = "n-dbg-raw",
                            ToPortId = "in",
                        },
                        new EdgeConnection
                        {
                            Id = "e-4",
                            FromNodeId = "n-caps",
                            FromPortId = "out",
                            ToNodeId = "n-kbd-out",
                            ToPortId = "in",
                        },
                    ],
                },
            ],
        };
    }
}
