// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;
using System.Text;

namespace NaSeqMen.Input.Windows;

internal sealed record PnpDeviceIdentity(Guid ContainerId, string? Name)
{
    private const uint GuidType = 13;
    private const uint StringType = 18;
    private static readonly Guid SystemContainer = new("00000000-0000-0000-ffff-ffffffffffff");
    private static readonly PropertyKey InstanceKey = new(new("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);
    private static readonly PropertyKey ContainerKey = new(new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
    private static readonly PropertyKey BusNameKey = new(new("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 4);

    public static PnpDeviceIdentity? Find(string interfacePath)
    {
        var buffer = new byte[2048];
        var size = (uint)buffer.Length;
        if (CM_Get_Device_Interface_PropertyW(interfacePath, in InstanceKey, out var type, buffer, ref size, 0) != 0 ||
            type != StringType || size < 4 ||
            CM_Locate_DevNodeW(out var node, ReadString(buffer, size), 0) != 0)
            return null;

        var container = ReadContainer(node, buffer);
        if (container == Guid.Empty || container == SystemContainer) return null;
        string? name = null;
        do
        {
            size = (uint)buffer.Length;
            if (CM_Get_DevNode_PropertyW(node, in BusNameKey, out type, buffer, ref size, 0) == 0 && type == StringType)
            {
                var reported = ReadString(buffer, size);
                if (!string.IsNullOrWhiteSpace(reported)) name = reported;
            }
        }
        while (CM_Get_Parent(out node, node, 0) == 0 && ReadContainer(node, buffer) == container);
        return new(container, name);
    }

    private static Guid ReadContainer(uint node, byte[] buffer)
    {
        var size = 16u;
        return CM_Get_DevNode_PropertyW(node, in ContainerKey, out var type, buffer, ref size, 0) == 0 &&
            type == GuidType && size == 16 ? new Guid(buffer.AsSpan(0, 16)) : Guid.Empty;
    }

    private static string ReadString(byte[] buffer, uint size) =>
        Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PropertyKey(Guid Format, uint Id);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_Interface_PropertyW(string path, in PropertyKey key,
        out uint type, [Out] byte[] buffer, ref uint size, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string instanceId, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_PropertyW(uint node, in PropertyKey key,
        out uint type, [Out] byte[] buffer, ref uint size, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
}
