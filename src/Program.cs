using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace TrayMon;

internal static class Program
{
	/// <summary>Kept so the crash handlers can pull the icons out of the tray before exiting.</summary>
	private static TrayApp _app;

	[STAThread]
	private static int Main(string[] args)
	{
		if (args.Any(a => a.Equals("--once", StringComparison.OrdinalIgnoreCase)))
			return RunOnce(args.Any(a => a.Equals("--icons", StringComparison.OrdinalIgnoreCase)));

		// A GUID belongs to an icon, not to a process: a second copy does not duplicate the
		// icons, it takes them away from the first, which then sits there invisible. That reads
		// as "the program will not start", and the menu offers two ways to arrange it (autostart
		// plus a desktop shortcut), so it is stopped here instead of being explained in a README.
		// Local\ scopes this to the logon session, which is also the scope of a tray.
		using var single = new Mutex(true, @"Local\TrayMon.SingleInstance", out var first);
		if (!first)
		{
			MessageBox.Show(
				"TrayMon уже запущен в этом сеансе.\n\n" +
				"Второй экземпляр забрал бы значки у первого: позиция значка в трее принадлежит\n" +
				"паре «путь к exe + GUID», а не процессу.",
				"TrayMon", MessageBoxButtons.OK, MessageBoxIcon.Information);
			return 2;
		}

		ApplicationConfiguration.Initialize();

		// Without this a single exception on a tick opens a ThreadExceptionDialog every two
		// seconds — invisible on a server with the RDP session disconnected, and each one holding
		// USER handles until they run out.
		Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
		Application.ThreadException += (_, e) => _app?.Trouble(e.Exception);
		AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal(e.ExceptionObject as Exception);

		_app = new TrayApp();
		// Disposed explicitly rather than left to whoever owns the context: Dispose is what
		// sends NIM_DELETE, and an icon that is not deleted stays in the tray as a ghost until
		// the shell notices the process is gone.
		try { Application.Run(_app); }
		finally { _app.Dispose(); }
		return 0;
	}

	/// <summary>Last chance to take the icons out of the tray, so they do not linger as ghosts.</summary>
	private static void Fatal(Exception ex)
	{
		try { _app?.Panic(ex); } catch (Exception) { /* going down anyway */ }
	}

	[DllImport("kernel32.dll")]
	private static extern bool AttachConsole(int processId);

	/// <summary>
	/// Smoke test: print every value once, with the cost of each source, then exit.
	/// With --icons it also runs one full tick of the tray layer without registering anything,
	/// and prints which slot got which GUID and what it would draw — the parts of the program
	/// that are actually fragile, and that reading the sensors alone never touches.
	///
	/// Paths and addresses are redacted here: this output goes into public issue trackers.
	/// The unredacted version is the "Диагностика…" window, which stays on the machine.
	/// </summary>
	private static int RunOnce(bool withIcons)
	{
		AttachConsole(-1);   // ATTACH_PARENT_PROCESS — a WinExe has no console of its own
		var ci = CultureInfo.InvariantCulture;
		var r = new Readings();
		var sw = new Stopwatch();
		var config = Config.Load();

		using var perf = new PerfSensors(config.Net);
		Thread.Sleep(1000);   // PDH needs two samples for a rate counter
		sw.Restart(); perf.Read(r, true); var tCpu = sw.Elapsed.TotalMilliseconds;
		sw.Restart(); r.TopIo = perf.TopIoProcesses(3); var tIo = sw.Elapsed.TotalMilliseconds;

		sw.Restart(); r.UptimeHours = perf.ReadUptime(); var tUptime = sw.Elapsed.TotalMilliseconds;
		sw.Restart(); MemorySensor.Read(r); var tMem = sw.Elapsed.TotalMilliseconds;
		sw.Restart(); r.Space = SpaceSensor.Read(r.Volumes.Select(v => v.Name)); var tSpace = sw.Elapsed.TotalMilliseconds;

		using var gpu = new GpuSensor();
		sw.Restart(); gpu.Read(r); var tGpu = sw.Elapsed.TotalMilliseconds;

		using var lhm = new LhmSensor();
		sw.Restart(); var cpuTemp = lhm.ReadCpuTemp(); var tCpuTemp = sw.Elapsed.TotalMilliseconds;
		sw.Restart(); var fans = lhm.ReadFans(); var tFans = sw.Elapsed.TotalMilliseconds;
		r.Slow = new SlowReading { CpuTemp = cpuTemp, Fans = fans };
		sw.Restart(); lhm.RefreshDiskTemps(); var tDisk = sw.Elapsed.TotalMilliseconds;
		r.Disks = lhm.DiskTemps();
		var hdd = new HddSensor(config.Tools);
		sw.Restart(); r.RaidDisks = hdd.ReadAll(); var tRaid = sw.Elapsed.TotalMilliseconds;

		var ups = new UpsSensor(config.Ups);
		sw.Restart(); ups.Read(r); var tUps = sw.Elapsed.TotalMilliseconds;

		Console.WriteLine();
		Console.WriteLine($"CPU      {Fmt(r.CpuLoad)} %      {Fmt(r.Slow.CpuTemp)} °C");
		Console.WriteLine($"RAM      {Fmt(r.MemLoad)} %      {r.MemUsedGb.ToString("0.0", ci)} / {r.MemTotalGb.ToString("0.0", ci)} GB");
		if (r.Gpus.Count == 0)
			Console.WriteLine($"GPU      no NVIDIA driver ({gpu.LastError ?? "nvml.dll missing"})");
		foreach (var g in r.Gpus)
		{
			Console.WriteLine($"GPU {g.Index}    {Fmt(g.Load)} %      {Fmt(g.Temp)} °C   {g.Name}");
			Console.WriteLine($"GPU {g.Index} mem {Fmt(g.MemLoad)} %      {g.MemUsedGb.ToString("0.0", ci)} / {g.MemTotalGb.ToString("0.0", ci)} GB");
			Console.WriteLine($"GPU {g.Index} fan {(g.FanRpm.HasValue ? g.FanRpm.Value.ToString("0", ci).PadLeft(4) + " rpm" : "   — rpm")}   " +
							  $"{(g.FanDuty.HasValue ? g.FanDuty.Value.ToString("0", ci) + "%" : "—")}");
		}
		foreach (var d in r.Disks)
			Console.WriteLine($"disk     {d.Temp.ToString("0", ci)} °C     {d.Name}");
		if (r.Disks.Count == 0)
			Console.WriteLine("disk     no temperatures (elevated token missing, or disks hidden behind RAID)");
		if (r.RaidDisks.Count == 0)
			Console.WriteLine("raid     no answer over CSMI (smartctl.exe missing, or no RAID controller here)");
		foreach (var d in r.RaidDisks)
			Console.WriteLine($"raid     {d.Temp.ToString("0", ci)} °C     {d.Name} <serial>   " +
							  $"health {(string.IsNullOrEmpty(d.Health) ? "—" : d.Health)}   (behind RAID controller)");
		foreach (var f in r.Slow.Fans)
			Console.WriteLine($"fan      {f.Rpm.ToString("0", ci).PadLeft(4)} rpm   {(f.Duty.HasValue ? f.Duty.Value.ToString("0", ci) + "%" : "—")}   {f.Name}{(f.Rpm == 0 ? "   (header empty)" : "")}");
		foreach (var n in r.Nets)
			Console.WriteLine($"net      ↓ {(n.InMb * 8).ToString("0.00", ci)} / ↑ {(n.OutMb * 8).ToString("0.00", ci)} Mbit/s   " +
							  $"link {(n.LinkMb * 8).ToString("0", ci)} Mbit/s   {n.Name}");
		if (r.Nets.Count == 0)
			Console.WriteLine("net      no physical adapter with a link");
		foreach (var v in r.Volumes)
			Console.WriteLine($"volume   {v.Name,-4} R {v.ReadMb.ToString("0.00", ci).PadLeft(8)} / W {v.WriteMb.ToString("0.00", ci).PadLeft(8)} MB/s");
		foreach (var s in r.Space)
			Console.WriteLine($"free     {s.Name,-4} {s.FreeGb.ToString("0", ci).PadLeft(6)} of {s.TotalGb.ToString("0", ci)} GB");
		Console.WriteLine($"uptime   {(r.UptimeHours.HasValue ? r.UptimeHours.Value.ToString("0.0", ci) + " h" : "—")}");
		Console.WriteLine($"top io   {string.Join(", ", r.TopIo.Select(t => $"{t.Name} {t.Mb.ToString("0.0", ci)}"))}");
		if (ups.Present)
			Console.WriteLine($"ups      {Fmt(r.Ups.Charge)} %      {(r.Ups.OnBattery == true ? "on battery" : r.Ups.OnBattery == false ? "on line" : "state unknown")}, " +
							  $"{Fmt(r.Ups.RunTimeMin)} min left, load {Fmt(r.Ups.Load)} %{(r.Ups.NeedsNewBattery ? ", REPLACE BATTERY" : "")}");
		else
			Console.WriteLine($"ups      no answer over SNMP at <host from TrayMon.json> ({ups.LastError})");

		Console.WriteLine();
		Console.WriteLine($"counter in use: {perf.CounterInUse}");
		Console.WriteLine($"sensor driver:  {(lhm.Available ? "loaded" : "UNAVAILABLE — " + (lhm.LastError ?? "run elevated"))}");
		Console.WriteLine($"elevated:       {(TrayApp.IsElevated ? "yes" : "no")}");
		Console.WriteLine($"smartctl:       {(hdd.Available ? "found" : "not found")}");
		// Whether, not who: the identity would be a domain and account name, and this output goes
		// into public issue trackers. The name is in the diagnostics window instead.
		var loose = Autostart.WritableByNonAdmins(AppContext.BaseDirectory, out _);
		Console.WriteLine($"install folder: {(Autostart.LastCheckError is not null ? "permissions not readable — " + Autostart.LastCheckError : loose ? "WRITABLE by a non-administrator — do not enable autostart from here" : "writable by administrators only")}");
		Console.WriteLine($"settings file:  TrayMon.json {(File.Exists(Config.Path) ? "found" : "absent, defaults in use")}" +
						  $"{(config.LoadError is null ? "" : " — " + config.LoadError)}");
		Console.WriteLine($"cost, ms:       perf(cpu+net+disk) {tCpu.ToString("0.0", ci)}  topio {tIo.ToString("0.0", ci)}  " +
						  $"mem {tMem.ToString("0.0", ci)}  uptime {tUptime.ToString("0.0", ci)}  " +
						  $"space {tSpace.ToString("0.0", ci)}  gpu {tGpu.ToString("0.0", ci)}  " +
						  $"cputemp {tCpuTemp.ToString("0.0", ci)}  fans {tFans.ToString("0.0", ci)}  disktemp {tDisk.ToString("0.0", ci)}  " +
						  $"raid {tRaid.ToString("0.0", ci)}  ups {tUps.ToString("0.0", ci)}");

		if (withIcons)
		{
			Console.WriteLine();
			using var dry = new TrayApp(dryRun: true);
			Console.WriteLine(dry.DryRun());
		}
		Console.WriteLine();
		return 0;
	}

	private static string Fmt(double? v) => v.HasValue ? v.Value.ToString("0", CultureInfo.InvariantCulture).PadLeft(3) : "  —";
}

/// <summary>
/// Tray icons driven by one timer: CPU %, RAM GB, GPU %, VRAM GB, a temperature per sensor
/// (CPU package, GPU, every NVMe) and a speed per running fan. Sources are polled at different
/// rates because they cost different amounts: CPU/RAM are syscall-cheap, a SMART query is not.
///
/// Every icon is a slot with a stable id; which slots are shown, what colour their plate is
/// and how they are labelled comes from <see cref="Config"/> and is edited from the tray menu.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
	private const int TickMs = 2000;

	// Phases as well as periods. Two schedules that share a divisor land on the same tick every
	// time, and that turns a smooth 25 ms tick into a periodic 75 ms one with the message pump
	// stopped: 30 was a multiple of 3, so the SMART refresh met the sensor-library read once a
	// minute exactly. The offsets below keep the heavy readers apart.
	private const int GpuEveryTicks = 2;         // 4 s
	private const int SlowEveryTicks = 3;        // 6 s — CPU temperature and fans, phase 1
	private const int IoEveryTicks = 3;          // 6 s — network and volume throughput, phase 0
	private const int TopIoEveryTicks = 6;       // 12 s — the ~250-instance process query, phase 4
	private const int DiskEveryTicks = 31;       // 62 s — prime, so it never falls in step with 3
	private const int RaidDiskEveryTicks = 300;  // 600 s — spawns smartctl.exe; HDD temperature drifts slowly
	private const int UpsEveryTicks = 15;        // 30 s — a UDP round trip to the SNMP agent
	private const int SpaceEveryTicks = 150;     // 300 s — free space changes slowly and costs a syscall
	private const int UptimeEveryTicks = 150;    // 300 s — collecting the System object is not cheap
	private const int StatsEveryTicks = 15;      // 30 s — recompute the min/avg/max line for tooltips

	/// <summary>A source silent for this long has its tray slot handed back to the pool.</summary>
	private const int ForgetAfterTicks = 43200;   // 24 h

	/// <summary>Thresholds high enough that no reading reaches them — the "no alert colouring" state.</summary>
	private const double NeverAlerts = 1e9;

	// Built-in plate colours: one per metric family, so icons are told apart without reading them.
	// No two are alike — GPU and "CPU temperature" used to be the same maroon down to the byte,
	// which made two unrelated metrics indistinguishable. A temperature now carries a lighter
	// shade of the family it belongs to, except the GPU one, which stays blue because a lighter
	// maroon would collide with the critical plate.
	private static readonly Color CpuPlate = Color.FromArgb(128, 0, 255);      // violet
	private static readonly Color RamPlate = Color.FromArgb(64, 128, 128);     // teal
	private static readonly Color GpuPlate = Color.FromArgb(128, 0, 0);        // maroon
	private static readonly Color VramPlate = Color.FromArgb(0, 128, 0);       // green
	private static readonly Color CpuTempPlate = Color.FromArgb(96, 48, 176);  // muted violet — CPU family
	private static readonly Color GpuTempPlate = Color.FromArgb(0, 0, 255);    // blue
	private static readonly Color DiskTempPlate = Color.FromArgb(255, 128, 0); // orange
	private static readonly Color FanPlate = Color.FromArgb(255, 0, 255);      // magenta
	private static readonly Color GpuFanPlate = Color.FromArgb(176, 0, 176);   // dark magenta — fan family
	private static readonly Color NetPlate = Color.FromArgb(0, 128, 192);      // azure
	private static readonly Color VolumePlate = Color.FromArgb(128, 64, 0);    // brown
	private static readonly Color FreePlate = Color.FromArgb(96, 96, 32);      // olive
	private static readonly Color RaidTempPlate = Color.FromArgb(255, 128, 0); // orange
	private static readonly Color UpsPlate = Color.FromArgb(64, 84, 104);      // steel — legible under white digits
	private static readonly Color UptimePlate = Color.FromArgb(72, 72, 88);    // slate
	private static readonly Color WorstPlate = Color.FromArgb(0, 96, 96);      // dark cyan

	// ---- per-icon identity for the tray ----
	//
	// Windows keeps the position the user dragged an icon to against these values — never
	// renumber them, or every icon jumps back to the end of the queue with its visibility reset.
	//
	// The suffixes are laid out in blocks of ten, not consecutively, so a family can grow
	// without stepping on the next one:
	//
	//     01-09  single icons          0A-0F  free (single icons)
	//     11-14  NVMe temperature      15-1F  free (NVMe)
	//     21-28  motherboard fans      29-2F  free (fans)
	//     31-38  volume throughput     39-3F  free (volumes)
	//     41-44  RAID disks            45-4F  free (RAID)
	//     51-57  network adapters      58-5F  free (network)
	//     61-68  free space            69-6F  free (space)
	//     71-7C  second and later GPU  7D+    free
	//
	// Add a new icon by taking the next value inside its block, or a whole free block for a new
	// family — never by inserting into the middle of one.
	private const string GuidPrefix = "6f2a1c40-9d3b-4f7e-a1c2-7c9e5b0000";
	private static readonly Guid CpuGuid = new(GuidPrefix + "01");
	private static readonly Guid RamGuid = new(GuidPrefix + "02");
	private static readonly Guid VramGuid = new(GuidPrefix + "04");
	private static readonly Guid CpuTempGuid = new(GuidPrefix + "05");
	private static readonly Guid UpsGuid = new(GuidPrefix + "09");
	private static readonly Guid WorstGuid = new(GuidPrefix + "0A");
	private static readonly Guid UptimeGuid = new(GuidPrefix + "0B");

	// The first slot of each GPU family reuses the GUID of the single-card icon it used to be,
	// so a machine with one card keeps the places its icons were dragged to.
	private static readonly Guid[] GpuGuids = { new(GuidPrefix + "03"), new(GuidPrefix + "71"), new(GuidPrefix + "72"), new(GuidPrefix + "73") };
	private static readonly Guid[] VramGuids = { VramGuid, new(GuidPrefix + "74"), new(GuidPrefix + "75"), new(GuidPrefix + "76") };
	private static readonly Guid[] GpuTempGuids = { new(GuidPrefix + "06"), new(GuidPrefix + "77"), new(GuidPrefix + "78"), new(GuidPrefix + "79") };
	private static readonly Guid[] GpuFanGuids = { new(GuidPrefix + "07"), new(GuidPrefix + "7A"), new(GuidPrefix + "7B"), new(GuidPrefix + "7C") };

	// The first slot reuses the GUID of the single "NET" icon this used to be, for the same reason.
	private static readonly Guid[] NetGuids =
	{
		new(GuidPrefix + "08"), new(GuidPrefix + "51"), new(GuidPrefix + "52"), new(GuidPrefix + "53"),
		new(GuidPrefix + "54"), new(GuidPrefix + "55"), new(GuidPrefix + "56"), new(GuidPrefix + "57"),
	};
	private static readonly Guid[] RaidTempGuids =
	{
		new(GuidPrefix + "41"), new(GuidPrefix + "42"), new(GuidPrefix + "43"), new(GuidPrefix + "44"),
	};
	private static readonly Guid[] DiskGuids =
	{
		new(GuidPrefix + "11"), new(GuidPrefix + "12"), new(GuidPrefix + "13"), new(GuidPrefix + "14"),
	};
	private static readonly Guid[] VolumeGuids =
	{
		new(GuidPrefix + "31"), new(GuidPrefix + "32"), new(GuidPrefix + "33"), new(GuidPrefix + "34"),
		new(GuidPrefix + "35"), new(GuidPrefix + "36"), new(GuidPrefix + "37"), new(GuidPrefix + "38"),
	};
	private static readonly Guid[] FreeGuids =
	{
		new(GuidPrefix + "61"), new(GuidPrefix + "62"), new(GuidPrefix + "63"), new(GuidPrefix + "64"),
		new(GuidPrefix + "65"), new(GuidPrefix + "66"), new(GuidPrefix + "67"), new(GuidPrefix + "68"),
	};
	private static readonly Guid[] FanGuids =
	{
		new(GuidPrefix + "21"), new(GuidPrefix + "22"), new(GuidPrefix + "23"), new(GuidPrefix + "24"),
		new(GuidPrefix + "25"), new(GuidPrefix + "26"), new(GuidPrefix + "27"), new(GuidPrefix + "28"),
	};

	/// <summary>
	/// Which SuperIO header carries the CPU cooler. Chips rarely say so: most report plain
	/// "Fan #N". If a sensor names itself (some boards do), that name is used as is; otherwise
	/// the first header gets the "CPU fan" hint, which holds on most desktop boards — and the
	/// user can rename any icon from the menu.
	/// </summary>
	private const string CpuFanSensorName = "Fan #1";

	// ---- metric descriptions ----

	/// <summary>
	/// The constant half of an icon: everything that does not change while the program runs.
	/// Keeping colour, thresholds, unit, menu group and GUID pool together is what stops the
	/// tables that describe one metric from drifting apart across three hundred lines.
	/// </summary>
	private sealed class Metric
	{
		public string Group;      // heading in "Показывать значки"
		public int Order;         // stable position inside that heading
		public string Label;      // default caption
		public string Unit;       // for the threshold hint in the menu
		public Color Plate;
		public double Warn, Crit;
		public Guid[] Pool;       // one entry for a single icon, several for a family
		public bool Inverted;     // low values are the alarming ones
	}

	private static readonly Metric CpuMetric = new() { Group = "Процессор", Order = 0, Label = "CPU", Unit = "%", Plate = CpuPlate, Warn = 70, Crit = 85, Pool = new[] { CpuGuid } };
	private static readonly Metric CpuTempMetric = new() { Group = "Процессор", Order = 1, Label = "Температура CPU", Unit = "°C", Plate = CpuTempPlate, Warn = 75, Crit = 85, Pool = new[] { CpuTempGuid } };
	private static readonly Metric RamMetric = new() { Group = "Память", Order = 0, Label = "RAM", Unit = "% объёма", Plate = RamPlate, Warn = 88, Crit = 95, Pool = new[] { RamGuid } };
	private static readonly Metric GpuMetric = new() { Group = "Видеокарта", Order = 0, Label = "GPU", Unit = "%", Plate = GpuPlate, Warn = 75, Crit = 90, Pool = GpuGuids };
	private static readonly Metric VramMetric = new() { Group = "Видеокарта", Order = 1, Label = "VRAM", Unit = "% объёма", Plate = VramPlate, Warn = 75, Crit = 90, Pool = VramGuids };
	private static readonly Metric GpuTempMetric = new() { Group = "Видеокарта", Order = 2, Label = "Температура GPU", Unit = "°C", Plate = GpuTempPlate, Warn = 80, Crit = 90, Pool = GpuTempGuids };
	private static readonly Metric GpuFanMetric = new() { Group = "Видеокарта", Order = 3, Label = "Вентилятор GPU", Unit = "об/мин", Plate = GpuFanPlate, Warn = 50, Crit = 90, Pool = GpuFanGuids, Inverted = true };
	private static readonly Metric DiskMetric = new() { Group = "Диски", Order = 0, Label = "NVMe", Unit = "°C", Plate = DiskTempPlate, Warn = 60, Crit = 70, Pool = DiskGuids };
	private static readonly Metric RaidMetric = new() { Group = "Диски", Order = 1, Label = "Диск за RAID", Unit = "°C", Plate = RaidTempPlate, Warn = 55, Crit = 65, Pool = RaidTempGuids };
	private static readonly Metric FanMetric = new() { Group = "Вентиляторы", Order = 0, Label = "Вентилятор", Unit = "об/мин", Plate = FanPlate, Warn = 50, Crit = 90, Pool = FanGuids, Inverted = true };
	private static readonly Metric NetMetric = new() { Group = "Сеть", Order = 0, Label = "Сеть", Unit = "% полосы", Plate = NetPlate, Warn = 70, Crit = 90, Pool = NetGuids };
	private static readonly Metric VolumeMetric = new() { Group = "Тома", Order = 0, Label = "Том", Unit = "МБ/с", Plate = VolumePlate, Warn = NeverAlerts, Crit = NeverAlerts, Pool = VolumeGuids };
	private static readonly Metric FreeMetric = new() { Group = "Тома", Order = 1, Label = "Свободно", Unit = "% занято", Plate = FreePlate, Warn = 85, Crit = 95, Pool = FreeGuids, Inverted = true };
	private static readonly Metric UpsMetric = new() { Group = "Питание", Order = 0, Label = "ИБП", Unit = "% заряда", Plate = UpsPlate, Warn = 50, Crit = 75, Pool = new[] { UpsGuid }, Inverted = true };
	private static readonly Metric UptimeMetric = new() { Group = "Прочее", Order = 0, Label = "Время работы", Unit = "ч", Plate = UptimePlate, Warn = NeverAlerts, Crit = NeverAlerts, Pool = new[] { UptimeGuid } };
	// 78 and 100 on the "percent of the way to its own red line" scale: at 78 every built-in
	// metric has just reached its own yellow threshold, and 100 means some metric is at its red.
	private static readonly Metric WorstMetric = new() { Group = "Прочее", Order = 1, Label = "Худшее состояние", Unit = "% от красного порога", Plate = WorstPlate, Warn = 78, Crit = 100, Pool = new[] { WorstGuid } };

	/// <summary>Order the headings appear in the menu, so a tick is where it was yesterday.</summary>
	private static readonly string[] Groups =
		{ "Процессор", "Память", "Видеокарта", "Диски", "Вентиляторы", "Сеть", "Тома", "Питание", "Прочее" };

	// ---- state ----

	/// <summary>
	/// Hands out a stable tray identity per key from a fixed pool, and never hands the same one
	/// to two live keys. Adapters, volumes, disks and fans all come and go while the machine
	/// runs, and a GUID taken by position in a sorted list moves the moment a new letter or a
	/// new serial number appears — the icon that owned it is then deleted by the newcomer's
	/// registration and never comes back.
	/// </summary>
	private sealed class GuidPool
	{
		private readonly Guid[] _pool;
		private readonly Dictionary<string, Guid> _taken = new(StringComparer.Ordinal);
		private readonly HashSet<string> _refused = new(StringComparer.Ordinal);

		public GuidPool(string what, Guid[] pool) { What = what; _pool = pool; }

		/// <summary>What this pool holds, for the "there was not enough room" message.</summary>
		public string What { get; }

		public int Size => _pool.Length;
		public int Used => _taken.Count;

		/// <summary>How many distinct sources were left without an icon. The limits used to be
		/// silent: twelve volumes on a server simply meant four of them did not exist, and the
		/// only place that said so was a line in the README.</summary>
		public int Refused => _refused.Count;

		public Guid? For(string key)
		{
			if (_taken.TryGetValue(key, out var known)) return known;
			foreach (var candidate in _pool)
			{
				if (_taken.ContainsValue(candidate)) continue;
				_taken[key] = candidate;
				_refused.Remove(key);
				return candidate;
			}
			if (_refused.Count < 64) _refused.Add(key);   // more sources than prepared slots
			return null;
		}

		public void Release(string key) => _taken.Remove(key);
	}

	/// <summary>Five minutes of one metric, for the min/avg/max line in the tooltip.</summary>
	private sealed class Stats
	{
		private readonly double[] _values = new double[150];
		private int _count, _at;

		public void Add(double v)
		{
			_values[_at] = v;
			_at = (_at + 1) % _values.Length;
			if (_count < _values.Length) _count++;
		}

		public bool Ready => _count > 1;

		public (double Min, double Avg, double Max) Window()
		{
			double min = double.MaxValue, max = double.MinValue, sum = 0;
			for (var i = 0; i < _count; i++)
			{
				var v = _values[i];
				if (v < min) min = v;
				if (v > max) max = v;
				sum += v;
			}
			return (min, sum / _count, max);
		}
	}

	private sealed class IconSlot
	{
		public string Id;
		public Metric Metric;
		public string DefaultLabel;
		public Guid Guid;
		public GuidPool Pool;      // null for a single icon that can never be reassigned
		public string PoolKey;
		public IconSettings Settings = IconSettings.Default;
		public TrayValueIcon Icon;
		public string LastText;
		public double? LastSeverity;
		public string LastDetail = "";
		public long SeenTick = -1;
		public bool Dead;
		/// <summary>Severity of the last reading, kept even for an icon nobody is showing —
		/// the summary icon exists precisely so a hidden metric can still raise the alarm.</summary>
		public double? Severity;
		public readonly Stats Stats = new();
		public string StatsText = "";
	}

	private Config _config = Config.Load();
	private readonly Dictionary<string, IconSlot> _slots = new(StringComparer.Ordinal);
	private readonly List<IconSlot> _order = new();

	private readonly GuidPool _gpuPool, _vramPool, _gpuTempPool, _gpuFanPool;
	private readonly GuidPool _netPool, _volumePool, _freePool, _diskPool, _raidPool, _fanPool;
	private readonly GuidPool[] _pools;

	private readonly PerfSensors _perf;
	private readonly GpuSensor _gpu = new();
	private readonly LhmSensor _lhm = new();
	private readonly HddSensor _hdd;
	private readonly Readings _r = new();
	private readonly UpsSensor _ups;

	private readonly ContextMenuStrip _menu = new();

	/// <summary>True while the "Показывать значки" list is open, which is when a click on an item
	/// must not close the menu. See the handler in the constructor.</summary>
	private bool _pickingIcons;
	private readonly System.Windows.Forms.Timer _timer;
	private readonly bool _dry;

	/// <summary>Fans that were spinning at startup. Headers with nothing plugged in stay hidden,
	/// but a fan that stops later keeps its icon and turns red.</summary>
	private List<string> _fanNames;

	private long _tick;
	private bool _firstRun;
	private bool _configDirty;
	private bool _greeted;
	private string _topIoLine = "";
	private bool? _lastOnBattery;
	private DateTime _lastLog = DateTime.MinValue;

	// Interlocked, not plain bools: a check followed by a set is not atomic, and the case the
	// flag exists for — a refresh that outlives its own period — is exactly the case where two
	// threads reach it together.
	private int _diskRefreshRunning;
	private int _raidRefreshRunning;
	private int _upsRefreshRunning;
	private int _slowRefreshRunning;

	private readonly List<Task> _background = new();

	/// <summary>Built once in the constructor so the per-tick loop over them allocates nothing.</summary>
	private readonly (string Name, Action Show)[] _families;

	// Source versions, so a family polled once every ten minutes is not re-formatted every two
	// seconds just to be thrown away by the deduplication inside the icon.
	private int _shownDisks = -1, _shownRaid = -1, _shownUps = -1;
	// Bumped by the pool threads that publish these readings and read on the UI thread through
	// Volatile.Read. Not declared volatile, because a volatile field cannot be passed by ref to
	// Interlocked without a warning that the reference is not treated as volatile.
	private int _disksVersion, _raidVersion, _upsVersion;

	private string _lastTickError;
	private int _tickErrors;
	private long _tickTicks;

	public static bool IsElevated
	{
		get
		{
			try
			{
				using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
				return new System.Security.Principal.WindowsPrincipal(identity)
					.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
			}
			catch (Exception) { return false; }
		}
	}

	public TrayApp(bool dryRun = false)
	{
		_dry = dryRun;
		_firstRun = !File.Exists(Config.Path);
		_perf = new PerfSensors(_config.Net);
		_hdd = new HddSensor(_config.Tools);
		_ups = new UpsSensor(_config.Ups);

		_gpuPool = new GuidPool("видеокарт", GpuGuids);
		_vramPool = new GuidPool("значков видеопамяти", VramGuids);
		_gpuTempPool = new GuidPool("температур GPU", GpuTempGuids);
		_gpuFanPool = new GuidPool("вентиляторов GPU", GpuFanGuids);
		_netPool = new GuidPool("сетевых адаптеров", NetGuids);
		_volumePool = new GuidPool("томов", VolumeGuids);
		_freePool = new GuidPool("значков свободного места", FreeGuids);
		_diskPool = new GuidPool("дисков NVMe", DiskGuids);
		_raidPool = new GuidPool("дисков за RAID", RaidTempGuids);
		_fanPool = new GuidPool("вентиляторов", FanGuids);
		_pools = new[] { _gpuPool, _vramPool, _gpuTempPool, _gpuFanPool, _netPool,
						 _volumePool, _freePool, _diskPool, _raidPool, _fanPool };

		_families = new (string, Action)[]
		{
			("ядро", ShowCore), ("GPU", ShowGpus), ("NVMe", ShowDisks), ("RAID", ShowRaidDisks),
			("вентиляторы", ShowFans), ("сеть", ShowNetwork), ("тома", ShowVolumes),
			("свободное место", ShowFreeSpace), ("ИБП", ShowUps), ("время работы", ShowUptime),
			("сводный значок", ShowWorst),
		};

		// A click on any menu item closes the whole chain, submenu included, so ticking five icons
		// in the list meant reopening the menu five times. The close is cancelled while that list
		// is the thing being used — and only then: every other item here is a command, and a
		// command that leaves its menu on the screen is worse than the reopening was.
		_menu.Closing += (_, e) =>
		{
			if (_pickingIcons && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
		};

		if (_dry) return;

		Spawn(RefreshDisks);   // first SMART query off the UI thread
		Spawn(RefreshUps);
		Spawn(RefreshSlow);

		_timer = new System.Windows.Forms.Timer { Interval = TickMs };
		_timer.Tick += OnTick;
		_timer.Start();
		OnTick(null, EventArgs.Empty);
	}

	/// <summary>
	/// One tick without the tray: builds every slot, assigns every GUID and formats every
	/// number, then reports it. This is the part --once never exercised, and it is the part the
	/// project calls its most fragile — GUID assignment, dead sources, conditional icons.
	/// </summary>
	public string DryRun()
	{
		_perf.Read(_r, true);
		MemorySensor.Read(_r);
		_gpu.Read(_r);
		_r.Slow = _lhm.ReadSlow();
		_lhm.RefreshDiskTemps();
		_r.Disks = _lhm.DiskTemps();
		_r.RaidDisks = _hdd.ReadAll();
		_ups.Read(_r);
		_r.Space = SpaceSensor.Read(_r.Volumes.Select(v => v.Name));
		_r.UptimeHours = _perf.ReadUptime();
		OnTick(null, EventArgs.Empty);   // through OnTick, so the tick times itself here too

		var report = new StringBuilder();
		report.AppendLine($"icons: {_order.Count(s => s.Settings.Enabled)} shown of {_order.Count} known");
		foreach (var slot in _order)
			report.AppendLine(
				$"  {(slot.Settings.Enabled ? "on " : "off")} {slot.Guid.ToString()[^2..]}  " +
				$"{slot.Id,-28} {(slot.LastText ?? "—"),5}  {LabelOf(slot)}   {slot.LastDetail}");
		foreach (var line in Overflows()) report.AppendLine("  " + line);
		report.AppendLine($"renders: {TrayValueIcon.Renders}   shell calls: {TrayValueIcon.ShellCalls}   " +
						  $"tick {(_tickTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, _tick)).ToString("0.00", CultureInfo.InvariantCulture)} ms");
		return report.ToString();
	}

	// ---- the tick ----

	private void OnTick(object sender, EventArgs e)
	{
		// One bad tick must cost one tick, not the program. The sources that throw are real —
		// NVML after a driver reset, the sensor library while its driver unloads — and the
		// alternative here is a modal dialog every two seconds on a machine nobody is looking at.
		// The tick times itself. Two QueryPerformanceCounter calls per two seconds cost nothing,
		// and until now the running program could not answer the one question it exists to
		// answer — what does watching this machine cost — outside of a console mode that needs
		// an elevated console to start.
		var started = Stopwatch.GetTimestamp();
		try { Tick(); }
		catch (Exception ex) { Trouble(ex); }
		finally { _tickTicks += Stopwatch.GetTimestamp() - started; }
	}

	private void Tick()
	{
		_tick++;
		_perf.Read(_r, _tick % IoEveryTicks == 0);   // CPU every tick, network and volumes every third
		MemorySensor.Read(_r);
		if (_tick % GpuEveryTicks == 0) _gpu.Read(_r);

		// The heavy readers are all off the UI thread now, each behind its own flag. The sensor
		// library alone measured 18-45 ms, which is a fifth of a second of a frozen message pump
		// every six seconds when it ran here.
		if (_tick % SlowEveryTicks == 1) Spawn(RefreshSlow);
		if (_tick % TopIoEveryTicks == 4)
			// Only worth ~250 counter instances when something is actually moving.
			_r.TopIo = _r.Volumes.Sum(v => v.ReadMb + v.WriteMb) > 0.5 ? _perf.TopIoProcesses(3) : new();
		if (_tick % DiskEveryTicks == 5) Spawn(RefreshDisks);
		// Offsets 1 and 2: smartctl and the SNMP round trip are the two slowest things here, and
		// starting them on the same tick would put both on the pool at once for no reason.
		if (_tick % RaidDiskEveryTicks == 1) Spawn(RefreshRaidDisk);
		if (_tick % UpsEveryTicks == 2) Spawn(RefreshUps);
		if (_tick % SpaceEveryTicks == 7) _r.Space = SpaceSensor.Read(_r.Volumes.Select(v => v.Name));
		// Uptime is a separate, rarely collected query — its counter looks free and is not.
		if (_tick % UptimeEveryTicks == 8) _r.UptimeHours = _perf.ReadUptime();

		// Each family on its own: a source that throws — NVML after a driver reset, the sensor
		// library while its driver unloads — must cost its own icons, not every icon that
		// happens to be displayed after it in this method. The array is built once, so the loop
		// allocates nothing per tick.
		foreach (var family in _families)
		{
			try { family.Show(); }
			catch (Exception ex) { Trouble(ex, family.Name); }
		}

		Fade();
		FirstRunHousekeeping();
		// Once the first tick has registered everything this machine has: a settings file with
		// every icon switched off is a legitimate hand edit, and it used to leave a process with
		// no icons, no menu and no way out but Task Manager.
		// On the second tick, not the first: the first runs inside the constructor, before the
		// message loop exists, and these can put a dialog on the screen.
		if (_tick == 2 && !_dry)
		{
			// Losing every colour, label and threshold is not something to mention only to
			// someone who thinks to open the diagnostics window.
			if (_config.LoadError is not null)
				Info($"Файл настроек не прочитан, взяты значения по умолчанию:\n\n{_config.LoadError}",
					MessageBoxIcon.Warning);
			EnsureSomethingVisible();
		}
		if (_tick % StatsEveryTicks == 3) RefreshStats();
		WriteLogLine();
	}

	/// <summary>CPU, memory and the CPU package temperature — the icons every machine has.</summary>
	private void ShowCore()
	{
		var ci = CultureInfo.InvariantCulture;

		var cpu = Slot("cpu", CpuMetric, CpuGuid);
		Record(cpu, _r.CpuLoad);
		if (cpu.Settings.Enabled)
			Show(cpu, Whole(_r.CpuLoad), _r.CpuLoad, $"{Pct(_r.CpuLoad)}   температура {Deg(_r.Slow.CpuTemp)}{cpu.StatsText}");

		// 88/95 rather than 80/90: on this hypervisor 156 of 192 GB in use is the normal
		// resting state, and a plate that is permanently yellow says nothing.
		var ram = Slot("ram", RamMetric, RamGuid);
		// Recorded in the unit the icon draws (gigabytes), coloured by the unit the thresholds
		// are in (percent) — otherwise the five-minute line in the tooltip is in a different
		// unit from the number above it.
		Record(ram, _r.MemLoad.HasValue ? _r.MemUsedGb : null, _r.MemLoad);
		if (ram.Settings.Enabled)
			Show(ram, Whole(_r.MemLoad.HasValue ? _r.MemUsedGb : null), _r.MemLoad,
				$"{_r.MemUsedGb.ToString("0.0", ci)} / {_r.MemTotalGb.ToString("0.0", ci)} ГБ   {Pct(_r.MemLoad)}{ram.StatsText}");

		// Only when there is a sensor library to read at all. A machine with no NVIDIA card used
		// to carry three permanent grey dashes and one without an elevated token a fourth, while
		// the README promised the opposite. What is missing now says why, in the tooltip.
		if (!_lhm.Available) return;
		var slow = _r.Slow;
		var cpuTemp = Slot("cpu.temp", CpuTempMetric, CpuTempGuid);
		Record(cpuTemp, slow.CpuTemp);
		if (cpuTemp.Settings.Enabled)
			Show(cpuTemp, Whole(slow.CpuTemp), slow.CpuTemp, $"пакет {Deg(slow.CpuTemp)}{cpuTemp.StatsText}");
	}

	/// <summary>
	/// Turns a slot that stopped being fed into a grey dash with a reason, and eventually gives
	/// its tray identity back. A monitor that keeps showing the last number of a source that
	/// went away is worse than one that says nothing: "network 0 Mbit/s" and "no network cable"
	/// look identical, and only one of them is true.
	/// </summary>
	private void Fade()
	{
		for (var i = _order.Count - 1; i >= 0; i--)
		{
			var slot = _order[i];
			if (slot.SeenTick == _tick) { slot.Dead = false; continue; }

			if (!slot.Dead)
			{
				slot.Dead = true;
				slot.LastText = null;
				slot.LastSeverity = null;
				slot.LastDetail = Reason(slot);
				slot.Icon?.Update(null, null, $"{LabelOf(slot)}   {slot.LastDetail}");
				continue;
			}

			// Gone for a day: hand the identity back so the next adapter or volume can have it.
			// Without this the pool of eight fills up with names last seen in March, and the
			// menu fills with them too.
			if (slot.Pool is null || _tick - slot.SeenTick <= ForgetAfterTicks) continue;
			slot.Icon?.Dispose();
			slot.Icon = null;
			slot.Pool.Release(slot.PoolKey);
			_slots.Remove(slot.Id);
			_order.RemoveAt(i);
		}
	}

	/// <summary>Why an icon went grey — the text that turns a silent dash into an explanation.</summary>
	private string Reason(IconSlot slot)
	{
		if (slot.Id.StartsWith("net.", StringComparison.Ordinal)) return "адаптер пропал из системы";
		if (slot.Id.StartsWith("vol.", StringComparison.Ordinal) ||
			slot.Id.StartsWith("free.", StringComparison.Ordinal)) return "том отключён";
		if (slot.Id.StartsWith("disk.raid.", StringComparison.Ordinal))
			return _hdd.LastError ?? "диск за RAID не отвечает";
		if (slot.Id.StartsWith("disk.", StringComparison.Ordinal)) return SensorHint("SMART не отдал температуру");
		if (slot.Id.StartsWith("fan.gpu", StringComparison.Ordinal)) return _gpu.LastError ?? "видеокарта не отвечает";
		if (slot.Id.StartsWith("fan.", StringComparison.Ordinal)) return SensorHint("датчик пропал");
		if (slot.Id == "ups") return _ups.LastError ?? "нет ответа от SNMP-агента";
		if (slot.Id == "cpu.temp") return SensorHint("датчик не отвечает");
		if (slot.Id.StartsWith("gpu", StringComparison.Ordinal) ||
			slot.Id.StartsWith("vram", StringComparison.Ordinal)) return _gpu.LastError ?? "нет драйвера NVIDIA";
		return "источник молчит";
	}

	/// <summary>
	/// Why a sensor-library reading is missing. The elevation check comes first on purpose: the
	/// library opens perfectly well without an elevated token and then returns null for every
	/// temperature, so "Available" is not the same as "can read", and the user needs to be told
	/// the one thing that would actually fix it.
	/// </summary>
	private string SensorHint(string whenWorking) =>
		!IsElevated ? "нужен запуск от администратора — температуры читаются через драйвер"
		: !_lhm.Available ? "драйвер датчиков не загрузился" + (_lhm.LastError is null ? "" : ": " + _lhm.LastError)
		: whenWorking;

	// ---- the metric families ----

	private void ShowGpus()
	{
		foreach (var g in _r.Gpus)
		{
			var key = g.Index.ToString(CultureInfo.InvariantCulture);
			var suffix = _r.Gpus.Count > 1 ? $" {g.Index + 1}" : "";

			var load = Pooled($"gpu.{key}", GpuMetric, _gpuPool, key, GpuMetric.Label + suffix);
			if (load is not null)
			{
				Record(load, g.Load);
				if (load.Settings.Enabled)
					Show(load, Whole(g.Load), g.Load, $"{g.Name}   ядро {Pct(g.Load)}   температура {Deg(g.Temp)}{load.StatsText}");
			}

			var vram = Pooled($"vram.{key}", VramMetric, _vramPool, key, VramMetric.Label + suffix);
			if (vram is not null)
			{
				Record(vram, g.MemLoad.HasValue ? g.MemUsedGb : null, g.MemLoad);
				if (vram.Settings.Enabled)
					Show(vram, Whole(g.MemLoad.HasValue ? g.MemUsedGb : null), g.MemLoad,
						$"{g.MemUsedGb.ToString("0.0", CultureInfo.InvariantCulture)} / " +
						$"{g.MemTotalGb.ToString("0.0", CultureInfo.InvariantCulture)} ГБ   {Pct(g.MemLoad)}{vram.StatsText}");
			}

			var temp = Pooled($"gpu.temp.{key}", GpuTempMetric, _gpuTempPool, key, GpuTempMetric.Label + suffix);
			if (temp is not null)
			{
				Record(temp, g.Temp);
				if (temp.Settings.Enabled)
					Show(temp, Whole(g.Temp), g.Temp, $"{Deg(g.Temp)}   загрузка {Pct(g.Load)}{temp.StatsText}");
			}

			if (!g.FanRpm.HasValue && !g.FanDuty.HasValue) continue;
			// A driver without nvmlDeviceGetFanSpeedRPM leaves only the duty cycle, and a bare
			// "35" next to fan icons where "1.2" means twelve hundred revolutions is a different
			// unit with nothing to say so. The caption carries the unit in that case.
			var byDuty = !g.FanRpm.HasValue;
			var fanLabel = GpuFanMetric.Label + suffix + (byDuty ? " (%)" : "");
			var fan = Pooled($"fan.gpu.{key}", GpuFanMetric, _gpuFanPool, key, fanLabel);
			if (fan is null) continue;
			var speed = g.FanRpm ?? g.FanDuty ?? 0;
			Record(fan, speed, Stalled(speed));
			if (!fan.Settings.Enabled) continue;
			Show(fan,
				byDuty ? Whole(g.FanDuty) : Rpm(g.FanRpm.Value),
				Stalled(speed),
				byDuty
					? $"{Pct(g.FanDuty)} задания (драйвер не сообщает обороты){fan.StatsText}"
					: $"{g.FanRpm.Value.ToString("0", CultureInfo.InvariantCulture)} об/мин   задание {Pct(g.FanDuty)}{fan.StatsText}");
		}
	}

	/// <summary>One icon per disk; they appear once the first SMART query comes back.</summary>
	private void ShowDisks()
	{
		var disks = _r.Disks;   // one read of the field: it is replaced whole from a pool thread
		var version = Volatile.Read(ref _disksVersion);
		var changed = version != _shownDisks;
		for (var i = 0; i < disks.Count; i++)
		{
			var d = disks[i];
			var slot = Pooled($"disk.{i}", DiskMetric, _diskPool, i.ToString(CultureInfo.InvariantCulture), d.Name);
			if (slot is null) continue;
			Record(slot, d.Temp);
			if (!changed || !slot.Settings.Enabled) continue;
			Show(slot, Whole(d.Temp), d.Temp, $"{Deg(d.Temp)}{slot.StatsText}");
		}
		_shownDisks = version;
	}

	/// <summary>
	/// One icon per disk behind a RAID controller, keyed by serial number — so the icon and its
	/// settings follow the disk, and the tray identity follows the icon, even when the controller
	/// renumbers its ports.
	/// </summary>
	private void ShowRaidDisks()
	{
		var disks = _r.RaidDisks;
		var version = Volatile.Read(ref _raidVersion);
		var changed = version != _shownRaid;
		for (var i = 0; i < disks.Count; i++)
		{
			var d = disks[i];
			var key = string.IsNullOrEmpty(d.Serial) ? i.ToString(CultureInfo.InvariantCulture) : d.Serial;
			var label = string.IsNullOrEmpty(d.Serial) ? d.Name : $"{d.Name} {d.Serial}";
			var slot = Pooled($"disk.raid.{key}", RaidMetric, _raidPool, key, label);
			if (slot is null) continue;

			// A disk that is failing matters more than a disk that is warm, so a bad health
			// verdict takes the plate straight to red whatever the temperature says.
			var failing = HddSensor.Failing(d.Health);
			Record(slot, d.Temp, failing ? NeverAlerts : d.Temp);
			if (!changed || !slot.Settings.Enabled) continue;
			var health = string.IsNullOrEmpty(d.Health) ? "" : failing ? $"   ЗДОРОВЬЕ: {d.Health}" : "   здоровье в норме";
			Show(slot, Whole(d.Temp), failing ? NeverAlerts : d.Temp, $"{Deg(d.Temp)}{health}{slot.StatsText}");
		}
		_shownRaid = version;
	}

	/// <summary>
	/// One icon per fan that was turning at startup, plus every GPU fan. A stopped fan reads 0
	/// and goes red — that is the state worth noticing, so severity is inverted here.
	/// </summary>
	private void ShowFans()
	{
		var slow = _r.Slow;
		// Latching an empty list would be permanent: the condition to recompute was "not yet
		// set", and a filter that returns nothing still sets it. A cold start, a passive
		// system or a driver still loading right after logon all report every header at zero,
		// and no fan would ever get an icon again until the process was restarted.
		if ((_fanNames is null || _fanNames.Count == 0) && slow.Fans.Count > 0)
		{
			var spinning = slow.Fans.Where(f => f.Rpm > 0).Select(f => f.Name).Take(FanGuids.Length).ToList();
			if (spinning.Count > 0) _fanNames = spinning;
		}
		if (_fanNames is null) return;

		foreach (var name in _fanNames)
		{
			// One pass, not an Any followed by a First: two walks and two closures per fan per
			// tick for a list that is being walked anyway.
			var found = false;
			(string Name, double Rpm, double? Duty) fan = default;
			foreach (var candidate in slow.Fans)
			{
				if (candidate.Name != name) continue;
				fan = candidate;
				found = true;
				break;
			}
			if (!found) continue;

			var label = name == CpuFanSensorName ? $"Вентилятор CPU ({name})" : name;
			var slot = Pooled($"fan.{name}", FanMetric, _fanPool, name, label);
			if (slot is null) continue;
			Record(slot, fan.Rpm, Stalled(fan.Rpm));
			if (!slot.Settings.Enabled) continue;
			var duty = fan.Duty.HasValue ? $"   задание {Pct(fan.Duty)}" : "";
			Show(slot, Rpm(fan.Rpm), Stalled(fan.Rpm),
				$"{fan.Rpm.ToString("0", CultureInfo.InvariantCulture)} об/мин{duty}{slot.StatsText}");
		}
	}

	/// <summary>
	/// One icon per physical adapter, received plus sent; virtual switch chatter between VMs is
	/// not counted. An adapter with no link and no traffic — an empty Ethernet socket next to the
	/// Wi-Fi actually in use — gets no icon at all.
	/// </summary>
	private void ShowNetwork()
	{
		foreach (var n in _r.Nets)
		{
			// continue, not break. The list is sorted by name, so one adapter without a free
			// slot in the middle of the alphabet used to silence every adapter after it —
			// including ones whose icons were already registered and working.
			var slot = Pooled($"net.{n.Name}", NetMetric, _netPool, n.Name, n.Name);
			if (slot is null) continue;

			var total = n.InMb + n.OutMb;
			var utilisation = n.LinkMb > 0 ? 100 * total / n.LinkMb : 0;
			Record(slot, total * 8, utilisation);   // drawn in megabits, coloured by % of the link
			if (!slot.Settings.Enabled) continue;
			var of = n.LinkMb > 0
				? $"   {utilisation.ToString("0", CultureInfo.InvariantCulture)}% от " +
				  $"{(n.LinkMb * 8).ToString("0", CultureInfo.InvariantCulture)} Мбит/с"
				: "";
			Show(slot, MbitWhole(total), utilisation,
				$"↓ {Mbit(n.InMb)} / ↑ {Mbit(n.OutMb)} Мбит/с   ({Mb2(n.InMb)} / {Mb2(n.OutMb)} МБ/с){of}{slot.StatsText}");
		}
	}

	/// <summary>One icon per lettered volume, showing read plus write.</summary>
	private void ShowVolumes()
	{
		var built = false;
		foreach (var v in _r.Volumes)
		{
			var slot = Pooled($"vol.{v.Name}", VolumeMetric, _volumePool, v.Name, v.Name);
			if (slot is null) continue;
			var total = v.ReadMb + v.WriteMb;
			Record(slot, total, 0);   // volumes never alarm: there is no "too much I/O"
			if (!slot.Settings.Enabled) continue;

			// Built at most once per tick, and only if some volume icon is actually shown: the
			// line does not depend on the volume, and the list behind it is refreshed once
			// every twelve seconds.
			if (!built)
			{
				built = true;
				_topIoLine = _r.TopIo.Count > 0
					? "\n" + string.Join(" · ", _r.TopIo.Select(t => $"{t.Name} {Mb2(t.Mb)}"))
					: "";
			}
			Show(slot, Mb(total), 0,
				$"{Mb2(total)} МБ/с   чтение {Mb2(v.ReadMb)} / запись {Mb2(v.WriteMb)}" +
				// The per-process figures are for every device at once: splitting them by volume
				// needs a kernel trace costing 5-10 % of a core, which is the whole budget.
				$"{(_topIoLine.Length > 0 ? "\nввод-вывод по всем устройствам:" + _topIoLine : "")}");
		}
	}

	/// <summary>Sources that found no free tray slot, so the limits are not silent any more.</summary>
	private List<string> Overflows() => _pools
		.Where(p => p.Refused > 0)
		.Select(p => $"{p.What}: без значка осталось {p.Refused.ToString(CultureInfo.InvariantCulture)} " +
					 $"(заготовлено {p.Size.ToString(CultureInfo.InvariantCulture)})")
		.ToList();

	/// <summary>Free space per volume — whole gigabytes, so the icon repaints once in hours.</summary>
	private void ShowFreeSpace()
	{
		foreach (var s in _r.Space)
		{
			var slot = Pooled($"free.{s.Name}", FreeMetric, _freePool, s.Name, $"Свободно {s.Name}");
			if (slot is null) continue;
			var usedPercent = s.TotalGb > 0 ? 100 * (s.TotalGb - s.FreeGb) / s.TotalGb : 0;
			Record(slot, s.FreeGb, usedPercent);   // drawn in gigabytes free, coloured by % used
			if (!slot.Settings.Enabled) continue;
			Show(slot, Whole(s.FreeGb), usedPercent,
				$"{s.FreeGb.ToString("0.0", CultureInfo.InvariantCulture)} ГБ свободно из " +
				$"{s.TotalGb.ToString("0.0", CultureInfo.InvariantCulture)}   занято {Pct(usedPercent)}");
		}
	}

	/// <summary>
	/// Charge of the UPS, once its SNMP agent has answered. Severity is inverted — a low charge
	/// is what matters — and running on battery goes straight to red whatever the charge is,
	/// because that is the state worth walking over to the rack for.
	/// </summary>
	private void ShowUps()
	{
		if (!_ups.Present) return;

		var ups = _r.Ups;   // one read: charge and the battery flag must come from one answer
		var slot = Slot("ups", UpsMetric, UpsGuid);
		Record(slot, ups.Charge,
			ups.Charge.HasValue ? ups.OnBattery == true ? 100 : 100 - ups.Charge.Value : null);

		// Going onto battery is the event a UPS is bought for, and until now only somebody
		// staring at the tray would see it. Checked before the "nothing changed" shortcut and
		// outside the enabled check — a hidden icon is not a reason to stay quiet about it.
		if (ups.OnBattery.HasValue && _lastOnBattery.HasValue && ups.OnBattery != _lastOnBattery)
		{
			var left = ups.RunTimeMin.HasValue
				? $", ещё {ups.RunTimeMin.Value.ToString("0", CultureInfo.InvariantCulture)} мин"
				: "";
			var carrier = slot.Icon ?? _order.FirstOrDefault(s => s.Icon is not null)?.Icon;
			carrier?.Notify("TrayMon — ИБП",
				ups.OnBattery.Value
					? $"ИБП перешёл на батарею. Заряд {Pct(ups.Charge)}{left}."
					: "Питание от сети восстановлено.");
		}
		if (ups.OnBattery.HasValue) _lastOnBattery = ups.OnBattery;

		// Nothing known at all: Fade() greys the icon and puts the reason in the tooltip, which
		// is more honest than the old behaviour of keeping the last "on line" text next to a
		// plate that meant "no answer".
		if (!ups.Answered) return;
		var version = Volatile.Read(ref _upsVersion);
		if (version == _shownUps || !slot.Settings.Enabled) { _shownUps = version; return; }
		_shownUps = version;

		double? severity = ups.Charge.HasValue ? (ups.OnBattery == true ? 100 : 100 - ups.Charge.Value) : null;
		var power = ups.OnBattery switch { true => "от батареи", false => "от сети", _ => "состояние неизвестно" };
		var minutes = ups.RunTimeMin.HasValue
			? $"   ещё {ups.RunTimeMin.Value.ToString("0", CultureInfo.InvariantCulture)} мин"
			: "";
		var load = ups.Load.HasValue ? $"   нагрузка {Pct(ups.Load)}" : "";
		var replace = ups.NeedsNewBattery ? "   ТРЕБУЕТ ЗАМЕНЫ" : "";
		Show(slot, Whole(ups.Charge), severity, $"{Pct(ups.Charge)} {power}{minutes}{load}{replace}");
	}

	private void ShowUptime()
	{
		if (!_r.UptimeHours.HasValue) return;
		var slot = Slot("uptime", UptimeMetric, UptimeGuid);
		Record(slot, _r.UptimeHours.Value, 0);   // a long uptime is not an alarm
		if (!slot.Settings.Enabled) return;
		var hours = _r.UptimeHours.Value;
		var days = (int)(hours / 24);
		var detail = days > 0
			? $"{days} сут {(hours - days * 24).ToString("0", CultureInfo.InvariantCulture)} ч без перезагрузки"
			: $"{hours.ToString("0.0", CultureInfo.InvariantCulture)} ч без перезагрузки";
		// Severity 0, not null: null is the code for "this source is dead" and would paint the
		// plate grey for ever. Uptime is never alarming, it just has nothing to warn about.
		Show(slot, Whole(hours < 99 ? hours : Math.Round(hours / 24)), 0,
			detail + (hours >= 99 ? " (на значке — сутки)" : ""));
	}

	/// <summary>
	/// One icon for the worst thing happening anywhere. Seventeen icons do not fit into a
	/// Windows 11 tray, and this is the answer that stays inside "one icon, one number": the
	/// highest severity among everything else, labelled with whichever metric it came from.
	/// </summary>
	private void ShowWorst()
	{
		var slot = Slot("worst", WorstMetric, WorstGuid);
		Alive(slot);   // derived from the others, so it exists whenever anything else does

		IconSlot worst = null;
		double top = double.MinValue;
		foreach (var candidate in _order)
		{
			// Severity comes from Record, not from Show, so an icon the user has hidden still
			// counts here — the whole point is to watch everything with one slot in the tray.
			if (ReferenceEquals(candidate, slot) || candidate.Dead) continue;
			if (candidate.SeenTick != _tick || !candidate.Severity.HasValue) continue;
			if (!AlertsOn(candidate)) continue;   // volumes and uptime never alarm; skip them

			// Scaled against each metric's own critical threshold, because the raw numbers are
			// not comparable: 85 means a hot CPU, a cool disk and a healthy UPS. 100 is "at its
			// own red line", so one pair of thresholds fits every metric.
			var crit = CritOf(candidate);
			if (crit <= 0 || crit >= NeverAlerts) continue;
			var score = Math.Min(100 * candidate.Severity.Value / crit, 999);
			if (score <= top) continue;
			top = score;
			worst = candidate;
		}

		if (!slot.Settings.Enabled) return;
		if (worst is null) { Show(slot, "ок", 0, "тревог нет"); return; }
		Show(slot, Whole(top), top,
			$"{LabelOf(worst)} — {Whole(top)} % от своего красного порога" +
			$"{(top >= 100 ? "   ПОРОГ ПРЕВЫШЕН" : "")}");
	}

	// ---- slots ----

	/// <summary>
	/// Registers the slot on first sight. Registering does *not* make it alive — that is
	/// <see cref="Record"/>, and only a reading does it, so a source that exists but answers
	/// nothing goes grey with a reason instead of showing a dash for ever.
	/// </summary>
	private IconSlot Slot(string id, Metric metric, Guid guid, string label = null)
	{
		if (!_slots.TryGetValue(id, out var slot))
		{
			slot = new IconSlot
			{
				Id = id,
				Metric = metric,
				DefaultLabel = label ?? metric.Label,
				Guid = guid,
				Settings = _config.Get(id),
			};
			Adopt(slot);
		}
		return slot;
	}

	/// <summary>The same, for a family that draws its tray identity from a pool.</summary>
	private IconSlot Pooled(string id, Metric metric, GuidPool pool, string key, string label)
	{
		if (_slots.TryGetValue(id, out var known)) return known;

		var guid = pool.For(key);
		if (guid is null) return null;   // more sources than prepared slots — reported in the menu

		var slot = new IconSlot
		{
			Id = id,
			Metric = metric,
			DefaultLabel = label ?? metric.Label,
			Guid = guid.Value,
			Pool = pool,
			PoolKey = key,
			Settings = _config.Get(id),
		};
		Adopt(slot);
		return slot;
	}

	private void Adopt(IconSlot slot)
	{
		// Seventeen icons on a first run go straight into the Windows 11 overflow, where nothing
		// is visible — and the menu lives on the icons, so there is then no way to find the
		// program at all. Start with the four that answer "is this machine busy", and let the
		// rest be switched on from the menu. Existing installations are untouched: they have a
		// settings file already, so this only takes effect on a machine with no TrayMon.json.
		if (_firstRun && !DefaultOn(slot.Id))
		{
			_config.For(slot.Id).Enabled = false;
			_configDirty = true;
		}
		slot.Settings = _config.Get(slot.Id);
		_slots[slot.Id] = slot;
		_order.Add(slot);
	}

	/// <summary>The four load meters, and only for the first card: every adapter, every volume and
	/// every second card would put the count back where it was. "gpu.0" is the load of card 0 —
	/// its temperature is "gpu.temp.0" and stays off.</summary>
	private static bool DefaultOn(string id) => id is "cpu" or "ram" or "gpu.0" or "vram.0";

	/// <summary>
	/// Feeds the five-minute window behind the tooltip and marks the slot alive.
	///
	/// A missing value is what "this source is not answering" means, so it does *not* count as
	/// alive: the sensor library opens happily without an elevated token and then returns null
	/// for every temperature, which used to leave a permanent dash on the icon and no hint that
	/// the fix was to run as administrator.
	/// </summary>
	/// <param name="severity">What the thresholds apply to, when that is not the value itself —
	/// a fan is judged on being stopped, the UPS on how little charge is left. Recorded here and
	/// not in <see cref="Show"/> so that a hidden icon still feeds the summary one.</param>
	private void Record(IconSlot slot, double? value, double? severity = null)
	{
		if (!value.HasValue) return;
		slot.SeenTick = _tick;
		slot.Stats.Add(value.Value);
		slot.Severity = severity ?? value;
	}

	/// <summary>Marks a slot alive when it is present by construction rather than by a reading.</summary>
	private void Alive(IconSlot slot) => slot.SeenTick = _tick;

	/// <summary>Pushes a value into the icon. Only ever called for a slot that is switched on.</summary>
	private void Show(IconSlot slot, string text, double? severity, string detail)
	{
		// A source that produced nothing this tick belongs to Fade(), which owns the grey plate
		// and the explanation in the tooltip. Letting a caller overwrite that with its own empty
		// formatting is how "—" ends up with no reason next to it.
		if (slot.SeenTick != _tick) return;

		slot.LastText = text;
		slot.LastSeverity = severity;
		slot.LastDetail = detail;
		if (_dry) return;

		slot.Icon ??= new TrayValueIcon(OnIconRightClick, OnIconLeftClick, PlateOf(slot), slot.Guid,
			WarnOf(slot), CritOf(slot));
		slot.Icon.SetThresholds(WarnOf(slot), CritOf(slot));
		slot.Icon.SetInk(InkOf(slot));
		slot.Icon.Update(text, severity, $"{LabelOf(slot)}   {detail}");
	}

	private void RefreshStats()
	{
		foreach (var slot in _order)
		{
			if (!slot.Stats.Ready) { slot.StatsText = ""; continue; }
			var (min, avg, max) = slot.Stats.Window();
			var ci = CultureInfo.InvariantCulture;
			var digits = max < 10 ? "0.0" : "0";
			// Kept short on purpose: a tray tooltip is 127 characters and no more, and a network
			// icon's own line already takes a hundred of them. A verbose form was simply cut off.
			slot.StatsText = $"\n5 мин: {min.ToString(digits, ci)}…{max.ToString(digits, ci)}, ср {avg.ToString(digits, ci)}";
		}
	}

	private void FirstRunHousekeeping()
	{
		if (!_firstRun) return;

		if (!_greeted && _tick >= 2)
		{
			_greeted = true;
			var first = _order.FirstOrDefault(s => s.Icon is not null);
			first?.Icon?.Notify("TrayMon работает",
				"Значки в области уведомлений. Правой кнопкой по любому из них — меню: " +
				"остальные метрики, цвета, автозапуск.");
		}
		// Written once, not on every tick: a settings file is the one thing here that touches
		// the disk on a schedule.
		if (_configDirty && _tick % 15 == 0) { _config.Save(); _configDirty = false; }
	}

	/// <summary>
	/// Optional CSV trail. Off by default and never faster than every 30 s — at tick rate this
	/// would cost as much as the sensors it records.
	/// </summary>
	private void WriteLogLine()
	{
		if (_dry || !_config.Log.Enabled) return;
		var now = DateTime.Now;
		if ((now - _lastLog).TotalSeconds < _config.Log.EverySeconds) return;
		_lastLog = now;

		try
		{
			var path = string.IsNullOrWhiteSpace(_config.Log.Path)
				? Path.Combine(AppContext.BaseDirectory, "TrayMon.csv")
				: _config.Log.Path;
			var ci = CultureInfo.InvariantCulture;
			var gpu = _r.Gpus.Count > 0 ? _r.Gpus[0] : null;
			var ups = _r.Ups;
			if (!File.Exists(path))
				File.AppendAllText(path, "time;cpu%;cpu_c;mem%;mem_gb;gpu%;gpu_c;net_mbit;ups%;ups_on_battery\n");
			File.AppendAllText(path, string.Join(';', new[]
			{
				now.ToString("yyyy-MM-dd HH:mm:ss", ci),
				Num(_r.CpuLoad), Num(_r.Slow.CpuTemp), Num(_r.MemLoad), _r.MemUsedGb.ToString("0.0", ci),
				Num(gpu?.Load), Num(gpu?.Temp),
				(_r.Nets.Sum(n => n.InMb + n.OutMb) * 8).ToString("0.0", ci),
				Num(ups.Charge), ups.OnBattery switch { true => "1", false => "0", _ => "" },
			}) + "\n");
		}
		catch (Exception ex)
		{
			// A full or read-only disk must not take the monitor down with it.
			_lastTickError = "журнал: " + ex.Message;
		}
	}

	private static string Num(double? v) =>
		v.HasValue ? v.Value.ToString("0.0", CultureInfo.InvariantCulture) : "";

	// ---- background refreshes ----

	private void Spawn(Action work)
	{
		var task = Task.Run(work);
		lock (_background)
		{
			_background.RemoveAll(t => t.IsCompleted);
			_background.Add(task);
		}
	}

	private void RefreshSlow()
	{
		if (Interlocked.Exchange(ref _slowRefreshRunning, 1) == 1) return;
		try { _r.Slow = _lhm.ReadSlow(); }
		catch (Exception ex) { Trouble(ex); }
		finally { Volatile.Write(ref _slowRefreshRunning, 0); }
	}

	private void RefreshDisks()
	{
		if (Interlocked.Exchange(ref _diskRefreshRunning, 1) == 1) return;
		try
		{
			_lhm.RefreshDiskTemps();
			_r.Disks = _lhm.DiskTemps();
			Interlocked.Increment(ref _disksVersion);
		}
		catch (Exception ex) { Trouble(ex); }
		finally { Volatile.Write(ref _diskRefreshRunning, 0); }
	}

	private void RefreshRaidDisk()
	{
		if (Interlocked.Exchange(ref _raidRefreshRunning, 1) == 1) return;
		try
		{
			_r.RaidDisks = _hdd.ReadAll();
			Interlocked.Increment(ref _raidVersion);
		}
		catch (Exception ex) { Trouble(ex); }
		finally { Volatile.Write(ref _raidRefreshRunning, 0); }
	}

	private void RefreshUps()
	{
		if (Interlocked.Exchange(ref _upsRefreshRunning, 1) == 1) return;
		try
		{
			_ups.Read(_r);
			Interlocked.Increment(ref _upsVersion);
		}
		catch (Exception ex) { Trouble(ex); }
		finally { Volatile.Write(ref _upsRefreshRunning, 0); }
	}

	/// <summary>
	/// Remembers a failure instead of putting a dialog in front of an empty chair. On a server
	/// with the RDP session disconnected nobody sees a modal box, and the timer keeps firing
	/// underneath it — the old default opened a new one every two seconds until USER handles
	/// ran out. The count and the last message live in "Диагностика…".
	/// </summary>
	public void Trouble(Exception ex, string where = null)
	{
		_tickErrors++;
		var place = where is null ? "" : $" [{where}]";
		_lastTickError = $"{DateTime.Now:HH:mm:ss}{place} {ex.GetType().Name}: {ex.Message}";
	}

	/// <summary>Called when the process is going down anyway — take the icons with it.</summary>
	public void Panic(Exception ex)
	{
		Trouble(ex);
		foreach (var slot in _order) { try { slot.Icon?.Dispose(); } catch (Exception) { } }
	}

	// ---- settings helpers ----

	private double WarnOf(IconSlot slot) => slot.Settings.Warn ?? slot.Metric.Warn;
	private double CritOf(IconSlot slot) => slot.Settings.Crit ?? slot.Metric.Crit;
	private bool AlertsOn(IconSlot slot) => WarnOf(slot) < NeverAlerts;

	private Color PlateOf(IconSlot slot)
	{
		var hex = slot.Settings.Color;
		if (!string.IsNullOrWhiteSpace(hex))
		{
			try { return ColorTranslator.FromHtml(hex); }
			catch (Exception) { /* hand-edited nonsense in the file — fall back to the default */ }
		}
		return slot.Metric.Plate;
	}

	private string LabelOf(IconSlot slot) => slot.Settings.Label ?? slot.DefaultLabel;

	/// <summary>Digit colour chosen by the user; null lets the icon pick it by the plate colour.</summary>
	private Color? InkOf(IconSlot slot) => slot.Settings.Ink switch
	{
		"light" => Color.White,
		"dark" => Color.Black,
		_ => null,
	};

	/// <summary>Takes a private settings entry for this slot so it can be written to.</summary>
	private IconSettings Own(IconSlot slot) => slot.Settings = _config.For(slot.Id);

	private void Persist(IconSlot slot)
	{
		_config.Tidy(slot.Id);
		slot.Settings = _config.Get(slot.Id);
		if (_config.Save(out var error)) return;
		Info($"Настройки не сохранены: {error}\n\nИзменение действует до перезапуска программы.",
			MessageBoxIcon.Warning);
	}

	// ---- tray menu ----

	private void OnIconLeftClick(TrayValueIcon icon) => ShowSummary();

	private void OnIconRightClick(TrayValueIcon icon)
	{
		var clicked = _order.FirstOrDefault(s => ReferenceEquals(s.Icon, icon));

		// A right-click on another icon does not close the menu that is already up, and the items
		// about to be thrown away are the ones it is showing.
		if (_menu.Visible) _menu.Close();
		ClearMenu();
		if (clicked is not null)
		{
			_menu.Items.Add(new ToolStripMenuItem(LabelOf(clicked)) { Enabled = false });
			_menu.Items.Add(new ToolStripSeparator());
			_menu.Items.Add("Цвет фона…", null, (_, _) => PickColor(clicked));
			_menu.Items.Add(InkMenu(clicked));
			_menu.Items.Add("Переименовать…", null, (_, _) => Rename(clicked));
			_menu.Items.Add("Пороги…", null, (_, _) => EditThresholds(clicked));
			var alerts = new ToolStripMenuItem("Подсвечивать при перегрузке")
			{
				Checked = AlertsOn(clicked),
				CheckOnClick = true,
				ToolTipText = ThresholdHint(clicked),
			};
			alerts.Click += (_, _) => SetAlerts(clicked, alerts.Checked);
			_menu.Items.Add(alerts);
			_menu.Items.Add("Скрыть этот значок", null, (_, _) => SetEnabled(clicked, false));
			_menu.Items.Add(new ToolStripSeparator());
		}

		_menu.Items.Add(VisibilityMenu());
		_menu.Items.Add("Опросить датчики сейчас", null, (_, _) => PollNow());
		_menu.Items.Add(new ToolStripSeparator());

		var autostart = new ToolStripMenuItem("Запускать при входе в Windows")
		{
			Checked = Autostart.IsEnabled,
			CheckOnClick = true,
			ToolTipText = "Задача планировщика с правами администратора — без неё не читается температура CPU",
		};
		autostart.Click += (_, _) => ToggleAutostart(autostart.Checked);
		_menu.Items.Add(autostart);

		_menu.Items.Add("Создать ярлык на рабочем столе", null, (_, _) => CreateShortcut());
		_menu.Items.Add("Настройки в файле…", null, (_, _) => OpenConfigFile());
		_menu.Items.Add("Перечитать настройки", null, (_, _) => ReloadConfig());
		_menu.Items.Add(new ToolStripSeparator());
		_menu.Items.Add("Сводка…", null, (_, _) => ShowSummary());
		_menu.Items.Add("Диагностика…", null, (_, _) => ShowDiagnostics());
		_menu.Items.Add("О программе…", null, (_, _) => ShowAbout());
		_menu.Items.Add("Удалить TrayMon…", null, (_, _) => Uninstall());
		_menu.Items.Add(new ToolStripSeparator());
		_menu.Items.Add("Выход", null, (_, _) => ExitThread());

		icon.PrepareMenu();
		_menu.Show(Cursor.Position);
	}

	/// <summary>Menu items are Components; Clear alone leaves each of them to the finaliser,
	/// and an opened submenu has a window handle no finaliser destroys.</summary>
	private void ClearMenu()
	{
		_pickingIcons = false;   // the item that would have said so is about to be disposed
		var items = _menu.Items.Cast<ToolStripItem>().ToArray();
		_menu.Items.Clear();
		foreach (var item in items) item.Dispose();
	}

	/// <summary>
	/// The list of every icon, grouped and in a fixed order. It used to follow the order slots
	/// happened to be created in — which depends on when asynchronous sensors answer, so it
	/// changed between runs and yesterday's tick was somewhere else today.
	/// </summary>
	private ToolStripMenuItem VisibilityMenu()
	{
		var all = new ToolStripMenuItem("Показывать значки");
		all.DropDownOpened += (_, _) => _pickingIcons = true;
		all.DropDownClosed += (_, _) => _pickingIcons = false;
		// The chain closes from the top down, so the parent has to be held open too — that half
		// is the handler on _menu in the constructor.
		all.DropDown.Closing += (_, e) =>
		{
			if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
		};
		foreach (var group in Groups)
		{
			var members = _order
				.Where(s => s.Metric.Group == group)
				.OrderBy(s => s.Metric.Order)
				.ThenBy(s => LabelOf(s), StringComparer.CurrentCultureIgnoreCase)
				.ToList();
			if (members.Count == 0) continue;

			if (all.DropDownItems.Count > 0) all.DropDownItems.Add(new ToolStripSeparator());
			all.DropDownItems.Add(new ToolStripMenuItem(group) { Enabled = false });
			foreach (var slot in members)
			{
				var item = new ToolStripMenuItem(LabelOf(slot) + (slot.Dead ? "  (нет данных)" : ""))
				{
					Checked = slot.Settings.Enabled,
					CheckOnClick = true,
				};
				var captured = slot;
				// The tick is put there by CheckOnClick before anything is asked of us, so a
				// refusal has to take it back — the list stays open now, and a tick next to an
				// icon that is not there is a lie the user goes on looking at.
				item.Click += (_, _) => { if (!SetEnabled(captured, item.Checked)) item.Checked = !item.Checked; };
				all.DropDownItems.Add(item);
			}
		}
		var overflows = Overflows();
		if (overflows.Count > 0)
		{
			all.DropDownItems.Add(new ToolStripSeparator());
			foreach (var line in overflows)
				all.DropDownItems.Add(new ToolStripMenuItem(line) { Enabled = false });
		}
		return all;
	}

	/// <summary>
	/// Light or dark digits for this icon. "Авто" picks by the brightness of the plate, so a
	/// light background chosen from the colour dialog gets black digits instead of white on white.
	/// </summary>
	private ToolStripMenuItem InkMenu(IconSlot slot)
	{
		var menu = new ToolStripMenuItem("Цвет цифр");
		var current = slot.Settings.Ink;
		foreach (var (label, value) in new[] { ("Авто", (string)null), ("Светлые", "light"), ("Тёмные", "dark") })
		{
			var choice = value;
			var item = new ToolStripMenuItem(label) { Checked = current == choice };
			item.Click += (_, _) => SetInk(slot, choice);
			menu.DropDownItems.Add(item);
		}
		return menu;
	}

	/// <summary>What the thresholds mean for this icon, in the units it is actually read in.</summary>
	private string ThresholdHint(IconSlot slot)
	{
		if (!AlertsOn(slot)) return "Сейчас выключено: плашка всегда своего цвета";
		var ci = CultureInfo.InvariantCulture;
		var warn = WarnOf(slot);
		var crit = CritOf(slot);
		if (slot.Id.StartsWith("fan.", StringComparison.Ordinal))
			return "Красная, когда вентилятор остановлен";
		if (slot.Id == "ups")
			return $"Жёлтая при заряде ниже {(100 - warn).ToString("0", ci)} %, " +
				   $"красная ниже {(100 - crit).ToString("0", ci)} % и сразу при переходе на батарею";
		if (slot.Id.StartsWith("free.", StringComparison.Ordinal))
			return $"Жёлтая когда занято больше {warn.ToString("0", ci)} %, красная больше {crit.ToString("0", ci)} %";
		if (slot.Id == "worst")
			return "Повторяет самый тревожный из остальных значков";
		return $"Жёлтая при {warn.ToString("0", ci)} {slot.Metric.Unit}, красная при {crit.ToString("0", ci)} {slot.Metric.Unit}";
	}

	private void SetInk(IconSlot slot, string ink)
	{
		Own(slot).Ink = ink;
		Persist(slot);
		slot.Icon?.SetInk(InkOf(slot));
	}

	private void PickColor(IconSlot slot)
	{
		using var dialog = new ColorDialog { Color = PlateOf(slot), FullOpen = true, AnyColor = true };
		if (dialog.ShowDialog() != DialogResult.OK) return;
		Own(slot).Color = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
		Persist(slot);
		slot.Icon?.SetPlate(dialog.Color);

		// A plate held at its warning colour ignores the chosen one, which looks like the
		// setting did nothing.
		if (slot.Icon?.IsAlerting == true)
			Info($"Цвет сохранён, но сейчас значок подсвечен порогом " +
				 $"({slot.LastSeverity?.ToString("0", CultureInfo.InvariantCulture)} ≥ " +
				 $"{WarnOf(slot).ToString("0", CultureInfo.InvariantCulture)}).\n" +
				 "Выбранный цвет появится, когда значение опустится ниже порога, либо снимите\n" +
				 "галочку «Подсвечивать при перегрузке».");
	}

	private void SetAlerts(IconSlot slot, bool on)
	{
		var settings = Own(slot);
		settings.Warn = on ? null : NeverAlerts;
		settings.Crit = on ? null : NeverAlerts;
		Persist(slot);
		slot.Icon?.SetThresholds(WarnOf(slot), CritOf(slot));
	}

	private void EditThresholds(IconSlot slot)
	{
		var ci = CultureInfo.InvariantCulture;
		var warn = Prompt($"Жёлтый порог ({slot.Metric.Unit})\n{ThresholdHint(slot)}",
			WarnOf(slot).ToString("0.###", ci));
		if (warn is null) return;
		var crit = Prompt($"Красный порог ({slot.Metric.Unit})", CritOf(slot).ToString("0.###", ci));
		if (crit is null) return;

		if (!double.TryParse(warn, NumberStyles.Float, ci, out var w) ||
			!double.TryParse(crit, NumberStyles.Float, ci, out var c))
		{
			Info("Порог должен быть числом; точка как разделитель дробной части.", MessageBoxIcon.Warning);
			return;
		}

		var settings = Own(slot);
		settings.Warn = w;
		settings.Crit = c;
		Persist(slot);
		slot.Icon?.SetThresholds(WarnOf(slot), CritOf(slot));
	}

	private void Rename(IconSlot slot)
	{
		var name = Prompt("Подпись значка", LabelOf(slot));
		if (name is null) return;
		Own(slot).Label = string.IsNullOrWhiteSpace(name) ? null : name;   // empty restores the default
		Persist(slot);
		slot.Icon?.Update(slot.LastText, slot.LastSeverity, $"{LabelOf(slot)}   {slot.LastDetail}");
	}

	/// <returns>False when the change was refused, so the caller can put its tick back.</returns>
	private bool SetEnabled(IconSlot slot, bool enabled)
	{
		// The menu lives on the icons; hiding the last one would leave no way back in — not
		// even to quit. Slots that went grey do not count: they have no icon to right-click.
		if (!enabled && _order.Count(s => s.Settings.Enabled && !s.Dead) <= 1)
		{
			Info("Последний значок скрыть нельзя — иначе не останется меню.");
			return false;
		}

		Own(slot).Enabled = enabled;
		Persist(slot);
		if (!enabled)
		{
			if (slot.Icon is not null) { slot.Icon.Dispose(); slot.Icon = null; }
			return true;
		}
		// The user's thresholds and ink, not the built-in ones: creating the icon with
		// slot.Metric.Warn made a freshly unhidden icon flash yellow for a tick even with the
		// highlight switched off.
		slot.Icon ??= new TrayValueIcon(OnIconRightClick, OnIconLeftClick, PlateOf(slot), slot.Guid,
			WarnOf(slot), CritOf(slot));
		slot.Icon.SetInk(InkOf(slot));
		slot.Icon.Update(slot.LastText, slot.LastSeverity, $"{LabelOf(slot)}   {slot.LastDetail}");
		return true;
	}

	/// <summary>
	/// Forces the slow sources round now. Having installed smartctl.exe or started an SNMP
	/// agent, the alternative was waiting up to ten minutes with nothing to say it was working.
	/// </summary>
	private void PollNow()
	{
		Spawn(RefreshSlow);
		Spawn(RefreshDisks);
		Spawn(RefreshRaidDisk);
		Spawn(RefreshUps);
		_r.Space = SpaceSensor.Read(_r.Volumes.Select(v => v.Name));
		_r.UptimeHours = _perf.ReadUptime();
		Info("Опрос запущен. Медленные источники (SMART, RAID, ИБП) ответят в течение нескольких секунд.");
	}

	private static void ToggleAutostart(bool on)
	{
		var ok = on ? Autostart.Enable(out var error) : Autostart.Disable(out error);
		if (ok)
		{
			// A security check that silently did not run must not look like one that passed.
			var unverified = on && Autostart.LastCheckError is not null
				? "\n\nПрава на папку проверить не удалось (" + Autostart.LastCheckError + ").\n" +
				  "Убедитесь сами, что писать в неё может только администратор."
				: "";
			Info((on ? "TrayMon будет запускаться при входе в Windows." : "Автозапуск отключён.") + unverified);
			return;
		}
		Info($"Не получилось: {error}", MessageBoxIcon.Warning);
	}

	private static void CreateShortcut()
	{
		var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		if (Autostart.CreateShortcut(desktop, out var path, out var error))
			Info($"Ярлык создан:\n{path}");
		else
			Info($"Не получилось: {error}", MessageBoxIcon.Warning);
	}

	private static void OpenConfigFile()
	{
		try
		{
			Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Config.Path}\"") { UseShellExecute = true });
			Info("Файл читается только при старте, а любое изменение из меню перезаписывает его целиком.\n\n" +
				 "Сохранив правку, выберите «Перечитать настройки» — иначе она пропадёт.");
		}
		catch (Exception ex)
		{
			Info($"Не удалось открыть файл настроек: {ex.Message}", MessageBoxIcon.Warning);
		}
	}

	/// <summary>
	/// Re-reads the file and applies it to the live icons. Without this a hand edit — the only
	/// way to set the UPS address — did nothing until a restart, and was then wiped by the first
	/// change made from the menu.
	/// </summary>
	private void ReloadConfig()
	{
		_config = Config.Load();
		if (_config.LoadError is not null)
		{
			Info($"Файл не прочитан: {_config.LoadError}", MessageBoxIcon.Warning);
			return;
		}

		foreach (var slot in _order)
		{
			slot.Settings = _config.Get(slot.Id);
			if (!slot.Settings.Enabled)
			{
				if (slot.Icon is not null) { slot.Icon.Dispose(); slot.Icon = null; }
				continue;
			}
			slot.Icon ??= new TrayValueIcon(OnIconRightClick, OnIconLeftClick, PlateOf(slot), slot.Guid,
				WarnOf(slot), CritOf(slot));
			slot.Icon.SetPlate(PlateOf(slot));
			slot.Icon.SetInk(InkOf(slot));
			slot.Icon.SetThresholds(WarnOf(slot), CritOf(slot));
			slot.Icon.Update(slot.LastText, slot.LastSeverity, $"{LabelOf(slot)}   {slot.LastDetail}");
		}
		EnsureSomethingVisible();
		Info("Настройки перечитаны.\n\nАдрес ИБП, путь к smartctl и фильтр адаптеров применятся " +
			 "после перезапуска программы — они читаются один раз при старте.");
	}

	/// <summary>
	/// The "you cannot hide them all" rule, applied to the file as well as to the menu. Setting
	/// every icon to false by hand is a legitimate edit of a file the README invites people to
	/// edit, and it left a process with no icons, no menu and no way out but Task Manager.
	/// </summary>
	private void EnsureSomethingVisible()
	{
		if (_order.Count == 0 || _order.Any(s => s.Settings.Enabled)) return;
		var first = _order[0];
		Own(first).Enabled = true;
		_config.Save();
		first.Settings = _config.Get(first.Id);
		Info("В настройках были скрыты все значки — без них не осталось бы меню.\n" +
			 $"Включён «{LabelOf(first)}».");
	}

	// ---- windows ----

	private void ShowSummary()
	{
		var ci = CultureInfo.InvariantCulture;
		var text = new StringBuilder();
		foreach (var group in Groups)
		{
			var members = _order.Where(s => s.Metric.Group == group).OrderBy(s => s.Metric.Order).ToList();
			if (members.Count == 0) continue;
			text.AppendLine(group);
			foreach (var slot in members)
				text.AppendLine($"    {LabelOf(slot),-32} {(slot.LastText ?? "—"),6}   {slot.LastDetail.Replace("\n", " · ")}");
			text.AppendLine();
		}
		text.AppendLine($"тик {_tick.ToString(ci)}, ошибок за сеанс: {_tickErrors.ToString(ci)}");
		TextWindow("TrayMon — сводка", text.ToString());
	}

	/// <summary>
	/// Everything that explains why an icon is empty, in the place the user actually is. All of
	/// it was already in memory and visible only from a console mode that a
	/// requireAdministrator manifest makes awkward to reach.
	/// </summary>
	private void ShowDiagnostics()
	{
		var ci = CultureInfo.InvariantCulture;
		var text = new StringBuilder();
		text.AppendLine($"TrayMon {Version}");
		text.AppendLine($"exe:              {Environment.ProcessPath}");
		text.AppendLine($"права:            {(IsElevated ? "администратор" : "обычный пользователь")}");
		text.AppendLine();
		text.AppendLine($"счётчик CPU:      {_perf.CounterInUse}");
		text.AppendLine($"драйвер датчиков: {(!_lhm.Available ? "НЕДОСТУПЕН — " + (_lhm.LastError ?? "нужен запуск от администратора") : IsElevated ? "загружен" : "загружен, но без прав администратора температуры не читаются")}");
		text.AppendLine($"NVML (GPU):       {(_gpu.CardCount > 0 ? $"карт: {_gpu.CardCount}" : "нет — " + (_gpu.LastError ?? "драйвер NVIDIA не найден"))}");
		text.AppendLine($"smartctl:         {(_hdd.Available ? _hdd.ExePath : "не найден: " + _hdd.ExePath)}");
		if (_hdd.LastError is not null) text.AppendLine($"                  {_hdd.LastError}");
		text.AppendLine($"ИБП:              {(_ups.Present ? "отвечает" : "нет ответа")} на {_ups.Endpoint}" +
						$"{(_ups.LastError is null ? "" : " — " + _ups.LastError)}");
		text.AppendLine();
		text.AppendLine($"файл настроек:    {Config.Path}");
		if (_config.LoadError is not null) text.AppendLine($"                  {_config.LoadError}");
		if (_config.ChangedOnDisk) text.AppendLine("                  файл изменён на диске — «Перечитать настройки»");
		text.AppendLine($"автозапуск:       {(Autostart.IsEnabled ? "включён" : "выключен")}");
		if (Autostart.PointsElsewhere(out var command))
			text.AppendLine($"                  задача запускает другой файл: {command}");
		if (Autostart.WritableByNonAdmins(Path.GetDirectoryName(Environment.ProcessPath), out var who))
			text.AppendLine($"                  ВНИМАНИЕ: папку может изменить {who} — автозапуск отсюда небезопасен");
		text.AppendLine();
		text.AppendLine($"значков:          показано {_order.Count(s => s.Settings.Enabled).ToString(ci)} из {_order.Count.ToString(ci)}");
		text.AppendLine($"слоты:            сеть {_netPool.Used}/{_netPool.Size}, тома {_volumePool.Used}/{_volumePool.Size}, " +
						$"свободно {_freePool.Used}/{_freePool.Size}, NVMe {_diskPool.Used}/{_diskPool.Size}, " +
						$"RAID {_raidPool.Used}/{_raidPool.Size}, вентиляторы {_fanPool.Used}/{_fanPool.Size}, " +
						$"GPU {_gpuPool.Used}/{_gpuPool.Size}");
		foreach (var line in Overflows()) text.AppendLine($"                  {line}");
		var dead = _order.Where(s => s.Dead).ToList();
		if (dead.Count > 0)
		{
			text.AppendLine();
			text.AppendLine("молчащие источники:");
			foreach (var slot in dead) text.AppendLine($"    {LabelOf(slot),-32} {slot.LastDetail}");
		}
		text.AppendLine();
		text.AppendLine($"тик:              {_tick.ToString(ci)} (по {TickMs.ToString(ci)} мс)");
		var perTick = _tick > 0 ? _tickTicks * 1000.0 / Stopwatch.Frequency / _tick : 0;
		text.AppendLine($"время тика:       {perTick.ToString("0.00", ci)} мс в среднем " +
						$"({(perTick / TickMs * 100).ToString("0.00", ci)} % одного ядра на потоке интерфейса)");
		text.AppendLine($"отрисовок:        {TrayValueIcon.Renders.ToString(ci)}");
		text.AppendLine($"вызовов в трей:   {TrayValueIcon.ShellCalls.ToString(ci)}");
		text.AppendLine($"ошибок за сеанс:  {_tickErrors.ToString(ci)}");
		if (_lastTickError is not null) text.AppendLine($"последняя:        {_lastTickError}");
		TextWindow("TrayMon — диагностика", text.ToString());
	}

	private static string Version =>
		Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
		?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
		?? "—";

	private void ShowAbout() =>
		TextWindow("TrayMon — о программе",
			$"TrayMon {Version}\n" +
			"Монитор железа в области уведомлений Windows: одна иконка — одно число.\n\n" +
			"Лицензия MIT. Использует LibreHardwareMonitorLib (MPL 2.0) для температур\n" +
			"и вентиляторов, NVML для видеокарт, smartctl для дисков за RAID-контроллером.\n\n" +
			$".NET {Environment.Version}\n" +
			$"{Environment.OSVersion.VersionString}\n" +
			$"{(IsElevated ? "Запущен с правами администратора" : "Запущен без прав администратора")}");

	/// <summary>
	/// Undoes what the program can create. Being able to remove something cleanly is a condition
	/// for trying an unfamiliar executable at all — and deleting the exe on its own used to leave
	/// a scheduled task launching a file that is no longer there, at every logon.
	/// </summary>
	private void Uninstall()
	{
		var answer = MessageBox.Show(
			"Удалить задачу планировщика, ярлык на рабочем столе и запомненные Windows позиции значков?\n\n" +
			"Да — удалить и настройки TrayMon.json тоже.\n" +
			"Нет — оставить TrayMon.json.\n" +
			"Отмена — ничего не делать.\n\n" +
			"Сам файл TrayMon.exe программа не удаляет — уберите его вручную после выхода.",
			"TrayMon — удаление", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
		if (answer == DialogResult.Cancel) return;

		var report = Autostart.Uninstall(answer == DialogResult.Yes);
		report.Add("");
		report.Add("Осталось удалить сам файл TrayMon.exe и папку программы.");
		TextWindow("TrayMon — удаление", string.Join('\n', report));
		ExitThread();
	}

	// ---- small dialogs ----

	/// <summary>
	/// A message box that cannot end up behind other windows. The program has no visible window
	/// of its own, so an unowned box goes to the back and looks like a hang.
	/// </summary>
	private static void Info(string text, MessageBoxIcon icon = MessageBoxIcon.Information)
	{
		using var owner = Anchor();
		MessageBox.Show(owner, text, "TrayMon", MessageBoxButtons.OK, icon);
	}

	private static Form Anchor()
	{
		var form = new Form
		{
			StartPosition = FormStartPosition.CenterScreen,
			FormBorderStyle = FormBorderStyle.None,
			ShowInTaskbar = false,
			Size = new Size(1, 1),
			Opacity = 0,
			TopMost = true,
		};
		form.Show();
		return form;
	}

	/// <summary>A read-only window for text worth copying — diagnostics, the summary, a report.</summary>
	private static void TextWindow(string title, string text)
	{
		// Declared before the form so it outlives every control that points at it, and is
		// released on the way out whatever happens inside.
		using var mono = new Font("Consolas", 9f);
		using var form = new Form
		{
			Text = title,
			StartPosition = FormStartPosition.CenterScreen,
			// Everything is laid out by the layout engine and scaled by font, because this
			// program declares PerMonitorV2 and therefore owns the consequences at 150 %.
			AutoScaleMode = AutoScaleMode.Font,
			ClientSize = new Size(680, 460),
			MinimizeBox = false,
			ShowInTaskbar = false,
			TopMost = true,
		};
		var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8) };
		layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
		layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		var box = new TextBox
		{
			Dock = DockStyle.Fill,
			Multiline = true,
			ReadOnly = true,
			ScrollBars = ScrollBars.Both,
			WordWrap = false,
			Font = mono,
			Text = text.Replace("\n", Environment.NewLine),
		};
		var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
		var close = new Button { Text = "Закрыть", AutoSize = true, DialogResult = DialogResult.OK };
		var copy = new Button { Text = "Копировать", AutoSize = true };
		copy.Click += (_, _) => { try { Clipboard.SetText(box.Text); } catch (Exception) { /* clipboard busy */ } };
		buttons.Controls.Add(close);
		buttons.Controls.Add(copy);
		layout.Controls.Add(box, 0, 0);
		layout.Controls.Add(buttons, 0, 1);
		form.Controls.Add(layout);
		form.AcceptButton = close;
		form.CancelButton = close;
		form.ShowDialog();
	}

	private static string Prompt(string title, string current)
	{
		using var form = new Form
		{
			Text = "TrayMon",
			AutoScaleMode = AutoScaleMode.Font,
			AutoSize = true,
			AutoSizeMode = AutoSizeMode.GrowAndShrink,
			FormBorderStyle = FormBorderStyle.FixedDialog,
			StartPosition = FormStartPosition.CenterScreen,
			MinimizeBox = false,
			MaximizeBox = false,
			ShowInTaskbar = false,
			TopMost = true,
		};
		var layout = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 1,
			RowCount = 3,
			AutoSize = true,
			AutoSizeMode = AutoSizeMode.GrowAndShrink,
			Padding = new Padding(12),
		};
		var caption = new Label { Text = title, AutoSize = true, MaximumSize = new Size(360, 0), Margin = new Padding(0, 0, 0, 6) };
		var box = new TextBox { Text = current, Width = 360, Margin = new Padding(0, 0, 0, 8) };
		var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
		var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
		var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
		buttons.Controls.Add(cancel);
		buttons.Controls.Add(ok);
		layout.Controls.Add(caption, 0, 0);
		layout.Controls.Add(box, 0, 1);
		layout.Controls.Add(buttons, 0, 2);
		form.Controls.Add(layout);
		form.AcceptButton = ok;
		form.CancelButton = cancel;
		return form.ShowDialog() == DialogResult.OK ? box.Text.Trim() : null;
	}

	// ---- number formatting ----

	/// <summary>
	/// MB/s for an icon — always whole. Tenths would be unreadable at 16 pixels anyway, and
	/// every changed digit costs a repaint plus a call into the shell: five icons flickering
	/// through decimals measured at +0.66 % of a core, three times the price of the data itself.
	/// </summary>
	private static string Mb(double v) => Math.Round(v, 0).ToString("0", CultureInfo.InvariantCulture);

	/// <summary>MB/s for a tooltip, always with one decimal.</summary>
	private static string Mb2(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

	/// <summary>
	/// Network icons count in megabits, not megabytes — and whole ones, like every other icon.
	/// Megabits are the unit a link is rated in (this Wi-Fi links at 1200 Mbit/s) and the only
	/// one in which an ordinary working day is visible at all: background chatter of 30 KB/s is
	/// 0 MB/s however many decimals are drawn, but 1 Mbit/s. Decimals stay out of it for the
	/// reason they are out of the rest — a changed digit costs a repaint and a call into the shell.
	/// </summary>
	private static string MbitWhole(double megabytesPerSecond) =>
		Math.Round(megabytesPerSecond * 8, 0).ToString("0", CultureInfo.InvariantCulture);

	/// <summary>Mbit/s for a tooltip, always with one decimal.</summary>
	private static string Mbit(double megabytesPerSecond) =>
		(megabytesPerSecond * 8).ToString("0.0", CultureInfo.InvariantCulture);

	/// <summary>
	/// Fan speed in thousands: 586 rpm shows as 0.6, 1240 as 1.2. Four digits do not fit into
	/// 16 pixels, and a coarser number also stops the icon repainting on every small drift.
	/// </summary>
	private static string Rpm(double v) => (v / 1000).ToString("0.0", CultureInfo.InvariantCulture);

	/// <summary>Severity for a fan: only a standstill is alarming, so it is 100 or 0.</summary>
	private static double Stalled(double rpm) => rpm > 0 ? 0 : 100;

	private static string Whole(double? v) => v.HasValue ? Math.Round(v.Value, 0).ToString("0", CultureInfo.InvariantCulture) : null;
	private static string Pct(double? v) => v.HasValue ? v.Value.ToString("0", CultureInfo.InvariantCulture) + "%" : "—";
	private static string Deg(double? v) => v.HasValue ? v.Value.ToString("0", CultureInfo.InvariantCulture) + "°C" : "—";

	private bool _disposed;

	protected override void Dispose(bool disposing)
	{
		// Guarded because the context can be disposed both by the framework and by Main.
		if (disposing && !_disposed)
		{
			_disposed = true;
			_timer?.Stop();
			_timer?.Dispose();

			// Wait for the background readers first. Closing the sensor library unloads a ring-0
			// driver, and doing that while a SMART query is still in flight took the process down
			// on exit — the tasks were not kept anywhere, so nothing could wait for them.
			Task[] pending;
			lock (_background) pending = _background.Where(t => !t.IsCompleted).ToArray();
			if (pending.Length > 0)
			{
				try { Task.WaitAll(pending, 5000); } catch (Exception) { /* going away regardless */ }
			}

			foreach (var slot in _order) slot.Icon?.Dispose();
			_menu?.Dispose();
			_perf?.Dispose();
			_gpu?.Dispose();
			_lhm?.Dispose();
		}
		base.Dispose(disposing);
	}
}
