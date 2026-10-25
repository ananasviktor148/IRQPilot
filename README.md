# IRQPilot

A Windows desktop tool for managing **CPU interrupt affinity** — a modern, simpler take on
Microsoft's Interrupt Affinity Policy Tool, with a built-in LatencyMon-style **Core Radar**
that shows which cores are least loaded by interrupts so you can pin devices to the quiet ones.

Windows 10 21H2+ / Windows 11, x64 · .NET 8 · WPF (MVVM, CommunityToolkit.Mvvm) · **no kernel drivers**.

---

## What it does

### Devices tab
- Enumerates present PnP devices that participate in interrupt management (via CfgMgr32),
  showing class, friendly name, instance id, and MSI/MSI-X support.
- Reads each device's current affinity policy from the registry.
- Lets you pick a policy (`IRQ_DEVICE_POLICY`: MachineDefault, AllCloseProcessors,
  OneCloseProcessor, AllProcessorsInMachine, SpecifiedProcessors,
  SpreadMessagesAcrossAllProcessors) and a set of CPU cores on a grid whose tiles are
  **colored by live interrupt load** — selection and monitoring are the same view.
- Applying writes the registry values, optionally restarts the device, and marks it
  **Pending** until a restart/reboot activates the change.
- Before the first write to a device, its existing values are backed up to JSON.
  *Restore defaults* (per-device or global) puts back exactly what was there —
  nothing else in the registry is touched.

### Core Radar tab
- 500 ms sampling of per-logical-CPU counters: **% Interrupt Time, % DPC Time,
  Interrupts/sec, DPCs Queued/sec** (PerformanceCounter, `Processor` category).
- Heatmap grid (green → red), four per-core line charts, big readable numbers.
- **ETW latency mode** (toggle): captures rolling 10-second windows with the NT Kernel
  Logger (DPC + ISR + image-load events) to an ETL file, then analyzes the file offline
  with the TraceProcessor engine (`Microsoft.Windows.EventTracing`). Shows per-core
  maximum DPC/ISR time in µs and a table of top offending driver modules.
  The windowed capture-analyze-display loop is used because it is far more reliable
  than a live real-time session under load.
- **Recommend least-used cores**: ranks cores by combined interrupt+DPC load over the
  last 60 s (core 0 excluded by default), highlights them in the grid, and can assign
  them to the currently selected device in one click.

### Profiles tab
- Save the current device → {policy, mask} mappings as named JSON profiles.
- Loading a profile shows a **diff** (current vs. new per device) with checkboxes;
  apply only what you want.

## How it works (the registry mechanism)

Windows stores per-device interrupt policy under:

```
HKLM\SYSTEM\CurrentControlSet\Enum\<device-instance-id>\
    Device Parameters\Interrupt Management\Affinity Policy
```

Values (verified against the WDK documentation, see sources below):

| Value | Type | Meaning |
|---|---|---|
| `DevicePolicy` | REG_DWORD | `IRQ_DEVICE_POLICY`: 0 MachineDefault, 1 AllCloseProcessors, 2 OneCloseProcessor, 3 AllProcessorsInMachine, 4 SpecifiedProcessors, 5 SpreadMessagesAcrossAllProcessors (6 is reserved) |
| `DevicePriority` | REG_DWORD | `IRQ_PRIORITY`: 1 Low, 2 Medium, 3 High, 4 Critical |
| `AssignmentSetOverride` | REG_DWORD / REG_QWORD / REG_BINARY | KAFFINITY mask used when `DevicePolicy` = 4 (SpecifiedProcessors) |

KAFFINITY is a 64-bit little-endian mask within one **processor group** (max 64 logical
processors per group). IRQPilot encodes a single group-0 mask in the smallest documented
form (DWORD, then QWORD). A selection spanning multiple groups has no documented scalar
form, so IRQPilot writes a REG_BINARY array of `GROUP_AFFINITY` records
(8-byte mask + 2-byte group + 6 reserved bytes per record, little-endian) and reads back
all three documented forms plus this record form.

## Why administrator rights are required

- `HKLM\SYSTEM\CurrentControlSet\Enum\...` is only writable by administrators/SYSTEM.
- Disabling/re-enabling devices (CfgMgr32) requires elevation.
- The NT Kernel Logger ETW session requires the `SeSystemProfilePrivilege`, held by admins.

The manifest declares `requireAdministrator`. If the app is somehow started without
elevation, it shows a clear message and exits instead of failing halfway.

## Why a device restart is needed

The PnP manager hands the affinity policy to the driver when the device's resources are
(filtered and) assigned. Values written while the device is running are **not** picked up
until the device is re-enumerated. IRQPilot does a disable → enable cycle via
`CM_Disable_DevNode` / `CM_Enable_DevNode` after asking for confirmation — the device
briefly stops working (network or audio may drop for a second).

**Boot-critical devices are hard-blocked** from restart: anything with a boot-start
driver (`Start=0` in its service key), the `CONFIGFLAG_BOOT_DEVICE` config flag, or a
system class (System, Computer, Processor, ACPI, Chipset). For those, the change applies
on the next reboot and the device shows as *Pending*.

## Backup & restore

Backups live in `%ProgramData%\CorePilot\Backups\<device>.json`, profiles in
`%ProgramData%\CorePilot\Profiles\<name>.json`.

- The **first** time IRQPilot writes to a device, it snapshots that device's existing
  `DevicePolicy` / `DevicePriority` / `AssignmentSetOverride` values (name, type, data).
- **Restore defaults** (per device) or **Restore all** deletes the values IRQPilot wrote
  and re-creates any that existed before with their original type and data.
  Unrelated registry data is never touched.
- Restore needs a device restart (or reboot) to take effect, just like applying.

## Install and run

IRQPilot is a portable WPF application; no system-wide installer is required. It supports
Windows 10 21H2+ / Windows 11 x64 and must run as Administrator.

### Option 1: Build and run from source

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and
   [Git for Windows](https://git-scm.com/download/win).
2. Open PowerShell and run:

```powershell
git clone https://github.com/ananasviktor148/IRQPilot.git
cd IRQPilot
dotnet build .\src\CorePilot\CorePilot.csproj -c Release
dotnet test .\tests\CorePilot.Tests\CorePilot.Tests.csproj -c Release
```

3. Start the app:

```powershell
.\src\CorePilot\bin\Release\net8.0-windows\IRQPilot.exe
```

4. Approve the UAC prompt. The manifest requests Administrator rights because IRQPilot
   writes device settings under HKLM and can restart devices.

### Option 2: Create a portable build

Run this on a machine with the .NET 8 SDK installed:

```powershell
dotnet publish .\src\CorePilot\CorePilot.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o .\publish\IRQPilot
```

Copy the **entire** `publish\IRQPilot` folder to any location or another x64 Windows
machine, then run `IRQPilot.exe` inside it and approve the UAC prompt. The published
copy includes the .NET runtime, so .NET does not need to be installed on the target
machine. Do not move the executable away from its accompanying files.

## Dependencies

- `CommunityToolkit.Mvvm` — MVVM source generators.
- `Microsoft.Windows.EventTracing` — offline ETL analysis (WPA's TraceProcessor engine).
- Everything else (CfgMgr32 / AdvApi32 / Kernel32 interop, charts) is hand-rolled:
  the charts are a small custom WPF canvas control, no charting library.

## Notes & limitations

- Only one NT Kernel Logger session can exist system-wide. If WPR, xperf, or Process
  Explorer is tracing, ETW mode tells you to close it.
- If Windows performance counters are corrupted (`lodctr /R` fixes), Core Radar reports
  the problem instead of crashing.
- Process-affinity pinning (a fourth tab) was intentionally left out of this build —
  the registry policy path above is the correct mechanism for interrupt latency work.
- Devices whose written setting hasn't been activated yet show **Pending restart**.

## License

IRQPilot is available under the [MIT License](LICENSE).

## Sources (verified during implementation)

- Interrupt Affinity (registry values, KAFFINITY):
  https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/interrupt-affinity-and-priority
- IRQ_DEVICE_POLICY enumeration values:
  https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/ne-wdm-_irq_device_policy
- CM_Disable_DevNode / CM_Enable_DevNode:
  https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_disable_devnode
- TraceProcessor (Microsoft.Windows.EventTracing):
  https://learn.microsoft.com/en-us/windows/apps/trace-processing/overview
