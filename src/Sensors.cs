using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace TrayMon;

/// <summary>One reading of everything TrayMon shows. Null means "source unavailable".</summary>
public sealed class Readings
{
	public double? CpuLoad;        // % of all logical processors
	public double? CpuTemp;        // °C, package
	public double? MemLoad;        // %
	public double MemUsedGb;
	public double MemTotalGb;
	public double? GpuLoad;        // %
	public double? GpuTemp;        // °C
	public double? GpuFanRpm;      // null when the driver only reports duty
	public double? GpuFanDuty;     // %
	public double? GpuMemLoad;     // %
	public double GpuMemUsedGb;
	public double GpuMemTotalGb;
	public List<(string Name, double Temp)> Disks = new();
	public List<(string Name, double Temp, string Serial)> RaidDisks = new();   // disks behind a RAID controller
	public List<(string Name, double Rpm, double? Duty)> Fans = new();

	public double NetInMb;         // MB/s over physical adapters
	public double NetOutMb;
	public double NetLinkMb;       // link speed of the fastest physical adapter, MB/s
	public List<(string Name, double ReadMb, double WriteMb)> Volumes = new();
	public List<(string Name, double Mb)> TopIo = new();
}

/// <summary>Physical memory via GlobalMemoryStatusEx — a single syscall, no counters involved.</summary>
public static class MemorySensor
{
	[StructLayout(LayoutKind.Sequential)]
	private sealed class MemoryStatusEx
	{
		public uint dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
		public uint dwMemoryLoad;
		public ulong ullTotalPhys;
		public ulong ullAvailPhys;
		public ulong ullTotalPageFile;
		public ulong ullAvailPageFile;
		public ulong ullTotalVirtual;
		public ulong ullAvailVirtual;
		public ulong ullAvailExtendedVirtual;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

	public static void Read(Readings r)
	{
		var m = new MemoryStatusEx();
		if (!GlobalMemoryStatusEx(m)) return;
		const double gb = 1024.0 * 1024 * 1024;
		r.MemTotalGb = m.ullTotalPhys / gb;
		r.MemUsedGb = (m.ullTotalPhys - m.ullAvailPhys) / gb;
		r.MemLoad = m.dwMemoryLoad;
	}
}

/// <summary>GPU load, memory and temperature straight from nvml.dll (ships with the NVIDIA driver).</summary>
public sealed class GpuSensor : IDisposable
{
	[DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] private static extern int NvmlInit();
	[DllImport("nvml.dll", EntryPoint = "nvmlShutdown")] private static extern int NvmlShutdown();
	[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] private static extern int NvmlGetHandle(uint index, out IntPtr device);
	[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")] private static extern int NvmlGetUtilization(IntPtr device, out NvmlUtilization util);
	[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")] private static extern int NvmlGetMemory(IntPtr device, out NvmlMemory mem);
	[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")] private static extern int NvmlGetTemperature(IntPtr device, uint sensorType, out uint temp);
	[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeed_v2")] private static extern int NvmlGetFanDuty(IntPtr device, uint fan, out uint speed);
	[DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeedRPM")] private static extern int NvmlGetFanRpm(IntPtr device, ref NvmlFanSpeedInfo info);

	[StructLayout(LayoutKind.Sequential)]
	private struct NvmlUtilization { public uint Gpu; public uint Memory; }

	[StructLayout(LayoutKind.Sequential)]
	private struct NvmlFanSpeedInfo { public uint Version; public uint Fan; public uint Speed; }

	[StructLayout(LayoutKind.Sequential)]
	private struct NvmlMemory { public ulong Total; public ulong Free; public ulong Used; }

	private bool _ready;
	private bool _fanRpmSupported = true;
	private IntPtr _device;

	public GpuSensor()
	{
		try
		{
			if (NvmlInit() != 0) return;
			if (NvmlGetHandle(0, out _device) != 0) { NvmlShutdown(); return; }
			_ready = true;
		}
		catch (DllNotFoundException) { /* no NVIDIA driver — GPU rows stay empty */ }
	}

	public void Read(Readings r)
	{
		if (!_ready) return;
		if (NvmlGetUtilization(_device, out var util) == 0) r.GpuLoad = util.Gpu;
		if (NvmlGetMemory(_device, out var mem) == 0 && mem.Total > 0)
		{
			const double gb = 1024.0 * 1024 * 1024;
			r.GpuMemUsedGb = mem.Used / gb;
			r.GpuMemTotalGb = mem.Total / gb;
			r.GpuMemLoad = 100.0 * mem.Used / mem.Total;
		}
		if (NvmlGetTemperature(_device, 0 /* NVML_TEMPERATURE_GPU */, out var t) == 0) r.GpuTemp = t;

		// Prefer real RPM; older drivers only expose the duty cycle.
		if (_fanRpmSupported)
		{
			var info = new NvmlFanSpeedInfo { Version = (uint)(Marshal.SizeOf<NvmlFanSpeedInfo>() | (1 << 24)), Fan = 0 };
			try
			{
				if (NvmlGetFanRpm(_device, ref info) == 0 && info.Speed > 0) r.GpuFanRpm = info.Speed;
				else _fanRpmSupported = false;
			}
			catch (EntryPointNotFoundException) { _fanRpmSupported = false; }
		}
		if (NvmlGetFanDuty(_device, 0, out var duty) == 0) r.GpuFanDuty = duty;
	}

	public void Dispose()
	{
		if (_ready) NvmlShutdown();
		_ready = false;
	}
}

/// <summary>
/// CPU package temperature, NVMe temperatures and motherboard fan speeds via
/// LibreHardwareMonitor. Costs differ a lot — CPU ~40 ms, motherboard ~7 ms, storage a SMART
/// query — so the caller refreshes them on different schedules. The GPU is deliberately not
/// enabled here: LibreHardwareMonitor spends ~68 ms per GPU update, while NVML answers in ~3 ms.
/// HDDs behind the Intel RAID volume are invisible here — nothing in the stack exposes them.
/// </summary>
public sealed class LhmSensor : IDisposable
{
	private readonly Computer _computer;
	private readonly List<(string Name, double Temp)> _disks = new();

	public bool Available { get; }

	/// <summary>Why the sensor library did not come up; shown by --once.</summary>
	public string LastError { get; private set; }

	public LhmSensor()
	{
		try
		{
			_computer = new Computer { IsCpuEnabled = true, IsStorageEnabled = true, IsMotherboardEnabled = true };
			_computer.Open();
			Available = true;
		}
		catch (Exception ex)
		{
			Available = false;   // not elevated, or the driver refused to load
			LastError = ex.GetType().Name + ": " + ex.Message;
		}
	}

	public double? ReadCpuTemp()
	{
		if (!Available) return null;
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
	public List<(string Name, double Rpm, double? Duty)> ReadFans()
	{
		var fans = new List<(string, double, double?)>();
		if (!Available) return fans;

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
		foreach (var hw in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage))
		{
			hw.Update();
			var t = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name == "Temperature");
			if (t?.Value != null) fresh.Add((hw.Name, Math.Round(t.Value.Value, 0)));
		}
		lock (_disks) { _disks.Clear(); _disks.AddRange(fresh); }
	}

	public List<(string Name, double Temp)> DiskTemps()
	{
		lock (_disks) return new List<(string, double)>(_disks);
	}

	public void Dispose()
	{
		if (Available) _computer.Close();
	}
}
