// SPDX-License-Identifier: MIT
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using System.Collections.Immutable;
using System.Globalization;
using NaSeqMen.Contracts;

namespace NaSeqMen.Scripting;

public sealed class ScriptContext
{
    private readonly Func<string, string, string, Dictionary<string, string>, CancellationToken, ValueTask<ActionResult>> _emit;
    private readonly Action<string> _trace;

    public ScriptContext(
        Func<string, string, string, Dictionary<string, string>, CancellationToken, ValueTask<ActionResult>> emit,
        Action<string> trace, CancellationToken cancellationToken)
    {
        _emit = emit;
        _trace = trace;
        CancellationToken = cancellationToken;
    }

    public CancellationToken CancellationToken { get; }
    public ValueTask<ActionResult> EmitAsync(string module, string connectionId, string operation,
        Dictionary<string, string>? arguments = null)
        => _emit(module, connectionId, operation, arguments ?? [], CancellationToken);
    public void Trace(string message) => _trace(message);
}

public sealed class ScriptGlobals
{
    public required Signal Input { get; init; }
    public required ScriptContext Context { get; init; }
}

public sealed class ScriptCompiler
{
    private readonly ScriptOptions _options = ScriptOptions.Default
        .WithReferences(typeof(Signal).Assembly, typeof(ScriptContext).Assembly,
            typeof(Enumerable).Assembly, typeof(object).Assembly)
        .WithImports("System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks",
            "NaSeqMen.Contracts", "NaSeqMen.Scripting");

    public ScriptRunner<Signal?> Compile(string code)
        => Compile<Signal?, ScriptGlobals>(code);

    public ScriptRunner<T> Compile<T, TGlobals>(string code, params System.Reflection.Assembly[] references)
    {
        var script = CSharpScript.Create<T>(code, _options.AddReferences(references).AddImports("System.Text.Json"), typeof(TGlobals));
        var errors = script.Compile().Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        if (errors.Length != 0)
            throw new CompilationErrorException(string.Join(Environment.NewLine, errors.Select(d =>
            {
                var position = d.Location.GetLineSpan().StartLinePosition;
                return $"{d.Id} ({position.Line + 1},{position.Character + 1}): {d.GetMessage(CultureInfo.InvariantCulture)}";
            })), errors);
        return script.CreateDelegate();
    }
}
