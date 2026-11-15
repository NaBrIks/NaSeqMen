// SPDX-License-Identifier: MIT
using NaSeqMen.Contracts;

namespace NaSeqMen.ProbeModule;

// Test-only side effect: never included in the distributed alpha modules.
public sealed class ProbeModule : NaModule
{
    public override ModuleDescriptor Descriptor => new("checks.probe", "Проверка границы действий", "1.0", false, false,
        [new("mark", "Записать свидетельство вызова", ["path"])], []);

    public override async ValueTask<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Operation != "mark") return new(false, "Unknown operation");
        await File.AppendAllTextAsync(request.Arguments["path"], request.Input.Value + Environment.NewLine, cancellationToken);
        return new(true, "Probe invoked");
    }
}
