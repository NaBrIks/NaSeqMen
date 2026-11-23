using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using NaSeqMen.Core.Messages;
using NaSeqMen.Core.Nodes;
using NaSeqMen.Core.Telemetry;

namespace NaSeqMen.Nodes.Scripting;

public sealed class CSharpScriptNode : NodeBase
{
    private const string DefaultCode =
        "// Modify msg, set Suppress(true), SendKeys(\"LCtrl+X\"), or return a new payload\nreturn msg;";

    public static readonly NodeTypeDescriptor TypeDescriptor = new()
    {
        TypeId = "script.csharp",
        DisplayName = "C# Function",
        Category = "Transform",
        Color = "#6366f1",
        Description = "Executes a pre-compiled Roslyn C# script on each incoming message basket.",
        Inputs =
        [
            new PortDefinition
            {
                Id = "in",
                Label = "Input",
                Direction = PortDirection.Input,
                DataType = PortDataType.Any,
                Description = "Incoming message basket.",
            },
        ],
        Outputs =
        [
            new PortDefinition
            {
                Id = "out",
                Label = "Output",
                Direction = PortDirection.Output,
                DataType = PortDataType.Any,
                Description = "Transformed message basket (unless Drop() is called).",
            },
            new PortDefinition
            {
                Id = "error",
                Label = "Error",
                Direction = PortDirection.Output,
                DataType = PortDataType.String,
                Description = "Emits runtime or compilation error details.",
            },
        ],
        Parameters =
        [
            new ParameterDefinition
            {
                Key = "code",
                Label = "C# Script",
                Kind = ParameterKind.Multiline,
                Inline = false,
                DefaultValue = DefaultCode,
                Description = "Available: msg, payload, text, topic, props, state, Suppress(bool), SendKeys(seq), SendMouse(act), Drop(), Log(str)",
            },
        ],
    };

    private static readonly ScriptOptions DefaultScriptOptions = BuildScriptOptions();

    private readonly Dictionary<string, object?> _state = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private ScriptRunner<object?>? _runner;
    private string? _compileError;

    public override NodeTypeDescriptor Descriptor => TypeDescriptor;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _state.Clear();
        _runner = null;
        _compileError = null;

        var code = GetString("code", DefaultCode);

        await Task.Run(() => CompileScript(code, cancellationToken), cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _runner = null;
        _compileError = null;
        _state.Clear();
        return Task.CompletedTask;
    }

    public override async Task OnInputAsync(string inputPortId, FlowMessage message, CancellationToken cancellationToken)
    {
        var runner = _runner;
        if (runner is null)
        {
            if (!string.IsNullOrEmpty(_compileError))
            {
                var errMsg = new FlowMessage
                {
                    Topic = message.Topic ?? "script.compile-error",
                    Payload = _compileError,
                    Properties =
                    {
                        ["errorKind"] = "compile",
                    },
                };
                await EmitAsync("error", errMsg, cancellationToken);
            }
            return;
        }

        var working = message.Clone();
        var globals = new ScriptGlobals(working, _state, Context);

        object? result = null;
        bool dropped = false;
        Exception? runtimeError = null;

        await _executionLock.WaitAsync(cancellationToken);
        try
        {
            result = await runner(globals, cancellationToken);
            dropped = globals.IsDropped;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            runtimeError = ex;
        }
        finally
        {
            _executionLock.Release();
        }

        if (runtimeError is not null)
        {
            Context.ReportStatus("runtime error");
            PublishScriptDebug("runtime error", runtimeError.Message, working);

            var errMsg = new FlowMessage
            {
                Topic = working.Topic ?? "script.error",
                Payload = runtimeError.Message,
                Properties =
                {
                    ["errorKind"] = "runtime",
                    ["exceptionType"] = runtimeError.GetType().Name,
                    ["stackTrace"] = runtimeError.StackTrace,
                },
            };

            await EmitAsync("error", errMsg, cancellationToken);
            return;
        }

        if (dropped)
        {
            Context.ReportStatus("dropped");
            return;
        }

        var outgoing = result switch
        {
            FlowMessage returnedMsg => returnedMsg,
            not null => working.WithPayload(result),
            null => working,
        };

        Context.ReportStatus("ok");
        await EmitAsync("out", outgoing, cancellationToken);
    }

    public override ValueTask DisposeAsync()
    {
        _executionLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private void CompileScript(string code, CancellationToken cancellationToken)
    {
        try
        {
            var script = CSharpScript.Create<object?>(code, DefaultScriptOptions, typeof(ScriptGlobals));
            var diagnostics = script.Compile(cancellationToken);
            var errors = diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();

            if (errors.Count > 0)
            {
                _compileError = string.Join(Environment.NewLine, errors.Select(e => e.ToString()));
                Context.ReportStatus("compile error");
                PublishScriptDebug("compile error", _compileError);
                return;
            }

            _runner = script.CreateDelegate(cancellationToken);
            Context.ReportStatus("ready");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CompilationErrorException ex)
        {
            _compileError = string.Join(Environment.NewLine, ex.Diagnostics.Select(d => d.ToString()));
            Context.ReportStatus("compile error");
            PublishScriptDebug("compile error", _compileError);
        }
        catch (Exception ex)
        {
            _compileError = ex.Message;
            Context.ReportStatus("compile error");
            PublishScriptDebug("compile error", _compileError);
        }
    }

    private void PublishScriptDebug(string label, string detail, FlowMessage? sourceMsg = null)
    {
        var entry = new DebugEntry
        {
            FlowId = Context.FlowId,
            NodeId = Context.NodeId,
            NodeTitle = string.IsNullOrWhiteSpace(Context.Title) ? Descriptor.DisplayName : Context.Title,
            Label = label,
            SourceNodeId = sourceMsg?.SourceNodeId,
            SourcePortId = sourceMsg?.SourcePortId,
            MessageId = sourceMsg?.Id,
            Topic = sourceMsg?.Topic ?? "script",
            Payload = detail,
            FullBasket = false,
        };

        Context.PublishDebug(entry);
    }

    private static ScriptOptions BuildScriptOptions()
    {
        var assemblies = new HashSet<Assembly>
        {
            typeof(object).Assembly,
            typeof(Enumerable).Assembly,
            typeof(JsonDocument).Assembly,
            typeof(Regex).Assembly,
            typeof(FlowMessage).Assembly,
            typeof(ScriptGlobals).Assembly,
        };

        try
        {
            assemblies.Add(Assembly.Load("System.Runtime"));
        }
        catch
        {
            // CoreLib fallback is already referenced above.
        }

        return ScriptOptions.Default
            .AddReferences(assemblies)
            .AddImports(
                "System",
                "System.Linq",
                "System.Collections.Generic",
                "System.Text",
                "System.Text.Json",
                "System.Text.RegularExpressions",
                "NaSeqMen.Core.Messages");
    }
}
