using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace NaSeqMen.Nodes.Interception;

/// <summary>
/// Resolves friendly human-readable vendor and USB product names for Interception hardware IDs
/// using the BSD-3-Clause USB VID table plus Windows CfgMgr32 BusReportedDeviceDesc on the parent USB node.
/// </summary>
internal static partial class UsbDeviceNameResolver
{
    private static readonly ConcurrentDictionary<string, string?> BusNameCache = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})", RegexOptions.Compiled)]
    private static partial Regex VidPidRegex();

    public static (string DisplayName, string? VendorName, string? ProductName) Resolve(string hardwareId, string kind)
    {
        if (string.IsNullOrWhiteSpace(hardwareId))
            return (kind == "mouse" ? "Unknown Mouse" : "Unknown Keyboard", null, null);

        if (hardwareId.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase))
        {
            var ps2Name = kind == "mouse" ? "PS/2 Mouse" : "PS/2 Keyboard";
            return ($"{ps2Name} ({hardwareId})", "PS/2", ps2Name);
        }

        var m = VidPidRegex().Match(hardwareId);
        if (!m.Success)
            return (hardwareId, null, null);

        var vid = m.Groups[1].Value.ToUpperInvariant();
        var pid = m.Groups[2].Value.ToUpperInvariant();
        var vidPidKey = $"VID_{vid}&PID_{pid}";

        var vendor = UsbVendorTable.TryGetVendor(vid);
        var product = BusNameCache.GetOrAdd(vidPidKey, LookupUsbBusReportedName);

        // Avoid unhelpful generic product strings if we already have a vendor name
        var cleanProduct = product;
        if (!string.IsNullOrWhiteSpace(cleanProduct) &&
            (cleanProduct.Equals("USB Device", StringComparison.OrdinalIgnoreCase) ||
             cleanProduct.Equals("USB Composite Device", StringComparison.OrdinalIgnoreCase) ||
             cleanProduct.Equals("USB Input Device", StringComparison.OrdinalIgnoreCase)) &&
            vendor is not null)
        {
            cleanProduct = null;
        }

        string display;
        if (vendor is not null && !string.IsNullOrWhiteSpace(cleanProduct))
        {
            display = cleanProduct.Contains(vendor, StringComparison.OrdinalIgnoreCase)
                ? $"{cleanProduct} ({vid}:{pid})"
                : $"{vendor} — {cleanProduct} ({vid}:{pid})";
        }
        else if (vendor is not null)
        {
            display = $"{vendor} ({vid}:{pid})";
        }
        else if (!string.IsNullOrWhiteSpace(product))
        {
            display = $"{product} ({vid}:{pid})";
        }
        else
        {
            display = $"USB {vid}:{pid}";
        }

        return (display, vendor, product);
    }

    private static string? LookupUsbBusReportedName(string vidPidKey)
    {
        try
        {
            var filter = $"USB\\{vidPidKey}";
            if (CM_Get_Device_ID_List_SizeW(out var len, filter, CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT) != CR_SUCCESS || len <= 1)
            {
                // Fallback: scan all present USB enumerator instances and match prefix
                if (CM_Get_Device_ID_List_SizeW(out len, "USB", CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT) != CR_SUCCESS || len <= 1)
                    return null;

                var allBuf = new char[len];
                if (CM_Get_Device_ID_ListW("USB", allBuf, len, CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT) != CR_SUCCESS)
                    return null;

                foreach (var devId in SplitMultiSz(allBuf))
                {
                    if (!devId.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var name = ReadBusReportedDesc(devId);
                    if (!string.IsNullOrWhiteSpace(name))
                        return name;
                }

                return null;
            }

            var buf = new char[len];
            if (CM_Get_Device_ID_ListW(filter, buf, len, CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT) != CR_SUCCESS)
                return null;

            foreach (var devId in SplitMultiSz(buf))
            {
                var name = ReadBusReportedDesc(devId);
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }
        catch
        {
            // Never fail device enumeration if CfgMgr32 is unavailable
        }

        return null;
    }

    private static string? ReadBusReportedDesc(string deviceInstanceId)
    {
        if (CM_Locate_DevNodeW(out var devInst, deviceInstanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS)
            return null;

        var key = DEVPKEY_Device_BusReportedDeviceDesc;
        uint bufSize = 512;
        var buffer = new byte[bufSize];
        var cr = CM_Get_DevNode_PropertyW(devInst, ref key, out var propType, buffer, ref bufSize, 0);
        if (cr != CR_SUCCESS || propType != DEVPROP_TYPE_STRING || bufSize < 2)
            return null;

        var text = Encoding.Unicode.GetString(buffer, 0, (int)bufSize).TrimEnd('\0').Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static IEnumerable<string> SplitMultiSz(char[] buffer)
    {
        var start = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0')
                continue;

            if (i == start)
                yield break;

            yield return new string(buffer, start, i - start);
            start = i + 1;
        }
    }

    private const uint CR_SUCCESS = 0;
    private const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;
    private const uint CM_GETIDLIST_FILTER_PRESENT = 0x00000100;
    private const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;
    private const uint DEVPROP_TYPE_STRING = 0x00000012;

    // {540b947e-8b40-45bc-a8a2-6a0b894cbda2}, 4
    private static readonly DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc = new()
    {
        fmtid = new Guid(0x540b947e, 0x8b40, 0x45bc, 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2),
        pid = 4,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint pulLen, string? pszFilter, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_ListW(string? pszFilter, [Out] char[] buffer, uint bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(
        uint dnDevInst,
        ref DEVPROPKEY propertyKey,
        out uint propertyType,
        [Out] byte[] propertyBuffer,
        ref uint propertyBufferSize,
        uint ulFlags);
}
