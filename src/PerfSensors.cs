using System.Runtime.InteropServices;

namespace TrayMon;

/// <summary>
/// Thin wrapper over PDH. One query holds many counters and a single collect refreshes all of
/// them, which is why CPU, network and disks share one: six counters cost 4.1 ms per collect
/// against 1.7 ms for the CPU counter alone.
/// </summary>
public sealed unsafe class PdhQuery : IDisposable
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
	/// Reads a wildcard counter as instance name → value, into a list the caller owns and reuses.
	///
	/// The buffer is kept between calls: allocating and freeing one per call, plus the extra
	/// sizing call PDH wants, made five reads per tick cost about ten times what they should.
	/// Items are read straight out of it — <c>PdhCounterItem</c> is blittable, so walking it with
	/// a pointer saves the marshaller a trip per instance, and \Process(*) has about 250 of them.
	/// </summary>
	public void ReadArray(IntPtr counter, List<(string Instance, double Value)> into)
	{
		into.Clear();
		if (counter == IntPtr.Zero) return;

		// Four attempts, not two. The first sizes the buffer and the second reads — but the set
		// of instances can grow in between, and for \Process(*) it regularly does, because
		// processes start all the time. Two attempts meant the whole list came back empty.
		for (var attempt = 0; attempt < 4; attempt++)
		{
			var size = _bufferSize;
			var rc = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out var count, _buffer);
			if (rc == 0)
			{
				if (count > into.Capacity) into.Capacity = (int)count;
				var items = (PdhCounterItem*)_buffer;
				for (var i = 0u; i < count; i++)
				{
					var name = Marshal.PtrToStringUni(items[i].Name);
					if (!string.IsNullOrEmpty(name)) into.Add((name, items[i].Value.DoubleValue));
				}
				return;
			}
			if (rc != PDH_MORE_DATA || size == 0) return;

			if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
			_bufferSize = size;
			_buffer = Marshal.AllocHGlobal((int)size);
		}
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
/// CPU load, uptime, network throughput and per-volume disk throughput — everything that comes
/// from performance counters, in one query and one collect per tick.
///
/// CPU deliberately uses the hypervisor counter: on a Hyper-V host the plain \Processor counter
/// only sees the root partition — it read 13 % while the machine was actually 65 % busy.
/// </summary>
public sealed class PerfSensors : IDisposable
{
	private const string HyperVCpu = @"\Hyper-V Hypervisor Logical Processor(_Total)\% Total Run Time";
	private const string PlainCpu = @"\Processor Information(_Total)\% Processor Time";

	private readonly PdhQuery _query = new();
	private readonly PdhQuery _processQuery = new();
	private readonly PdhQuery _uptimeQuery = new();
	private readonly IntPtr _cpu, _uptime, _netIn, _netOut, _netBandwidth, _diskRead, _diskWrite, _processIo;

	private readonly string[] _notPhysical;

	// Scratch lists, reused every tick. The number of adapters and volumes barely changes, so
	// there is no reason to hand the collector a fresh set of lists six times a minute.
	private readonly List<(string Instance, double Value)> _in = new();
	private readonly List<(string Instance, double Value)> _out = new();
	private readonly List<(string Instance, double Value)> _bandwidth = new();
	private readonly List<(string Instance, double Value)> _reads = new();
	private readonly List<(string Instance, double Value)> _writes = new();
	private readonly List<(string Instance, double Value)> _processIoRaw = new();
	private readonly Dictionary<string, double> _sentBy = new(StringComparer.Ordinal);
	private readonly Dictionary<string, double> _bandwidthBy = new(StringComparer.Ordinal);
	private readonly Dictionary<string, double> _writtenBy = new(StringComparer.Ordinal);

	// Whether a name is a physical adapter is decided by substring matching against eleven
	// patterns — deterministic per name, and the same handful of names come back every tick.
	private readonly Dictionary<string, bool> _physical = new(StringComparer.Ordinal);

	private static readonly Comparison<(string Name, double InMb, double OutMb, double LinkMb)> ByNetName =
		(a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
	private static readonly Comparison<(string Name, double ReadMb, double WriteMb)> ByVolumeName =
		(a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal);

	public string CounterInUse { get; } = "none";

	public PerfSensors(NetSettings net = null)
	{
		_notPhysical = net?.Filters ?? NetSettings.BuiltInNotPhysical;

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

		// Uptime gets its own query for the same reason, and it is not obvious: the counter is
		// a single number, but a collect gathers every object the query mentions, and the System
		// object carries Processes and Threads — so reading it walks the process table. Sharing
		// the main query for it measured 5.7 ms per tick against 1.8 ms, tripling the price of
		// the cheapest part of the program for a value that changes by two seconds per tick.
		_uptime = _uptimeQuery.Add(@"\System\System Up Time");
		_uptimeQuery.Collect();
	}

	/// <summary>
	/// Hours since the machine booted. Deliberately on its own query and its own slow schedule —
	/// see the constructor.
	/// </summary>
	public double? ReadUptime()
	{
		_uptimeQuery.Collect();
		var up = _uptimeQuery.Read(_uptime);
		return up.HasValue && up.Value >= 0 ? up.Value / 3600.0 : null;
	}

	/// <summary>
	/// Refreshes CPU always; network and volumes only when asked. The collect itself is cheap
	/// — what costs is walking the wildcard arrays, so the caller does that at a third of the
	/// tick rate. Uptime is not here: see <see cref="ReadUptime"/>.
	/// </summary>
	public void Read(Readings r, bool includeIo)
	{
		_query.Collect();

		var cpu = _query.Read(_cpu);
		r.CpuLoad = cpu.HasValue ? Math.Clamp(cpu.Value, 0, 100) : null;
		if (!includeIo) return;

		const double mb = 1024.0 * 1024;

		// Link speed comes from the same counters as the traffic: on a Hyper-V host the physical
		// NIC belongs to the external switch and does not appear among .NET network interfaces
		// at all — only "vEthernet (...)" does.
		_query.ReadArray(_netIn, _in);
		_query.ReadArray(_netOut, _out);
		_query.ReadArray(_netBandwidth, _bandwidth);

		r.Nets.Clear();
		Index(_out, _sentBy);
		Index(_bandwidth, _bandwidthBy);
		foreach (var x in _in)
		{
			if (!IsPhysical(x.Instance)) continue;
			var outMb = _sentBy.TryGetValue(x.Instance, out var o) ? o / mb : 0;
			var linkMb = _bandwidthBy.TryGetValue(x.Instance, out var b) ? b / 8 / mb : 0;
			var inMb = x.Value / mb;
			// An unplugged adapter reports zero bandwidth; one that carries traffic is kept even
			// then, so a driver that does not fill Current Bandwidth in cannot hide its icon.
			if (linkMb <= 0 && inMb <= 0 && outMb <= 0) continue;
			r.Nets.Add((x.Instance, inMb, outMb, linkMb));
		}
		r.Nets.Sort(ByNetName);

		_query.ReadArray(_diskRead, _reads);
		_query.ReadArray(_diskWrite, _writes);
		Index(_writes, _writtenBy);
		r.Volumes.Clear();
		foreach (var x in _reads)
		{
			if (!IsVolume(x.Instance)) continue;
			var write = _writtenBy.TryGetValue(x.Instance, out var w) ? w / mb : 0;
			r.Volumes.Add((x.Instance, x.Value / mb, write));
		}
		r.Volumes.Sort(ByVolumeName);
	}

	/// <summary>
	/// Builds the name → value lookup for one counter. Matching two wildcard arrays by walking
	/// one of them per element of the other was O(n²) with a string compare and a fresh closure
	/// at every step, over 15-40 instances.
	/// </summary>
	private static void Index(List<(string Instance, double Value)> items, Dictionary<string, double> into)
	{
		into.Clear();
		foreach (var x in items) into[x.Instance] = x.Value;
	}

	/// <summary>
	/// Processes with the most I/O. Counted across all devices — per-process counters do not
	/// split by volume, and the only thing that would is a kernel trace costing 5-10 % of a core.
	/// </summary>
	public List<(string Name, double Mb)> TopIoProcesses(int take)
	{
		_processQuery.Collect();
		const double mb = 1024.0 * 1024;
		_processQuery.ReadArray(_processIo, _processIoRaw);
		return _processIoRaw
			.Where(x => x.Instance != "_Total" && x.Instance != "Idle" && x.Value > 0)
			.OrderByDescending(x => x.Value)
			.Take(take)
			.Select(x => (x.Instance, x.Value / mb))
			.ToList();
	}

	private bool IsPhysical(string instance)
	{
		if (_physical.TryGetValue(instance, out var known)) return known;
		// The set of adapter descriptions on a machine is small and stable, but PDH invents
		// "_2", "_3" suffixes for duplicates and a process running for months should not be
		// able to grow this without bound.
		if (_physical.Count > 256) _physical.Clear();
		var decision = true;
		foreach (var bad in _notPhysical)
		{
			if (!instance.Contains(bad, StringComparison.OrdinalIgnoreCase)) continue;
			decision = false;
			break;
		}
		_physical[instance] = decision;
		return decision;
	}

	/// <summary>Keeps lettered volumes: "_Total" and "HarddiskVolume5" are not interesting here.</summary>
	private static bool IsVolume(string instance) =>
		instance.Length >= 2 && char.IsLetter(instance[0]) && instance[1] == ':';

	public void Dispose()
	{
		_query.Dispose();
		_processQuery.Dispose();
		_uptimeQuery.Dispose();
	}
}
