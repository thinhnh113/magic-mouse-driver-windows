// Shared definitions between MagicTouchFilter.sys and the user-mode app.
// Keep in sync with src/MagicMouse.Core/Driver/FilterProtocol.cs.
#pragma once

#define MMF_DEVICE_NAME     L"\\Device\\MagicTouchFilter"
#define MMF_SYMLINK_NAME    L"\\DosDevices\\MagicTouchFilter"
#define MMF_USER_PATH       "\\\\.\\MagicTouchFilter"

#define MMF_PROTOCOL_VERSION 3
#define MMF_MAX_DEVICES      8
#define MMF_MAX_REPORT       128

#define MMF_IOCTL(fn) CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800 + (fn), METHOD_BUFFERED, FILE_ANY_ACCESS)

// out: MMF_DEVICE_LIST
#define IOCTL_MMF_GET_DEVICES   MMF_IOCTL(0)
// in: MMF_CONFIG
#define IOCTL_MMF_SET_CONFIG    MMF_IOCTL(1)
// out: MMF_REPORT (pends until a touch report is available)
#define IOCTL_MMF_READ_REPORT   MMF_IOCTL(2)
// in: MMF_FEATURE
#define IOCTL_MMF_SEND_FEATURE  MMF_IOCTL(3)

typedef enum _MMF_MODEL {
    MmfModelUnknown = 0,
    MmfModelMM1 = 1,   // 2009, report 0x29
    MmfModelMM2 = 2,   // 2015, report 0x12
    MmfModelMM3 = 3,   // 2024 USB-C, report 0x12
} MMF_MODEL;

typedef enum _MMF_TRANSPORT {
    MmfTransportUnknown = 0,
    MmfTransportBluetooth = 1,
    MmfTransportUsb = 2,
} MMF_TRANSPORT;

// Config flags
#define MMF_CFG_MULTITOUCH        0x00000001  // send mode switch, divert touch frames
#define MMF_CFG_SUPPRESS_BUTTONS  0x00000002  // zero buttons in the report passed up (app injects clicks)
#define MMF_CFG_SUPPRESS_MOTION   0x00000004  // zero X/Y passed up (e.g. "ignore input if lifted")
#define MMF_CFG_KEEPALIVE         0x00000008  // periodically re-send mode switch over BT

#pragma pack(push, 1)
typedef struct _MMF_DEVICE_INFO {
    ULONG Index;
    ULONG Model;          // MMF_MODEL
    ULONG Transport;      // MMF_TRANSPORT
    USHORT VendorId;
    USHORT ProductId;
    ULONG Connected;      // channel open and mode switch sent
    ULONG ReportsSeen;
    WCHAR InstanceId[128];
    // Diagnostics (v2)
    ULONG BrbCount;          // BT requests from hidbth / URBs from hidusb seen
    ULONG InputReports;      // all input reports seen (any id/length)
    ULONG ChannelOpens;      // successful L2CAP open / open-response completions
    ULONG ControlKnown;      // BT control channel handle learned
    ULONG InterruptKnown;    // BT interrupt channel handle learned
    ULONG ModeSwitchSends;   // mode-switch attempts
    LONG LastSendStatus;     // NTSTATUS of last feature send (submit or completion)
    UCHAR LastReport[16];    // first bytes of the most recent input report
    ULONG LastReportLength;
    UCHAR LastRaw[16];       // most recent raw packet (BT: with 0xA1 header)
    ULONG LastRawLength;
    ULONG MaxRawLength;      // largest raw packet seen
    // Battery (v3): report 0x90 = [0x90, flags (bit1 charging, bit2 full), level 0..100]
    ULONG BatteryReports;    // 0x90 reports seen (unsolicited or GET_REPORT replies)
    UCHAR LastBattery[4];    // most recent 0x90 report as received
    UCHAR GoodBattery[4];    // most recent 0x90 report that was not all-zero
    LONGLONG GoodBatteryTime;// KeQueryInterruptTime of GoodBattery (0 = never)
} MMF_DEVICE_INFO;

typedef struct _MMF_DEVICE_LIST {
    ULONG ProtocolVersion;
    ULONG Count;
    MMF_DEVICE_INFO Devices[MMF_MAX_DEVICES];
} MMF_DEVICE_LIST;

typedef struct _MMF_CONFIG {
    ULONG Index;
    ULONG Flags;
    ULONG KeepAlivePeriodMs;
} MMF_CONFIG;

typedef struct _MMF_REPORT {
    ULONG Index;
    ULONG Length;         // bytes valid in Data, Data[0] = report ID
    LONGLONG Timestamp;   // KeQueryPerformanceCounter ticks converted to 100ns
    UCHAR Data[MMF_MAX_REPORT];
} MMF_REPORT;

typedef struct _MMF_FEATURE {
    ULONG Index;
    ULONG Length;         // Data[0] = report ID
    UCHAR Data[64];
} MMF_FEATURE;
#pragma pack(pop)
