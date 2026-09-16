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
	private const uint PDH_CSTATUS_VALID_DATA = 0x00000000;
	private const uint PDH_CSTATUS_NEW_DATA = 0x00000001;

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
					if (string.IsNullOrEmpty(name)) continue;
					// The array as a whole succeeds while individual elements do not: an instance
					// that appeared after the previous collect has no baseline for a rate counter,
					// and one that exited has none at all. Both hand back a zero or a stray number
					// that reads as a real measurement — every twelve seconds among ~250 processes,
					// every six among adapters and volumes.
					var status = items[i].Value.CStatus;
					if (status != PDH_CSTATUS_VALID_DATA && status != PDH_CSTATUS_NEW_DATA) continue;
					into.Add((name, items[i].Value.DoubleValue));
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

	private readonly PdhQuery _query = new();
	private readonly PdhQuery _processQuery = new();
	private readonly IntPtr _cpu, _netIn, _netOut, _netBandwidth, _diskRead, _diskWrite, _processIo;

	private readonly string[] _notPhysical;
	private readonly string[] _include;
	private readonly NetSettings _net;

	/// <summary>
	/// Physical adapters PDH listed at the last read, link or no link. An adapter that is still
	/// enumerated but carries nothing has an unplugged cable; one that is gone has been disabled
	/// or removed. Telling those apart is the whole reason the network icon greys out at all.
	/// </summary>
	private readonly HashSet<string> _adapters = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>When the process query was last collected, for the baseline rule in
	/// <see cref="TopIoProcesses"/>.</summary>
	private long _processCollectedAt;

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
		_net = net;
		_notPhysical = net?.Filters ?? NetSettings.BuiltInNotPhysical;
		_include = net?.Included ?? Array.Empty<string>();

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
		_processCollectedAt = Environment.TickCount64;
	}

	/// <summary>
	/// Hours since the machine booted.
	///
	/// This used to be <c>\System\System Up Time</c> in a PDH query of its own, kept apart
	/// because collecting the <c>System</c> object walks the process table and cost 5.7 ms per
	/// tick against 1.8 when it shared the main query. GetTickCount64 answers the same question —
	/// milliseconds since boot, sleep included, exactly like that counter — for no query, no
	/// collect and no schedule. The lesson the counter taught (look at the object, not at the
	/// counter) is still true; <c>\Process(*)</c> illustrates it and is actually needed.
	/// </summary>
	public static double ReadUptime() => Environment.TickCount64 / 3_600_000.0;

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
		// Decimal, not binary, for anything that ends up drawn in megabits: a link is rated in
		// millions of bits per second, so dividing by 1024² printed a gigabit port as 954 Mbit/s
		// and understated every reading by 4.9 % against every other tool on the machine.
		const double netMb = 1_000_000.0;

		// Link speed comes from the same counters as the traffic: on a Hyper-V host the physical
		// NIC belongs to the external switch and does not appear among .NET network interfaces
		// at all — only "vEthernet (...)" does.
		_query.ReadArray(_netIn, _in);
		_query.ReadArray(_netOut, _out);
		_query.ReadArray(_netBandwidth, _bandwidth);

		r.Nets.Clear();
		_adapters.Clear();
		Index(_out, _sentBy);
		Index(_bandwidth, _bandwidthBy);
		foreach (var x in _in)
		{
			if (!IsPhysical(x.Instance)) continue;
			_adapters.Add(x.Instance);
			var outMb = _sentBy.TryGetValue(x.Instance, out var o) ? o / netMb : 0;
			var linkMb = _bandwidthBy.TryGetValue(x.Instance, out var b) ? b / 8 / netMb : 0;
			// A configured link speed wins. Current Bandwidth is missing on some drivers and
			// meaningless on Wi-Fi (it is the momentary rate, not a capacity), and without a
			// figure to divide by the utilisation reads 0 % — which looks like an idle line
			// rather than like an unknown one.
			var configured = _net?.BandwidthFor(x.Instance);
			if (configured.HasValue) linkMb = configured.Value / 8;
			var inMb = x.Value / netMb;
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

	/// <summary>Adapters PDH still lists, whether or not they have a link. See <see cref="_adapters"/>.</summary>
	public bool AdapterStillThere(string name) => _adapters.Contains(name);

	/// <summary>
	/// Processes with the most I/O, summed per executable name. Counted across all devices —
	/// per-process counters do not split by volume, and the only thing that would is a kernel
	/// trace costing 5-10 % of a core.
	///
	/// A rate counter in PDH is the delta between the last two collects, and this query is only
	/// collected when the volumes are actually moving. After a quiet night the first sample would
	/// therefore report the average over that whole night and name whichever process was busy
	/// before it went quiet — so a sample that follows a long gap is used as a baseline only.
	/// </summary>
	public List<(string Name, double Mb)> TopIoProcesses(int take, int maxGapMs)
	{
		var now = Environment.TickCount64;
		var stale = now - _processCollectedAt > maxGapMs;
		_processQuery.Collect();
		_processCollectedAt = now;
		if (stale) return new List<(string, double)>();

		const double mb = 1024.0 * 1024;
		_processQuery.ReadArray(_processIo, _processIoRaw);
		// Summed by name: four Code processes doing 24, 18, 6 and 2 MB/s are one editor, and
		// three rows of the same name pushed everything else out of a list of three.
		var byName = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
		foreach (var x in _processIoRaw)
		{
			if (x.Value <= 0 || x.Instance == "_Total" || x.Instance == "Idle") continue;
			// PDH invents "chrome#3" for duplicates; the name before the '#' is the process.
			var hash = x.Instance.IndexOf('#');
			var name = hash > 0 ? x.Instance.Substring(0, hash) : x.Instance;
			byName.TryGetValue(name, out var sum);
			byName[name] = sum + x.Value;
		}
		return byName
			.OrderByDescending(p => p.Value)
			.Take(take)
			.Select(p => (p.Key, p.Value / mb))
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
		// An explicit include wins over the denylist. Switching the VPN or the virtual adapter you
		// actually watch back on used to mean copying the whole built-in denylist out of the README
		// and editing it — and every substring forgotten on the way brought back a Bluetooth icon.
		foreach (var wanted in _include)
			if (instance.Contains(wanted, StringComparison.OrdinalIgnoreCase))
			{
				_physical[instance] = true;
				return true;
			}
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
	}
}
