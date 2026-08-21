using System.Runtime.InteropServices;

namespace TrayMon;

/// <summary>
/// Thin wrapper over PDH. One query holds many counters and a single collect refreshes all of
/// them, which is why CPU, network and disks share one: six counters cost 4.1 ms per collect
/// against 1.7 ms for the CPU counter alone.
/// </summary>
public sealed class PdhQuery : IDisposable
{
	private const uint PDH_FMT_DOUBLE = 0x00000200;

	[DllImport("pdh.dll", CharSet = CharSet.Unicode)]
	private static extern uint PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);
	[DllImport("pdh.dll", CharSet = CharSet.Unicode)]
	private static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);
	[DllImport("pdh.dll")]
	private static extern uint PdhCollectQueryData(IntPtr query);
	[DllImport("pdh.dll")]
	private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhCounterValue value);
	[DllImport("pdh.dll", CharSet = CharSet.Unicode)]
	private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr items);
	[DllImport("pdh.dll")]
	private static extern uint PdhCloseQuery(IntPtr query);

	[StructLayout(LayoutKind.Explicit)]
	private struct PdhCounterValue
	{
		[FieldOffset(0)] public uint CStatus;
		[FieldOffset(8)] public double DoubleValue;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PdhCounterItem
	{
		public IntPtr Name;
		public PdhCounterValue Value;
	}

	private IntPtr _query;

	public bool Available => _query != IntPtr.Zero;

	public PdhQuery()
	{
		if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) _query = IntPtr.Zero;
	}

	/// <summary>Adds a counter; returns IntPtr.Zero when the counter does not exist here.</summary>
	public IntPtr Add(string path)
	{
		if (!Available) return IntPtr.Zero;
		return PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var counter) == 0 ? counter : IntPtr.Zero;
	}

	/// <summary>Refreshes every counter of this query. Rate counters need two collects to read.</summary>
	public void Collect()
	{
		if (Available) PdhCollectQueryData(_query);
	}

	public double? Read(IntPtr counter)
	{
		if (counter == IntPtr.Zero) return null;
		if (PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE, out _, out var v) != 0) return null;
		return v.DoubleValue;
	}

	private const uint PDH_MORE_DATA = 0x800007D2;

	private IntPtr _buffer = IntPtr.Zero;
	private uint _bufferSize;

	/// <summary>
	/// Reads a wildcard counter as instance name → value.
	/// The buffer is kept between calls: allocating and freeing one per call, plus the extra
	/// sizing call PDH wants, made five reads per tick cost about ten times what they should.
	/// </summary>
	public List<(string Instance, double Value)> ReadArray(IntPtr counter)
	{
		var result = new List<(string, double)>();
		if (counter == IntPtr.Zero) return result;

		for (var attempt = 0; attempt < 2; attempt++)
		{
			var size = _bufferSize;
			var rc = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out var count, _buffer);
			if (rc == 0)
			{
				var stride = Marshal.SizeOf<PdhCounterItem>();
				for (var i = 0; i < count; i++)
				{
					var item = Marshal.PtrToStructure<PdhCounterItem>(_buffer + i * stride);
					var name = Marshal.PtrToStringUni(item.Name);
					if (!string.IsNullOrEmpty(name)) result.Add((name, item.Value.DoubleValue));
				}
				return result;
			}
			if (rc != PDH_MORE_DATA || size == 0) return result;

			if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
			_bufferSize = size;
			_buffer = Marshal.AllocHGlobal((int)size);
		}
		return result;
	}

	public void Dispose()
	{
		if (_query != IntPtr.Zero) PdhCloseQuery(_query);
		_query = IntPtr.Zero;
		if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
		_buffer = IntPtr.Zero;
		_bufferSize = 0;
	}
}

/// <summary>
/// CPU load, network throughput and per-volume disk throughput — everything that comes from
/// performance counters, in one query and one collect per tick.
///
/// CPU deliberately uses the hypervisor counter: on a Hyper-V host the plain \Processor counter
/// only sees the root partition — it read 13 % while the machine was actually 65 % busy.
/// </summary>
public sealed class PerfSensors : IDisposable
{
	private const string HyperVCpu = @"\Hyper-V Hypervisor Logical Processor(_Total)\% Total Run Time";
	private const string PlainCpu = @"\Processor Information(_Total)\% Processor Time";

	/// <summary>
	/// Adapters that are not a physical NIC. PDH reports adapter descriptions, so virtual
	/// switches, tunnels and capture drivers are filtered out by name — the user asked for
	/// traffic that actually leaves the machine.
	/// </summary>
	private static readonly string[] NotPhysical =
	{
		"Loopback", "isatap", "Teredo", "vEthernet", "Virtual", "Pseudo", "Npcap",
		"WAN Miniport", "Bluetooth", "QoS", "Filter",
	};

	private readonly PdhQuery _query = new();
	private readonly PdhQuery _processQuery = new();
	private readonly IntPtr _cpu, _netIn, _netOut, _netBandwidth, _diskRead, _diskWrite, _processIo;

	public string CounterInUse { get; } = "none";

	public PerfSensors()
	{
		_cpu = _query.Add(HyperVCpu);
		if (_cpu != IntPtr.Zero) CounterInUse = HyperVCpu;
		else
		{
			_cpu = _query.Add(PlainCpu);
			if (_cpu != IntPtr.Zero) CounterInUse = PlainCpu;
		}

		_netIn = _query.Add(@"\Network Interface(*)\Bytes Received/sec");
		_netOut = _query.Add(@"\Network Interface(*)\Bytes Sent/sec");
		_netBandwidth = _query.Add(@"\Network Interface(*)\Current Bandwidth");
		_diskRead = _query.Add(@"\LogicalDisk(*)\Disk Read Bytes/sec");
		_diskWrite = _query.Add(@"\LogicalDisk(*)\Disk Write Bytes/sec");
		_query.Collect();   // first collect only establishes the baseline

		// Kept in its own query: ~250 instances are not worth collecting on every tick.
		_processIo = _processQuery.Add(@"\Process(*)\IO Data Bytes/sec");
		_processQuery.Collect();
	}

	/// <summary>
	/// Refreshes CPU always; network and volumes only when asked. The collect itself is cheap
	/// (~4 ms for the whole query) — what costs is walking the wildcard arrays, so the caller
	/// does that at half the tick rate.
	/// </summary>
	public void Read(Readings r, bool includeIo)
	{
		_query.Collect();

		var cpu = _query.Read(_cpu);
		r.CpuLoad = cpu.HasValue ? Math.Clamp(cpu.Value, 0, 100) : null;
		if (!includeIo) return;

		const double mb = 1024.0 * 1024;
		var inBytes = SumPhysical(_query.ReadArray(_netIn));
		var outBytes = SumPhysical(_query.ReadArray(_netOut));
		r.NetInMb = inBytes / mb;
		r.NetOutMb = outBytes / mb;

		// Link speed comes from the same counters as the traffic: on a Hyper-V host the physical
		// NIC belongs to the external switch and does not appear among .NET network interfaces
		// at all — only "vEthernet (...)" does.
		var bandwidths = _query.ReadArray(_netBandwidth)
			.Where(x => !NotPhysical.Any(bad => x.Instance.Contains(bad, StringComparison.OrdinalIgnoreCase)))
			.Select(x => x.Value)
			.Where(v => v > 0)
			.ToList();
		r.NetLinkMb = bandwidths.Count == 0 ? 0 : bandwidths.Max() / 8 / mb;

		var reads = _query.ReadArray(_diskRead);
		var writes = _query.ReadArray(_diskWrite);
		r.Volumes = reads
			.Where(x => IsVolume(x.Instance))
			.Select(x => (
				Name: x.Instance,
				ReadMb: x.Value / mb,
				WriteMb: writes.FirstOrDefault(w => w.Instance == x.Instance).Value / mb))
			.OrderBy(v => v.Name)
			.ToList();
	}

	/// <summary>
	/// Processes with the most I/O. Counted across all devices — per-process counters do not
	/// split by volume, and the only thing that would is a kernel trace costing 5-10 % of a core.
	/// </summary>
	public List<(string Name, double Mb)> TopIoProcesses(int take)
	{
		_processQuery.Collect();
		const double mb = 1024.0 * 1024;
		return _processQuery.ReadArray(_processIo)
			.Where(x => x.Instance != "_Total" && x.Instance != "Idle" && x.Value > 0)
			.OrderByDescending(x => x.Value)
			.Take(take)
			.Select(x => (x.Instance, x.Value / mb))
			.ToList();
	}

	private static double SumPhysical(List<(string Instance, double Value)> rows) =>
		rows.Where(x => !NotPhysical.Any(bad => x.Instance.Contains(bad, StringComparison.OrdinalIgnoreCase)))
			.Sum(x => x.Value);

	/// <summary>Keeps lettered volumes: "_Total" and "HarddiskVolume5" are not interesting here.</summary>
	private static bool IsVolume(string instance) =>
		instance.Length >= 2 && char.IsLetter(instance[0]) && instance[1] == ':';

	public void Dispose()
	{
		_query.Dispose();
		_processQuery.Dispose();
	}
}
