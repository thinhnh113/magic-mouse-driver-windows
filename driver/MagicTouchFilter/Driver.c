// MagicTouchFilter: KMDF lower filter for Apple Magic Mouse (BT: below hidbth, USB: below hidusb).
//
// Responsibilities
//  * Send the multitouch mode-switch feature report (undeclared in Apple's descriptor, so user mode
//    cannot send it through HidD_SetFeature).
//  * Copy every multitouch report (0x12 > 8 bytes, MM1 0x29) into a ring that the app drains through
//    a control device (\\.\MagicTouchFilter), then trim the report back to the 8-byte mouse report the
//    stock descriptor declares so Windows keeps moving the pointer.
//  * Optionally mask buttons / motion in the report passed up (the app then injects them itself).

#include <ntddk.h>
#include <wdf.h>
#include <initguid.h>
#include <devpkey.h>
#include <bthdef.h>
#include <bthguid.h>
#include <bthioctl.h>
#include <bthddi.h>
#include <usb.h>
#include <usbioctl.h>
#include <usbdlib.h>
#include "Public.h"

#define MMF_TAG 'fTmM'
#define MMF_RING_SIZE 64
#define HIDP_DATA_INPUT          0xA1   // BT HIDP: DATA | Input
#define HIDP_SET_REPORT_FEATURE  0x53   // BT HIDP: SET_REPORT | Feature
#define MODE_SWITCH_MIN_INTERVAL (10 * 1000 * 1000)  // 1s in 100ns

typedef struct _FILTER_CONTEXT {
    WDFDEVICE Device;
    ULONG Index;
    MMF_MODEL Model;
    MMF_TRANSPORT Transport;
    USHORT VendorId;
    USHORT ProductId;
    WCHAR InstanceId[128];

    volatile LONG Flags;
    ULONG KeepAlivePeriodMs;
    volatile LONG ReportsSeen;
    volatile LONG ModeSwitched;
    LONGLONG LastModeSwitch;

    // Bluetooth
    BTH_PROFILE_DRIVER_INTERFACE BthIf;
    BOOLEAN BthIfValid;
    BTH_ADDR BtAddress;
    L2CAP_CHANNEL_HANDLE ControlChannel;
    L2CAP_CHANNEL_HANDLE InterruptChannel;

    // USB
    UCHAR UsbInterface;

    WDFWORKITEM ModeSwitchWorkItem;
    WDFTIMER KeepAliveTimer;

    // Diagnostics (reported through IOCTL_MMF_GET_DEVICES)
    volatile LONG BrbCount;
    volatile LONG InputReports;
    volatile LONG ChannelOpens;
    volatile LONG ModeSwitchSends;
    volatile LONG LastSendStatus;
    UCHAR LastReport[16];
    ULONG LastReportLength;
    UCHAR LastRaw[16];       // raw packet as received (BT: includes the 0xA1 HIDP header)
    ULONG LastRawLength;
    ULONG MaxRawLength;

    // Battery cache: in multitouch mode GET_REPORT(0x90) answers with zeros, so keep the last real value.
    volatile LONG BatteryReports;
    UCHAR LastBattery[4];
    UCHAR GoodBattery[4];
    LONGLONG GoodBatteryTime;
} FILTER_CONTEXT, *PFILTER_CONTEXT;
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(FILTER_CONTEXT, FilterGetContext)

typedef struct _SEND_CONTEXT {
    PFILTER_CONTEXT Filter;
    PVOID Brb;      // BT
    PURB Urb;       // USB
    PUCHAR Buffer;
} SEND_CONTEXT, *PSEND_CONTEXT;
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(SEND_CONTEXT, SendGetContext)

// Per-request state for input reads whose buffer we replaced.
#define MMF_BIG_BUFFER 256
typedef struct _REQ_CONTEXT {
    PVOID OrigBuffer;
    PMDL OrigMdl;
    ULONG OrigSize;
    PUCHAR Big;
} REQ_CONTEXT, *PREQ_CONTEXT;
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(REQ_CONTEXT, ReqGetContext)

// ---- globals (protected by g_Lock) ----
static KSPIN_LOCK g_Lock;
static WDFDEVICE g_ControlDevice;
static WDFQUEUE g_PendingReads;
static PFILTER_CONTEXT g_Devices[MMF_MAX_DEVICES];
static MMF_REPORT g_Ring[MMF_RING_SIZE];
static ULONG g_RingHead, g_RingCount;
static WDFWAITLOCK g_ControlLock;

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD EvtDeviceAdd;
EVT_WDF_DEVICE_PREPARE_HARDWARE EvtPrepareHardware;
EVT_WDF_DEVICE_D0_ENTRY EvtD0Entry;
EVT_WDF_OBJECT_CONTEXT_CLEANUP EvtControlCleanup;
EVT_WDF_OBJECT_CONTEXT_CLEANUP EvtFilterCleanup;
EVT_WDF_IO_QUEUE_IO_INTERNAL_DEVICE_CONTROL EvtInternalIoctl;
EVT_WDF_REQUEST_COMPLETION_ROUTINE BrbCompletion;
EVT_WDF_REQUEST_COMPLETION_ROUTINE UrbCompletion;
EVT_WDF_REQUEST_COMPLETION_ROUTINE SendCompletion;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL EvtControlIoctl;
EVT_WDF_FILE_CLEANUP EvtControlFileCleanup;
EVT_WDF_WORKITEM ModeSwitchWork;
EVT_WDF_TIMER KeepAliveTick;

static VOID ForwardAndForget(WDFREQUEST Request, WDFIOTARGET Target);
static VOID QueueModeSwitch(PFILTER_CONTEXT Ctx, BOOLEAN Force);

// ------------------------------------------------------------------ helpers

static BOOLEAN WcsContainsI(PCWSTR Hay, PCWSTR Needle)
{
    for (; *Hay; Hay++) {
        PCWSTR h = Hay, n = Needle;
        while (*h && *n && RtlUpcaseUnicodeChar(*h) == RtlUpcaseUnicodeChar(*n)) { h++; n++; }
        if (!*n) return TRUE;
    }
    return FALSE;
}

static VOID DetectModel(PFILTER_CONTEXT Ctx)
{
    PCWSTR id = Ctx->InstanceId;
    Ctx->Transport = WcsContainsI(id, L"BTHENUM") ? MmfTransportBluetooth
                   : WcsContainsI(id, L"USB\\") ? MmfTransportUsb : MmfTransportUnknown;
    if (WcsContainsI(id, L"PID&030D") || WcsContainsI(id, L"PID&0310")) {
        Ctx->Model = MmfModelMM1; Ctx->VendorId = 0x05AC; Ctx->ProductId = 0x030D;
    } else if (WcsContainsI(id, L"PID&0269") || WcsContainsI(id, L"PID_0269")) {
        Ctx->Model = MmfModelMM2; Ctx->VendorId = 0x05AC; Ctx->ProductId = 0x0269;
    } else if (WcsContainsI(id, L"PID&0323") || WcsContainsI(id, L"PID_0323")) {
        Ctx->Model = MmfModelMM3; Ctx->VendorId = 0x05AC; Ctx->ProductId = 0x0323;
    }
    Ctx->UsbInterface = WcsContainsI(id, L"MI_01") ? 1 : 0;
}

static PUCHAR MapBuffer(PVOID Buffer, PMDL Mdl)
{
    if (Buffer) return (PUCHAR)Buffer;
    if (Mdl) return (PUCHAR)MmGetSystemAddressForMdlSafe(Mdl, NormalPagePriority | MdlMappingNoExecute);
    return NULL;
}

// Push a raw report (Data[0] = report id) and satisfy a pending read if there is one.
static VOID PushReport(PFILTER_CONTEXT Ctx, const UCHAR* Data, ULONG Length)
{
    KIRQL irql;
    WDFREQUEST req = NULL;
    MMF_REPORT* slot;
    MMF_REPORT tmp;

    if (Length > MMF_MAX_REPORT) Length = MMF_MAX_REPORT;
    InterlockedIncrement(&Ctx->ReportsSeen);

    RtlZeroMemory(&tmp, sizeof(tmp));
    tmp.Index = Ctx->Index;
    tmp.Length = Length;
    tmp.Timestamp = (LONGLONG)KeQueryInterruptTime();
    RtlCopyMemory(tmp.Data, Data, Length);

    KeAcquireSpinLock(&g_Lock, &irql);
    if (g_PendingReads && NT_SUCCESS(WdfIoQueueRetrieveNextRequest(g_PendingReads, &req))) {
        KeReleaseSpinLock(&g_Lock, irql);
        MMF_REPORT* out;
        if (NT_SUCCESS(WdfRequestRetrieveOutputBuffer(req, sizeof(MMF_REPORT), (PVOID*)&out, NULL))) {
            *out = tmp;
            WdfRequestCompleteWithInformation(req, STATUS_SUCCESS, sizeof(MMF_REPORT));
        } else {
            WdfRequestComplete(req, STATUS_BUFFER_TOO_SMALL);
        }
        return;
    }
    slot = &g_Ring[(g_RingHead + g_RingCount) % MMF_RING_SIZE];
    if (g_RingCount == MMF_RING_SIZE) g_RingHead = (g_RingHead + 1) % MMF_RING_SIZE; // drop oldest
    else g_RingCount++;
    *slot = tmp;
    KeReleaseSpinLock(&g_Lock, irql);
}

// Handle one HID input report in place (Report[0] = report id). Returns the new length to pass up.
static ULONG ProcessInputReport(PFILTER_CONTEXT Ctx, PUCHAR Report, ULONG Length)
{
    LONG flags = Ctx->Flags;
    if (Length == 0) return Length;

    InterlockedIncrement(&Ctx->InputReports);
    Ctx->LastReportLength = Length;
    RtlCopyMemory(Ctx->LastReport, Report, min(Length, sizeof(Ctx->LastReport)));

    if (Report[0] == 0x90 && Length >= 3) {
        InterlockedIncrement(&Ctx->BatteryReports);
        RtlZeroMemory(Ctx->LastBattery, sizeof(Ctx->LastBattery));
        RtlCopyMemory(Ctx->LastBattery, Report, min(Length, sizeof(Ctx->LastBattery)));
        if (Report[1] != 0 || Report[2] != 0) {
            RtlCopyMemory(Ctx->GoodBattery, Ctx->LastBattery, sizeof(Ctx->GoodBattery));
            Ctx->GoodBatteryTime = (LONGLONG)KeQueryInterruptTime();
        }
        return Length;
    }

    if (Ctx->Model == MmfModelMM1 && Report[0] == 0x29) {
        // MM1 multitouch report is not in the descriptor; Windows drops it. App synthesizes motion.
        PushReport(Ctx, Report, Length);
        return Length;
    }
    if (Report[0] != 0x12) return Length;

    if (Length > 8) {
        PushReport(Ctx, Report, Length);
        InterlockedExchange(&Ctx->ModeSwitched, 1);
        Report[6] = 0; Report[7] = 0;
        Length = 8; // trim to the declared mouse report
    } else if ((flags & MMF_CFG_MULTITOUCH) && Length == 8) {
        // Plain mouse report while multitouch is wanted: the device reset (sleep/reconnect).
        QueueModeSwitch(Ctx, FALSE);
    }
    if (flags & MMF_CFG_SUPPRESS_BUTTONS) Report[1] = 0;
    if (flags & MMF_CFG_SUPPRESS_MOTION) { Report[2] = Report[3] = Report[4] = Report[5] = 0; }
    return Length;
}

// ------------------------------------------------------------------ driver / device setup

NTSTATUS DriverEntry(PDRIVER_OBJECT DriverObject, PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;
    NTSTATUS status;

    KeInitializeSpinLock(&g_Lock);
    WDF_DRIVER_CONFIG_INIT(&config, EvtDeviceAdd);
    status = WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
    if (NT_SUCCESS(status)) status = WdfWaitLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &g_ControlLock);
    return status;
}

// ---- control device lifetime ----
// The control device (\\.\MagicTouchFilter) exists while at least one filtered mouse exists. Creation,
// deletion and the device list are serialized by g_ControlLock (PASSIVE). It is re-created on demand
// (AddDevice, D0Entry) if it ever disappears, and a few counters are written to
// HKLM\SYSTEM\CurrentControlSet\Services\MagicTouchFilter\Parameters for troubleshooting.

static struct {
    ULONG Adds, Cleanups, D0Entries, CdoCreates, CdoDeletes, CdoUnexpectedDeletes;
    LONG LastCdoStatus;
} g_Stats;

static VOID SaveStats(VOID)
{
    WDFKEY key;
    if (KeGetCurrentIrql() != PASSIVE_LEVEL) return;
    if (!NT_SUCCESS(WdfDriverOpenParametersRegistryKey(WdfGetDriver(), KEY_WRITE, WDF_NO_OBJECT_ATTRIBUTES, &key)))
        return;
#define SAVE(n, v) { DECLARE_CONST_UNICODE_STRING(s, L##n); WdfRegistryAssignULong(key, &s, (ULONG)(v)); }
    SAVE("Adds", g_Stats.Adds);
    SAVE("Cleanups", g_Stats.Cleanups);
    SAVE("D0Entries", g_Stats.D0Entries);
    SAVE("CdoCreates", g_Stats.CdoCreates);
    SAVE("CdoDeletes", g_Stats.CdoDeletes);
    SAVE("CdoUnexpectedDeletes", g_Stats.CdoUnexpectedDeletes);
    SAVE("LastCdoStatus", g_Stats.LastCdoStatus);
#undef SAVE
    WdfRegistryClose(key);
}

// Runs whenever the control device object is destroyed, however that happens.
VOID EvtControlCleanup(WDFOBJECT Object)
{
    KIRQL irql;
    KeAcquireSpinLock(&g_Lock, &irql);
    if (g_ControlDevice == (WDFDEVICE)Object) {
        // Not deleted by us: forget it so the next AddDevice / D0Entry creates a new one.
        g_ControlDevice = NULL;
        g_PendingReads = NULL;
        g_RingCount = 0;
        g_Stats.CdoUnexpectedDeletes++;
    }
    KeReleaseSpinLock(&g_Lock, irql);
}

static NTSTATUS CreateControlDevice(WDFDRIVER Driver)
{
    PWDFDEVICE_INIT init;
    WDFDEVICE dev = NULL;
    WDF_OBJECT_ATTRIBUTES ca;
    WDF_IO_QUEUE_CONFIG qc;
    WDFQUEUE q;
    NTSTATUS status;
    KIRQL irql;
    DECLARE_CONST_UNICODE_STRING(name, MMF_DEVICE_NAME);
    DECLARE_CONST_UNICODE_STRING(link, MMF_SYMLINK_NAME);
    // Admins and the interactive user (the tray app runs unelevated).
    DECLARE_CONST_UNICODE_STRING(sddl, L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)");

    // A link left behind by an earlier control device would make WdfDeviceCreateSymbolicLink fail.
    IoDeleteSymbolicLink((PUNICODE_STRING)&link);

    init = WdfControlDeviceInitAllocate(Driver, &sddl);
    if (!init) return STATUS_INSUFFICIENT_RESOURCES;
    WdfDeviceInitSetExclusive(init, FALSE);
    {
        WDF_FILEOBJECT_CONFIG fc;
        WDF_FILEOBJECT_CONFIG_INIT(&fc, WDF_NO_EVENT_CALLBACK, WDF_NO_EVENT_CALLBACK, EvtControlFileCleanup);
        WdfDeviceInitSetFileObjectConfig(init, &fc, WDF_NO_OBJECT_ATTRIBUTES);
    }
    status = WdfDeviceInitAssignName(init, &name);
    WDF_OBJECT_ATTRIBUTES_INIT(&ca);
    ca.EvtCleanupCallback = EvtControlCleanup;
    if (NT_SUCCESS(status)) status = WdfDeviceCreate(&init, &ca, &dev);
    if (!NT_SUCCESS(status)) { if (init) WdfDeviceInitFree(init); return status; }

    status = WdfDeviceCreateSymbolicLink(dev, &link);
    if (!NT_SUCCESS(status)) goto fail;

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&qc, WdfIoQueueDispatchSequential);
    qc.EvtIoDeviceControl = EvtControlIoctl;
    status = WdfIoQueueCreate(dev, &qc, WDF_NO_OBJECT_ATTRIBUTES, &q);
    if (!NT_SUCCESS(status)) goto fail;

    WDF_IO_QUEUE_CONFIG_INIT(&qc, WdfIoQueueDispatchManual);
    status = WdfIoQueueCreate(dev, &qc, WDF_NO_OBJECT_ATTRIBUTES, &q);
    if (!NT_SUCCESS(status)) goto fail;

    KeAcquireSpinLock(&g_Lock, &irql);
    g_PendingReads = q;
    g_ControlDevice = dev;
    KeReleaseSpinLock(&g_Lock, irql);
    WdfControlFinishInitializing(dev);
    return STATUS_SUCCESS;
fail:
    WdfObjectDelete(dev);
    IoDeleteSymbolicLink((PUNICODE_STRING)&link);
    return status;
}

// Caller holds g_ControlLock.
static VOID EnsureControlDevice(VOID)
{
    NTSTATUS status;
    if (g_ControlDevice) return;
    status = CreateControlDevice(WdfGetDriver());
    g_Stats.LastCdoStatus = status;
    if (NT_SUCCESS(status)) g_Stats.CdoCreates++;
}

NTSTATUS EvtDeviceAdd(WDFDRIVER Driver, PWDFDEVICE_INIT DeviceInit)
{
    WDF_OBJECT_ATTRIBUTES attrs;
    WDF_PNPPOWER_EVENT_CALLBACKS pnp;
    WDF_IO_QUEUE_CONFIG qc;
    WDF_WORKITEM_CONFIG wic;
    WDF_TIMER_CONFIG tc;
    WDFDEVICE device;
    PFILTER_CONTEXT ctx;
    NTSTATUS status;
    ULONG i, size = 0;
    DEVPROPTYPE type;
    KIRQL irql;
    UNREFERENCED_PARAMETER(Driver);

    WdfFdoInitSetFilter(DeviceInit);

    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnp);
    pnp.EvtDevicePrepareHardware = EvtPrepareHardware;
    pnp.EvtDeviceD0Entry = EvtD0Entry;
    WdfDeviceInitSetPnpPowerEventCallbacks(DeviceInit, &pnp);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attrs, REQ_CONTEXT);
    WdfDeviceInitSetRequestAttributes(DeviceInit, &attrs);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attrs, FILTER_CONTEXT);
    attrs.EvtCleanupCallback = EvtFilterCleanup;
    status = WdfDeviceCreate(&DeviceInit, &attrs, &device);
    if (!NT_SUCCESS(status)) return status;

    ctx = FilterGetContext(device);
    RtlZeroMemory(ctx, sizeof(*ctx));
    ctx->Device = device;
    ctx->Index = MAXULONG;
    ctx->KeepAlivePeriodMs = 2000;
    {
        WDF_DEVICE_PROPERTY_DATA pd;
        WDF_DEVICE_PROPERTY_DATA_INIT(&pd, &DEVPKEY_Device_InstanceId);
        WdfDeviceQueryPropertyEx(device, &pd, sizeof(ctx->InstanceId) - sizeof(WCHAR),
                                 ctx->InstanceId, &size, &type);
    }
    DetectModel(ctx);

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&qc, WdfIoQueueDispatchParallel);
    qc.EvtIoInternalDeviceControl = EvtInternalIoctl;
    status = WdfIoQueueCreate(device, &qc, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
    if (!NT_SUCCESS(status)) return status;

    WDF_WORKITEM_CONFIG_INIT(&wic, ModeSwitchWork);
    WDF_OBJECT_ATTRIBUTES_INIT(&attrs);
    attrs.ParentObject = device;
    status = WdfWorkItemCreate(&wic, &attrs, &ctx->ModeSwitchWorkItem);
    if (!NT_SUCCESS(status)) return status;

    WDF_TIMER_CONFIG_INIT(&tc, KeepAliveTick);
    WDF_OBJECT_ATTRIBUTES_INIT(&attrs);
    attrs.ParentObject = device;
    status = WdfTimerCreate(&tc, &attrs, &ctx->KeepAliveTimer);
    if (!NT_SUCCESS(status)) return status;

    WdfWaitLockAcquire(g_ControlLock, NULL);
    g_Stats.Adds++;
    KeAcquireSpinLock(&g_Lock, &irql);
    for (i = 0; i < MMF_MAX_DEVICES; i++) {
        if (!g_Devices[i]) { g_Devices[i] = ctx; ctx->Index = i; break; }
    }
    KeReleaseSpinLock(&g_Lock, irql);
    // A missing control device only disables MagicMouse features; never fail the mouse for it.
    EnsureControlDevice();
    WdfWaitLockRelease(g_ControlLock);
    SaveStats();
    return STATUS_SUCCESS;
}

NTSTATUS EvtPrepareHardware(WDFDEVICE Device, WDFCMRESLIST Raw, WDFCMRESLIST Translated)
{
    PFILTER_CONTEXT ctx = FilterGetContext(Device);
    UNREFERENCED_PARAMETER(Raw);
    UNREFERENCED_PARAMETER(Translated);
    if (ctx->Transport == MmfTransportBluetooth && !ctx->BthIfValid) {
        NTSTATUS s = WdfFdoQueryForInterface(Device, &GUID_BTHDDI_PROFILE_DRIVER_INTERFACE,
                                             (PINTERFACE)&ctx->BthIf, sizeof(ctx->BthIf),
                                             BTHDDI_PROFILE_DRIVER_INTERFACE_VERSION_FOR_QI, NULL);
        ctx->BthIfValid = NT_SUCCESS(s);
    }
    return STATUS_SUCCESS;  // never fail the mouse because of us
}

// Every start and wake: make sure the app can reach us.
NTSTATUS EvtD0Entry(WDFDEVICE Device, WDF_POWER_DEVICE_STATE PreviousState)
{
    UNREFERENCED_PARAMETER(Device);
    UNREFERENCED_PARAMETER(PreviousState);
    WdfWaitLockAcquire(g_ControlLock, NULL);
    g_Stats.D0Entries++;
    EnsureControlDevice();
    WdfWaitLockRelease(g_ControlLock);
    SaveStats();
    return STATUS_SUCCESS;
}

VOID EvtFilterCleanup(WDFOBJECT Object)
{
    PFILTER_CONTEXT ctx = FilterGetContext((WDFDEVICE)Object);
    KIRQL irql;
    ULONG i, remaining = 0;
    WDFDEVICE cdo = NULL;
    DECLARE_CONST_UNICODE_STRING(link, MMF_SYMLINK_NAME);

    if (ctx->BthIfValid && ctx->BthIf.Interface.InterfaceDereference)
        ctx->BthIf.Interface.InterfaceDereference(ctx->BthIf.Interface.Context);

    WdfWaitLockAcquire(g_ControlLock, NULL);
    g_Stats.Cleanups++;
    KeAcquireSpinLock(&g_Lock, &irql);
    for (i = 0; i < MMF_MAX_DEVICES; i++) {
        if (g_Devices[i] == ctx) g_Devices[i] = NULL;
        if (g_Devices[i]) remaining++;
    }
    if (remaining == 0) { cdo = g_ControlDevice; g_ControlDevice = NULL; g_PendingReads = NULL; g_RingCount = 0; }
    KeReleaseSpinLock(&g_Lock, irql);
    if (cdo) {
        // Last mouse gone: delete the control device so the driver can unload (and be updated).
        WdfObjectDelete(cdo);
        IoDeleteSymbolicLink((PUNICODE_STRING)&link);
        g_Stats.CdoDeletes++;
    }
    WdfWaitLockRelease(g_ControlLock);
    SaveStats();
}

// ------------------------------------------------------------------ IO from hidbth / hidusb

static VOID ForwardAndForget(WDFREQUEST Request, WDFIOTARGET Target)
{
    WDF_REQUEST_SEND_OPTIONS opts;
    WDF_REQUEST_SEND_OPTIONS_INIT(&opts, WDF_REQUEST_SEND_OPTION_SEND_AND_FORGET);
    if (!WdfRequestSend(Request, Target, &opts)) {
        WdfRequestComplete(Request, WdfRequestGetStatus(Request));
    }
}

static VOID ForwardWithCompletion(WDFREQUEST Request, WDFIOTARGET Target,
                                  PFN_WDF_REQUEST_COMPLETION_ROUTINE Routine, PFILTER_CONTEXT Ctx)
{
    WdfRequestFormatRequestUsingCurrentType(Request);
    WdfRequestSetCompletionRoutine(Request, Routine, Ctx);
    if (!WdfRequestSend(Request, Target, WDF_NO_SEND_OPTIONS)) {
        WdfRequestComplete(Request, WdfRequestGetStatus(Request));
    }
}

// hidbth / hidusb size their input reads for the 8-byte mouse report the stock descriptor declares, which
// is too small for a multitouch report. Swap in our own buffer for the trip down, then copy the (trimmed)
// mouse report back into the caller's buffer on completion.
static BOOLEAN SwapInBigBuffer(PREQ_CONTEXT Rc, PVOID* Buffer, PMDL* Mdl, ULONG* Size)
{
    Rc->Big = (PUCHAR)ExAllocatePool2(POOL_FLAG_NON_PAGED, MMF_BIG_BUFFER, MMF_TAG);
    if (!Rc->Big) return FALSE;
    Rc->OrigBuffer = *Buffer;
    Rc->OrigMdl = *Mdl;
    Rc->OrigSize = *Size;
    *Buffer = Rc->Big;
    *Mdl = NULL;
    *Size = MMF_BIG_BUFFER;
    return TRUE;
}

// Restores the caller's buffer; copies Length bytes of our buffer into it. Returns bytes copied.
static ULONG SwapOutBigBuffer(PREQ_CONTEXT Rc, PVOID* Buffer, PMDL* Mdl, ULONG Length)
{
    ULONG n = 0;
    PUCHAR dst = MapBuffer(Rc->OrigBuffer, Rc->OrigMdl);
    if (dst) {
        n = min(Length, Rc->OrigSize);
        RtlCopyMemory(dst, Rc->Big, n);
    }
    *Buffer = Rc->OrigBuffer;
    *Mdl = Rc->OrigMdl;
    ExFreePoolWithTag(Rc->Big, MMF_TAG);
    Rc->Big = NULL;
    return n;
}

static BOOLEAN IsOpenType(USHORT Type)
{
    return Type == BRB_L2CA_OPEN_CHANNEL || Type == BRB_L2CA_OPEN_CHANNEL_RESPONSE ||
           Type == BRB_L2CA_OPEN_ENHANCED_CHANNEL || Type == BRB_L2CA_OPEN_ENHANCED_CHANNEL_RESPONSE;
}

VOID EvtInternalIoctl(WDFQUEUE Queue, WDFREQUEST Request, size_t OutLen, size_t InLen, ULONG Code)
{
    WDFDEVICE device = WdfIoQueueGetDevice(Queue);
    PFILTER_CONTEXT ctx = FilterGetContext(device);
    WDFIOTARGET target = WdfDeviceGetIoTarget(device);
    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(WdfRequestWdmGetIrp(Request));
    PREQ_CONTEXT rc = ReqGetContext(Request);
    UNREFERENCED_PARAMETER(OutLen);
    UNREFERENCED_PARAMETER(InLen);

    rc->Big = NULL;
    if (Code == IOCTL_INTERNAL_BTH_SUBMIT_BRB || Code == IOCTL_INTERNAL_USB_SUBMIT_URB) {
        InterlockedIncrement(&ctx->BrbCount);
    }

    if (Code == IOCTL_INTERNAL_BTH_SUBMIT_BRB) {
        BRB_HEADER* hdr = (BRB_HEADER*)stack->Parameters.Others.Argument1;
        if (hdr && (hdr->Type == BRB_L2CA_ACL_TRANSFER || hdr->Type == BRB_L2CA_CLOSE_CHANNEL || IsOpenType(hdr->Type))) {
            if (hdr->Type == BRB_L2CA_ACL_TRANSFER) {
                struct _BRB_L2CA_ACL_TRANSFER* t = (struct _BRB_L2CA_ACL_TRANSFER*)hdr;
                if (!(t->TransferFlags & ACL_TRANSFER_DIRECTION_IN)) {
                    // Outgoing from hidbth: non-DATA transactions (GET/SET_REPORT, SET_PROTOCOL...) only
                    // travel on the control channel.
                    PUCHAR b = MapBuffer(t->Buffer, t->BufferMDL);
                    if (b && t->BufferSize > 0 && (b[0] & 0xF0) != 0xA0) ctx->ControlChannel = t->ChannelHandle;
                    ctx->BtAddress = t->BtAddress;
                    ForwardAndForget(Request, target);
                    return;
                }
                if (t->BufferSize < MMF_BIG_BUFFER && ctx->Model != MmfModelUnknown) {
                    t->TransferFlags |= ACL_SHORT_TRANSFER_OK;
                    SwapInBigBuffer(rc, &t->Buffer, &t->BufferMDL, &t->BufferSize);
                }
            }
            ForwardWithCompletion(Request, target, BrbCompletion, ctx);
            return;
        }
    } else if (Code == IOCTL_INTERNAL_USB_SUBMIT_URB) {
        PURB urb = (PURB)stack->Parameters.Others.Argument1;
        if (urb && urb->UrbHeader.Function == URB_FUNCTION_BULK_OR_INTERRUPT_TRANSFER &&
            (urb->UrbBulkOrInterruptTransfer.TransferFlags & USBD_TRANSFER_DIRECTION_IN)) {
            struct _URB_BULK_OR_INTERRUPT_TRANSFER* x = &urb->UrbBulkOrInterruptTransfer;
            if (x->TransferBufferLength < MMF_BIG_BUFFER && ctx->Model != MmfModelUnknown) {
                x->TransferFlags |= USBD_SHORT_TRANSFER_OK;
                SwapInBigBuffer(rc, &x->TransferBuffer, &x->TransferBufferMDL, &x->TransferBufferLength);
            }
            ForwardWithCompletion(Request, target, UrbCompletion, ctx);
            return;
        }
    }
    ForwardAndForget(Request, target);
}

static VOID NoteRawPacket(PFILTER_CONTEXT Ctx, const UCHAR* Data, ULONG Length)
{
    ULONG n = min(Length, (ULONG)sizeof(Ctx->LastRaw));
    RtlCopyMemory(Ctx->LastRaw, Data, n);
    Ctx->LastRawLength = Length;
    if (Length > Ctx->MaxRawLength) Ctx->MaxRawLength = Length;
}

VOID BrbCompletion(WDFREQUEST Request, WDFIOTARGET Target, PWDF_REQUEST_COMPLETION_PARAMS Params, WDFCONTEXT Context)
{
    PFILTER_CONTEXT ctx = (PFILTER_CONTEXT)Context;
    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(WdfRequestWdmGetIrp(Request));
    BRB_HEADER* hdr = (BRB_HEADER*)stack->Parameters.Others.Argument1;
    PREQ_CONTEXT rc = ReqGetContext(Request);
    BOOLEAN ok = NT_SUCCESS(Params->IoStatus.Status);
    UNREFERENCED_PARAMETER(Target);

    if (hdr && hdr->Type == BRB_L2CA_ACL_TRANSFER) {
        struct _BRB_L2CA_ACL_TRANSFER* t = (struct _BRB_L2CA_ACL_TRANSFER*)hdr;
        PUCHAR b = MapBuffer(t->Buffer, t->BufferMDL);
        ULONG len = ok ? t->BufferSize : 0;
        if (b && len > 0) NoteRawPacket(ctx, b, len);
        if (b && len >= 2 && b[0] == HIDP_DATA_INPUT) {
            // Input on the interrupt channel (input on the control channel = GET_REPORT replies).
            if (t->ChannelHandle != ctx->ControlChannel) ctx->InterruptChannel = t->ChannelHandle;
            ctx->BtAddress = t->BtAddress;
            len = 1 + ProcessInputReport(ctx, b + 1, len - 1);
        }
        if (rc->Big) len = SwapOutBigBuffer(rc, &t->Buffer, &t->BufferMDL, len);
        t->BufferSize = len;
        WdfRequestComplete(Request, Params->IoStatus.Status);
        return;
    }

    if (ok && hdr) {
        if (hdr->Type == BRB_L2CA_OPEN_CHANNEL || hdr->Type == BRB_L2CA_OPEN_ENHANCED_CHANNEL) {
            // Host-initiated: the PSM tells us which channel this is.
            struct _BRB_L2CA_OPEN_ENHANCED_CHANNEL* o = (struct _BRB_L2CA_OPEN_ENHANCED_CHANNEL*)hdr;
            InterlockedIncrement(&ctx->ChannelOpens);
            ctx->BtAddress = o->BtAddress;
            if (o->Psm == 0x11) ctx->ControlChannel = o->ChannelHandle;
            else if (o->Psm == 0x13) ctx->InterruptChannel = o->ChannelHandle;
        } else if (hdr->Type == BRB_L2CA_OPEN_CHANNEL_RESPONSE || hdr->Type == BRB_L2CA_OPEN_ENHANCED_CHANNEL_RESPONSE) {
            // Device-initiated (usual for a paired mouse). No PSM in the response, but HID requires the
            // control channel (0x11) to be opened before the interrupt channel (0x13).
            struct _BRB_L2CA_OPEN_ENHANCED_CHANNEL* o = (struct _BRB_L2CA_OPEN_ENHANCED_CHANNEL*)hdr;
            ctx->BtAddress = o->BtAddress;
            if (o->Response == CONNECT_RSP_RESULT_SUCCESS && o->ChannelHandle) {
                InterlockedIncrement(&ctx->ChannelOpens);
                if (!ctx->ControlChannel) ctx->ControlChannel = o->ChannelHandle;
                else if (o->ChannelHandle != ctx->ControlChannel) ctx->InterruptChannel = o->ChannelHandle;
            }
        } else if (hdr->Type == BRB_L2CA_CLOSE_CHANNEL) {
            struct _BRB_L2CA_CLOSE_CHANNEL* c = (struct _BRB_L2CA_CLOSE_CHANNEL*)hdr;
            if (c->ChannelHandle == ctx->ControlChannel) ctx->ControlChannel = NULL;
            if (c->ChannelHandle == ctx->InterruptChannel) ctx->InterruptChannel = NULL;
            InterlockedExchange(&ctx->ModeSwitched, 0);
        }
    }
    WdfRequestComplete(Request, Params->IoStatus.Status);
}

VOID UrbCompletion(WDFREQUEST Request, WDFIOTARGET Target, PWDF_REQUEST_COMPLETION_PARAMS Params, WDFCONTEXT Context)
{
    PFILTER_CONTEXT ctx = (PFILTER_CONTEXT)Context;
    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(WdfRequestWdmGetIrp(Request));
    PURB urb = (PURB)stack->Parameters.Others.Argument1;
    PREQ_CONTEXT rc = ReqGetContext(Request);
    UNREFERENCED_PARAMETER(Target);

    if (urb) {
        struct _URB_BULK_OR_INTERRUPT_TRANSFER* x = &urb->UrbBulkOrInterruptTransfer;
        BOOLEAN ok = NT_SUCCESS(Params->IoStatus.Status) && USBD_SUCCESS(urb->UrbHeader.Status);
        PUCHAR b = MapBuffer(x->TransferBuffer, x->TransferBufferMDL);
        ULONG len = ok ? x->TransferBufferLength : 0;
        if (b && len > 0) {
            NoteRawPacket(ctx, b, len);
            len = ProcessInputReport(ctx, b, len);
        }
        if (rc->Big) len = SwapOutBigBuffer(rc, &x->TransferBuffer, &x->TransferBufferMDL, len);
        x->TransferBufferLength = len;
    }
    WdfRequestComplete(Request, Params->IoStatus.Status);
}

// ------------------------------------------------------------------ mode switch / feature send

// Internal IOCTLs carry a raw pointer (BRB / URB) in Argument1, which the WDF format call can't express
// (it takes WDFMEMORY), so format with no args and patch the next stack location directly.
static NTSTATUS FormatInternalOthers(WDFIOTARGET Target, WDFREQUEST Request, ULONG Code, PVOID Arg1)
{
    NTSTATUS status = WdfIoTargetFormatRequestForInternalIoctlOthers(Target, Request, Code,
                                                                      NULL, NULL, NULL, NULL, NULL, NULL);
    if (NT_SUCCESS(status)) {
        IoGetNextIrpStackLocation(WdfRequestWdmGetIrp(Request))->Parameters.Others.Argument1 = Arg1;
    }
    return status;
}

static NTSTATUS SendFeature(PFILTER_CONTEXT Ctx, const UCHAR* Data, ULONG Length)
{
    WDF_OBJECT_ATTRIBUTES attrs;
    WDFREQUEST req;
    PSEND_CONTEXT sc;
    WDFIOTARGET target = WdfDeviceGetIoTarget(Ctx->Device);
    NTSTATUS status;

    if (Length == 0 || Length > 64) return STATUS_INVALID_PARAMETER;

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attrs, SEND_CONTEXT);
    status = WdfRequestCreate(&attrs, target, &req);
    if (!NT_SUCCESS(status)) return status;
    sc = SendGetContext(req);
    RtlZeroMemory(sc, sizeof(*sc));
    sc->Filter = Ctx;
    sc->Buffer = (PUCHAR)ExAllocatePool2(POOL_FLAG_NON_PAGED, 1 + Length, MMF_TAG);
    if (!sc->Buffer) { WdfObjectDelete(req); return STATUS_INSUFFICIENT_RESOURCES; }

    if (Ctx->Transport == MmfTransportBluetooth) {
        struct _BRB_L2CA_ACL_TRANSFER* t;
        if (!Ctx->BthIfValid) { status = STATUS_NOINTERFACE; goto fail; }
        if (!Ctx->ControlChannel) { status = STATUS_DEVICE_NOT_READY; goto fail; }
        t = (struct _BRB_L2CA_ACL_TRANSFER*)Ctx->BthIf.BthAllocateBrb(BRB_L2CA_ACL_TRANSFER, MMF_TAG);
        if (!t) { status = STATUS_INSUFFICIENT_RESOURCES; goto fail; }
        sc->Brb = t;
        sc->Buffer[0] = HIDP_SET_REPORT_FEATURE;
        RtlCopyMemory(sc->Buffer + 1, Data, Length);
        t->BtAddress = Ctx->BtAddress;
        t->ChannelHandle = Ctx->ControlChannel;
        t->TransferFlags = ACL_TRANSFER_DIRECTION_OUT;
        t->BufferSize = 1 + Length;
        t->Buffer = sc->Buffer;
        status = FormatInternalOthers(target, req, IOCTL_INTERNAL_BTH_SUBMIT_BRB, t);
    } else if (Ctx->Transport == MmfTransportUsb) {
        PURB urb = (PURB)ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(struct _URB_CONTROL_VENDOR_OR_CLASS_REQUEST), MMF_TAG);
        if (!urb) { status = STATUS_INSUFFICIENT_RESOURCES; goto fail; }
        sc->Urb = urb;
        RtlCopyMemory(sc->Buffer, Data, Length);
        // HID class SET_REPORT: wValue = (Feature << 8) | ReportId, wIndex = interface
        UsbBuildVendorRequest(urb, URB_FUNCTION_CLASS_INTERFACE,
                              sizeof(struct _URB_CONTROL_VENDOR_OR_CLASS_REQUEST),
                              USBD_TRANSFER_DIRECTION_OUT | USBD_SHORT_TRANSFER_OK, 0,
                              0x09, (USHORT)(0x0300 | Data[0]), Ctx->UsbInterface,
                              sc->Buffer, NULL, Length, NULL);
        status = FormatInternalOthers(target, req, IOCTL_INTERNAL_USB_SUBMIT_URB, urb);
    } else {
        status = STATUS_NOT_SUPPORTED;
    }
    if (!NT_SUCCESS(status)) goto fail;

    WdfRequestSetCompletionRoutine(req, SendCompletion, NULL);
    if (!WdfRequestSend(req, target, WDF_NO_SEND_OPTIONS)) {
        status = WdfRequestGetStatus(req);
        goto fail;
    }
    InterlockedExchange(&Ctx->LastSendStatus, STATUS_PENDING);
    return STATUS_SUCCESS;
fail:
    InterlockedExchange(&Ctx->LastSendStatus, status);
    if (sc->Brb) Ctx->BthIf.BthFreeBrb((PBRB)sc->Brb);
    if (sc->Urb) ExFreePoolWithTag(sc->Urb, MMF_TAG);
    if (sc->Buffer) ExFreePoolWithTag(sc->Buffer, MMF_TAG);
    WdfObjectDelete(req);
    return status;
}

VOID SendCompletion(WDFREQUEST Request, WDFIOTARGET Target, PWDF_REQUEST_COMPLETION_PARAMS Params, WDFCONTEXT Context)
{
    PSEND_CONTEXT sc = SendGetContext(Request);
    UNREFERENCED_PARAMETER(Target);
    UNREFERENCED_PARAMETER(Context);
    InterlockedExchange(&sc->Filter->LastSendStatus, Params->IoStatus.Status);
    if (sc->Brb) sc->Filter->BthIf.BthFreeBrb((PBRB)sc->Brb);
    if (sc->Urb) ExFreePoolWithTag(sc->Urb, MMF_TAG);
    if (sc->Buffer) ExFreePoolWithTag(sc->Buffer, MMF_TAG);
    WdfObjectDelete(Request);
}

static NTSTATUS SendModeSwitch(PFILTER_CONTEXT Ctx)
{
    static const UCHAR mm1[] = { 0xD7, 0x01 };
    static const UCHAR mm2[] = { 0xF1, 0x02, 0x01 };
    Ctx->LastModeSwitch = (LONGLONG)KeQueryInterruptTime();
    InterlockedIncrement(&Ctx->ModeSwitchSends);
    if (Ctx->Model == MmfModelMM1) return SendFeature(Ctx, mm1, sizeof(mm1));
    if (Ctx->Model == MmfModelMM2 || Ctx->Model == MmfModelMM3) return SendFeature(Ctx, mm2, sizeof(mm2));
    return STATUS_NOT_SUPPORTED;
}

static VOID QueueModeSwitch(PFILTER_CONTEXT Ctx, BOOLEAN Force)
{
    LONGLONG now = (LONGLONG)KeQueryInterruptTime();
    if (!Force && now - Ctx->LastModeSwitch < MODE_SWITCH_MIN_INTERVAL) return;
    Ctx->LastModeSwitch = now;
    WdfWorkItemEnqueue(Ctx->ModeSwitchWorkItem);
}

VOID ModeSwitchWork(WDFWORKITEM WorkItem)
{
    PFILTER_CONTEXT ctx = FilterGetContext((WDFDEVICE)WdfWorkItemGetParentObject(WorkItem));
    if (ctx->Flags & MMF_CFG_MULTITOUCH) SendModeSwitch(ctx);
}

VOID KeepAliveTick(WDFTIMER Timer)
{
    PFILTER_CONTEXT ctx = FilterGetContext((WDFDEVICE)WdfTimerGetParentObject(Timer));
    if ((ctx->Flags & (MMF_CFG_MULTITOUCH | MMF_CFG_KEEPALIVE)) == (MMF_CFG_MULTITOUCH | MMF_CFG_KEEPALIVE)) {
        QueueModeSwitch(ctx, TRUE);
        WdfTimerStart(Timer, WDF_REL_TIMEOUT_IN_MS(ctx->KeepAlivePeriodMs));
    }
}

// ------------------------------------------------------------------ control device (app interface)

static PFILTER_CONTEXT LookupDevice(ULONG Index)
{
    return Index < MMF_MAX_DEVICES ? g_Devices[Index] : NULL;
}

// The app went away (exit or crash): stop masking buttons/motion so the mouse keeps working natively.
VOID EvtControlFileCleanup(WDFFILEOBJECT FileObject)
{
    KIRQL irql;
    ULONG i;
    UNREFERENCED_PARAMETER(FileObject);
    KeAcquireSpinLock(&g_Lock, &irql);
    for (i = 0; i < MMF_MAX_DEVICES; i++) {
        if (g_Devices[i]) {
            InterlockedAnd(&g_Devices[i]->Flags, ~(LONG)(MMF_CFG_SUPPRESS_BUTTONS | MMF_CFG_SUPPRESS_MOTION));
        }
    }
    KeReleaseSpinLock(&g_Lock, irql);
}

VOID EvtControlIoctl(WDFQUEUE Queue, WDFREQUEST Request, size_t OutLen, size_t InLen, ULONG Code)
{
    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    ULONG_PTR info = 0;
    KIRQL irql;
    UNREFERENCED_PARAMETER(Queue);
    UNREFERENCED_PARAMETER(OutLen);
    UNREFERENCED_PARAMETER(InLen);

    switch (Code) {
    case IOCTL_MMF_GET_DEVICES: {
        MMF_DEVICE_LIST* out;
        ULONG i;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(MMF_DEVICE_LIST), (PVOID*)&out, NULL);
        if (!NT_SUCCESS(status)) break;
        RtlZeroMemory(out, sizeof(*out));
        out->ProtocolVersion = MMF_PROTOCOL_VERSION;
        KeAcquireSpinLock(&g_Lock, &irql);
        for (i = 0; i < MMF_MAX_DEVICES; i++) {
            PFILTER_CONTEXT c = g_Devices[i];
            MMF_DEVICE_INFO* d;
            if (!c) continue;
            d = &out->Devices[out->Count++];
            d->Index = c->Index;
            d->Model = c->Model;
            d->Transport = c->Transport;
            d->VendorId = c->VendorId;
            d->ProductId = c->ProductId;
            d->Connected = c->ModeSwitched;
            d->ReportsSeen = (ULONG)c->ReportsSeen;
            RtlCopyMemory(d->InstanceId, c->InstanceId, sizeof(d->InstanceId));
            d->BrbCount = (ULONG)c->BrbCount;
            d->InputReports = (ULONG)c->InputReports;
            d->ChannelOpens = (ULONG)c->ChannelOpens;
            d->ControlKnown = c->ControlChannel != NULL;
            d->InterruptKnown = c->InterruptChannel != NULL;
            d->ModeSwitchSends = (ULONG)c->ModeSwitchSends;
            d->LastSendStatus = c->LastSendStatus;
            RtlCopyMemory(d->LastReport, c->LastReport, sizeof(d->LastReport));
            d->LastReportLength = c->LastReportLength;
            RtlCopyMemory(d->LastRaw, c->LastRaw, sizeof(d->LastRaw));
            d->LastRawLength = c->LastRawLength;
            d->MaxRawLength = c->MaxRawLength;
            d->BatteryReports = (ULONG)c->BatteryReports;
            RtlCopyMemory(d->LastBattery, c->LastBattery, sizeof(d->LastBattery));
            RtlCopyMemory(d->GoodBattery, c->GoodBattery, sizeof(d->GoodBattery));
            d->GoodBatteryTime = c->GoodBatteryTime;
        }
        KeReleaseSpinLock(&g_Lock, irql);
        info = sizeof(MMF_DEVICE_LIST);
        break;
    }
    case IOCTL_MMF_SET_CONFIG: {
        MMF_CONFIG* in;
        PFILTER_CONTEXT c;
        status = WdfRequestRetrieveInputBuffer(Request, sizeof(MMF_CONFIG), (PVOID*)&in, NULL);
        if (!NT_SUCCESS(status)) break;
        c = LookupDevice(in->Index);
        if (!c) { status = STATUS_NO_SUCH_DEVICE; break; }
        c->KeepAlivePeriodMs = in->KeepAlivePeriodMs >= 250 ? in->KeepAlivePeriodMs : 2000;
        InterlockedExchange(&c->Flags, (LONG)in->Flags);
        if (in->Flags & MMF_CFG_MULTITOUCH) QueueModeSwitch(c, TRUE);
        if (in->Flags & MMF_CFG_KEEPALIVE) WdfTimerStart(c->KeepAliveTimer, WDF_REL_TIMEOUT_IN_MS(c->KeepAlivePeriodMs));
        else WdfTimerStop(c->KeepAliveTimer, FALSE);
        break;
    }
    case IOCTL_MMF_SEND_FEATURE: {
        MMF_FEATURE* in;
        PFILTER_CONTEXT c;
        status = WdfRequestRetrieveInputBuffer(Request, sizeof(MMF_FEATURE), (PVOID*)&in, NULL);
        if (!NT_SUCCESS(status)) break;
        c = LookupDevice(in->Index);
        status = c ? SendFeature(c, in->Data, in->Length) : STATUS_NO_SUCH_DEVICE;
        break;
    }
    case IOCTL_MMF_READ_REPORT: {
        MMF_REPORT* out;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(MMF_REPORT), (PVOID*)&out, NULL);
        if (!NT_SUCCESS(status)) break;
        KeAcquireSpinLock(&g_Lock, &irql);
        if (g_RingCount > 0) {
            *out = g_Ring[g_RingHead];
            g_RingHead = (g_RingHead + 1) % MMF_RING_SIZE;
            g_RingCount--;
            KeReleaseSpinLock(&g_Lock, irql);
            WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, sizeof(MMF_REPORT));
            return;
        }
        // Park under the lock so PushReport cannot miss it.
        status = WdfRequestForwardToIoQueue(Request, g_PendingReads);
        KeReleaseSpinLock(&g_Lock, irql);
        if (NT_SUCCESS(status)) return;
        break;
    }
    default:
        break;
    }
    WdfRequestCompleteWithInformation(Request, status, info);
}


