// SPDX-License-Identifier: MIT
using NaSeqMen.Editor.Web;
using static NaSeqMen.Contracts.MessageCatalog;

var root = Path.GetFullPath(Argument("--root") ?? Environment.CurrentDirectory);
var port = Port("--port", 17964);
var executorPort = Port("--executor-port", 17963);
await using var app = WebEditorHost.Build(root, port, executorPort);
await app.RunAsync();

string? Argument(string name)
{
    var index = Array.IndexOf(args, name);
    if (index < 0) return null;
    if (index + 1 >= args.Length) throw Error<ArgumentException>(M("common.argumentMissing", ("argument", name)));
    return args[index + 1];
}
int Port(string name, int fallback)
{
    var text = Argument(name);
    if (text is null) return fallback;
    if (!int.TryParse(text, out var value) || value is < 1024 or > 65535 || value == 443) throw Error<ArgumentException>(M("common.invalidPort"));
    return value;
}
