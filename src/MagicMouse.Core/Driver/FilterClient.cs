using System.ComponentModel;
using System.Runtime.InteropServices;
using MagicMouse.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace MagicMouse.Core.Driver;

/// <summary>User-mode side of the MagicTouchFilter control device. Thread-safe: reads may block on one
/// thread while configuration calls are made from another (the handle is overlapped).</summary>
public sealed class FilterClient : IDisposable
{
    private readonly SafeFileHandle _handle;
    private volatile bool _disposed;

    private FilterClient(SafeFileHandle handle) => _handle = handle;

    public static FilterClient? TryOpen()
    {
        var h = NativeMethods.CreateFile(FilterProtocol.UserPath,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero,
            NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        return new FilterClient(h);
    }

    public unsafe IReadOnlyList<FilterDevice> GetDevices()
    {
        MmfDeviceList list;
        if (!NativeMethods.IoctlWait(_handle, FilterProtocol.IoctlGetDevices, null, 0, &list,
                sizeof(MmfDeviceList), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var result = new List<FilterDevice>();
        var p = &list.D0;
        for (int i = 0; i < Math.Min(list.Count, (uint)FilterProtocol.MaxDevices); i++)
        {
            var d = p[i];
            var diag = new FilterDiagnostics(d.BrbCount, d.InputReports, d.ChannelOpens, d.ControlKnown != 0,
                d.InterruptKnown != 0, d.ModeSwitchSends, d.LastSendStatus,
                new ReadOnlySpan<byte>(d.LastReport, (int)Math.Min(d.LastReportLength, 16u)).ToArray(),
                new ReadOnlySpan<byte>(d.LastRaw, (int)Math.Min(d.LastRawLength, 16u)).ToArray(),
                d.LastRawLength, d.MaxRawLength);
            // Older drivers return a shorter entry: the v3 battery fields would be garbage, so skip them.
            FilterBattery? battery = list.ProtocolVersion >= 3
                ? new FilterBattery(d.BatteryReports, new ReadOnlySpan<byte>(d.LastBattery, 4).ToArray(),
                    d.GoodBatteryTime != 0 ? new ReadOnlySpan<byte>(d.GoodBattery, 4).ToArray() : null)
                : null;
            result.Add(new FilterDevice((int)d.Index, (DeviceModel)d.Model, (Transport)d.Transport,
                d.VendorId, d.ProductId, d.Connected != 0, d.ReportsSeen,
                new string(d.InstanceId).TrimEnd('\0'), diag, battery));
        }
        return result;
    }

    public unsafe void SetConfig(int index, FilterFlags flags, uint keepAlivePeriodMs)
    {
        var cfg = new MmfConfig { Index = (uint)index, Flags = (uint)flags, KeepAlivePeriodMs = keepAlivePeriodMs };
        if (!NativeMethods.IoctlWait(_handle, FilterProtocol.IoctlSetConfig, &cfg, sizeof(MmfConfig), null, 0, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public unsafe void SendFeature(int index, ReadOnlySpan<byte> report)
    {
        if (report.Length is 0 or > 64) throw new ArgumentOutOfRangeException(nameof(report));
        var f = new MmfFeature { Index = (uint)index, Length = (uint)report.Length };
        report.CopyTo(new Span<byte>(f.Data, 64));
        if (!NativeMethods.IoctlWait(_handle, FilterProtocol.IoctlSendFeature, &f, sizeof(MmfFeature), null, 0, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>Blocks until the driver has a multitouch report. Returns null when cancelled/disposed.</summary>
    public unsafe RawReport? ReadReport()
    {
        MmfReport r;
        if (!NativeMethods.IoctlWait(_handle, FilterProtocol.IoctlReadReport, null, 0, &r, sizeof(MmfReport), out _))
        {
            int err = Marshal.GetLastWin32Error();
            if (_disposed || err == NativeMethods.ERROR_OPERATION_ABORTED) return null;
            throw new Win32Exception(err);
        }
        int len = (int)Math.Min(r.Length, (uint)FilterProtocol.MaxReport);
        return new RawReport((int)r.Index, r.Timestamp, new ReadOnlySpan<byte>(r.Data, len).ToArray());
    }

    /// <summary>Wakes threads blocked in <see cref="ReadReport"/>.</summary>
    public void CancelPendingReads()
    {
        if (!_handle.IsClosed) NativeMethods.CancelIoEx(_handle, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelPendingReads();
        _handle.Dispose();
    }
}
