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

Measured over 120 seconds in one sitting on the same machine, all three with 17 icons enabled —
the number of icons moves the price more than anything in the code. The limits of those figures
are worth stating: they are each **process's** own `Process.TotalProcessorTime` on one machine
with one set of icons; "55 times" is the ratio of the two competitors' sum to TrayMon, not an
advantage over either one (that is about 30 and 26); and the tray repaint inside `explorer.exe`
is missing from all three columns, which does **not** mean it is the same for all three — how
often an icon repaints is exactly what differs. Comparing that needs its own measurement, the
delta of `explorer.exe`'s own processor time over the same window. TrayMon loses on memory —
that is the price of the .NET runtime.

**That is the entire product.** Everything below follows from it: the polling intervals, the
deliberately coarse numbers, the choice of NVML over a sensor library for the GPU. A change that
costs measurably more CPU is not an improvement here, it is a regression. The program reports its
own cost: "Диагностика…" (diagnostics) shows total processor time — the only line there that
measures CPU — plus the *duration* of a tick and of the background polls, which are stopwatch
readings and include waiting on a socket, on an external process and on a lock.

## What it shows

One icon, one number. The plate colour identifies the metric, so icons are told apart
without reading them. No two plates are alike, except disks behind a RAID controller, which
deliberately share the colour of directly attached disks:

| Icon | Value | Plate | Yellow / red at |
|---|---|---|---|
| CPU | load, % | violet | 70 / 85 % |
| °C | CPU package temperature | muted violet | 75 / 85 °C |
| RAM | used, GB | teal | 88 / 95 % of capacity |
| GPU ×N | core load, % | maroon | 75 / 90 % |
| VRAM ×N | video memory used, GB | green | 75 / 90 % of capacity |
| °C ×N | GPU temperature | blue | 80 / 90 °C |
| rpm ×N | graphics card fan (slowest of its fans) | dark magenta | standstill (0) |
| °C ×N | temperature of each disk | orange | 60 / 70 °C, or NVMe wear exhausted |
| °C ×N | temperature of each disk behind RAID | orange | 55 / 65 °C, or a bad SMART verdict |
| rpm ×N | speed of each motherboard fan | magenta | standstill (0) |
| NET ×N | throughput per adapter, Mbit/s (in + out; Gbit/s past 10000, unit in the caption) | azure | 70 / 90 % of the link |
| C: D: … | volume throughput, MB/s (read + write; GB/s past 10000) | brown | never highlighted |
| GB ×N | free space per volume, GB (TB past 1000 GB, unit in the caption) | olive | 85 / 95 % used, or less than a configured number of GB left |
| UPS | UPS battery charge, % | steel | 50 / 25 % charge, on battery, bypass, AVR or a worn battery |
| BAT | laptop battery charge, % | moss | 40 / 15 % charge while on battery |
| h | uptime since last reboot | slate | never highlighted |
| ! | worst state across all icons | dark cyan | 78 / 100 arbitrary units |

The summary scale is built so that **78 is any metric's own yellow threshold and 100 is its own
red**, whatever those thresholds are set to. The number is in those arbitrary units, not a
percentage of anything; the colour is taken straight from the worst state among the other icons,
so the summary plate is always exactly the worst plate in the tray. Above 100 the number says by
how much the red line has been passed. With nothing measurable to rank, the icon goes grey — "no
alarms" over no data would not be true — and a source marked as expected that has fallen silent
is counted in its tooltip.

Each icon has its own tooltip with the detail: gigabytes, percent and commit charge for RAM,
read and write separately plus the three heaviest I/O processes for a volume, exact rpm and duty
for a fan, in and out separately for the network in both megabits and megabytes plus the
percentage of the link. The minimum, mean and maximum of the last five minutes are shown where
they mean something (loads, temperatures, fan speeds, network, free space); uptime, the UPS and
the summary icon have no such line.

The network gets one icon per physical adapter, captioned with its description. PDH writes that
description with square brackets — `Intel[R] Wi-Fi 6E AX211 160MHz` — and the id in
`TrayMon.json` matches. Virtual switches, tunnels and Bluetooth do not count. An empty Ethernet
socket next to a working Wi-Fi gets no icon at all. Link utilisation is the busier direction,
not the sum of both: a link is full duplex, so 45 % each way is a quiet line, not 90 %.

**An icon goes grey and shows "—" when its source falls silent,** and the tooltip says why and
what it last showed: "no link — cable unplugged, last 12 at 14:20", "administrator rights are
needed", "the agent answers but carries no battery-charge OID". "The adapter is gone" and "there
is no link" are different answers and are told apart. A frozen last value is the worst kind of
failure for a monitor: "network 0 Mbit/s" and "the cable is unplugged" would look identical, and
only one of them is true.

**That covers a stuck poll, not only a device that went away.** Every background measurement is
published with the moment it was taken, and a snapshot older than two periods does not count as
alive: the icon goes grey saying "опрос не завершился — данные устарели на N с". Without that a
hung reader looked exactly like a working one — the flag that stops a slow answer from overlapping
the next one also stops new attempts being made. The diagnostics window lists the age of every
source and when it is due again.

The numbers are coarse on purpose — fan speeds in thousands (`0.6` means 586 rpm), volume
throughput, free space and network in whole units. Every changed digit costs a repaint and a
call into the shell: five icons flickering through decimals measured at +0.66 % of a core,
three times the price of collecting the data itself. Decimals live in the tooltip. The icon and
the tooltip round the same way (away from zero), or the same tick would draw `50` on a plate
whose tooltip said `51 %`.

**The network is counted in megabits, not megabytes.** Megabits are the unit a link is rated
in (Wi-Fi connects at 1200 Mbit/s, a port at 2.5 Gbit/s) and the only unit in which background
load is visible at all — exactly eight times more sensitive. In whole numbers `0` ends at
62 KB/s instead of 500 KB/s: browser and mail chatter (30 KB/s) is invisible in both, but
anything above a trickle shows up in megabits. Megabits are decimal (10⁶ bits), like the link
speed, so a gigabit port prints as `1000` rather than `954`.

**Plate colour has hysteresis:** an alarm is raised immediately and cleared only once the value
has fallen 3 % of the scale below the threshold. Otherwise a CPU hovering around 70 % against a
threshold of 70 repaints the icon every tick — and a repaint plus a call into the shell is the
most expensive thing this program does. **The level is decided in one place** and used for the
plate, the balloon, the event-log entry and the summary icon alike: there used to be three, so
85 → 82.9 against 70/85 left the icon red while the summary turned yellow, and a value
oscillating around a threshold wrote "entered the red" into the journal again and again for an
icon that never left it.

**Fans, the UPS, the battery and free space have inverted severity:** a stopped fan is alarming,
not a fast one; a low charge, not a high one; little free space, not a lot. Going onto battery
turns the plate red at any charge. The plate also turns yellow on bypass, while AVR is
trimming or boosting the mains, and when the UPS asks for a new battery; it turns red when
less than five minutes of runtime are left, whatever the charge gauge claims — and when the agent
carries no charge OID at all, the icon shows "—" and still raises the alarm. The threshold dialog
for such metrics asks for and shows **charge**, not the internal severity. A laptop battery's
charge thresholds only apply while it is discharging: on the mains there is no highlight.

**What a stopped fan means is a choice** ("При остановке"): any standstill is an alarm (the
default), a legitimate zero-rpm mode is allowed (red only when the fan stands still while its
duty cycle is being driven), or watch without alarming. A duty cycle is not proof of rotation, so
a GPU fan whose driver reports no rpm raises no standstill alarm at all, and says so.

**Transitions into the red raise a notification and are written to the Windows Application
log** (source TrayMon) — by default for disks, RAID disks, fans, free space and the UPS. A balloon
needs somebody in front of the screen; a server's existing monitoring reads the event log without
one. It can be switched on or off per icon, and **the UPS going onto battery now obeys that same
checkbox** — it used to take a path of its own and ignore it. A state already present at startup
is reported too: a RAID disk already failing, a volume already full, a UPS already on battery.
The last 50 colour changes are listed in the diagnostics window.

**A source can be marked as expected** ("Источник обязателен"): losing and regaining its data is
then an event of its own, and the summary icon counts such gaps. Everything is optional by
default — a fresh machine must not raise an alarm for every technology it does not have.

**Alarms are not encoded in colour alone.** A yellow plate grows a triangular notch in its
top-right corner, a red one gets two, top and bottom. Colour cannot be told apart under
deuteranopia or on a bad panel; shape can. The notches follow the state, not the plate's RGB:
picking the warning colour by hand used to give a healthy metric the alarm shape.

## Settings

Right-click any icon. The root menu holds the frequent things — "Настроить этот значок ▸"
(background colour, digit colour, rename, thresholds, a gigabytes-left threshold for free space,
the standstill policy for fans, highlight on/off, notify on red, mark as expected), hide this
icon, the full list of icons grouped and in a stable order, a summary window (also on left click;
on the summary icon it is ordered by severity), "poll now" — and everything about the installation
lives in "Программа ▸": start with Windows, desktop shortcut, open and re-read the settings file,
diagnostics, about, uninstall. Then "Выход".

Thresholds are not offered where there is nothing to set: fans alarm only on standing still,
volumes and uptime never alarm at all. Switching the highlight off keeps whatever thresholds
were configured. "Poll now" also rediscovers the disks behind the controller and restores SNMP
OIDs the agent once refused.

**Threshold highlighting overrides the chosen plate colour.** While a value is above the warning
threshold the icon is yellow or red whatever colour was picked; the program says so when a
colour is chosen for an icon that is currently highlighted.

**The first run shows two to four icons** — CPU and RAM, plus GPU and VRAM on a machine with an
NVIDIA card. The rest are switched on from the menu: seventeen icons at once would go straight
into the Windows 11 overflow, and the menu lives on the icons.

**While the session is locked or the RDP session is disconnected, drawing slows from 2 s to
30 s.** Nobody can see the icons. The **measurement** schedules do not change with it — they are
in seconds, not in ticks — so the UPS is still asked every 30 s and the RAID disks every ten
minutes, and the Windows event-log entries still arrive on time. Those exist precisely for when
nobody is watching. Returning to the session refreshes everything at once.

Everything is written to `TrayMon.json` next to the executable:

```json
{
  "TickMs": 2000,
  "Icons": {
    "cpu":  { "Enabled": true, "Color": "#1C5CA8" },
    "ram":  { "Enabled": true, "Color": "#408080", "Warn": 88, "Crit": 95 },
    "gpu.8f3a1c27": { "Enabled": true, "Color": "#E8E8E8", "Ink": "dark" },
    "fan.Nuvoton NCT6796D/Fan #1": { "Enabled": true, "Label": "CPU fan", "Alerts": false },
    "fan.Nuvoton NCT6796D/Fan #3": { "Enabled": true, "Stall": "zero-ok" },
    "free.C:": { "Enabled": true, "WarnGb": 40, "CritGb": 10 },
    "ups":  { "Enabled": true, "Required": true }
  },
  "Ups":     { "Host": "127.0.0.1", "Port": 161, "Community": "public", "TimeoutMs": 1500 },
  "Net":     { "NotPhysical": ["Loopback", "isatap", "Teredo", "vEthernet", "Virtual",
                               "Pseudo", "Npcap", "WAN Miniport", "Bluetooth", "QoS", "Filter"],
               "Include": ["vEthernet (LAN)"],
               "Bandwidth": { "Wi-Fi": 1200 } },
  "Tools":   { "Smartctl": "C:\\Program Files\\smartmontools\\bin\\smartctl.exe",
               "SmartctlScan": "--scan-open -d csmi" },
  "Sensors": { "UseSensorDriver": true },
  "Log":     { "Enabled": false, "EverySeconds": 60, "MaxMb": 32, "Path": "" },
  "Slots":   { "net": { "Realtek Gaming 2.5GbE Family Controller": "6f2a1c40-…-7c9e5b000008" } }
}
```

Icon ids: `cpu`, `ram`, `cpu.temp`, `gpu.<card key>`, `vram.<card key>`, `gpu.temp.<card key>`,
`fan.gpu.<card key>`, `disk.<model>` (identical models get a `#2`, `#3` suffix in serial-number
order), `disk.raid.<serial>` — or `disk.raid.<index>` when the disk reports no serial number —
`fan.<chip>/<sensor name>`, `net.<adapter description>`, `vol.<drive letter>`,
`free.<drive letter>`, `ups`, `battery`, `uptime`, `worst`.

**A card key is the last eight characters of its NVML UUID**, not its index: the index moved when
a card was added, removed or swapped, and the settings then belonged to a different card. A fan key
always includes the chip — it used to be the bare name while that name was unique, and flipped
back and forth as a second SuperIO chip appeared and went. Older ids (`gpu.0`, `fan.Fan #1`) are
carried over automatically on the first run of this version.

Per-icon fields: `Enabled`, `Color`, `Label`, `Ink`, `Warn`, `Crit`, `Alerts`, `NotifyOnCritical`,
`Required`, `Stall` (`zero-ok` or `off`), `WarnGb` / `CritGb` (free space only). `Alerts: false`
switches threshold colouring off without touching the thresholds. Unusable values (`NaN`,
`Infinity`, a red threshold on the wrong side of the yellow one) are dropped on load and reported
in the diagnostics window; such a pair used to load in silence and paint a red plate the summary
icon refused to rank. That is a **note, not a refusal to read the file** — everything else in it
still applies, and the "could not be read" dialog is reserved for a file that really did not parse.

**Files from older versions are migrated, not rejected.** Before `Alerts` existed, a switched-off
highlight was written as `"Warn": 1000000000, "Crit": 1000000000`, and such files are still around.
That pair is an instruction, not junk: it is read as `"Alerts": false` and the file is rewritten in
the current spelling on the next save. Dropping it would switch the highlight back on for icons
somebody had deliberately silenced.

- `TickMs` — the **drawing** interval, upwards only: anything below 2000 is refused. Measurement
  periods are separate and in seconds, so raising it makes the program cheaper without pushing
  detection of a real problem beyond one tick.
- `Net.NotPhysical` — **a custom list replaces the built-in one entirely**, which is why it is
  spelled out in full above. `Net.Include` is the way to keep a VPN or a virtual adapter you do
  watch without rewriting that list; `Net.Bandwidth` gives the percentage a figure to divide by
  where the driver leaves `Current Bandwidth` empty or where a Wi-Fi link rate is not a capacity.
  With no link speed the utilisation is not invented: there is no threshold colour and the tooltip
  says the link speed is unknown.
- `Tools.Smartctl` — a relative path is resolved against the program folder, never against the
  working directory. It is refused only in the case where refusing buys something: **the program
  folder is protected and the configured tool is somewhere weaker** — a settings file leading an
  elevated process out of a safe folder. When the program folder itself is writable by a
  non-administrator, smartctl runs as usual and the diagnostics window says so: TrayMon.exe and
  its DLLs sitting beside it can be replaced just as easily, so blocking one tool would change
  nothing about the risk while silently costing the RAID temperatures.
- `Sensors.UseSensorDriver` — `false` keeps LibreHardwareMonitor, and the WinRing0 driver it
  loads, unopened. CPU temperature, motherboard fans and NVMe wear then have no source and say so.
- `Log` — `time;id;value;unit;severity;status;measured_at`, one row per icon. The raw number in a
  fixed unit, not the text from the plate (which changes unit by itself: GB→TB, hours→days), ids
  quoted, timestamps with a UTC offset. **Floor** of 30 s, not a ceiling; the file rolls over to
  `.1` at `MaxMb`; the write happens on a background thread, because on a network share it used to
  block the whole poll.
- `Slots` — which tray identity each source holds. Written by the program: without it, a restart
  with one device absent handed its GUID, its tray position and its visibility to the next device.
- `Ups.Community` — `public` is rarely the right answer; set your own.

**An edit to the file is picked up automatically** on the first poll after the file stops
changing (a few seconds at the built-in interval, longer at a raised one); "Перечитать настройки"
does it at once. The UPS address, the smartctl path, the adapter filter, `TickMs` and the sensor
profile only take effect on restart. A change from the menu rewrites the file whole — but re-reads
it first when it was edited from outside, so an external edit is not overwritten. Auto-reload never
moves an unreadable file aside: `.bad` is only made at startup, and an existing `.bad` is kept.

You cannot hide every icon, from the menu or through the file: the menu lives on them, and
without the last one there would be no way even to quit.

## Where the data comes from

| Metric | Source | Period | Cost |
|---|---|---|---|
| CPU load | PDH: `\Hyper-V Hypervisor Logical Processor(_Total)` or `\Processor Information(_Total)` | 2 s | 3 ms |
| RAM and commit charge | `GlobalMemoryStatusEx` | 2 s | 1 ms |
| Laptop battery | `GetSystemPowerStatus` | 2 s | &lt; 0.1 ms |
| Uptime | `GetTickCount64` | 2 s | 0 ms |
| GPU load, memory, temperature, fan | `nvml.dll` (P/Invoke), every card | 4 s | 4 ms per card |
| CPU temperature, fans | LibreHardwareMonitorLib, on a background thread | 6 s | 20–45 ms |
| Network per adapter, volume throughput | PDH wildcards | 6 s | 6 ms |
| Heaviest I/O processes | PDH: `\Process(*)\IO Data Bytes/sec`, background | 12 s | 12 ms |
| Disk temperatures | `IOCTL_STORAGE_QUERY_PROPERTY`, background | 62 s | &lt; 1 ms per disk |
| NVMe wear and spare | LibreHardwareMonitorLib, SMART, background | 62 s | 6 ms |
| Free space | `GetDiskFreeSpaceEx`, background | 300 s | 2 ms |
| Temperature and health of disks behind RAID | `smartctl.exe`, background | 600 s | ~90 ms per disk |
| UPS charge, runtime, load | SNMP, PowerNet MIB, hand-rolled over UDP, background | 30 s | 17 ms |

Anything costing more than a few milliseconds is read in a `Task.Run` behind a flag that stops
a slow answer from overlapping the next one, and those pool threads run in background mode, so
they compete with the real work of the machine for neither CPU nor I/O.

**The periods above are wall-clock, not multiples of the tick.** While they were multiples, a
`TickMs` of 30000 — or a locked session — turned the UPS's "30 s" into 450 and the RAID "600 s"
into two and a half hours, taking the event-log entries with them. A source is now polled on the
first tick after its deadline.

A rule used to be documented here: "if two periods share a divisor, their phases must differ
modulo it". It was wrong — a collision happens exactly when the phases agree modulo the *gcd* —
and its own table broke it, since 31 and 3 are coprime. What actually matters is not arithmetic:
no heavy read happens on the UI thread at all, the sensor library cannot be entered from two
threads (its own lock), and a slow answer cannot pile up on the next one (the running flags). Two
background polls overlapping is allowed and costs the UI thread nothing.

A source that is not there — the SNMP agent defaults to the loopback, `smartctl.exe` may never
have been copied — is polled every five minutes instead of every thirty seconds after five
failures in a row. "Опросить датчики сейчас" (poll now) resets that immediately.

Uptime used to be `\System\System Up Time` and cost 5.7 ms per tick against 1.8, because one
collect gathers **every** object the query mentions and the `System` object carries the process
and thread counters. `GetTickCount64` answers the same question for nothing. The lesson stands —
look at the counter's object, not at the counter — and `\Process(*)` is the live illustration:
it really is needed, and it lives in a query of its own on a slow schedule.

Four decisions this was written for:

**CPU is read from the hypervisor counter when there is one.** On a Hyper-V host
`\Processor Information` only sees the root partition: it read 13 % while the hardware was
actually 65 % busy.

**The GPU goes through NVML, not through the sensor library.** One GPU update in
LibreHardwareMonitor costs 68 ms against 4 ms in NVML, so `IsGpuEnabled = false` is assigned
explicitly, with a comment, rather than left to the default.

**Disk temperature comes from the storage driver, not from the sensor driver.**
`IOCTL_STORAGE_QUERY_PROPERTY` with `StorageDeviceTemperatureProperty` (52, not 77 — one version
had the wrong id here and the query failed on every driver) asks the same question through the
ordinary storage stack: no kernel driver and **no elevation** — the device is opened with zero
desired access, which is all a property query needs. NVMe wear and spare are an NVMe log page and
still come from the sensor library, matched up by model — but only unambiguously: where two disks
fit one description, neither is given a wear figure. Disks only the library can see do not vanish
from the list when the driver answers for others. Without elevation the wear figures are simply
absent while the temperatures remain.

**Disks behind a RAID controller** are exposed by no Windows API — an array is one device to
the system. `smartctl` from [smartmontools](https://www.smartmontools.org/) reaches them over
CSMI (configurable: LSI and Adaptec need a different `-d`). Its **JSON** output is parsed rather
than its human-readable table: Samsung and some Intel SATA SSDs report temperature in attribute
190 rather than 194, NVMe and SCSI answers have no attribute table at all, and a row saying
`FAILING_NOW` broke the pattern exactly as the disk started to fail. Sleeping drives are not
woken (`-n standby,0`). Devices are deduplicated by serial number and the device list is
rediscovered hourly and on "poll now" — one healthy old disk used to hold the old list for ever.
The same query brings back the SMART health verdict: a failing disk matters more than a warm one,
so a disk **with no temperature but a bad verdict** gets an icon too. It used to be dropped from
the list before its health was even looked at.

**The UPS is asked over SNMP rather than Windows.** An old Smart-UPS on RS-232 is invisible
to the system — no `Win32_Battery`, no battery-class device. Only the service holding the COM
port has the readings, and it can republish them over SNMP. Note that APC NMC firmware 6.x and
later, and PowerChute Business Edition 10 and later, ship with **SNMPv1 disabled** — it has to
be enabled on the card. A five-OID GET is all-or-nothing, so an agent missing one of them
answers `noSuchName` and returns nothing at all; the program reads the error index, drops the
named OID and retries, remembering the working set. **Only `noSuchName` drops a varbind** — any
other error is the agent having a bad moment, and treating those the same way could cost the charge
or the power status for the rest of the session. "Poll now" asks for the full set again. Accepted
numbers are range-checked: a charge of −1 or 200 is not a charge.

The reply is treated as hostile input: Windows has no privileged ports, so any process can
occupy `127.0.0.1:161` while the real service is stopped. It is worth being precise about what
that buys. Such a process receives the request itself, community and request id included, so
checking the PDU type, the id and the community keeps out *other* datagrams — stale replies and
stray traffic — not a squatter. Against a squatter the defence is that there is nothing to
attack: every length is bounded by the packet, the parser cannot be hung or crashed, and the
worst a forgery can do is lie about the battery.

## Icon order

Every icon registers through `Shell_NotifyIcon` with its own `guidItem` rather than through
WinForms `NotifyIcon`. Without a GUID the Windows 11 tray treats every icon of a process as
one group and drags them together. With one, each icon keeps the place the user put it.

A metric always gets the same GUID, and families that come in numbers (adapters, volumes,
disks, fans, cards) take one from their own pool keyed by the name of the source — never by
position in a list, which used to move the moment a USB stick added a drive letter. **The
assignment is remembered in `TrayMon.json`** (`Slots`): the table used to be rebuilt at every
start in the order sources first answered, so a restart without device A gave A's GUID, its tray
position and its visibility to device B. A slot is released only after its source has been silent
for a day — and never if it is the last icon the menu lives on.

If the shell refuses an icon with a GUID — usually because of an entry left over from an
earlier location of the executable — the program removes the stale entry and retries, and after
several refusals in a row falls back to registering by window handle. Icons then group together
again and lose their positions; they keep working. `ERROR_TIMEOUT` is handled separately: the
shell was merely busy and the icon may even have been added, so it is not deleted — the program
tries `NIM_MODIFY` and retries later. Without that, two timeouts during logon, which is exactly
when `explorer.exe` is busiest, cost the icons their GUIDs for the whole session.

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
program checks the folder permissions **and those of every folder above it** — renaming
`D:\Tools\TrayMon` out of the way is no harder than replacing a file inside it — **and the files
themselves**: the executable, every DLL beside it, `TrayMon.json`, and the `smartctl.exe` that
file can point at. A file inside a well-protected folder can carry its own permissive ACL, and
the external tool is started with our token. The task is refused when write access is open to
anyone but administrators — **and when the permissions could not be read at all**, because a check
that silently did not run must not look like one that passed. The folder's owner (who can grant
themselves anything, holding no explicit permission) is reported in the diagnostics window but
does not block anything: on a normally installed machine that owner is the administrator who
created the folder.

Running the assembly through the runtime (`dotnet out\TrayMon.dll`) is a way to see the
unelevated behaviour, not an installation: `Environment.ProcessPath` is then dotnet.exe. With no
`TrayMon.exe` beside the assembly the install actions refuse instead of registering a task for
dotnet.exe without its argument.

Optional alongside it: `smartctl.exe` and `drivedb.h` from smartmontools, for disks behind a
RAID controller.

## Requirements

- Windows 10 / 11 / Server 2016+ (x64)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) or newer — the
  build rolls forward, so a machine with only .NET 10 runs it too
- **Administrator rights** for three things: the CPU temperature and NVMe wear come from a
  kernel sensor driver, the autostart task is created with `HighestAvailable`, and the settings
  file is opened in Notepad from `%ProgramFiles%`. Everything else works without elevation,
  disk temperatures included
- optional: `smartctl.exe`, an NVIDIA driver, a UPS with an SNMP agent

## Building and checking

```powershell
dotnet publish src/TrayMon.csproj -c Release -o out
out\TrayMon.exe --once            # from an ELEVATED console
out\TrayMon.exe --once --icons    # plus one tick of the icon layer
dotnet test tests/TrayMon.Tests/TrayMon.Tests.csproj
```

The manifest asks for `requireAdministrator`, so `--once` will not start from an ordinary
console (`ERROR_ELEVATION_REQUIRED`). To see the unelevated behaviour, run the build through
the runtime instead: `dotnet out\TrayMon.dll --once`.

The code that never touches a driver is covered by tests — the hand-rolled SNMP parser, the GUID
pool and its persistence, the alert level with its hysteresis, the time-based statistics window,
the summary scale, the number formatters, the storage-temperature descriptor, the redactor, CSV
quoting and the settings round-trip against a hostile file. The git secret-guard hooks have their
own regressions in `tools/Test-GitHooks.sh`, driven by a real `git commit -m` in a temporary
repository — the only way one of their bugs was visible at all. Everything else is hardware,
drivers and the Windows shell, and `--once` is
the smoke test for that part: it prints every value with the cost of each source in
milliseconds, **twice**, cold and warm. The cold call — PDH buffers unallocated, instance lists
not warmed — is not a steady-state measurement and two builds cannot be compared by it; the
warm one is a first approximation of the steady state, though it still does not include the
price of the icons.

`--once --icons` additionally runs a real tick of the icon layer without registering anything
in the tray and prints the transitions between ticks, then which slot got which GUID, what would
be drawn and what the tooltip says; `--ticks N` runs several with a real pause between them, so
fading, a source coming back to life and non-garbage rate counters are visible. A non-zero exit
code means the icon layer threw somewhere — the report used to print and return success. The tick
is dearer here than in the running program on purpose: the background reads happen *inside* it, so
that the dry run exercises the same publication path rather than one of its own. Redraws and calls
into the shell are always
zero in that mode — the tray is not touched at all — and the report says so, because 0/0 there
looks exactly like the broken registration those counters exist to catch. Read them in the
diagnostics window of a running program, where about 1.5 calls per tick across the whole tray
is normal.

`--once` output is deliberately anonymised — no absolute paths, no SNMP address, no disk
serial numbers — because people paste it into public issue trackers. The full picture is in
the "Диагностика…" (diagnostics) menu item, which stays on the machine.

## Uninstalling

The **"Удалить TrayMon…"** menu item lists what will go, offers a checkbox for `TrayMon.json` and
two buttons named after the action. (It used to be Yes / No / Cancel, where "No" also uninstalled
— it only meant "keep TrayMon.json".) Only our own things are removed: the scheduled task if it
starts **this** copy, the desktop shortcut if it points at it, and the tray positions keyed by
"path to exe + GUID". Anything belonging to another installation is left alone and the report says
so. After that, delete the executable and its folder. Deleting the executable alone is not enough
— the task would remain, launching a file that no longer exists at every logon.

The autostart checkbox reflects what the scheduler actually holds: the task must be enabled, carry
an enabled logon trigger, belong to this account and start this file. A task with no action, a
disabled one or somebody else's does not tick it, and the item's tooltip says which. The task name
is shared by every installation, so an existing task that starts another file is never overwritten
without asking.

Tray positions live in `HKCU\Control Panel\NotifyIconSettings`, which is a **Windows 11**
mechanism; Windows 10 and Server 2019–2022 keep the same state in an undocumented binary blob
inside `TrayNotify\IconStreams`, which the program does not touch and says so.

## Limitations

- Windows and x64 only
- Three- and four-digit values are drawn in a smaller font. There are no five-digit ones: the
  unit moves up a step and the caption says so — free space in TB past 1000 GB, the network in
  Gbit/s past 10000 Mbit/s, volume throughput in GB/s past 10000 MB/s. A fifth font size exists as
  a backstop; a five-digit number used to be clipped at both ends
- Limits: 4 graphics cards, 4 disks, 4 disks behind RAID, 8 fans, 8 volumes, 8 free-space
  icons, 8 network adapters — one per prepared GUID. Anything beyond that gets no icon, and
  the diagnostics window says so
- The I/O figures in a volume tooltip are across all devices and all kinds of I/O, network
  included — the counter is `IO Data Bytes/sec`. Splitting them by volume needs a kernel trace
  costing 5–10 % of a core, which is the entire budget of this program
- RAID array state itself is not checked — only the temperature and SMART verdict of each disk
- AMD and Intel graphics are not supported: LibreHardwareMonitor costs 68 ms per GPU poll
  against 4 ms, and a hand-written P/Invoke to ADL and IGCL has not been done. AMD *processors*
  are supported on equal terms with Intel
- **The sensor driver is a known risk, and it remains one.** The CPU temperature and NVMe wear
  come from LibreHardwareMonitor 0.9.4, which loads the WinRing0 1.2.0 driver. Microsoft lists
  that driver as vulnerable (CVE-2020-14979: the device is created with no restricting DACL, so
  any process gets MSR, I/O port and physical memory access), so HVCI blocks it on recent
  Windows 11 and Defender may quarantine the file. TrayMon narrows the device's DACL right after
  the driver loads — the diagnostics window says whether that worked — and reports `BLOCKED`
  when the driver is loaded but reads nothing. Only LibreHardwareMonitor ≥ 0.9.5 with PawnIO
  closes this completely; that is a new installation prerequisite and a measurement of its own,
  so it is not done here. Everything else, disk temperatures included, works without the driver —
  and `"Sensors": {"UseSensorDriver": false}` keeps it unloaded altogether, at the cost of the CPU
  temperature, the motherboard fans and the NVMe wear figures

## Licence

MIT — see [LICENSE](LICENSE).

Uses [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
(MPL 2.0) for temperatures and fans.
