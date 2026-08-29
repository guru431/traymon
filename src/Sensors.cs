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
/// CPU temperature and fan speeds — everything that comes from the sensor library. Published as
/// one object rather than as separate fields because it is filled on a background thread: a
/// <c>double?</c> is sixteen bytes and its write is not atomic, so a reader could otherwise see
/// the "has value" half of one poll and the number of the previous one.
/// </summary>
public sealed class SlowReading
{
	public static readonly SlowReading Empty = new();

	public double? CpuTemp;
	public List<(string Name, double Rpm, double? Duty)> Fans = new();
}

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
	public bool? OnBattery;        // null when the agent did not report the status
	public bool NeedsNewBattery;
}

/// <summary>One reading of everything TrayMon shows. Null means "source unavailable".</summary>
public sealed class Readings
{
	// ---- filled on the UI thread ----
	public double? CpuLoad;        // % of all logical processors
	public double? MemLoad;        // %
	public double MemUsedGb;
	public double MemTotalGb;
	public double? UptimeHours;
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
	public volatile List<(string Name, double Temp)> Disks = new();
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
		if (!GlobalMemoryStatusEx(ref m)) return;
		const double gb = 1024.0 * 1024 * 1024;
		r.MemTotalGb = m.ullTotalPhys / gb;
		r.MemUsedGb = (m.ullTotalPhys - m.ullAvailPhys) / gb;
		r.MemLoad = m.dwMemoryLoad;
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

	private const int NvmlNotSupported = 3;
	private const int NvmlFunctionNotFound = 13;

	/// <summary>As many cards as there are prepared tray slots for.</summary>
	public const int MaxCards = 4;

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
	}

	private readonly List<Card> _cards = new();
	private bool _ready;

	/// <summary>Why NVML did not come up; shown by --once and by the diagnostics window.</summary>
	public string LastError { get; private set; }

	public int CardCount => _cards.Count;

	public GpuSensor()
	{
		// Everything: a driver too old for nvmlInit_v2 throws EntryPointNotFound, a 32-bit
		// nvml.dll on PATH throws BadImageFormat, and this runs in a field initialiser of
		// TrayApp — an escaping exception there stops the program from starting at all.
		try
		{
			if (NvmlInit() != 0) { LastError = "nvmlInit вернул ошибку"; return; }
			if (NvmlGetCount(out var count) != 0) count = 1;
			for (var i = 0u; i < Math.Min(count, MaxCards); i++)
			{
				if (NvmlGetHandle(i, out var device) != 0) continue;
				_cards.Add(new Card { Index = (int)i, Handle = device, Name = NameOf(device, (int)i) });
			}
			if (_cards.Count == 0) { LastError = "NVML не отдал ни одной карты"; NvmlShutdown(); return; }
			_ready = true;
		}
		catch (Exception ex)
		{
			LastError = ex.GetType().Name + ": " + ex.Message;   // no NVIDIA driver — GPU rows stay empty
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
		if (!_ready) return;
		try
		{
			var cards = new List<GpuReading>(_cards.Count);
			foreach (var card in _cards) cards.Add(ReadCard(card));
			r.Gpus = cards;
		}
		catch (Exception ex)
		{
			// A driver reset pulls the handles out from under us; keep the last values and say why.
			LastError = ex.GetType().Name + ": " + ex.Message;
		}
	}

	private GpuReading ReadCard(Card card)
	{
		double? load = null, temp = null, memLoad = null, fanRpm = null, fanDuty = null;
		double usedGb = 0, totalGb = 0;

		if (NvmlGetUtilization(card.Handle, out var util) == 0) load = util.Gpu;
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
		if (card.FanRpmSupported)
		{
			var info = new NvmlFanSpeedInfo { Version = (uint)(Marshal.SizeOf<NvmlFanSpeedInfo>() | (1 << 24)), Fan = 0 };
			try
			{
				var rc = NvmlGetFanRpm(card.Handle, ref info);
				if (rc == 0) fanRpm = info.Speed;
				else if (rc is NvmlNotSupported or NvmlFunctionNotFound) card.FanRpmSupported = false;
			}
			catch (EntryPointNotFoundException) { card.FanRpmSupported = false; }
		}
		if (NvmlGetFanDuty(card.Handle, 0, out var duty) == 0) fanDuty = duty;

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
	private readonly List<(string Name, double Temp)> _disks = new();
	private bool _closed;

	public bool Available { get; }

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
		}
		catch (Exception ex)
		{
			Available = false;   // not elevated, or the driver refused to load
			LastError = ex.GetType().Name + ": " + ex.Message;
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

	public List<(string Name, double Rpm, double? Duty)> ReadFans()
	{
		if (!Available) return new List<(string, double, double?)>();
		lock (_gate)
		{
			if (_closed) return new List<(string, double, double?)>();
			try { return ReadFansLocked(); }
			catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; return new List<(string, double, double?)>(); }
		}
	}

	private double? ReadCpuTempLocked()
	{
		foreach (var hw in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu))
		{
			hw.Update();
			var package = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name == "CPU Package")
					   ?? hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name == "Core Max");
			if (package?.Value != null) return Math.Round(package.Value.Value, 0);
		}
		return null;
	}

	/// <summary>
	/// Fan speeds off the SuperIO chip, with the duty cycle of the matching control channel.
	/// Headers with nothing plugged in report 0 — the caller decides what to do with those.
	/// </summary>
	private List<(string Name, double Rpm, double? Duty)> ReadFansLocked()
	{
		var fans = new List<(string, double, double?)>();
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
					fans.Add((fan.Name, Math.Round(fan.Value.Value, 0), duty.HasValue ? Math.Round(duty.Value, 0) : null));
				}
			}
		}
		return fans;
	}

	/// <summary>Refreshes the cached disk temperatures. Slow (SMART) — call it rarely.</summary>
	public void RefreshDiskTemps()
	{
		if (!Available) return;
		var fresh = new List<(string, double)>();
		lock (_gate)
		{
			if (_closed) return;
			try
			{
				foreach (var hw in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage))
				{
					hw.Update();
					var t = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name == "Temperature");
					if (t?.Value != null) fresh.Add((hw.Name, Math.Round(t.Value.Value, 0)));
				}
			}
			catch (Exception ex)
			{
				LastError = ex.GetType().Name + ": " + ex.Message;
				return;
			}
			lock (_disks) { _disks.Clear(); _disks.AddRange(fresh); }
		}
	}

	public List<(string Name, double Temp)> DiskTemps()
	{
		lock (_disks) return new List<(string, double)>(_disks);
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
