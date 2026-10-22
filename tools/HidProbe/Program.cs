using System.Diagnostics;
using MagicMouse.Core.Driver;
using MagicMouse.Core.Hid;
using MagicMouse.Core.Protocol;
using Microsoft.Win32;

// HidProbe: diagnostics for the Magic Mouse stack.
//   HidProbe              list HID collections, battery, filter status
//   HidProbe stream [s]   stream decoded touch frames from the filter for s seconds (default 10)
//   HidProbe raw [s]      same, but hex dump of raw reports
//   HidProbe sdp          print the HID report descriptor cached from the mouse's SDP record

var mode = args.FirstOrDefault() ?? "info";
int seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 10;

switch (mode)
{
    case "info": Info(); break;
    case "stream": Stream(seconds, raw: false); break;
    case "raw": Stream(seconds, raw: true); break;
    case "sdp": Sdp(); break;
    case "battery": BatteryRaw(seconds); break;
    case "mtoff": MultitouchOffTest(args.Length > 1 ? args[1] : "F10200"); break;
    case "disconnect":
    {
        using var fc = FilterClient.TryOpen();
        var addr = BluetoothLink.ParseAddress(fc?.GetDevices().FirstOrDefault()?.BluetoothAddress);
        if (addr is not { } a) { Console.WriteLine("No Bluetooth Magic Mouse found."); break; }
        int err = BluetoothLink.Disconnect(a);
        Console.WriteLine(err == 0 ? $"Disconnected {a:X12}." : $"Disconnect {a:X12} failed: Win32 error {err}");
        break;
    }
    default: Console.WriteLine("usage: HidProbe [info | stream [sec] | raw [sec] | sdp]"); break;
}

static void Info()
{
    Console.WriteLine("== Apple HID collections");
    foreach (var c in HidDevices.Enumerate((v, _) => HidDevices.AppleVendorIds.Contains(v)))
    {
        Console.WriteLine($"  VID {c.VendorId:X4} PID {c.ProductId:X4}  page {c.UsagePage:X4} usage {c.Usage:X2}  " +
                          $"in {c.InputLength} out {c.OutputLength} feat {c.FeatureLength}  {c.Product}");
        Console.WriteLine($"    {c.InstanceId}");
    }

    Console.WriteLine("== Battery");
    FilterDevice? first = null;
    using (var fc = FilterClient.TryOpen()) first = fc?.GetDevices().FirstOrDefault();
    var b = BatteryReader.Read(first?.BluetoothAddress, first?.Battery);
    Console.WriteLine(b == null ? "  not available" : $"  {b.Percent}% charging={b.Charging} via {b.Source}");

    Console.WriteLine("== MagicTouchFilter");
    using var f = FilterClient.TryOpen();
    if (f == null)
    {
        Console.WriteLine($"  not present ({FilterProtocol.UserPath} cannot be opened)");
        return;
    }
    foreach (var d in f.GetDevices())
    {
        Console.WriteLine($"  #{d.Index} {d.Model} {d.Transport} {d.VendorId:X4}:{d.ProductId:X4} " +
                          $"multitouch={d.MultitouchActive} touchReports={d.ReportsSeen}\n    {d.InstanceId}");
        if (d.Diagnostics is { } g)
            Console.WriteLine($"    requests={g.Requests} inputReports={g.InputReports} channelOpens={g.ChannelOpens} " +
                              $"control={g.ControlChannelKnown} interrupt={g.InterruptChannelKnown}\n" +
                              $"    modeSwitchSends={g.ModeSwitchSends} lastSend={g.LastSendStatusText} " +
                              $"lastReport={Convert.ToHexString(g.LastReport)}\n" +
                              $"    lastRaw[{g.LastRawLength}]={Convert.ToHexString(g.LastRaw)} maxRaw={g.MaxRawLength}");
        if (d.Battery is { } bat)
            Console.WriteLine($"    batteryReports={bat.ReportsSeen} last={Convert.ToHexString(bat.Last)} " +
                              $"good={(bat.Good is { } gb ? Convert.ToHexString(gb) : "none")}");
    }
}

static void Stream(int seconds, bool raw)
{
    using var f = FilterClient.TryOpen();
    if (f == null) { Console.WriteLine("MagicTouchFilter not present."); return; }
    var devices = f.GetDevices().ToDictionary(d => d.Index);
    if (devices.Count == 0) { Console.WriteLine("No filtered Magic Mouse."); return; }
    foreach (var d in devices.Values) f.SetConfig(d.Index, FilterFlags.Multitouch, 2000);

    Console.WriteLine($"Streaming {seconds}s: touch the mouse surface…");
    var sw = Stopwatch.StartNew();
    using var stop = new Timer(_ => f.CancelPendingReads(), null, seconds * 1000, Timeout.Infinite);
    int count = 0;
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        var r = f.ReadReport();
        if (r is not { } rep) break;
        count++;
        if (raw)
        {
            Console.WriteLine($"{sw.ElapsedMilliseconds,6}ms #{rep.DeviceIndex} [{rep.Data.Length}] {Convert.ToHexString(rep.Data)}");
            continue;
        }
        var model = devices.TryGetValue(rep.DeviceIndex, out var dev) ? dev.Model : DeviceModel.Unknown;
        var frame = TouchReportParser.Parse(model, rep.Data, rep.Timestamp100ns);
        if (frame == null) { Console.WriteLine($"undecoded {Convert.ToHexString(rep.Data)}"); continue; }
        var touches = string.Join("  ", frame.Touches.Select(t =>
            $"[{t.Id} {t.X,5},{t.Y,5} maj{t.Major,3} st{t.RawState:X2}]"));
        Console.WriteLine($"{sw.ElapsedMilliseconds,6}ms btn={frame.Buttons,-5} d={frame.Dx,3},{frame.Dy,3} {touches}");
    }
    Console.WriteLine($"{count} reports.");
}

// Experiment: does <hex> (default F1 02 00) take the mouse out of multitouch mode?
// Touch reports (0x12, >8 bytes) vs plain mouse reports (0x12, 8 bytes) are counted per second from the
// driver's counters. Multitouch is always restored at the end.
static void MultitouchOffTest(string hex)
{
    using var f = FilterClient.TryOpen();
    if (f == null) { Console.WriteLine("MagicTouchFilter not present."); return; }
    var dev = f.GetDevices().FirstOrDefault();
    if (dev?.Diagnostics == null) { Console.WriteLine("No filtered Magic Mouse."); return; }
    int idx = dev.Index;
    (uint touch, uint plain) Sample()
    {
        var d = f.GetDevices().First(x => x.Index == idx);
        uint batt = d.Battery?.ReportsSeen ?? 0;
        return (d.ReportsSeen, d.Diagnostics!.InputReports - d.ReportsSeen - batt);
    }
    void Phase(string name, int secs)
    {
        Console.WriteLine($"-- {name}");
        var prev = Sample();
        for (int i = 0; i < secs; i++)
        {
            Thread.Sleep(1000);
            var cur = Sample();
            Console.WriteLine($"   {i + 1,2}s  touch reports +{cur.touch - prev.touch,4}   plain mouse reports +{cur.plain - prev.plain,4}");
            prev = cur;
        }
    }

    try
    {
        // Keep the driver from switching multitouch straight back on when it sees plain reports.
        f.SetConfig(idx, FilterFlags.None, 2000);
        Console.WriteLine("Waiting for a finger on the surface (up to 60 s)...");
        var start = Sample();
        var sw = Stopwatch.StartNew();
        while (Sample().touch - start.touch < 50 && sw.Elapsed.TotalSeconds < 60) Thread.Sleep(200);
        if (sw.Elapsed.TotalSeconds >= 60) { Console.WriteLine("No touches seen; aborting."); return; }

        Phase("baseline: multitouch on", 3);
        var bytes = Convert.FromHexString(hex);
        f.SendFeature(idx, bytes);
        Console.WriteLine($"Sent feature report {hex}");
        Phase("after command", 10);
    }
    finally
    {
        f.SetConfig(idx, FilterFlags.Multitouch, 2000);   // also re-sends F1 02 01
        Console.WriteLine("Restored multitouch.");
    }
}

// Raw view of the battery collection: GET_REPORT replies plus unsolicited input reports.
static void BatteryRaw(int seconds)
{
    foreach (var c in HidDevices.Enumerate(HidDevices.IsMagicMouse))
    {
        if (c.UsagePage != 0xFF00 || c.Usage != 0x14) continue;
        Console.WriteLine($"{c.InstanceId}  inLen={c.InputLength}");
        using var h = HidDevices.Open(c.Path, readWrite: true);
        for (int i = 0; i < 3; i++)
        {
            var buf = new byte[c.InputLength];
            buf[0] = 0x90;
            bool ok = MagicMouse.Core.Native.NativeMethods.HidD_GetInputReport(h, buf, buf.Length);
            Console.WriteLine($"  GetInputReport #{i}: ok={ok} err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()} {Convert.ToHexString(buf)}");
            Thread.Sleep(300);
        }
        Console.WriteLine($"  listening {seconds}s for unsolicited reports…");
        using var fs = new FileStream(h, FileAccess.Read, c.InputLength, isAsync: false);
        var sw = Stopwatch.StartNew();
        var readTask = Task.Run(() =>
        {
            var b = new byte[c.InputLength];
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                int n = fs.Read(b, 0, b.Length);
                if (n <= 0) break;
                Console.WriteLine($"  {sw.ElapsedMilliseconds,6}ms input: {Convert.ToHexString(b, 0, n)}");
            }
        });
        readTask.Wait(TimeSpan.FromSeconds(seconds));
        return;
    }
    Console.WriteLine("battery collection not found");
}

static void Sdp()
{
    using var devs = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices");
    if (devs == null) { Console.WriteLine("BTHPORT device cache not readable."); return; }
    foreach (var addr in devs.GetSubKeyNames())
    {
        using var cached = devs.OpenSubKey(addr + @"\CachedServices");
        if (cached == null) continue;
        foreach (var name in cached.GetValueNames())
        {
            if (cached.GetValue(name) is not byte[] rec) continue;
            // HID descriptor list entry: 0x08 0x22 (uint8 0x22 = Report) followed by a text/bytes element.
            for (int i = 0; i + 4 < rec.Length; i++)
            {
                if (rec[i] != 0x08 || rec[i + 1] != 0x22) continue;
                int len, start;
                if (rec[i + 2] == 0x25) { len = rec[i + 3]; start = i + 4; }
                else if (rec[i + 2] == 0x26) { len = rec[i + 3] << 8 | rec[i + 4]; start = i + 5; }
                else continue;
                if (start + len > rec.Length) continue;
                Console.WriteLine($"{addr} record {name}: HID report descriptor ({len} bytes)");
                Console.WriteLine(Convert.ToHexString(rec, start, len));
            }
        }
    }
}
