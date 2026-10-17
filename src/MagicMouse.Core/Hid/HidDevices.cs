using MagicMouse.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace MagicMouse.Core.Hid;

public sealed record HidCollection(string Path, string? InstanceId, ushort VendorId, ushort ProductId,
    ushort UsagePage, ushort Usage, int InputLength, int OutputLength, int FeatureLength, string? Product);

public static class HidDevices
{
    public static readonly ushort[] AppleVendorIds = { 0x05AC, 0x004C };
    public static readonly ushort[] MagicMouseProductIds = { 0x030D, 0x0310, 0x0269, 0x0323 };

    public static SafeFileHandle Open(string path, bool readWrite)
    {
        // Access 0 still allows HidD_Get/SetFeature on exclusive (mouse) collections.
        uint access = readWrite ? NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE : 0;
        return NativeMethods.CreateFile(path, access, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
    }

    public static IEnumerable<HidCollection> Enumerate(Func<ushort, ushort, bool>? filter = null)
    {
        NativeMethods.HidD_GetHidGuid(out var guid);
        foreach (var path in NativeMethods.GetInterfaces(guid))
        {
            using var h = Open(path, readWrite: false);
            if (h.IsInvalid) continue;
            var attr = new NativeMethods.HIDD_ATTRIBUTES { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.HIDD_ATTRIBUTES>() };
            if (!NativeMethods.HidD_GetAttributes(h, ref attr)) continue;
            if (filter != null && !filter(attr.VendorID, attr.ProductID)) continue;

            NativeMethods.HIDP_CAPS caps = default;
            if (NativeMethods.HidD_GetPreparsedData(h, out var pp))
            {
                NativeMethods.HidP_GetCaps(pp, out caps);
                NativeMethods.HidD_FreePreparsedData(pp);
            }
            var name = new char[128];
            string? product = NativeMethods.HidD_GetProductString(h, name, name.Length * 2)
                ? new string(name).TrimEnd('\0') : null;
            yield return new HidCollection(path, NativeMethods.GetInterfaceInstanceId(path), attr.VendorID,
                attr.ProductID, caps.UsagePage, caps.Usage, caps.InputReportByteLength,
                caps.OutputReportByteLength, caps.FeatureReportByteLength, product);
        }
    }

    public static bool IsMagicMouse(ushort vid, ushort pid) =>
        AppleVendorIds.Contains(vid) && MagicMouseProductIds.Contains(pid);
}
