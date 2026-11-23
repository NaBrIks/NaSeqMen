using System.Text.Json;
using NaSeqMen.Core.Graph;
using NaSeqMen.Engine;
using NaSeqMen.Nodes.Basic;
using NaSeqMen.Nodes.Controls;
using NaSeqMen.Nodes.Interception;
using NaSeqMen.Nodes.Obs;
using NaSeqMen.Nodes.Scripting;
using NaSeqMen.Nodes.Windows;

namespace NaSeqMen.Tests;

internal sealed class TestAssertionException(string message) : Exception(message);

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
            throw new TestAssertionException(message);
    }

    public static void False(bool condition, string message)
    {
        if (condition)
            throw new TestAssertionException(message);
    }

    public static void Equal<T>(T expected, T actual, string? context = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            var prefix = string.IsNullOrEmpty(context) ? "" : $"{context}: ";
            throw new TestAssertionException($"{prefix}Expected <{expected}>, got <{actual}>.");
        }
    }

    public static void Close(double expected, double actual, double tolerance = 0.2, string? context = null)
    {
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
        {
            var prefix = string.IsNullOrEmpty(context) ? "" : $"{context}: ";
            throw new TestAssertionException($"{prefix}Expected ~{expected} (±{tolerance}), got {actual}.");
        }
    }

    public static void Contains(string substring, string actual, string? context = null)
    {
        if (actual is null || !actual.Contains(substring, StringComparison.OrdinalIgnoreCase))
        {
            var prefix = string.IsNullOrEmpty(context) ? "" : $"{context}: ";
            throw new TestAssertionException($"{prefix}Expected '{actual}' to contain '{substring}'.");
        }
    }

    public static void NotNull<T>(T? value, string message)
    {
        if (value is null)
            throw new TestAssertionException(message);
    }
}

internal static class TestHarness
{
    public static NodeRegistry CreateFullRegistry()
    {
        var registry = new NodeRegistry();
        registry.RegisterPlugin(new BasicNodesPlugin());
        registry.RegisterPlugin(new WindowsNodesPlugin());
        registry.RegisterPlugin(new InterceptionNodesPlugin());
        registry.RegisterPlugin(new ObsNodesPlugin());
        registry.RegisterPlugin(new ControlsNodesPlugin());
        registry.RegisterPlugin(new ScriptingNodesPlugin());
        return registry;
    }

    public static FlowEngine CreateEngine() => new(CreateFullRegistry());

    public static Dictionary<string, JsonElement> Params(params (string Key, object? Value)[] items)
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in items)
        {
            dict[key] = JsonSerializer.SerializeToElement(value);
        }
        return dict;
    }

    public static NodeInstance Node(string id, string typeId, params (string Key, object? Value)[] parameters)
        => new()
        {
            Id = id,
            TypeId = typeId,
            Title = id,
            Enabled = true,
            Parameters = Params(parameters),
        };

    public static EdgeConnection Edge(string fromNode, string fromPort, string toNode, string toPort)
        => new()
        {
            FromNodeId = fromNode,
            FromPortId = fromPort,
            ToNodeId = toNode,
            ToPortId = toPort,
        };

    public static WorkspaceDocument Workspace(IEnumerable<NodeInstance> nodes, IEnumerable<EdgeConnection> edges)
        => new()
        {
            Name = "Test Workspace",
            Flows =
            [
                new FlowTab
                {
                    Id = "flow-1",
                    Name = "Test Flow",
                    Enabled = true,
                    Nodes = nodes.ToList(),
                    Edges = edges.ToList(),
                },
            ],
        };

    public static JsonElement ToJsonElement(object obj)
        => JsonSerializer.SerializeToElement(obj);

    public static JsonElement? GetWidgetJson(FlowEngine engine, string nodeId)
    {
        var states = engine.GetWidgetStates();
        if (!states.TryGetValue(nodeId, out var raw) || raw is null)
            return null;
        return JsonSerializer.SerializeToElement(raw);
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 4000, string? failMessage = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
                return;
            await Task.Delay(15);
        }

        if (!condition())
            throw new TestAssertionException(failMessage ?? $"Condition not met within {timeoutMs}ms.");
    }
}
