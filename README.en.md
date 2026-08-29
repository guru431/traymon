# TrayMon

Machine load and temperatures as numbers on icons in the Windows notification area — no
windows, no graphs. Written to replace HWiNFO64 + AIDA64, which together were eating about
30 % of a core on one server purely for sitting open.

*(Русская версия: [README.md](README.md). The program's own interface is in Russian.)*

| | CPU | Private RAM |
|---|---|---|
| **TrayMon** (17 icons) | **0.52 %** of a core | 48 MB |
| HWiNFO64 | 15.8 % | 19 MB |
| AIDA64 | 13.7 % | 25 MB |

Measured over 120 seconds in one sitting on the same machine. TrayMon loses on memory —
that is the price of the .NET runtime; on CPU it is roughly 55 times cheaper.

**That is the entire product.** Everything below follows from it: the polling intervals, the
deliberately coarse numbers, the choice of NVML over a sensor library for the GPU. A change
that costs measurably more CPU is not an improvement here, it is a regression.

## What it shows

One icon, one number. The plate colour identifies the metric, so icons are told apart
without reading them. No two plates are alike, except disks behind a RAID controller, which
deliberately share the colour of NVMe drives:

| Icon | Value | Plate | Yellow / red at |
|---|---|---|---|
| CPU | load, % | violet | 70 / 85 % |
| °C | CPU package temperature | muted violet | 75 / 85 °C |
| RAM | used, GB | teal | 88 / 95 % of capacity |
| GPU ×N | core load, % | maroon | 75 / 90 % |
| VRAM ×N | video memory used, GB | green | 75 / 90 % of capacity |
| °C ×N | GPU temperature | blue | 80 / 90 °C |
| rpm ×N | graphics card fan | dark magenta | standstill (0) |
| °C ×N | temperature of each NVMe | orange | 60 / 70 °C |
| °C ×N | temperature of each disk behind RAID | orange | 55 / 65 °C, or a bad SMART verdict |
| rpm ×N | speed of each motherboard fan | magenta | standstill (0) |
| NET ×N | throughput per adapter, Mbit/s (in + out) | azure | 70 / 90 % of the link |
| C: D: … | volume throughput, MB/s (read + write) | brown | never highlighted |
| GB ×N | free space per volume, GB | olive | 85 / 95 % used |
| UPS | UPS battery charge, % | steel | 50 / 25 % charge, or running on battery |
| h | uptime since last reboot | slate | never highlighted |
| ! | worst state across all icons | dark cyan | 70 / 90 arbitrary units |

Each icon has its own tooltip with the detail: gigabytes and percent for RAM, read and write
separately plus the three heaviest I/O processes for a volume, exact rpm and duty for a fan,
in and out separately for the network in both megabits and megabytes plus the percentage of
the link. All of them carry the minimum, mean and maximum of the last five minutes.

**An icon goes grey and shows "—" when its source falls silent,** and the tooltip says why:
the adapter disappeared, administrator rights are needed, the SNMP agent is not answering.
A frozen last value is the worst kind of failure for a monitor: "network 0 Mbit/s" and
"the cable is unplugged" would look identical, and only one of them is true.

The numbers are coarse on purpose — fan speeds in thousands (`0.6` means 586 rpm), volume
throughput, free space and network in whole units. Every changed digit costs a repaint and a
call into the shell: five icons flickering through decimals measured at +0.66 % of a core,
three times the price of collecting the data itself. Decimals live in the tooltip.

**The network is counted in megabits, not megabytes.** Megabits are the unit a link is rated
in (Wi-Fi connects at 1200 Mbit/s, a port at 2.5 Gbit/s) and the only unit in which an
ordinary working day is visible at all: background chatter from a browser and a mail client
is 30 KB/s — that is `0` MB/s in whole numbers, but a visible figure in megabits.

**Fans, the UPS and free space have inverted severity:** a stopped fan is alarming, not a
fast one; a low charge, not a high one; little free space, not a lot. Going onto battery
turns the plate red at any charge and raises a notification.

**Alarms are not encoded in colour alone.** A yellow plate grows a triangular notch in its
top-right corner, a red one gets two, top and bottom. Colour cannot be told apart under
deuteranopia or on a bad panel; shape can.

## Settings

Right-click any icon: background colour, digit colour (light, dark, or automatic by plate
brightness), rename, thresholds, highlight on/off, hide, the full list of icons grouped and
in a stable order, "poll now", start with Windows, desktop shortcut, open and re-read the
settings file, a summary window (also on left click), a diagnostics window, about, uninstall,
exit.

**The first run shows four or five icons** — CPU, RAM, CPU temperature and the network. The
rest are switched on from the menu: seventeen icons at once would go straight into the
Windows 11 overflow, and the menu lives on the icons.

Everything is written to `TrayMon.json` next to the executable:

```json
{
  "Icons": {
    "cpu":  { "Enabled": true, "Color": "#1C5CA8" },
    "ram":  { "Enabled": true, "Color": "#408080", "Warn": 88, "Crit": 95 },
    "gpu.0":{ "Enabled": true, "Color": "#E8E8E8", "Ink": "dark" }
  },
  "Ups":   { "Host": "127.0.0.1", "Port": 161, "Community": "public", "TimeoutMs": 1500 },
  "Net":   { "NotPhysical": ["Loopback", "vEthernet", "Virtual"] },
  "Tools": { "Smartctl": "C:\\Program Files\\smartmontools\\bin\\smartctl.exe" },
  "Log":   { "Enabled": false, "EverySeconds": 60, "Path": "" }
}
```

Icon ids: `cpu`, `ram`, `cpu.temp`, `gpu.<card>`, `vram.<card>`, `gpu.temp.<card>`,
`fan.gpu.<card>`, `disk.<n>`, `disk.raid.<serial>`, `fan.<sensor name>`,
`net.<adapter description>`, `vol.<drive letter>`, `free.<drive letter>`, `ups`, `uptime`,
`worst`.

**The file is read only at startup, and any change made from the menu rewrites it whole.**
After editing it by hand, choose "Перечитать настройки" (re-read settings), or the edit is
lost on the next click in the menu. **Polling intervals are deliberately not configurable** —
they are what buys the number this program exists for.

You cannot hide every icon, from the menu or through the file: the menu lives on them, and
without the last one there would be no way even to quit.

## Where the data comes from

| Metric | Source | Period | Cost |
|---|---|---|---|
| CPU load | PDH: `\Hyper-V Hypervisor Logical Processor(_Total)` or `\Processor Information(_Total)` | 2 s | 3 ms |
| RAM | `GlobalMemoryStatusEx` | 2 s | 1 ms |
| GPU load, memory, temperature, fan | `nvml.dll` (P/Invoke), every card | 4 s | 4 ms per card |
| CPU temperature, fans | LibreHardwareMonitorLib, on a background thread | 6 s | 20–45 ms |
| Network per adapter, volume throughput | PDH wildcards | 6 s | 6 ms |
| Heaviest I/O processes | PDH: `\Process(*)\IO Data Bytes/sec` | 12 s | 12 ms |
| NVMe temperatures | LibreHardwareMonitorLib, SMART, background | 62 s | 6 ms |
| Free space | `GetDiskFreeSpaceEx` | 300 s | 2 ms |
| Uptime | PDH: `\System\System Up Time`, in a query of its own | 300 s | 3 ms |
| Temperature and health of disks behind RAID | `smartctl.exe`, background | 600 s | ~90 ms per disk |
| UPS charge, runtime, load | SNMP, PowerNet MIB, hand-rolled over UDP, background | 30 s | 17 ms |

Anything costing more than a few milliseconds is read in a `Task.Run` behind a flag that
stops a slow answer from overlapping the next tick. The periods share no common divisors on
purpose: two schedules with one meet on the same tick *always*, not occasionally.

Uptime sits in a PDH query of its own, which is not pedantry: one collect gathers **every**
object the query mentions, and the `System` object carries the process and thread counters —
so reading it walks the process table. Sharing the main query cost 5.7 ms per tick instead of
1.8, three times the price, for a number that moves by two seconds per tick.

Three decisions this was written for:

**CPU is read from the hypervisor counter when there is one.** On a Hyper-V host
`\Processor Information` only sees the root partition: it read 13 % while the hardware was
actually 65 % busy.

**The GPU goes through NVML, not through the sensor library.** One GPU update in
LibreHardwareMonitor costs 68 ms against 4 ms in NVML, so `IsGpuEnabled = false` is assigned
explicitly, with a comment, rather than left to the default.

**Disks behind a RAID controller** are exposed by no Windows API — an array is one device to
the system. `smartctl` from [smartmontools](https://www.smartmontools.org/) reaches them over
CSMI. Devices are discovered automatically and deduplicated by serial number. The same query
brings back the SMART health verdict: a failing disk matters more than a warm one.

**The UPS is asked over SNMP rather than Windows.** An old Smart-UPS on RS-232 is invisible
to the system — no `Win32_Battery`, no battery-class device. Only the service holding the COM
port has the readings, and it can republish them over SNMP. The reply is treated as hostile
input: Windows has no privileged ports, so any process can occupy `127.0.0.1:161` while the
real service is stopped. The PDU type, request id and community are all checked, and every
length is bounded by the packet.

## Icon order

Every icon registers through `Shell_NotifyIcon` with its own `guidItem` rather than through
WinForms `NotifyIcon`. Without a GUID the Windows 11 tray treats every icon of a process as
one group and drags them together. With one, each icon keeps the place the user put it.

A metric always gets the same GUID, and families that come in numbers (adapters, volumes,
disks, fans, cards) take one from their own pool keyed by the name of the source — never by
position in a list, which used to move the moment a USB stick added a drive letter. A slot is
released only after its source has been silent for a day.

**Two copies must not run at once, and the program prevents it.** A GUID belongs to the icon,
not the process: a second instance would take the icons away from the first.

**Do not change or renumber the GUIDs in `Program.cs`.** Windows keeps the icon's position
against the pair "path to exe + GUID". A restart of `explorer.exe` is handled: the shell
broadcasts `TaskbarCreated` and the icons register again.

## Installing

There is no installer: one executable plus a few DLLs.

**Put it in `%ProgramFiles%\TrayMon`, not in a profile folder.** Autostart is a scheduled
task running elevated with no UAC prompt, so a folder an ordinary user can write to would
mean any process of that user could replace the executable and inherit those rights. The
program checks the folder permissions and refuses to create the task from a folder writable
by anyone but administrators.

Optional alongside it: `smartctl.exe` and `drivedb.h` from smartmontools, for disks behind a
RAID controller.

## Requirements

- Windows 10 / 11 / Server 2019+ (x64)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Administrator rights** — CPU and NVMe temperatures come from a kernel sensor driver.
  Everything else works without elevation; the temperature icons go grey and say so
- optional: `smartctl.exe`, an NVIDIA driver, a UPS with an SNMP agent

## Building and checking

```powershell
dotnet publish src/TrayMon.csproj -c Release -o out
out\TrayMon.exe --once            # from an ELEVATED console
out\TrayMon.exe --once --icons    # plus one tick of the icon layer
```

The manifest asks for `requireAdministrator`, so `--once` will not start from an ordinary
console (`ERROR_ELEVATION_REQUIRED`). To see the unelevated behaviour, run the build through
the runtime instead: `dotnet out\TrayMon.dll --once`.

There are no automated tests — everything meaningful here is hardware, drivers and the
Windows shell. `--once` prints every value with the cost of each source in milliseconds and
serves as the smoke test — but those are **cold** first calls, with PDH buffers not yet
allocated, so they are not a steady-state measurement and two builds cannot be compared by
them. `--once --icons` additionally runs one real tick of the icon layer
without registering anything in the tray and prints which slot got which GUID, what would be
drawn and what the tooltip says. Redraws and calls into the shell are counted separately,
because they cost more than reading the sensors and `cost, ms` does not cover them.

`--once` output is deliberately anonymised — no absolute paths, no SNMP address, no disk
serial numbers — because people paste it into public issue trackers. The full picture is in
the "Диагностика…" (diagnostics) menu item, which stays on the machine.

## Uninstalling

The **"Удалить TrayMon…"** menu item removes the scheduled task, the desktop shortcut and the
tray positions Windows remembers, and asks whether to delete `TrayMon.json`. After that,
delete the executable and its folder. Deleting the executable alone is not enough — the task
would remain, launching a file that no longer exists at every logon.

## Limitations

- Windows and x64 only
- Limits: 4 graphics cards, 4 NVMe, 4 disks behind RAID, 8 fans, 8 volumes, 8 free-space
  icons, 8 network adapters — one per prepared GUID. Anything beyond that gets no icon, and
  the diagnostics window says so
- The I/O figures in a volume tooltip are across all devices, not that volume: splitting them
  needs a kernel trace costing 5–10 % of a core, which is the entire budget of this program
- RAID array state itself is not checked — only the temperature and SMART verdict of each disk
- AMD and Intel graphics are not supported: LibreHardwareMonitor costs 68 ms per GPU poll
  against 4 ms, and a hand-written P/Invoke to ADL and IGCL has not been done

## Licence

MIT — see [LICENSE](LICENSE).

Uses [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
(MPL 2.0) for temperatures and fans.
