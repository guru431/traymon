using System.Runtime.InteropServices;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace TrayMon;

/// <summary>One video card, as NVML reports it.</summary>
public sealed record GpuReading(
	int Index, string Name, double? Load, double? Temp,
	double? FanRpm, double? FanDuty, double? MemLoad, double MemUsedGb, double MemTotalGb);

/// <summary>One disk behind a RAID controller.</summary>
public sealed record RaidDisk(string Name, double Temp, string Serial, string Health);

/// <summary>
/// One directly attached disk: temperature, and the NVMe wear figures when the sensor library
/// could read them. A disk that is running out of spare blocks matters more than a warm one —
/// the same argument that already put the SMART verdict on the RAID icons.
/// </summary>
public sealed record DiskReading(string Name, double Temp, double? WearPercent, double? SparePercent)
{
	/// <summary>Wear or spare capacity past the point where the drive is expected to fail.</summary>
	public bool Worn => WearPercent >= 95 || SparePercent is >= 0 and < 10;
}

/// <summary>Battery of a laptop or tablet, as Windows reports it.</summary>
public sealed class BatteryReading
{
	public double Charge;          // % of capacity
	public bool OnBattery;         // mains unplugged
	public double? MinutesLeft;    // null when Windows will not estimate
}

/// <summary>
/// CPU temperature and fan speeds — everything that comes from the sensor library. Published as
/// one object rather than as separate fields because it is filled on a background thread: a
/// <c>double?</c> is sixteen bytes and its write is not atomic, so a reader could otherwise see
/// the "has value" half of one poll and the number of the previous one.
/// </summary>
public sealed class SlowReading
{
	public static readonly SlowReading Empty = new();

	public double? CpuTemp;
	public List<FanReading> Fans = new();
}

/// <summary>
/// One fan header. <paramref name="Chip"/> is the SuperIO chip that reported it: boards with two
/// of them (a Nuvoton next to an ITE, common on ASUS and Supermicro) hand back two sensors both
/// called "Fan #1", and a lookup by name alone found whichever came first.
/// </summary>
public sealed record FanReading(string Chip, string Name, double Rpm, double? Duty);

/// <summary>
/// One answer from the UPS. Same reason as <see cref="SlowReading"/>: the charge and the
/// "on battery" flag are read together and must come from the same round trip, or a red plate
/// appears next to a full battery that was never reported.
/// </summary>
public sealed class UpsReading
{
	/// <summary>The agent has not answered — everything is unknown, including whether the UPS
	/// is on battery. Never claim "on line" from a missing answer.</summary>
	public static readonly UpsReading Silent = new();

	public bool Answered;
	public double? Charge;         // % of battery capacity
	public double? RunTimeMin;     // minutes left on battery
	public double? Load;           // % of the rated load of the UPS
	public bool NeedsNewBattery;

	/// <summary>Raw upsBasicOutputStatus; null when the agent did not carry the OID.</summary>
	public int? Status;

	/// <summary>
	/// Running on battery. Only 3 (onBattery) and 15 (onBatteryTest) mean that; 1 (unknown) means
	/// nothing is known and must not be reported as mains power. Everything that was not 3 used to
	/// read as "on line", which turned a UPS in hardware-failure bypass — and one that answered
	/// "unknown" — into a green plate.
	/// </summary>
	public bool? OnBattery => Status switch { 3 or 15 => true, null or 1 => null, _ => false };

	/// <summary>
	/// Working, but not the way it should be: AVR trimming or boosting the mains, or the load
	/// running through a bypass instead of the inverter. None of these is "on battery", and all
	/// of them mean the next outage may not be survived — so the plate turns yellow.
	/// </summary>
	public bool Degraded => Status is 4 or 6 or 7 or 8 or 9 or 10 or 12 or 16 or 17;

	/// <summary>What the status code means, for the tooltip.</summary>
	public string StatusText => Status switch
	{
		2 => "от сети",
		3 => "от батареи",
		4 => "от сети, AVR повышает напряжение",
		5 => "спящий режим по таймеру",
		6 => "программный байпас",
		7 => "выход выключен",
		8 => "перезагрузка",
		9 => "переключён на байпас",
		10 => "байпас из-за отказа оборудования",
		11 => "сон до восстановления питания",
		12 => "от сети, AVR понижает напряжение",
		13 => "эко-режим",
		14 => "горячий резерв",
		15 => "тест батареи",
		16 => "аварийный статический байпас",
		17 => "резерв статического байпаса",
		null or 1 => "состояние неизвестно",
		_ => "состояние " + Status.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
	};
}

/// <summary>One reading of everything TrayMon shows. Null means "source unavailable".</summary>
public sealed class Readings
{
	// ---- filled on the UI thread ----
	public double? CpuLoad;        // % of all logical processors
	public double? MemLoad;        // %
	public double MemUsedGb;
	public double MemTotalGb;
	public double? CommitUsedGb;   // commit charge, for the RAM tooltip
	public double CommitTotalGb;
	public double? UptimeHours;
	public BatteryReading Battery; // null on a machine without one
	public List<GpuReading> Gpus = new();

	// One entry per physical adapter that is up: throughput in MB/s and its link speed.
	public List<(string Name, double InMb, double OutMb, double LinkMb)> Nets = new();
	public List<(string Name, double ReadMb, double WriteMb)> Volumes = new();
	public List<(string Name, double Mb)> TopIo = new();
	public List<(string Name, double FreeGb, double TotalGb)> Space = new();

	// ---- published from background tasks: one reference each, always replaced whole ----
	// volatile, and every reader takes a local copy before using it: reading the field twice
	// (once for Count, once for the indexer) is how a shrinking list throws IndexOutOfRange.
	public volatile SlowReading Slow = SlowReading.Empty;
	public volatile List<DiskReading> Disks = new();
	public volatile List<RaidDisk> RaidDisks = new();
	public volatile UpsReading Ups = UpsReading.Silent;
}

/// <summary>Physical memory via GlobalMemoryStatusEx — a single syscall, no counters involved.</summary>
public static class MemorySensor
{
	// A struct rather than a class: this runs every two seconds, and a class meant a heap
	// allocation plus a Marshal.SizeOf call per tick for eight fields that never move.
	[StructLayout(LayoutKind.Sequential)]
	private struct MemoryStatusEx
	{
		public uint dwLength;
		public uint dwMemoryLoad;
		public ulong ullTotalPhys;
		public ulong ullAvailPhys;
		public ulong ullTotalPageFile;
		public ulong ullAvailPageFile;
		public ulong ullTotalVirtual;
		public ulong ullAvailVirtual;
		public ulong ullAvailExtendedVirtual;
	}

	private static readonly uint StructSize = (uint)Marshal.SizeOf<MemoryStatusEx>();

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

	public static void Read(Readings r)
	{
		var m = new MemoryStatusEx { dwLength = StructSize };
		if (!GlobalMemoryStatusEx(ref m))
		{
			// No value is what "this source is not answering" means, and the slot must go grey.
			// Leaving the previous numbers in place kept the icon alive and coloured on data
			// nobody had measured — the one failure mode this program is written to avoid.
			r.MemLoad = null;
			r.CommitUsedGb = null;
			return;
		}
		const double gb = 1024.0 * 1024 * 1024;
		r.MemTotalGb = m.ullTotalPhys / gb;
		r.MemUsedGb = (m.ullTotalPhys - m.ullAvailPhys) / gb;
		r.MemLoad = m.dwMemoryLoad;
		// Commit charge comes out of the same call, and it answers the question physical use
		// cannot: why the machine is swapping while RAM sits at 60 %.
		r.CommitUsedGb = (m.ullTotalPageFile - m.ullAvailPageFile) / gb;
		r.CommitTotalGb = m.ullTotalPageFile / gb;
	}
}

/// <summary>
/// Laptop or tablet battery, straight out of GetSystemPowerStatus — one syscall, no counters,
/// no driver. A UPS on a serial port needs the whole SNMP path in <see cref="UpsSensor"/>;
/// a battery Windows itself knows about does not.
/// </summary>
public static class BatterySensor
{
	[StructLayout(LayoutKind.Sequential)]
	private struct SystemPowerStatus
	{
		public byte ACLineStatus;      // 0 offline, 1 online, 255 unknown
		public byte BatteryFlag;       // 128 = no system battery, 255 = unknown
		public byte BatteryLifePercent;
		public byte SystemStatusFlag;
		public int BatteryLifeTime;    // seconds left, -1 when unknown
		public int BatteryFullLifeTime;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

	private const byte NoBattery = 128;

	public static void Read(Readings r)
	{
		if (!GetSystemPowerStatus(out var s) || s.BatteryFlag == NoBattery || s.BatteryLifePercent > 100)
		{
			r.Battery = null;
			return;
		}
		r.Battery = new BatteryReading
		{
			Charge = s.BatteryLifePercent,
			OnBattery = s.ACLineStatus == 0,
			MinutesLeft = s.BatteryLifeTime >= 0 ? s.BatteryLifeTime / 60.0 : null,
		};
	}
}

/// <summary>
/// Free space per lettered volume. The commonest real failure on a server — a full C: — and
/// the cheapest thing here to ask about: one syscall per volume, and the answer changes so
/// slowly that it is polled every few minutes and drawn as whole gigabytes, so the icon
/// repaints once in hours rather than once a tick.
/// </summary>
public static class SpaceSensor
{
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetDiskFreeSpaceExW(string directory, out ulong freeForCaller, out ulong total, out ulong free);

	public static List<(string Name, double FreeGb, double TotalGb)> Read(IEnumerable<string> volumes)
	{
		const double gb = 1024.0 * 1024 * 1024;
		var result = new List<(string, double, double)>();
		foreach (var name in volumes)
		{
			try
			{
				if (!GetDiskFreeSpaceExW(name + "\\", out var free, out var total, out _) || total == 0) continue;
				result.Add((name, free / gb, total / gb));
			}
			catch (Exception)
			{
				// A volume that went away between the counter read and this call — skip it.
			}
		}
		return result;
	}
}

/// <summary>
/// GPU load, memory and temperature straight from nvml.dll (ships with the NVIDIA driver).
/// Every card is read, not just the first one — a workstation with two of them used to show
/// only one and give no hint that the other existed.
/// </summary>
public sealed class GpuSensor : IDisposable
{
	[DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] private static extern int NvmlInit();
	[DllImport("nvml.dll", EntryPoint = "nvmlShutdown")] private static extern int NvmlShutdown();
	[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")] private static extern int NvmlGetCount(out uint count);
	[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] private static extern int NvmlGetHandle(uint index, out IntPtr device);
	[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName", CharSet = CharSet.Ansi)] private static extern int NvmlGetName(IntPtr device, StringBuilder name, uint length);
	[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")] private static extern int NvmlGetUtilization(IntPtr device, out NvmlUtilization util);
	[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")] private static extern int NvmlGetMemory(IntPtr device, out NvmlMemory mem);
	[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")] private static extern int NvmlGetTemperature(IntPtr device, uint sensorType, out uint temp);
	[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeed_v2")] private static extern int NvmlGetFanDuty(IntPtr device, uint fan, out uint speed);
	[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeedRPM")] private static extern int NvmlGetFanRpm(IntPtr device, ref NvmlFanSpeedInfo info);
	[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetNumFans")] private static extern int NvmlGetNumFans(IntPtr device, out uint count);

	private const int NvmlNotSupported = 3;
	private const int NvmlFunctionNotFound = 13;
	private const int NvmlUninitialized = 1;
	private const int NvmlGpuIsLost = 15;
	private const int NvmlUnknown = 999;

	/// <summary>
	/// Hard ceiling on cards, only so a broken driver cannot make this loop for ever. The tray
	/// limit is *not* enforced here: sources are cut down by the GUID pool, which is the one
	/// place that knows how many slots there are and the one place that reports the overflow.
	/// </summary>
	private const int MaxCards = 32;

	/// <summary>Never re-initialise more than once a minute; a card that is really gone stays gone.</summary>
	private const int ReinitEveryMs = 60000;

	[StructLayout(LayoutKind.Sequential)]
	private struct NvmlUtilization { public uint Gpu; public uint Memory; }

	[StructLayout(LayoutKind.Sequential)]
	private struct NvmlFanSpeedInfo { public uint Version; public uint Fan; public uint Speed; }

	[StructLayout(LayoutKind.Sequential)]
	private struct NvmlMemory { public ulong Total; public ulong Free; public ulong Used; }

	private sealed class Card
	{
		public int Index;
		public IntPtr Handle;
		public string Name;
		public bool FanRpmSupported = true;
		public uint Fans = 1;
	}

	private readonly List<Card> _cards = new();
	private bool _ready;
	private int _lostStreak;
	private long _reinitAt;

	/// <summary>Why NVML did not come up; shown by --once and by the diagnostics window.</summary>
	public string LastError { get; private set; }

	public int CardCount => _cards.Count;

	static GpuSensor()
	{
		// Where nvml.dll comes from, spelled out. The default probing order starts with the
		// folder the executable is in, and this process holds an elevated token: a DLL dropped
		// next to TrayMon.exe would be loaded in preference to the driver's own. The NVIDIA
		// installer puts the library in System32, so that is looked at first; the fallback to
		// default probing is kept because older drivers only place it under Program Files.
		try
		{
			NativeLibrary.SetDllImportResolver(typeof(GpuSensor).Assembly, (name, _, _) =>
			{
				if (!string.Equals(name, "nvml.dll", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
				var known = new[]
				{
					Path.Combine(Environment.SystemDirectory, "nvml.dll"),
					Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
								 "NVIDIA Corporation", "NVSMI", "nvml.dll"),
				};
				foreach (var candidate in known)
					if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
						return handle;
				return IntPtr.Zero;
			});
		}
		catch (Exception) { /* already set, or the runtime refused — default probing then */ }
	}

	public GpuSensor()
	{
		// Everything: a driver too old for nvmlInit_v2 throws EntryPointNotFound, a 32-bit
		// nvml.dll on PATH throws BadImageFormat, and this runs in a field initialiser of
		// TrayApp — an escaping exception there stops the program from starting at all.
		try
		{
			var rc = NvmlInit();
			if (rc != 0) { LastError = "nvmlInit вернул код " + rc; return; }
			if (!Enumerate()) { LastError = "NVML не отдал ни одной карты"; NvmlShutdown(); return; }
			_ready = true;
		}
		catch (Exception ex)
		{
			LastError = ex.GetType().Name + ": " + ex.Message;   // no NVIDIA driver — GPU rows stay empty
		}
	}

	private bool Enumerate()
	{
		_cards.Clear();
		if (NvmlGetCount(out var count) != 0) count = 1;
		for (var i = 0u; i < Math.Min(count, MaxCards); i++)
		{
			if (NvmlGetHandle(i, out var device) != 0) continue;
			var card = new Card { Index = (int)i, Handle = device, Name = NameOf(device, (int)i) };
			// Asked once: a card with three fans reports the stopped one only if all of them are
			// looked at, and the count does not change while the driver is loaded.
			if (NvmlGetNumFans(device, out var fans) == 0 && fans is > 0 and <= 8) card.Fans = fans;
			_cards.Add(card);
		}
		return _cards.Count > 0;
	}

	/// <summary>
	/// Takes the library down and brings it back up. A driver reset — a TDR, or simply a driver
	/// update on a machine that has been logged in for a month — invalidates every handle for
	/// good, and there was no path back: the GPU icons stayed grey until the process was
	/// restarted. nvidia-smi survives the same event this way.
	/// </summary>
	private void Reinit()
	{
		_reinitAt = Environment.TickCount64;
		_lostStreak = 0;
		try
		{
			NvmlShutdown();
			_ready = false;
			var rc = NvmlInit();
			if (rc != 0) { LastError = "nvmlInit после сброса драйвера вернул код " + rc; return; }
			if (!Enumerate()) { LastError = "после сброса драйвера NVML не отдал ни одной карты"; return; }
			_ready = true;
			LastError = null;
		}
		catch (Exception ex)
		{
			LastError = ex.GetType().Name + ": " + ex.Message;
		}
	}

	private static string NameOf(IntPtr device, int index)
	{
		try
		{
			var buffer = new StringBuilder(96);
			if (NvmlGetName(device, buffer, (uint)buffer.Capacity) == 0 && buffer.Length > 0)
				return buffer.ToString();
		}
		catch (Exception) { /* older driver without the entry point */ }
		return "GPU " + (index + 1);
	}

	public void Read(Readings r)
	{
		if (!_ready)
		{
			// Not ready and past the cool-down: the driver may have come back since.
			if (_lostStreak > 0 && Environment.TickCount64 - _reinitAt > ReinitEveryMs) Reinit();
			if (!_ready) { r.Gpus = new List<GpuReading>(); return; }
		}
		try
		{
			var cards = new List<GpuReading>(_cards.Count);
			var lost = 0;
			foreach (var card in _cards)
			{
				cards.Add(ReadCard(card, out var rc));
				if (rc != 0) lost++;
			}
			r.Gpus = cards;

			// Three polls in a row where not one card answered is a driver that has been reset or
			// replaced, not a busy moment. Handles do not recover on their own.
			if (lost == _cards.Count && _cards.Count > 0) _lostStreak++;
			else { _lostStreak = 0; LastError = null; }
			if (_lostStreak >= 3 && Environment.TickCount64 - _reinitAt > ReinitEveryMs) Reinit();
		}
		catch (Exception ex)
		{
			// A driver reset pulls the handles out from under us. Publishing an empty list rather
			// than leaving the old one: "no value" is what makes a slot go grey, and a coloured
			// plate over numbers from before the reset is the failure this program is written
			// to avoid. The reason goes into the tooltip through LastError.
			LastError = ex.GetType().Name + ": " + ex.Message;
			r.Gpus = new List<GpuReading>();
			_lostStreak++;
		}
	}

	private GpuReading ReadCard(Card card, out int status)
	{
		double? load = null, temp = null, memLoad = null, fanRpm = null, fanDuty = null;
		double usedGb = 0, totalGb = 0;

		status = NvmlGetUtilization(card.Handle, out var util);
		if (status == 0) load = util.Gpu;
		else if (status is NvmlGpuIsLost or NvmlUninitialized or NvmlUnknown)
			// "No NVIDIA driver" is what the icon used to say here, and it was wrong in the one
			// case that matters: the driver is there, the card is not.
			LastError = "NVML: карта не отвечает (код " + status + ")";

		if (NvmlGetMemory(card.Handle, out var mem) == 0 && mem.Total > 0)
		{
			const double gb = 1024.0 * 1024 * 1024;
			usedGb = mem.Used / gb;
			totalGb = mem.Total / gb;
			memLoad = 100.0 * mem.Used / mem.Total;
		}
		if (NvmlGetTemperature(card.Handle, 0 /* NVML_TEMPERATURE_GPU */, out var t) == 0) temp = t;

		// Prefer real RPM; older drivers only expose the duty cycle. A successful call reporting
		// zero means the fan has stopped — which is the whole point of the fan icon — so it must
		// not be mistaken for "this driver cannot answer": every card with a zero-RPM idle mode
		// stops its fans at the desktop, and treating that as "unsupported" left the icon showing
		// the last speed from when the card was busy.
		//
		// Every fan of the card, not fan 0: on a three-fan card one stopped fan is the failure
		// worth an icon, and reading only the first one hid it completely. The slowest wins, for
		// the same reason.
		if (card.FanRpmSupported)
		{
			try
			{
				for (var fan = 0u; fan < card.Fans; fan++)
				{
					var info = new NvmlFanSpeedInfo
					{
						Version = (uint)(Marshal.SizeOf<NvmlFanSpeedInfo>() | (1 << 24)),
						Fan = fan,
					};
					var rc = NvmlGetFanRpm(card.Handle, ref info);
					if (rc == 0) fanRpm = fanRpm.HasValue ? Math.Min(fanRpm.Value, info.Speed) : info.Speed;
					else if (rc is NvmlNotSupported or NvmlFunctionNotFound) { card.FanRpmSupported = false; break; }
				}
			}
			catch (EntryPointNotFoundException) { card.FanRpmSupported = false; }
		}
		for (var fan = 0u; fan < card.Fans; fan++)
			if (NvmlGetFanDuty(card.Handle, fan, out var duty) == 0)
				fanDuty = fanDuty.HasValue ? Math.Max(fanDuty.Value, duty) : duty;

		return new GpuReading(card.Index, card.Name, load, temp, fanRpm, fanDuty, memLoad, usedGb, totalGb);
	}

	public void Dispose()
	{
		if (_ready) { try { NvmlShutdown(); } catch (Exception) { /* driver already gone */ } }
		_ready = false;
	}
}

/// <summary>
/// CPU package temperature, NVMe temperatures and motherboard fan speeds via
/// LibreHardwareMonitor. Costs differ a lot — CPU ~40 ms, motherboard ~7 ms, storage a SMART
/// query — so the caller refreshes them on different schedules. The GPU is deliberately not
/// enabled here: LibreHardwareMonitor spends ~68 ms per GPU update, while NVML answers in ~3 ms.
/// HDDs behind the Intel RAID volume are invisible here — nothing in the stack exposes them.
///
/// Every read is serialised: one <c>Computer</c> is one handle to a ring-0 driver with shared
/// buffers, and the CPU path switches thread affinity — none of it is documented as safe to
/// enter from two threads, and the schedules do land on the same tick.
/// </summary>
public sealed class LhmSensor : IDisposable
{
	private readonly Computer _computer;
	private readonly object _gate = new();
	private readonly List<DiskReading> _disks = new();
	private bool _closed;

	public bool Available { get; }

	/// <summary>
	/// The library opened but its kernel driver cannot read anything. WinRing0 1.2.0 — the driver
	/// this version ships — is on the Microsoft vulnerable-driver block list and is refused by
	/// HVCI (Memory Integrity, on by default on new Windows 11) and quarantined by Defender.
	/// <c>Computer.Open()</c> does not throw when that happens, so "loaded" used to be printed
	/// next to permanently empty temperatures — the one unfixable state looking exactly like the
	/// working one.
	/// </summary>
	public bool DriverBlocked { get; private set; }

	/// <summary>Whether the ring-0 device could be locked down; see <see cref="RestrictRing0"/>.</summary>
	public string Ring0Note { get; private set; }

	/// <summary>Why the sensor library did not come up; shown by --once.</summary>
	public string LastError { get; private set; }

	public LhmSensor()
	{
		try
		{
			_computer = new Computer
			{
				IsCpuEnabled = true,
				IsStorageEnabled = true,
				IsMotherboardEnabled = true,
				// Stays off on purpose and is written out rather than left to the default: one
				// GPU update here costs ~68 ms against ~4 ms through NVML, so switching this on
				// would multiply the price of the whole program by seventeen for that source.
				IsGpuEnabled = false,
			};
			_computer.Open();
			Available = true;
			RestrictRing0();
			Probe();
		}
		catch (Exception ex)
		{
			Available = false;   // not elevated, or the driver refused to load
			LastError = ex.GetType().Name + ": " + ex.Message;
		}
	}

	/// <summary>
	/// Asks the CPU for a temperature once, at startup, so a driver that loaded but cannot read
	/// is reported as broken instead of as working. Only meaningful with an elevated token —
	/// without one the library returns null for every temperature by design, and the menu
	/// already says so.
	/// </summary>
	private void Probe()
	{
		if (!TrayApp.IsElevated) return;
		try
		{
			foreach (var hw in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu))
			{
				hw.Update();
				if (hw.Sensors.Any(s => s.SensorType == SensorType.Temperature && s.Value.HasValue)) return;
			}
			DriverBlocked = true;
			LastError = "драйвер датчиков загружен, но не читает MSR — вероятно, заблокирован " +
						"(целостность памяти HVCI или антивирус: WinRing0 в блок-листе Microsoft)";
		}
		catch (Exception ex)
		{
			LastError = ex.GetType().Name + ": " + ex.Message;
		}
	}

	// ---- ring-0 device hardening ----

	private const uint ReadControl = 0x00020000, WriteDac = 0x00040000;
	private const uint OpenExisting = 3;
	private const int SeKernelObject = 6;
	private const uint DaclSecurityInformation = 0x00000004;
	private const uint ProtectedDaclSecurityInformation = 0x80000000;

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security,
											 uint disposition, uint flags, IntPtr template);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr handle);

	[DllImport("advapi32.dll")]
	private static extern uint SetSecurityInfo(IntPtr handle, int objectType, uint securityInfo,
											   IntPtr owner, IntPtr group, byte[] dacl, IntPtr sacl);

	/// <summary>
	/// Narrows the DACL of the WinRing0 device to Administrators and SYSTEM.
	///
	/// WinRing0 1.2.0 creates <c>\\.\WinRing0_1_2_0</c> with no restricting DACL (CVE-2020-14979),
	/// and the driver stays loaded for as long as TrayMon runs. Any unprivileged process on the
	/// machine can then open it and read and write MSRs, I/O ports and physical memory — local
	/// privilege escalation handed over by a monitor. The library's own handle is already open,
	/// so tightening the device afterwards costs it nothing; a failure here is not fatal, but it
	/// is recorded, because a mitigation that quietly did not happen must not look like one that did.
	/// </summary>
	private void RestrictRing0()
	{
		// Without an elevated token the library never loaded the driver in the first place, and
		// the device does not exist to be tightened. Saying "could not" there would be noise.
		if (!TrayApp.IsElevated) return;

		var handle = IntPtr.Zero;
		try
		{
			handle = CreateFileW(@"\\.\WinRing0_1_2_0", ReadControl | WriteDac, 0, IntPtr.Zero,
								 OpenExisting, 0, IntPtr.Zero);
			if (handle == IntPtr.Zero || handle == new IntPtr(-1))
			{
				Ring0Note = "устройство WinRing0 не открылось — сузить права не удалось";
				return;
			}
			// Protected, so nothing inherits back in: full access to Administrators and SYSTEM only.
			var descriptor = new System.Security.AccessControl.RawSecurityDescriptor("D:P(A;;GA;;;BA)(A;;GA;;;SY)");
			var dacl = new byte[descriptor.DiscretionaryAcl.BinaryLength];
			descriptor.DiscretionaryAcl.GetBinaryForm(dacl, 0);
			var rc = SetSecurityInfo(handle, SeKernelObject,
									 DaclSecurityInformation | ProtectedDaclSecurityInformation,
									 IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);
			Ring0Note = rc == 0
				? "доступ к устройству WinRing0 ограничен администраторами"
				: "сузить права на устройство WinRing0 не удалось (код " + rc + ")";
		}
		catch (Exception ex)
		{
			Ring0Note = "сузить права на устройство WinRing0 не удалось: " + ex.Message;
		}
		finally
		{
			if (handle != IntPtr.Zero && handle != new IntPtr(-1)) CloseHandle(handle);
		}
	}

	/// <summary>CPU temperature and fan speeds in one pass, published as a single object.</summary>
	public SlowReading ReadSlow()
	{
		var reading = new SlowReading();
		if (!Available) return reading;
		lock (_gate)
		{
			if (_closed) return reading;
			try
			{
				reading.CpuTemp = ReadCpuTempLocked();
				reading.Fans = ReadFansLocked();
			}
			catch (Exception ex)
			{
				LastError = ex.GetType().Name + ": " + ex.Message;
			}
		}
		return reading;
	}

	public double? ReadCpuTemp()
	{
		if (!Available) return null;
		lock (_gate)
		{
			if (_closed) return null;
			try { return ReadCpuTempLocked(); }
			catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; return null; }
		}
	}

	public List<FanReading> ReadFans()
	{
		if (!Available) return new List<FanReading>();
		lock (_gate)
		{
			if (_closed) return new List<FanReading>();
			try { return ReadFansLocked(); }
			catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; return new List<FanReading>(); }
		}
	}

	/// <summary>
	/// The package temperature, whatever the vendor calls it. "CPU Package" and "Core Max" are
	/// Intel names; a Ryzen reports "Core (Tctl/Tdie)" and "CCD1 (Tdie)", so looking only for the
	/// Intel two left every AMD machine with a permanently dead icon saying "the sensor is not
	/// answering" — and AMD is half the machines a public mirror is read on. The last resort is
	/// simply the hottest temperature the CPU reports, which is what the icon means anyway.
	/// </summary>
	private double? ReadCpuTempLocked()
	{
		foreach (var hw in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu))
		{
			hw.Update();
			var temps = hw.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue).ToList();
			if (temps.Count == 0) continue;

			var package = temps.FirstOrDefault(s => s.Name == "CPU Package")
					   ?? temps.FirstOrDefault(s => s.Name == "Core Max")
					   ?? temps.FirstOrDefault(s => s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase))
					   ?? temps.FirstOrDefault(s => s.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase));
			if (package?.Value != null) return Math.Round(package.Value.Value, 0);
			return Math.Round(temps.Max(s => s.Value.Value), 0);
		}
		return null;
	}

	/// <summary>
	/// Fan speeds off the SuperIO chip, with the duty cycle of the matching control channel.
	/// Headers with nothing plugged in report 0 — the caller decides what to do with those.
	/// </summary>
	private List<FanReading> ReadFansLocked()
	{
		var fans = new List<FanReading>();
		foreach (var hw in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Motherboard))
		{
			hw.Update();
			foreach (var sub in hw.SubHardware)
			{
				sub.Update();
				foreach (var fan in sub.Sensors.Where(s => s.SensorType == SensorType.Fan && s.Value.HasValue))
				{
					var duty = sub.Sensors
						.FirstOrDefault(s => s.SensorType == SensorType.Control && s.Name == fan.Name)?.Value;
					fans.Add(new FanReading(sub.Name, fan.Name, Math.Round(fan.Value.Value, 0),
											duty.HasValue ? Math.Round(duty.Value, 0) : null));
				}
			}
		}
		return fans;
	}

	/// <summary>
	/// Refreshes the cached disk temperatures and NVMe wear figures. Slow (SMART) — call it
	/// rarely. A disk running out of spare blocks matters more than a warm one, which is the
	/// same argument that already put the SMART verdict on the RAID icons.
	/// </summary>
	public void RefreshDiskTemps()
	{
		if (!Available) return;
		var fresh = new List<DiskReading>();
		lock (_gate)
		{
			if (_closed) return;
			try
			{
				foreach (var hw in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage))
				{
					hw.Update();
					var t = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name == "Temperature");
					if (t?.Value is null) continue;
					var wear = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Level && s.Name == "Percentage Used")?.Value;
					var spare = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Level && s.Name == "Available Spare")?.Value;
					fresh.Add(new DiskReading(hw.Name, Math.Round(t.Value.Value, 0), wear, spare));
				}
			}
			catch (Exception ex)
			{
				// Clearing, not keeping: a disk that stopped answering SMART used to go on showing
				// the temperature from hours ago in a normal colour, because the cache was left
				// untouched and Fade() never saw a missing value. Empty is what makes it go grey.
				LastError = ex.GetType().Name + ": " + ex.Message;
				lock (_disks) _disks.Clear();
				return;
			}
			lock (_disks) { _disks.Clear(); _disks.AddRange(fresh); }
		}
	}

	public List<DiskReading> DiskTemps()
	{
		lock (_disks) return new List<DiskReading>(_disks);
	}

	public void Dispose()
	{
		if (!Available) return;
		// Under the same lock as the reads: closing the library unloads the ring-0 driver, and
		// doing that while a SMART query is in flight on another thread is how it takes the
		// process down on exit.
		lock (_gate)
		{
			if (_closed) return;
			_closed = true;
			try { _computer.Close(); } catch (Exception) { /* going away anyway */ }
		}
	}
}
